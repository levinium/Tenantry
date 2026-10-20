using Tenantry.Auth;
using Tenantry.Cache;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Lists;
using Tenantry.Core.Permissions;
using Tenantry.Core.Tenancy;
using Tenantry.Demo;
using Tenantry.Exchange;
using Tenantry.Graph;
using Tenantry.ViewModels;

namespace Tenantry.App.Services;

/// <summary>The real <see cref="ITenantConnector"/>: MSAL for sign-in, Graph for everything else.</summary>
public sealed class TenantConnector : ITenantConnector
{
    /// <summary>Overrides the setup sign-in client (for example, a Tenantry-owned bootstrap app).</summary>
    public const string BootstrapClientVariable = "TENANTRY_BOOTSTRAP_CLIENT_ID";

    static readonly HttpClient Http = new();

    // Purview region discovery reads a redirect rather than following it.
    static readonly HttpClient NoRedirectHttp = new(new HttpClientHandler { AllowAutoRedirect = false });

    readonly TenantProfileStore _store = new();

    public IReadOnlyList<TenantProfile> LoadProfiles() => _store.Load();

    public void SetDefaultProfile(string? profileId) => _store.SetDefault(profileId);

    const string DatabaseFile = "cache.db";

    public void RemoveProfile(string profileId)
    {
        _store.Remove(profileId);
        // The profile's token cache, local database and database key go with it.
        var folder = Path.Combine(AppPaths.Root, "tenants", profileId);
        CacheKeyStore.Delete(profileId, AppPaths.TenantFolder(profileId));
        SqliteEntityStore.Delete(Path.Combine(folder, DatabaseFile));
        if (System.IO.Directory.Exists(folder))
            System.IO.Directory.Delete(folder, recursive: true);
    }

    public async Task<ConnectedTenant> ConnectAsync(TenantProfile profile, CancellationToken cancellationToken = default)
    {
        var folder = AppPaths.TenantFolder(profile.Id);
        var tokens = await MsalTokenSource.CreateAsync(profile.ClientId, profile.TenantId, folder, profile.LastAccount);
        var graph = new GraphClient(Http, tokens);
        var context = await new TenantContextLoader(graph).LoadAsync(cancellationToken);
        _store.MarkUsed(profile.Id, tokens.Account, DateTimeOffset.UtcNow);

        var entities = OpenLocalCopy(profile.Id, folder);
        return new ConnectedTenant(profile.DisplayName, tokens.Account ?? "", new CachedTenantDirectory(entities, profile.DisplayName), context,
            ListState: new JsonListStateStore(folder),
            Sync: new DirectorySync(graph, entities, new RestCmdletRunner(Http, NoRedirectHttp, tokens, profile.TenantId)),
            Resources: entities);
    }

    /// <summary>
    /// The tenant's encrypted database, or an in-memory copy when the OS offers no secret store
    /// for its key: tenant data is never written to disk unencrypted.
    /// </summary>
    static IEntityStore OpenLocalCopy(string profileId, string folder) =>
        CacheKeyStore.TryGetOrCreate(profileId, folder) is { } key
            ? SqliteEntityStore.Open(Path.Combine(folder, DatabaseFile), key)
            : new MemoryEntityStore();

    /// <summary>
    /// The demo signs in as a User and Exchange Administrator in a tenant without Entra ID P1,
    /// so it shows both kinds of gated feature (missing role, missing license) out of the box.
    /// </summary>
    public ConnectedTenant ConnectDemo() => new(
        "Northgate (demo)",
        $"admin@{DemoTenantDirectory.Domain}",
        new DemoTenantDirectory(),
        new TenantContext(
            new HashSet<string>([DirectoryRoles.UserAdministrator, DirectoryRoles.ExchangeAdministrator]),
            new HashSet<string>(["EXCHANGE_S_STANDARD"]),
            OperatingSystem.IsWindows()),
        IsDemo: true);

    public async Task<SetupOutcome> SetUpAutomaticallyAsync(IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        var bootstrap = Environment.GetEnvironmentVariable(BootstrapClientVariable) is { Length: > 0 } custom
            ? custom
            : TenantryPermissions.GraphCommandLineToolsClientId;
        var tokens = await MsalTokenSource.CreateAsync(bootstrap, MsalTokenSource.AnyOrganization, cacheFolder: null);
        var graph = new GraphClient(Http, tokens, TenantryPermissions.BootstrapScopes);

        var result = await new AppRegistrationProvisioner(graph).ProvisionAsync(progress, cancellationToken);
        var profile = _store.SaveSetup(result.TenantId, result.TenantName, result.PrimaryDomain, result.ClientId, tokens.Account);
        return new SetupOutcome(profile, result.Reused, result.Unresolved);
    }

    public async Task<TenantProfile> AddManualAsync(string tenantId, string clientId, CancellationToken cancellationToken = default)
    {
        var existing = _store.Load().FirstOrDefault(p => string.Equals(p.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));
        var profileId = existing?.Id ?? TenantProfileStore.NewId();

        // Signing in with the admin's own registration is the verification: it proves the IDs,
        // the redirect URI, the consent and the assignment all line up.
        var tokens = await MsalTokenSource.CreateAsync(clientId, tenantId, AppPaths.TenantFolder(profileId));
        var graph = new GraphClient(Http, tokens);
        var org = await graph.GetFirstAsync("organization?$select=displayName,verifiedDomains", cancellationToken)
                  ?? throw new InvalidOperationException("Signed in, but the tenant returned no organization.");
        var name = org.GetProperty("displayName").GetString() ?? tenantId;
        var domain = org.GetProperty("verifiedDomains").EnumerateArray()
            .Where(d => d.GetProperty("isDefault").GetBoolean())
            .Select(d => d.GetProperty("name").GetString())
            .FirstOrDefault() ?? "";

        return _store.SaveSetup(tokens.TenantId ?? tenantId, name, domain, clientId, tokens.Account, profileId);
    }
}
