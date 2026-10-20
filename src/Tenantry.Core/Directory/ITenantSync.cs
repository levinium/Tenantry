namespace Tenantry.Core.Directory;

/// <summary>How much a sync does.</summary>
public enum SyncScope
{
    /// <summary>Fetch changes; slow full reads (mailboxes, sign-in times) only when they're due. The timer's sync.</summary>
    Changes,

    /// <summary>Fetch changes and redo every slow full read now. The Refresh button's sync.</summary>
    Everything,

    /// <summary>Forget the delta links and download everything again.</summary>
    StartOver,
}

/// <summary>
/// Keeps a tenant's local copy current. The first sync downloads everything; later ones fetch
/// only what changed since the last.
/// </summary>
public interface ITenantSync
{
    /// <summary>When the directory was last brought up to date, or null if it never has been.</summary>
    DateTimeOffset? LastSyncedAt { get; }

    /// <summary>True when the local copy survives a restart.</summary>
    bool IsPersistent { get; }

    /// <summary>
    /// What the last sync couldn't fetch and why, without failing ("Mailbox details: no Exchange
    /// role"). Shown in the sync status's tooltip.
    /// </summary>
    IReadOnlyList<string> Notes { get; }

    /// <summary>Brings the local copy up to date; returns how many objects changed.</summary>
    Task<int> SyncAsync(IProgress<string>? progress = null, SyncScope scope = SyncScope.Changes, CancellationToken cancellationToken = default);
}
