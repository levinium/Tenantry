using Tenantry.Auth;
using Tenantry.Cache;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
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

        // A newer Tenantry may need permissions this tenant hasn't granted yet. Check against a
        // fresh token (a cached one can predate a consent change) before saying so.
        var missing = MissingPermissions(tokens);
        if (missing.Count > 0)
        {
            await tokens.RefreshAsync([TenantryPermissions.Graph.DefaultScope], cancellationToken);
            missing = MissingPermissions(tokens);
        }

        var entities = OpenLocalCopy(profile.Id, folder);
        var account = tokens.Account ?? "";
        var exchange = new RestCmdletRunner(Http, NoRedirectHttp, tokens, profile.TenantId);
        var sharePoint = new SharePointAdmin(Http, tokens, graph);
        var passwordStyle = new JsonPasswordStyleStore(folder);
        var syncSettings = new Tenantry.Core.Directory.JsonSyncSettingsStore(folder);
        return new ConnectedTenant(profile.DisplayName, account, new CachedTenantDirectory(entities, profile.DisplayName), context,
            MissingPermissions: missing,
            UpdatePermissions: async (progress, ct) =>
            {
                // The same one-step setup as adding a tenant: it finds this tenant's existing
                // registration and grants whatever is missing.
                await SetUpAutomaticallyAsync(progress, ct);
                // Microsoft 365 takes a little while to apply a new grant: a token asked for at
                // once can still lack it (seen live: the first run "failed", a second one passed).
                // So check again over about a minute and a half before saying anything is missing.
                var missing = (IReadOnlyList<string>)[];
                foreach (var wait in PermissionWaits)
                {
                    if (wait > TimeSpan.Zero)
                    {
                        progress.Report("Waiting for Microsoft 365 to apply the new permissions (this can take a minute)…");
                        await Task.Delay(wait, ct);
                    }
                    await tokens.RefreshAsync([TenantryPermissions.Graph.DefaultScope], ct);
                    missing = MissingPermissions(tokens);
                    if (missing.Count == 0)
                        break;
                }
                return missing;
            },
            ListState: new JsonListStateStore(folder),
            Sync: new DirectorySync(graph, entities, exchange, settings: syncSettings.Load, sharePoint: sharePoint),
            Resources: entities,
            Changes: new ChangePipeline(new GraphChangeExecutor(graph, entities, exchange: exchange, sharePoint: sharePoint, passwordStyle: passwordStyle.Load),
                new ChangeLog(entities), new JsonTestModeStore(folder, TestModeByDefault), account),
            Records: new GraphUserRecords(graph, entities, exchange, sharePoint),
            SyncSettings: syncSettings,
            PasswordStyle: passwordStyle);
    }

    /// <summary>How long to wait before each re-check of newly granted permissions (about 90 s in all).</summary>
    static readonly TimeSpan[] PermissionWaits =
        [TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(35)];

    /// <summary>Graph permissions Tenantry asks for that this sign-in's token doesn't carry.</summary>
    static IReadOnlyList<string> MissingPermissions(MsalTokenSource tokens)
    {
        var granted = tokens.GrantedScopes(TenantryPermissions.Graph.Audience);
        if (granted.Count == 0)
            return [];
        return TenantryPermissions.Graph.DelegatedScopes
            .Where(s => s is not ("openid" or "profile" or "offline_access") && !granted.Contains(s))
            .ToArray();
    }

    /// <summary>
    /// Development builds start every tenant in Test mode (only zz-tenantry-test- objects can
    /// change); only the person using the app can turn it off, per tenant.
    /// </summary>
#if DEBUG
    const bool TestModeByDefault = true;
#else
    const bool TestModeByDefault = false;
#endif

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
    public ConnectedTenant ConnectDemo() => ConnectDemo(premium: false);

    /// <param name="premium">Act as a tenant with Entra ID P2 (PIM), for screenshots and checks of those pages.</param>
    public ConnectedTenant ConnectDemo(bool premium)
    {
        var directory = new DemoTenantDirectory();
        var records = new DemoUserRecords(directory, pim: premium);
        var passwordStyle = new MemoryPasswordStyleStore();
        var account = DemoUserRecords.Account;
        return new ConnectedTenant(
            "Northgate (demo)",
            account,
            directory,
            new TenantContext(
                new HashSet<string>([DirectoryRoles.UserAdministrator, DirectoryRoles.ExchangeAdministrator, DirectoryRoles.IntuneAdministrator,
                    DirectoryRoles.SharePointAdministrator, DirectoryRoles.PrivilegedRoleAdministrator]),
                new HashSet<string>(premium ? ["EXCHANGE_S_STANDARD", "AAD_PREMIUM", "AAD_PREMIUM_P2"] : ["EXCHANGE_S_STANDARD"]),
                OperatingSystem.IsWindows()),
            IsDemo: true,
            // Demo edits change the generated people in memory; nothing real, so no Test mode.
            Changes: new ChangePipeline(new DemoChangeExecutor(directory, records, passwordStyle.Load), new ChangeLog(new MemoryEntityStore()),
                new MemoryTestModeStore(false), account),
            Records: records,
            PasswordStyle: passwordStyle);
    }

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
