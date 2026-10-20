namespace Tenantry.Core.Directory;

/// <summary>
/// Read access to one tenant's directory. Implemented by the demo tenant now and by the local
/// cache later; view models depend only on this.
/// </summary>
public interface ITenantDirectory
{
    /// <summary>A display name for the tenant, shown in the title bar.</summary>
    string TenantName { get; }

    Task<IReadOnlyList<UserSummary>> GetUsersAsync(CancellationToken cancellationToken = default);
}
