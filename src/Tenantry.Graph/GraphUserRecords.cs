using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Core.Licensing;

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

    /// <summary>
    /// The user's licenses with where each comes from (licenseAssignmentStates: direct or a
    /// group, and any error), the tenant's licenses with counts from the local copy, and the
    /// enterprise apps they're assigned to.
    /// </summary>
    public async Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default)
    {
        var catalog = Catalog();
        var names = catalog.ToDictionary(s => s.SkuId, s => s.Name, StringComparer.OrdinalIgnoreCase);
        var groupNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var json in store.All(DirectorySync.Groups))
        {
            using var g = JsonDocument.Parse(json);
            if (g.RootElement.TryGetProperty("displayName", out var n) && n.GetString() is { } name)
                groupNames[g.RootElement.GetProperty("id").GetString()!] = name;
        }

        var user = await graph.GetAsync($"users/{userId}?$select=usageLocation,licenseAssignmentStates", cancellationToken);
        var assignments = new List<LicenseAssignment>();
        if (user.TryGetProperty("licenseAssignmentStates", out var states) && states.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in states.EnumerateArray())
            {
                var skuId = s.GetProperty("skuId").GetString()!;
                var group = s.TryGetProperty("assignedByGroup", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
                var error = s.TryGetProperty("error", out var e) && e.GetString() is { } code && code != "None" ? code : null;
                var disabled = s.TryGetProperty("disabledPlans", out var d) ? d.EnumerateArray().Select(p => p.GetString()!).ToArray() : [];
                assignments.Add(new LicenseAssignment(skuId, names.GetValueOrDefault(skuId, LicenseNames.For(skuId, null)),
                    group, group is null ? null : groupNames.GetValueOrDefault(group, "a group"), error, disabled));
            }
        }

        var apps = new List<AppAssignment>();
        await foreach (var a in graph.GetAllAsync($"users/{userId}/appRoleAssignments?$select=id,resourceDisplayName,resourceId,createdDateTime", cancellationToken))
        {
            apps.Add(new AppAssignment(a.GetProperty("id").GetString()!, a.TryGetProperty("resourceDisplayName", out var n) ? n.GetString() ?? "An app" : "An app",
                a.GetProperty("resourceId").GetString()!,
                a.TryGetProperty("createdDateTime", out var c) && c.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(c.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null));
        }

        return new UserLicensing(
            user.TryGetProperty("usageLocation", out var location) && location.ValueKind == JsonValueKind.String ? location.GetString() : null,
            assignments, catalog, apps);
    }

    /// <summary>The tenant's licenses from the local copy (kept current by sync and by license changes).</summary>
    IReadOnlyList<LicenseSku> Catalog() =>
        store.All(DirectorySync.Skus)
            .Select(json => JsonDocument.Parse(json).RootElement)
            .Where(s => !s.TryGetProperty("appliesTo", out var to) || to.GetString() != "Company")
            .Select(s =>
            {
                var id = s.GetProperty("skuId").GetString()!;
                var total = s.TryGetProperty("prepaidUnits", out var p) && p.TryGetProperty("enabled", out var en) ? en.GetInt32() : 0;
                var used = s.TryGetProperty("consumedUnits", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 0;
                var plans = s.TryGetProperty("servicePlans", out var sp) && sp.ValueKind == JsonValueKind.Array
                    ? sp.EnumerateArray()
                        .Where(x => !x.TryGetProperty("appliesTo", out var to) || to.GetString() != "Company")
                        .Select(x => new LicensePlan(x.GetProperty("servicePlanId").GetString()!,
                            LicenseNames.PlanName(x.GetProperty("servicePlanId").GetString()!, x.TryGetProperty("servicePlanName", out var pn) ? pn.GetString() : null)))
                        .OrderBy(x => x.Name, StringComparer.CurrentCulture)
                        .ToArray()
                    : [];
                return new LicenseSku(id, LicenseNames.For(id, s.TryGetProperty("skuPartNumber", out var code) ? code.GetString() : null), total, used, plans);
            })
            .OrderBy(s => s.Name, StringComparer.CurrentCulture)
            .ToArray();

    public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(store.All(DirectorySync.Domains)
            .Select(j => JsonDocument.Parse(j).RootElement)
            .Where(d => !d.TryGetProperty("isVerified", out var v) || v.ValueKind != JsonValueKind.False)
            .Select(d => d.GetProperty("id").GetString()!)
            .OrderBy(d => d.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToArray());
}
