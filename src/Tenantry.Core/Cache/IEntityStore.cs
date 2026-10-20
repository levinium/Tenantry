using System.Text.Json.Nodes;

namespace Tenantry.Core.Cache;

/// <summary>
/// A change to one cached object. <see cref="Properties"/> are merged into what is stored
/// (property by property, as Graph delta sends only what changed); null removes the object.
/// </summary>
/// <param name="OnlyIfExists">Merge into an existing object but never create one (for data
/// fetched separately, such as sign-in activity, that must not invent half-empty users).</param>
public sealed record EntityChange(string Id, JsonObject? Properties, bool OnlyIfExists = false)
{
    public static EntityChange Removed(string id) => new(id, null);
}

/// <summary>Where a sync left off: the service's cursor (a delta link) and when it ran.</summary>
public sealed record SyncMark(string? Cursor, DateTimeOffset SyncedAt);

/// <summary>
/// One tenant's local copy of directory objects, as JSON, grouped by kind ("user", "sku").
/// The store knows nothing about Graph; the sync decides what goes in and the directory decides
/// how to read it. Implementations are thread-safe: a sync writes while lists read.
/// </summary>
public interface IEntityStore : IDisposable
{
    /// <summary>True when the data survives a restart (an encrypted file), false when in memory.</summary>
    bool IsPersistent { get; }

    /// <summary>Every object of a kind, as JSON text.</summary>
    IReadOnlyList<string> All(string kind);

    /// <summary>One object as JSON text, or null.</summary>
    string? Get(string kind, string id);

    /// <summary>Applies changes in one transaction and returns how many objects actually changed.</summary>
    int Apply(string kind, IReadOnlyCollection<EntityChange> changes);

    /// <summary>Removes every object of a kind whose id is not in <paramref name="keep"/>; returns how many.</summary>
    int RemoveExcept(string kind, IReadOnlySet<string> keep);

    SyncMark? GetMark(string key);

    void SetMark(string key, SyncMark mark);
}
