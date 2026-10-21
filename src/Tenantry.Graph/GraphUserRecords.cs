using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>Live record-page reads from Graph.</summary>
public sealed class GraphUserRecords(GraphClient graph, IEntityStore store) : IUserRecords
{
    public async Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var select = string.Join(',', UserDetails.Properties.Append("externalUserState").Append("externalUserStateChangeDateTime"));
        var user = await graph.GetAsync($"users/{userId}?$select={select}&$expand=manager($select=id,displayName)", cancellationToken);
        var text = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in UserDetails.Properties)
        {
            if (!user.TryGetProperty(property, out var value))
                continue;
            text[property] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                // businessPhones is a list; the editor works on its first entry.
                JsonValueKind.Array => value.EnumerateArray().Select(v => v.GetString()).FirstOrDefault(s => !string.IsNullOrEmpty(s)),
                _ => null,
            };
        }
        var manager = user.TryGetProperty("manager", out var m) && m.ValueKind == JsonValueKind.Object ? m : (JsonElement?)null;
        return new UserDetails(
            userId,
            text,
            manager?.GetProperty("id").GetString(),
            manager is { } mm && mm.TryGetProperty("displayName", out var n) ? n.GetString() : null,
            user.TryGetProperty("externalUserState", out var state) ? state.GetString() : null,
            user.TryGetProperty("externalUserStateChangeDateTime", out var changed) && changed.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(changed.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                : null);
    }

    public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(store.All(DirectorySync.Domains)
            .Select(j => JsonDocument.Parse(j).RootElement)
            .Where(d => !d.TryGetProperty("isVerified", out var v) || v.ValueKind != JsonValueKind.False)
            .Select(d => d.GetProperty("id").GetString()!)
            .OrderBy(d => d.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToArray());
}
