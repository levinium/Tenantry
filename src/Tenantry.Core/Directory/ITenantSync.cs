namespace Tenantry.Core.Directory;

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

    /// <summary>Brings the local copy up to date; returns how many objects changed.</summary>
    /// <param name="full">Start over instead of fetching changes (the "refresh everything" escape hatch).</param>
    Task<int> SyncAsync(IProgress<string>? progress = null, bool full = false, CancellationToken cancellationToken = default);
}
