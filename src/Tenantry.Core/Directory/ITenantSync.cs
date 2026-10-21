namespace Tenantry.Core.Directory;

/// <summary>How much a sync does.</summary>
public enum SyncScope
{
    /// <summary>Fetch changes; slow full reads (mailboxes, sign-in times) only when they're due. The timer's sync.</summary>
    Changes,

    /// <summary>
    /// Fetch changes and redo the slow full reads (mailboxes, sign-in times) now; the background
    /// per-mailbox reads (sizes, replies) only if they're due. The Refresh button's sync.
    /// </summary>
    Refresh,

    /// <summary>Everything Refresh does, plus the background per-mailbox reads now. Settings' "Check everything now".</summary>
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

    /// <summary>Each part of the local copy and when it was last read, for Settings.</summary>
    IReadOnlyList<SyncArea> Areas => [];

    /// <summary>
    /// Raised, possibly on a worker thread, when work the sync started in the background (mailbox
    /// sizes and replies) has stored new values, so lists can re-read the local copy.
    /// </summary>
    event EventHandler? BackgroundUpdated { add { } remove { } }

    /// <summary>What the background work is doing ("Mailbox sizes: 120 of 604"), or null when idle.</summary>
    string? BackgroundStatus => null;

    /// <summary>Raised, possibly on a worker thread, whenever <see cref="BackgroundStatus"/> changes.</summary>
    event EventHandler? BackgroundStatusChanged { add { } remove { } }

    /// <summary>Brings the local copy up to date; returns how many objects changed.</summary>
    Task<int> SyncAsync(IProgress<string>? progress = null, SyncScope scope = SyncScope.Changes, CancellationToken cancellationToken = default);
}
