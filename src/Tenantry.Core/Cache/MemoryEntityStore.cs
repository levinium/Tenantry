namespace Tenantry.Core.Cache;

/// <summary>
/// An <see cref="IEntityStore"/> held in memory. Used by tests, and on machines with no OS
/// secret store to protect a database key: the app still syncs and works, it just starts from
/// scratch each launch rather than writing tenant data to disk unencrypted.
/// </summary>
public sealed class MemoryEntityStore : IEntityStore, ILogArchive
{
    readonly Lock _lock = new();
    readonly Dictionary<string, Dictionary<string, string>> _kinds = new(StringComparer.Ordinal);
    readonly Dictionary<string, SyncMark> _marks = new(StringComparer.Ordinal);
    readonly Dictionary<string, Dictionary<string, ArchivedEvent>> _events = new(StringComparer.Ordinal);

    public bool IsPersistent => false;

    public IReadOnlyList<string> All(string kind)
    {
        lock (_lock)
            return _kinds.TryGetValue(kind, out var items) ? items.Values.ToArray() : [];
    }

    public string? Get(string kind, string id)
    {
        lock (_lock)
            return _kinds.TryGetValue(kind, out var items) ? items.GetValueOrDefault(id) : null;
    }

    public int Apply(string kind, IReadOnlyCollection<EntityChange> changes)
    {
        lock (_lock)
        {
            if (!_kinds.TryGetValue(kind, out var items))
                _kinds[kind] = items = new Dictionary<string, string>(StringComparer.Ordinal);
            var changed = 0;
            foreach (var change in changes)
            {
                var exists = items.TryGetValue(change.Id, out var current);
                if (change.Properties is null)
                {
                    if (items.Remove(change.Id))
                        changed++;
                    continue;
                }
                if (!exists && change.OnlyIfExists)
                    continue;
                if (EntityMerge.Merge(current, change.Properties) is { } merged)
                {
                    items[change.Id] = merged;
                    changed++;
                }
            }
            return changed;
        }
    }

    public int RemoveExcept(string kind, IReadOnlySet<string> keep)
    {
        lock (_lock)
        {
            if (!_kinds.TryGetValue(kind, out var items))
                return 0;
            var gone = items.Keys.Where(id => !keep.Contains(id)).ToArray();
            foreach (var id in gone)
                items.Remove(id);
            return gone.Length;
        }
    }

    public SyncMark? GetMark(string key)
    {
        lock (_lock)
            return _marks.GetValueOrDefault(key);
    }

    public void SetMark(string key, SyncMark mark)
    {
        lock (_lock)
            _marks[key] = mark;
    }

    public int AddEvents(string kind, IReadOnlyCollection<ArchivedEvent> events)
    {
        lock (_lock)
        {
            if (!_events.TryGetValue(kind, out var held))
                _events[kind] = held = new Dictionary<string, ArchivedEvent>(StringComparer.Ordinal);
            return events.Count(e => held.TryAdd(e.Id, e));
        }
    }

    public IReadOnlyList<ArchivedEvent> ReadEvents(string kind, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        lock (_lock)
            return _events.TryGetValue(kind, out var held)
                ? [.. held.Values.Where(e => e.At >= from && e.At <= to).OrderByDescending(e => e.At).Take(limit)]
                : [];
    }

    public ArchiveSpan Span(string kind)
    {
        lock (_lock)
            return _events.TryGetValue(kind, out var held) && held.Count > 0
                ? new ArchiveSpan(held.Count, held.Values.Min(e => e.At), held.Values.Max(e => e.At))
                : new ArchiveSpan(0, null, null);
    }

    public int PruneEvents(string kind, DateTimeOffset before)
    {
        lock (_lock)
        {
            if (!_events.TryGetValue(kind, out var held))
                return 0;
            var old = held.Values.Where(e => e.At < before).Select(e => e.Id).ToArray();
            foreach (var id in old)
                held.Remove(id);
            return old.Length;
        }
    }

    public void Dispose()
    {
    }
}
