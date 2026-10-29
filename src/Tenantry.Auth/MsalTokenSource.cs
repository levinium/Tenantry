using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Tenantry.Core.Auth;

namespace Tenantry.Auth;

/// <summary>
/// The signed-in admin's tokens, from MSAL. Silent when it can be, the system browser when it
/// must (first sign-in, expired session, or a claims challenge such as a demand for recent
/// MFA). Interactive prompts are serialised so parallel calls never open several browser tabs.
/// </summary>
public sealed class MsalTokenSource : ITokenSource
{
    public const string RedirectUri = "http://localhost";

    /// <summary>Authority for a sign-in before the tenant is known (the setup wizard).</summary>
    public const string AnyOrganization = "organizations";

    readonly IPublicClientApplication _app;
    readonly SemaphoreSlim _interactive = new(1, 1);
    string? _loginHint;
    IAccount? _account;

    MsalTokenSource(IPublicClientApplication app, string? loginHint)
    {
        _app = app;
        _loginHint = loginHint;
    }

    public string? Account => _account?.Username;

    /// <summary>
    /// When set, interactive sign-in uses the device code flow and reports the code here,
    /// for machines with no usable browser (WSL, SSH). Many tenants block device code with
    /// Conditional Access, so the browser stays the default.
    /// </summary>
    public Func<string, Task>? DeviceCodePrompt { get; set; }

    /// <summary>Tenant of the signed-in account; for the setup wizard, how it learns the tenant.</summary>
    public string? TenantId { get; private set; }

    /// <summary>True when tokens survive restarts; false when no OS secret store was usable.</summary>
    public bool IsPersistent { get; private set; }

    /// <param name="cacheFolder">Folder for the encrypted token cache, or null to keep tokens in memory only.</param>
    /// <param name="cacheName">A second cache beside the main one (its own keychain entry), or null for the main one.</param>
    public static async Task<MsalTokenSource> CreateAsync(string clientId, string tenantId, string? cacheFolder, string? loginHint = null, string? cacheName = null)
    {
        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithRedirectUri(RedirectUri)
            .WithClientName("Tenantry")
            .Build();
        var source = new MsalTokenSource(app, loginHint);
        if (cacheFolder is not null)
            source.IsPersistent = await TryPersistAsync(app, cacheFolder, cacheName);
        return source;
    }

    static async Task<bool> TryPersistAsync(IPublicClientApplication app, string folder, string? name)
    {
        try
        {
            var properties = new StorageCreationPropertiesBuilder(name is null ? "msal.cache" : $"msal-{name}.cache", folder)
                .WithMacKeyChain("Tenantry.TokenCache", name is null ? "Tenantry" : $"Tenantry.{name}")
                .WithLinuxKeyring("org.tenantry.tokencache", MsalCacheHelper.LinuxKeyRingDefaultCollection,
                    "Tenantry token cache", new KeyValuePair<string, string>("Product", "Tenantry"),
                    new KeyValuePair<string, string>("Version", name is null ? "1" : $"1-{name}"))
                .Build();
            var helper = await MsalCacheHelper.CreateAsync(properties);
            helper.VerifyPersistence();
            helper.RegisterCache(app.UserTokenCache);
            return true;
        }
        catch (MsalCachePersistenceException)
        {
            // No usable secret store (a Linux box without a keyring, typically). Keeping the
            // tokens in memory means signing in each launch, which beats an unencrypted file.
            return false;
        }
    }

    public async Task<string> GetTokenAsync(IReadOnlyList<string> scopes, string? claims = null, CancellationToken cancellationToken = default)
    {
        var account = _account ?? await FindAccountAsync();

        if (claims is null && account is not null)
        {
            try
            {
                return Remember(await _app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken));
            }
            catch (MsalUiRequiredException)
            {
                // Fall through to the browser.
            }
        }

        await _interactive.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have signed in while this one waited.
            if (claims is null && _account is not null && account is null)
            {
                try
                {
                    return Remember(await _app.AcquireTokenSilent(scopes, _account).ExecuteAsync(cancellationToken));
                }
                catch (MsalUiRequiredException)
                {
                }
            }

            if (DeviceCodePrompt is { } prompt)
            {
                var deviceCode = _app.AcquireTokenWithDeviceCode(scopes, r => prompt(r.Message));
                if (claims is not null)
                    deviceCode = deviceCode.WithClaims(claims);
                return Remember(await deviceCode.ExecuteAsync(cancellationToken));
            }

            var builder = _app.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false);
            builder = (_account ?? account) is { } known ? builder.WithAccount(known)
                : _loginHint is not null ? builder.WithLoginHint(_loginHint)
                : builder.WithPrompt(Prompt.SelectAccount);
            if (claims is not null)
                builder = builder.WithClaims(claims);
            return Remember(await builder.ExecuteAsync(cancellationToken));
        }
        finally
        {
            _interactive.Release();
        }
    }

    /// <summary>
    /// Gets a fresh token rather than the cached one, so newly granted permissions show up
    /// without waiting for the cached token to expire.
    /// </summary>
    public async Task RefreshAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
    {
        if ((_account ?? await FindAccountAsync()) is not { } account)
            return;
        try
        {
            Remember(await _app.AcquireTokenSilent(scopes, account).WithForceRefresh(true).ExecuteAsync(cancellationToken));
        }
        catch (MsalUiRequiredException)
        {
            // The next ordinary call signs in again; nothing to refresh silently.
        }
    }

    /// <summary>Signs the account out of this app on this machine and clears its cached tokens.</summary>
    public async Task SignOutAsync()
    {
        foreach (var account in await _app.GetAccountsAsync())
            await _app.RemoveAsync(account);
        _account = null;
    }

    async Task<IAccount?> FindAccountAsync()
    {
        var accounts = (await _app.GetAccountsAsync()).ToList();
        return accounts.FirstOrDefault(a => _loginHint is not null && string.Equals(a.Username, _loginHint, StringComparison.OrdinalIgnoreCase))
               ?? (accounts.Count == 1 ? accounts[0] : null);
    }

    string Remember(AuthenticationResult result)
    {
        _account = result.Account;
        _loginHint = result.Account.Username;
        TenantId = result.TenantId;
        foreach (var scope in result.Scopes)
        {
            var slash = scope.LastIndexOf('/');
            var audience = slash > 0 ? scope[..slash] : "";
            var set = _granted.GetOrAdd(audience, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            set[scope[(slash + 1)..]] = 0;
        }
        return result.AccessToken;
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, byte>> _granted =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The permissions the last token for an API actually carries (what the tenant consented),
    /// by name ("User.ReadWrite.All"). Empty until a token for that audience has been issued.
    /// </summary>
    public IReadOnlySet<string> GrantedScopes(string audience) =>
        _granted.TryGetValue(audience.TrimEnd('/'), out var set) ? set.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
}
