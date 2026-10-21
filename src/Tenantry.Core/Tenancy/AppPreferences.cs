using System.Text.Json;

namespace Tenantry.Core.Tenancy;

/// <summary>
/// Choices about the app itself, the same in every tenant and remembered between launches.
/// (Per-list choices such as filters and columns live with each tenant's list state.)
/// </summary>
/// <param name="PreviewOpen">Whether lists show the preview pane.</param>
public sealed record AppPreferences(bool PreviewOpen = false);

public interface IAppPreferencesStore
{
    AppPreferences Load();
    void Save(AppPreferences preferences);
}

/// <summary>For tests and the snapshot tool: remembered for the session only.</summary>
public sealed class MemoryAppPreferencesStore : IAppPreferencesStore
{
    AppPreferences _preferences = new();

    public AppPreferences Load() => _preferences;

    public void Save(AppPreferences preferences) => _preferences = preferences;
}

/// <summary>
/// preferences.json in the app-data folder. A file that can't be read (hand-edited, from a
/// newer version) gives the defaults rather than stopping the app.
/// </summary>
public sealed class JsonAppPreferencesStore(string path) : IAppPreferencesStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonAppPreferencesStore() : this(Path.Combine(AppPaths.Root, "preferences.json")) { }

    public AppPreferences Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(AppPreferences preferences)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(preferences, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A preference that can't be saved just isn't remembered next time.
        }
    }
}
