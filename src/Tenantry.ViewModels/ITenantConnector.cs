using Tenantry.Core.Capabilities;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.Core.Tenancy;

namespace Tenantry.ViewModels;

/// <summary>A tenant after sign-in: what the main window needs to run.</summary>
/// <param name="Sync">Keeps the local copy current; null for the demo, which has nothing to sync.</param>
/// <param name="Resources">Released when the tenant is closed (the local database's connection).</param>
/// <param name="Changes">Stages, guards, applies and logs edits; null where editing isn't possible.</param>
public sealed record ConnectedTenant(
    string TenantName,
    string Account,
    ITenantDirectory Directory,
    TenantContext Context,
    bool IsDemo = false,
    IListStateStore? ListState = null,
    ITenantSync? Sync = null,
    IDisposable? Resources = null,
    Tenantry.Core.Changes.ChangePipeline? Changes = null,
    IUserRecords? Records = null,
    IReadOnlyList<string>? MissingPermissions = null,
    Func<IProgress<string>, CancellationToken, Task<IReadOnlyList<string>>>? UpdatePermissions = null,
    ISyncSettingsStore? SyncSettings = null,
    Tenantry.Core.Changes.IPasswordStyleStore? PasswordStyle = null,
    Tenantry.Core.PowerShell.IChangeScripts? Scripts = null,
    Func<Tenantry.Core.PowerShell.IPowerShellSession>? PowerShell = null);

/// <summary>The outcome of the automatic setup.</summary>
public sealed record SetupOutcome(TenantProfile Profile, bool Reused, IReadOnlyList<string> Unresolved);

/// <summary>
/// Everything the screens do that touches sign-in or a tenant. The app implements it with MSAL
/// and Graph; tests implement it with fakes, so no view model ever signs in to anything.
/// </summary>
public interface ITenantConnector
{
    IReadOnlyList<TenantProfile> LoadProfiles();

    void RemoveProfile(string profileId);

    /// <summary>Sets the tenant opened at startup, or clears it with null.</summary>
    void SetDefaultProfile(string? profileId);

    /// <summary>Signs in to a saved tenant (browser if needed) and loads roles and licenses.</summary>
    Task<ConnectedTenant> ConnectAsync(TenantProfile profile, CancellationToken cancellationToken = default);

    ConnectedTenant ConnectDemo();

    /// <summary>The automatic setup: sign in, create or update the app registration, save the profile.</summary>
    Task<SetupOutcome> SetUpAutomaticallyAsync(IProgress<string> progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// The manual path's last step: sign in with the admin-created registration to prove it
    /// works, then save the profile.
    /// </summary>
    Task<TenantProfile> AddManualAsync(string tenantId, string clientId, CancellationToken cancellationToken = default);
}
