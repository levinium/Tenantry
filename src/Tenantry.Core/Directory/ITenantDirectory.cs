namespace Tenantry.Core.Directory;

/// <summary>
/// Read access to one tenant's directory: the local copy for a real tenant, generated data for
/// the demo. View models depend only on this.
/// </summary>
public interface ITenantDirectory
{
    /// <summary>A display name for the tenant, shown in the title bar.</summary>
    string TenantName { get; }

    Task<IReadOnlyList<UserSummary>> GetUsersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GroupSummary>> GetGroupsAsync(CancellationToken cancellationToken = default);
}
