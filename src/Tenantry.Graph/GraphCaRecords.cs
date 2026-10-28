using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Conditional Access (8b), read live. Policies are kept as Graph's JSON; the IDs in them (people,
/// groups, roles, apps, named locations, authentication strengths) are named in a few batched reads.
/// What If uses the beta <c>identity/conditionalAccess/evaluate</c> action (its request is
/// signInIdentity / signInContext / signInConditions; the older field names answer 400, seen live).
/// </summary>
public sealed partial class GraphUserRecords : IConditionalAccessRecords
{
    const string Ca = "identity/conditionalAccess/";

    public async Task<CaDirectory> GetPoliciesAsync(CancellationToken cancellationToken = default)
    {
        var policies = new List<CaPolicy>();
        await foreach (var p in graph.GetAllAsync($"{Ca}policies", cancellationToken))
            policies.Add(new CaPolicy(Text(p, "id") ?? "", Text(p, "displayName") ?? "A policy", Text(p, "state") ?? "disabled",
                When(p, "createdDateTime"), When(p, "modifiedDateTime"), p.GetRawText()));

        var locations = new List<NamedLocation>();
        await foreach (var l in graph.GetAllAsync($"{Ca}namedLocations", cancellationToken))
        {
            var ip = Text(l, "@odata.type") == "#microsoft.graph.ipNamedLocation";
            var places = ip
                ? l.TryGetProperty("ipRanges", out var r) ? r.EnumerateArray().Select(x => Text(x, "cidrAddress")).OfType<string>().ToArray() : []
                : l.TryGetProperty("countriesAndRegions", out var c) ? c.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray() : [];
            locations.Add(new NamedLocation(Text(l, "id") ?? "", Text(l, "displayName") ?? "A location",
                l.TryGetProperty("isTrusted", out var t) && t.ValueKind == JsonValueKind.True, ip ? NamedLocation.IpRanges : NamedLocation.Countries, places,
                l.TryGetProperty("includeUnknownCountriesAndRegions", out var u) && u.ValueKind == JsonValueKind.True,
                Text(l, "countryLookupMethod") == "authenticatorAppGps"));
        }

        var strengths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var strengthList = new List<AuthStrength>();
        try
        {
            await foreach (var s in graph.GetAllAsync($"{Ca}authenticationStrength/policies", cancellationToken))
            {
                strengths[Text(s, "id") ?? ""] = Text(s, "displayName") ?? "An authentication strength";
                strengthList.Add(new AuthStrength(Text(s, "id") ?? "", Text(s, "displayName") ?? "An authentication strength", Text(s, "description"),
                    Text(s, "policyType") == "builtIn",
                    s.TryGetProperty("allowedCombinations", out var c) ? [.. c.EnumerateArray().Select(x => x.GetString()).OfType<string>()] : []));
            }
        }
        catch (GraphException)
        {
        }

        // The IDs the policies hold.
        var nodes = policies.Select(p => p.Node).ToArray();
        IEnumerable<string> Ids(string section, params string[] keys) => nodes.SelectMany(n => keys.SelectMany(k =>
            n["conditions"]?[section]?[k] is JsonArray a ? a.Select(x => x?.GetValue<string>()).OfType<string>() : []));
        static bool IsId(string s) => Guid.TryParse(s, out _);
        var directoryIds = Ids("users", "includeUsers", "excludeUsers", "includeGroups", "excludeGroups").Where(IsId).Distinct().ToArray();
        var people = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in directoryIds.Chunk(1000))
        {
            var answer = await graph.PostAsync("directoryObjects/getByIds",
                new JsonObject { ["ids"] = new JsonArray([.. chunk.Select(i => (JsonNode)i)]), ["types"] = new JsonArray("user", "group") }, cancellationToken);
            foreach (var o in answer.GetProperty("value").EnumerateArray())
                (Text(o, "@odata.type") == "#microsoft.graph.group" ? groups : people)[Text(o, "id") ?? ""] = Text(o, "displayName") ?? "Someone";
        }

        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Ids("users", "includeRoles", "excludeRoles").Any())
            await foreach (var r in graph.GetAllAsync("roleManagement/directory/roleDefinitions?$select=templateId,displayName", cancellationToken))
                if (Text(r, "templateId") is { } template)
                    roles[template] = Text(r, "displayName") ?? "A role";

        var apps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in Ids("applications", "includeApplications", "excludeApplications").Where(IsId).Distinct().Chunk(15))
            await foreach (var sp in graph.GetAllAsync($"servicePrincipals?$filter=appId in ({string.Join(',', chunk.Select(a => $"'{a}'"))})&$select=appId,displayName",
                               cancellationToken))
                apps[Text(sp, "appId") ?? ""] = Text(sp, "displayName") ?? "An app";

        return new CaDirectory([.. policies.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)],
            new CaNames(people, groups, roles, apps, locations.ToDictionary(l => l.Id, l => l.Name, StringComparer.OrdinalIgnoreCase), strengths),
            [.. locations.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)],
            [.. strengthList.OrderBy(s => s.IsBuiltIn ? 0 : 1).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)]);
    }

    public async Task<IReadOnlyList<WhatIfResult>> WhatIfAsync(WhatIfInput input, CancellationToken cancellationToken = default)
    {
        var conditions = new JsonObject
        {
            ["clientAppType"] = input.ClientApp,
            ["signInRiskLevel"] = input.SignInRisk,
            ["userRiskLevel"] = input.UserRisk,
        };
        if (input.Platform != "any")
            conditions["devicePlatform"] = input.Platform;
        if (input.Country is { Length: > 0 } country)
            conditions["country"] = country;
        if (input.IpAddress is { Length: > 0 } ip)
            conditions["ipAddress"] = ip;
        var answer = await graph.PostAsync("https://graph.microsoft.com/beta/identity/conditionalAccess/evaluate", new JsonObject
        {
            ["signInIdentity"] = new JsonObject { ["@odata.type"] = "#microsoft.graph.userSignIn", ["userId"] = input.UserId },
            ["signInContext"] = new JsonObject { ["@odata.type"] = "#microsoft.graph.applicationContext", ["includeApplications"] = new JsonArray(input.AppId) },
            ["signInConditions"] = conditions,
            ["appliedPoliciesOnly"] = false,
        }, cancellationToken);
        return
        [
            .. (answer.TryGetProperty("value", out var v) ? v.EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(r => new WhatIfResult(
                Text(r, "id") ?? "", Text(r, "displayName") ?? "A policy", Text(r, "state") ?? "disabled",
                r.TryGetProperty("policyApplies", out var a) && a.ValueKind == JsonValueKind.True,
                (Text(r, "analysisReasons") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => x != "notSet").Select(CaText.Reason).ToArray(),
                CaText.Requires(JsonNode.Parse(r.GetRawText()), CaNames.Empty)))
                .OrderByDescending(x => x.Applies).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
        ];
    }
}
