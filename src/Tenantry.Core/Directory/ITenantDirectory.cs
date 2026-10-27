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

    /// <summary>One user as the lists would show them; null when they're gone. Implementations can read just that one.</summary>
    async Task<UserSummary?> GetUserAsync(string id, CancellationToken cancellationToken = default) =>
        (await GetUsersAsync(cancellationToken)).FirstOrDefault(u => u.Id == id);

    /// <summary>Every device: Entra's and Intune's records joined. Empty where devices aren't kept.</summary>
    Task<IReadOnlyList<DeviceSummary>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeviceSummary>>([]);

    /// <summary>One group or shared mailbox as the lists would show it; null when it's gone.</summary>
    async Task<GroupSummary?> GetGroupAsync(string id, CancellationToken cancellationToken = default) =>
        (await GetGroupsAsync(cancellationToken)).FirstOrDefault(g => g.Id == id);
}

/// <summary>
/// Re-reads one object from Microsoft 365 into the local copy, the same way a sync would, so a
/// record page opened from it shows current data a moment later.
/// </summary>
public interface IRecordRefresher
{
    /// <summary>The user (Graph) and, when Exchange is connected, their mailbox and email apps (and who can use it, when shared).</summary>
    Task RefreshUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>A group with its members and owners. A shared mailbox's row is its account: refreshed as a user.</summary>
    Task RefreshGroupAsync(string groupId, CancellationToken cancellationToken = default);
}
