using Tenantry.Core.Cache;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>The log archive (10), run in the background on its own schedule, like mailbox sizes.</summary>
public sealed partial class DirectorySync
{
    /// <summary>When the archive last added new events.</summary>
    public const string LogsMark = "logarchive";

    Task? _logs;
    IReadOnlyList<string> _logNotes = [];

    /// <summary>Raised (on a worker thread) when the archive has added events.</summary>
    public event EventHandler? LogsArchived;

    /// <summary>What the last archive run couldn't read (a missing license or role).</summary>
    public IReadOnlyList<string> LogNotes => _logNotes;

    /// <summary>Only where asked for (the app; not tests of the rest of the sync).</summary>
    LogArchiver? Archiver => keepLogs && store is ILogArchive archive ? new LogArchiver(graph, archive, store, exchange, () => Settings, _now) : null;

    void StartLogArchive(SyncScope scope)
    {
        if (!Settings.LogsOn || _logs is { IsCompleted: false } || !Due(LogsMark, scope, Settings.LogsMinutes))
            return;
        _logs = ArchiveLogsNowAsync(null, CancellationToken.None);
    }

    /// <summary>Adds new sign-ins and audit events to the archive now (the archive page's Update Now).</summary>
    public async Task ArchiveLogsNowAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (Archiver is not { } archiver)
            return;
        try
        {
            var (added, notes) = await Task.Run(() => archiver.ArchiveAsync(progress, cancellationToken), cancellationToken);
            _logNotes = notes;
            store.SetMark(LogsMark, new SyncMark(null, _now()));
            if (added > 0)
                LogsArchived?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logNotes = [$"Sign-in and audit history wasn't archived: {e.Message}"];
        }
    }
}
