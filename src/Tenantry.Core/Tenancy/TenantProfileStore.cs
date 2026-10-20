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

    /// <summary>
    /// Records a tenant that setup just connected: updates the existing profile for that
    /// tenant (keeping its ID, and so its token cache) or creates one.
    /// </summary>
    public TenantProfile SaveSetup(string tenantId, string tenantName, string primaryDomain, string clientId, string? account, string? newProfileId = null)
    {
        var existing = Load().FirstOrDefault(p => string.Equals(p.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));
        var profile = existing is not null
            ? existing with { ClientId = clientId, LastAccount = account ?? existing.LastAccount, PrimaryDomain = primaryDomain }
            : new TenantProfile(newProfileId ?? NewId(), tenantName, tenantId, primaryDomain, clientId, DateTimeOffset.UtcNow, account);
        Upsert(profile);
        return profile;
    }

    /// <summary>Makes one profile the default (opened at startup), or none with null.</summary>
    public void SetDefault(string? profileId) =>
        Save(Load().Select(p => p with { IsDefault = profileId is not null && p.Id == profileId }));

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public void Remove(string profileId) =>
        Save(Load().Where(p => p.Id != profileId));
}
