using System.Text.Json.Nodes;

namespace Tenantry.Core.Directory;

/// <summary>One of Microsoft's Conditional Access templates: a starting point for a new policy.</summary>
/// <param name="Details">The template's conditions, grant and session controls, as Graph gives them.</param>
public sealed record CaTemplate(string Id, string Name, string Description, IReadOnlyList<string> Scenarios, string Details);

/// <summary>Terms of use the organization has set up, which a policy can require accepting.</summary>
public sealed record TermsOfUse(string Id, string Name);

/// <summary>What the policy editor needs from Entra: starters, terms of use, apps by name, admin roles.</summary>
public interface ICaEditingRecords
{
    Task<IReadOnlyList<CaTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Unavailable without the Agreement.Read.All permission.</summary>
    Task<Available<IReadOnlyList<TermsOfUse>>> GetTermsOfUseAsync(CancellationToken cancellationToken = default);

    /// <summary>Apps (service principals) whose name starts with the text: (app ID, name).</summary>
    Task<IReadOnlyList<(string AppId, string Name)>> SearchAppsAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reading and writing a policy's JSON in the editor: values at a path, and the shape Graph accepts
/// when a policy is created or changed (read-only properties stripped). Everything the editor doesn't
/// touch stays as Entra had it.
/// </summary>
public static class CaJson
{
    /// <summary>The object at a path ("conditions/users"), made if it isn't there.</summary>
    public static JsonObject Object(JsonObject root, string path)
    {
        var node = root;
        foreach (var part in path.Split('/'))
        {
            if (node[part] is not JsonObject child)
                node[part] = child = new JsonObject();
            node = child;
        }
        return node;
    }

    public static IReadOnlyList<string> Strings(JsonObject root, string path)
    {
        var parts = path.Split('/');
        JsonNode? node = root;
        foreach (var part in parts)
            node = node?[part];
        return node is JsonArray a ? [.. a.Select(x => x?.GetValue<string>()).OfType<string>()] : [];
    }

    public static void SetStrings(JsonObject root, string path, IEnumerable<string> values)
    {
        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? root : Object(root, path[..slash]);
        parent[slash < 0 ? path : path[(slash + 1)..]] = new JsonArray([.. values.Distinct().Select(v => (JsonNode)v)]);
    }

    static readonly string[] ReadOnly = ["id", "createdDateTime", "modifiedDateTime", "deletedDateTime", "templateId", "partialEnablementStrategy"];

    /// <summary>The policy as Graph takes it on create or change: no read-only or OData annotations; an authentication strength by ID only.</summary>
    public static JsonObject ForWrite(JsonObject policy)
    {
        var copy = policy.DeepClone().AsObject();
        foreach (var key in ReadOnly)
            copy.Remove(key);
        Clean(copy);
        if (copy["grantControls"]?["authenticationStrength"] is JsonObject strength && strength["id"]?.GetValue<string>() is { } id)
            copy["grantControls"]!["authenticationStrength"] = new JsonObject { ["id"] = id };
        return copy;
    }

    static void Clean(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            foreach (var key in o.Select(p => p.Key).Where(k => k.Contains("@odata", StringComparison.Ordinal)).ToArray())
                o.Remove(key);
            foreach (var (_, child) in o)
                Clean(child);
        }
        else if (node is JsonArray a)
            foreach (var child in a)
                Clean(child);
    }

    /// <summary>
    /// What's wrong with a policy before it can be saved, as Entra would refuse it (or as a lockout
    /// waits to happen), in plain words.
    /// </summary>
    public static IReadOnlyList<string> Problems(JsonObject policy, string? me)
    {
        var problems = new List<string>();
        var include = Strings(policy, "conditions/users/includeUsers");
        var anyone = include.Count > 0 && !include.SequenceEqual(["None"]) || Strings(policy, "conditions/users/includeGroups").Count > 0
                     || Strings(policy, "conditions/users/includeRoles").Count > 0 || policy["conditions"]?["clientApplications"] is JsonObject;
        if (!anyone)
            problems.Add("Choose who it covers.");
        var apps = Strings(policy, "conditions/applications/includeApplications");
        if (apps.Count == 0 && Strings(policy, "conditions/applications/includeUserActions").Count == 0
            && Strings(policy, "conditions/applications/includeAuthenticationContextClassReferences").Count == 0)
            problems.Add("Choose which apps (or user action) it covers.");
        var controls = Strings(policy, "grantControls/builtInControls");
        var strength = policy["grantControls"]?["authenticationStrength"] is JsonObject;
        var terms = Strings(policy, "grantControls/termsOfUse").Count > 0;
        var session = CaText.Session(policy["sessionControls"]) is not null;
        if (controls.Count == 0 && !strength && !terms && !session)
            problems.Add("Choose what it requires (or a session control).");
        if (controls.Contains("block") && (controls.Count > 1 || strength || terms))
            problems.Add("Block access can't be combined with other requirements.");
        if (controls.Contains("mfa") && strength)
            problems.Add("Require MFA and an authentication strength can't both be chosen: the strength already includes MFA.");
        if (controls.Contains("block") && include.Contains("All") && me is not null
            && !Strings(policy, "conditions/users/excludeUsers").Contains(me, StringComparer.OrdinalIgnoreCase)
            && policy["state"]?.GetValue<string>() == "enabled")
            problems.Add("It would block everyone, you included. Leave yourself out, or start it in report-only.");
        return problems;
    }
}
