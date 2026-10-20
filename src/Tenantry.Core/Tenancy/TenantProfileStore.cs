using System.Text.Json;

namespace Tenantry.Core.Tenancy;

/// <summary>
/// Reads and writes the tenant profile list as JSON. Writes go to a temporary file that then
/// replaces the original, so a crash mid-write can't leave a truncated list behind.
/// </summary>
public sealed class TenantProfileStore(string path)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TenantProfileStore() : this(AppPaths.TenantsFile) { }

    public IReadOnlyList<TenantProfile> Load()
    {
        if (!File.Exists(path))
            return [];
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<TenantProfile>>(stream, Json) ?? [];
    }

    public void Save(IEnumerable<TenantProfile> profiles)
    {
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, profiles.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(), Json);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Adds the profile, or replaces the one with the same ID.</summary>
    public void Upsert(TenantProfile profile) =>
        Save(Load().Where(p => p.Id != profile.Id).Append(profile));

    public void Remove(string profileId) =>
        Save(Load().Where(p => p.Id != profileId));
}
