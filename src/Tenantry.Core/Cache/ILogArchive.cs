namespace Tenantry.Core.Cache;

/// <summary>One archived log event: its id, when it happened, and the event as compact JSON.</summary>
public sealed record ArchivedEvent(string Id, DateTimeOffset At, string Json);

/// <summary>How much of one log the archive holds.</summary>
public sealed record ArchiveSpan(int Count, DateTimeOffset? Oldest, DateTimeOffset? Newest);

/// <summary>
/// Sign-in and audit history kept on this computer beyond Microsoft's retention (10), beside the
/// local copy in the same encrypted store, indexed by time so a date range reads quickly.
/// </summary>
public interface ILogArchive
{
    /// <summary>Adds events, skipping ones already held (same kind and id); returns how many were new.</summary>
    int AddEvents(string kind, IReadOnlyCollection<ArchivedEvent> events);

    /// <summary>Events between two times, newest first, at most <paramref name="limit"/>.</summary>
    IReadOnlyList<ArchivedEvent> ReadEvents(string kind, DateTimeOffset from, DateTimeOffset to, int limit);

    ArchiveSpan Span(string kind);

    /// <summary>Deletes events older than <paramref name="before"/>; returns how many.</summary>
    int PruneEvents(string kind, DateTimeOffset before);
}
