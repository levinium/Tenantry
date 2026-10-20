using Tenantry.Core.Capabilities;
using Tenantry.Core.Tenancy;
using Tenantry.ViewModels;

namespace Tenantry.Tests.Fakes;

/// <summary>Stands in for sign-in and setup, so screen tests never touch MSAL or Graph.</summary>
sealed class FakeConnector : ITenantConnector
{
    public List<TenantProfile> Profiles { get; } = [];
    public Func<TenantProfile, CancellationToken, Task<ConnectedTenant>>? OnConnect { get; set; }
    public Func<IProgress<string>, CancellationToken, Task<SetupOutcome>>? OnSetup { get; set; }
    public Func<string, string, CancellationToken, Task<TenantProfile>>? OnManual { get; set; }

    public static TenantProfile Profile(string id = "p1", string name = "Contoso") =>
        new(id, name, $"tid-{id}", $"{name.ToLowerInvariant()}.example", $"app-{id}", DateTimeOffset.UnixEpoch);

    public static ConnectedTenant Tenant(string[]? roles = null, string[]? plans = null) => new(
        "Contoso", "admin@contoso.example", FakeDirectory.Standard(),
        new TenantContext(new HashSet<string>(roles ?? [DirectoryRoles.GlobalAdministrator]), new HashSet<string>(plans ?? []), true));

    public IReadOnlyList<TenantProfile> LoadProfiles() => Profiles.ToList();

    public void RemoveProfile(string profileId) => Profiles.RemoveAll(p => p.Id == profileId);

    public Task<ConnectedTenant> ConnectAsync(TenantProfile profile, CancellationToken cancellationToken = default) =>
        OnConnect?.Invoke(profile, cancellationToken) ?? Task.FromResult(Tenant());

    public ConnectedTenant ConnectDemo() => Tenant() with { IsDemo = true };

    public Task<SetupOutcome> SetUpAutomaticallyAsync(IProgress<string> progress, CancellationToken cancellationToken = default) =>
        OnSetup?.Invoke(progress, cancellationToken) ?? throw new InvalidOperationException("no setup configured");

    public Task<TenantProfile> AddManualAsync(string tenantId, string clientId, CancellationToken cancellationToken = default) =>
        OnManual?.Invoke(tenantId, clientId, cancellationToken) ?? throw new InvalidOperationException("no manual configured");
}
