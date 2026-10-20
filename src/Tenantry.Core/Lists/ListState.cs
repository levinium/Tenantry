using System.Text.Json;

namespace Tenantry.Core.Lists;

public enum ListDensity
{
    Comfortable,
    Compact,
}

/// <summary>A column's place, visibility and width, in display order.</summary>
public sealed record ColumnLayout(string Key, bool Visible, double? Width = null);

/// <summary>
/// A named query plus, optionally, a column layout. Built-in views ship with the list and
/// can't be deleted; saved ones belong to the user.
/// </summary>
public sealed record SavedView(string Id, string Name, ListQuery Query, IReadOnlyList<ColumnLayout>? Columns = null, bool BuiltIn = false);

/// <summary>Everything about a list worth remembering between sessions.</summary>
public sealed record ListState(
    ListQuery Query,
    IReadOnlyList<ColumnLayout> Columns,
    ListDensity Density,
    IReadOnlyList<SavedView> Views,
    string? CurrentViewId);

public interface IListStateStore
{
    ListState? Load(string listKey);
    void Save(string listKey, ListState state);
}

/// <summary>Keeps state for the session only (the demo tenant, tests).</summary>
public sealed class MemoryListStateStore : IListStateStore
{
    readonly Dictionary<string, ListState> _states = new(StringComparer.Ordinal);

    public ListState? Load(string listKey) => _states.GetValueOrDefault(listKey);

    public void Save(string listKey, ListState state) => _states[listKey] = state;
}

/// <summary>
/// One JSON file per list, in the tenant's own folder under the OS app-data folder. A file that
/// can't be read (older format, hand-edited) is ignored rather than breaking the list.
/// </summary>
public sealed class JsonListStateStore(string folder) : IListStateStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    string PathFor(string listKey) => Path.Combine(folder, $"list-{listKey}.json");

    public ListState? Load(string listKey)
    {
        try
        {
            var path = PathFor(listKey);
            return File.Exists(path) ? JsonSerializer.Deserialize<ListState>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(string listKey, ListState state)
    {
        System.IO.Directory.CreateDirectory(folder);
        var path = PathFor(listKey);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, path, overwrite: true);
    }
}
