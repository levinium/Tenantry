using System.Text.Json.Nodes;
using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>
/// One Conditional Access policy, kept as Graph's JSON (<see cref="Json"/>) so nothing Tenantry
/// doesn't understand is lost; <see cref="CaText"/> reads it into words.
/// </summary>
/// <param name="State">"enabled", "enabledForReportingButNotEnforced" or "disabled".</param>
public sealed record CaPolicy(string Id, string Name, string State, DateTimeOffset? Created, DateTimeOffset? Modified, string Json)
{
    public JsonObject Node => JsonNode.Parse(Json)!.AsObject();

    public static string StateName(string state) => state switch
    {
        "enabled" => "On",
        "enabledForReportingButNotEnforced" => "Report-Only",
        _ => "Off",
    };
}

/// <summary>Names for the IDs a policy holds: people, groups, roles, apps, locations, authentication strengths.</summary>
public sealed record CaNames(
    IReadOnlyDictionary<string, string> People,
    IReadOnlyDictionary<string, string> Groups,
    IReadOnlyDictionary<string, string> Roles,
    IReadOnlyDictionary<string, string> Apps,
    IReadOnlyDictionary<string, string> Locations,
    IReadOnlyDictionary<string, string> Strengths)
{
    public static readonly CaNames Empty = new(new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>(),
        new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>());
}

/// <summary>A named location (trusted or not): IP ranges or countries.</summary>
public sealed record NamedLocation(string Id, string Name, bool IsTrusted, string Kind, IReadOnlyList<string> Places);

/// <summary>Every policy, with the names its IDs stand for and the named locations.</summary>
public sealed record CaDirectory(IReadOnlyList<CaPolicy> Policies, CaNames Names, IReadOnlyList<NamedLocation> Locations);

/// <summary>A sign-in to test against the policies (What If).</summary>
/// <param name="AppId">The app signed in to ("All" for any cloud app).</param>
/// <param name="Platform">"any", "windows", "macOS", "iOS", "android", "linux".</param>
/// <param name="ClientApp">"browser", "mobileAppsAndDesktopClients", "exchangeActiveSync", "other".</param>
/// <param name="Country">Two-letter country code, or null.</param>
/// <param name="IpAddress">An IP address, or null.</param>
public sealed record WhatIfInput(string UserId, string AppId, string Platform, string ClientApp, string? Country, string? IpAddress, string SignInRisk, string UserRisk);

/// <summary>What one policy would do to that sign-in, and why (or why not).</summary>
public sealed record WhatIfResult(string PolicyId, string Name, string State, bool Applies, IReadOnlyList<string> Reasons, string Requires);

/// <summary>Conditional Access, read live from Entra.</summary>
public interface IConditionalAccessRecords
{
    Task<CaDirectory> GetPoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>Which policies would apply to a sign-in (Entra's What If, beta).</summary>
    Task<IReadOnlyList<WhatIfResult>> WhatIfAsync(WhatIfInput input, CancellationToken cancellationToken = default);
}

/// <summary>Reads a policy's JSON into plain words, line by line, as its page shows it.</summary>
public static class CaText
{
    static IReadOnlyList<string> Strings(JsonNode? node, string name) =>
        node?[name] is JsonArray a ? [.. a.Select(x => x?.GetValue<string>()).OfType<string>()] : [];

    static string Join(IEnumerable<string> items)
    {
        var list = items.ToArray();
        return list.Length switch
        {
            0 => "",
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => string.Join(", ", list[..^1]) + " and " + list[^1],
        };
    }

    static string Name(IReadOnlyDictionary<string, string> names, string id) => names.TryGetValue(id, out var n) ? n : "someone no longer in the directory";

    /// <summary>Who: "Everyone", or the people, groups, roles and guests listed.</summary>
    public static string Who(JsonNode? users, CaNames names, bool exclude)
    {
        var prefix = exclude ? "exclude" : "include";
        var people = Strings(users, $"{prefix}Users");
        var parts = new List<string>();
        if (people.Contains("All"))
            parts.Add("everyone");
        if (people.Contains("GuestsOrExternalUsers") || users?[$"{prefix}GuestsOrExternalUsers"] is JsonObject)
            parts.Add("guests and external users");
        var named = people.Where(p => p is not ("All" or "None" or "GuestsOrExternalUsers")).Select(p => Name(names.People, p)).ToArray();
        if (named.Length > 0)
            parts.Add(named.Length <= 3 ? Join(named) : $"{named.Length} people");
        var groups = Strings(users, $"{prefix}Groups").Select(g => Name(names.Groups, g)).ToArray();
        if (groups.Length > 0)
            parts.Add(groups.Length <= 3 ? "members of " + Join(groups) : $"members of {groups.Length} groups");
        var roles = Strings(users, $"{prefix}Roles").Select(r => Name(names.Roles, r)).ToArray();
        if (roles.Length > 0)
            parts.Add(roles.Length <= 3 ? "holders of " + Join(roles) : $"holders of {roles.Length} admin roles");
        if (parts.Count == 0)
            return exclude ? "" : "nobody";
        var text = Join(parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    static readonly Dictionary<string, string> AppWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["All"] = "every cloud app",
        ["Office365"] = "Office 365",
        ["MicrosoftAdminPortals"] = "Microsoft admin portals",
        ["None"] = "no apps",
    };

    static string Apps(IReadOnlyList<string> ids, CaNames names) =>
        Join(ids.Select(a => AppWords.TryGetValue(a, out var w) ? w : names.Apps.TryGetValue(a, out var n) ? n : "an app no longer in the directory"));

    static readonly Dictionary<string, string> ClientWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["all"] = "any app",
        ["browser"] = "browsers",
        ["mobileAppsAndDesktopClients"] = "mobile and desktop apps",
        ["exchangeActiveSync"] = "Exchange ActiveSync",
        ["other"] = "older (legacy) sign-in",
        ["easSupported"] = "Exchange ActiveSync",
    };

    static readonly Dictionary<string, string> ControlWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["block"] = "Block access",
        ["mfa"] = "Require MFA",
        ["compliantDevice"] = "Require a compliant device",
        ["domainJoinedDevice"] = "Require a hybrid-joined device",
        ["approvedApplication"] = "Require an approved app",
        ["compliantApplication"] = "Require app protection",
        ["passwordChange"] = "Require a password change",
    };

    /// <summary>What it asks for, in a line: "Require MFA or Require a compliant device".</summary>
    public static string Requires(JsonNode? policy, CaNames names)
    {
        var grant = policy?["grantControls"];
        var controls = Strings(grant, "builtInControls").Select(c => ControlWords.TryGetValue(c, out var w) ? w : c).ToList();
        if (grant?["authenticationStrength"] is JsonObject strength)
            controls.Add($"Require {strength["displayName"]?.GetValue<string>() ?? (strength["id"]?.GetValue<string>() is { } id && names.Strengths.TryGetValue(id, out var s) ? s : "an authentication strength")}");
        if (Strings(grant, "termsOfUse").Count is > 0 and var terms)
            controls.Add(terms == 1 ? "Require accepting the terms of use" : $"Require accepting {terms} terms of use");
        var grantText = controls.Count == 0 ? null
            : string.Join(grant?["operator"]?.GetValue<string>() == "AND" ? " and " : " or ", controls);
        var session = Session(policy?["sessionControls"]);
        return string.Join(" · ", new[] { grantText, session }.OfType<string>()) is { Length: > 0 } all ? all : "Nothing (no controls)";
    }

    /// <summary>Session controls in words, or null for none.</summary>
    public static string? Session(JsonNode? session)
    {
        if (session is not JsonObject s)
            return null;
        var parts = new List<string>();
        if (s["signInFrequency"] is JsonObject f && f["isEnabled"]?.GetValue<bool>() == true)
            parts.Add(f["frequencyInterval"]?.GetValue<string>() == "everyTime" ? "Sign in again every time"
                : $"Sign in again every {f["value"]?.GetValue<int>()} {f["type"]?.GetValue<string>()}");
        if (s["persistentBrowser"] is JsonObject b && b["isEnabled"]?.GetValue<bool>() == true)
            parts.Add(b["mode"]?.GetValue<string>() == "never" ? "Browser never stays signed in" : "Browser always stays signed in");
        if (s["applicationEnforcedRestrictions"] is JsonObject r && r["isEnabled"]?.GetValue<bool>() == true)
            parts.Add("App-enforced restrictions");
        if (s["cloudAppSecurity"] is JsonObject c && c["isEnabled"]?.GetValue<bool>() == true)
            parts.Add("Defender for Cloud Apps session control");
        if (s["disableResilienceDefaults"]?.GetValue<bool>() == true)
            parts.Add("No resilience defaults");
        if (s["continuousAccessEvaluation"] is JsonObject e && e["mode"]?.GetValue<string>() is { } mode)
            parts.Add(mode == "disabled" ? "Continuous access evaluation off" : "Strict continuous access evaluation");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Every condition and control as labelled lines, in the order the portal reads them.</summary>
    public static IReadOnlyList<(string Label, string Text)> Lines(JsonObject policy, CaNames names)
    {
        var lines = new List<(string, string)>();
        void Add(string label, string? text)
        {
            if (!string.IsNullOrEmpty(text))
                lines.Add((label, char.ToUpperInvariant(text[0]) + text[1..]));
        }
        var c = policy["conditions"];
        var users = c?["users"];
        Add("Who", Who(users, names, exclude: false));
        Add("Except", Who(users, names, exclude: true));
        if (c?["clientApplications"] is JsonObject workload)
            Add("Workload Identities", Strings(workload, "includeServicePrincipals").Contains("All") ? "Every app identity in the organization"
                : $"{Strings(workload, "includeServicePrincipals").Count} app identities");
        var apps = c?["applications"];
        Add("Apps", Strings(apps, "includeApplications") is { Count: > 0 } include ? Apps(include, names) : null);
        Add("Except Apps", Apps(Strings(apps, "excludeApplications"), names));
        Add("User Actions", Join(Strings(apps, "includeUserActions").Select(a => a switch
        {
            "urn:user:registersecurityinfo" => "registering security info",
            "urn:user:registerdevice" => "registering or joining devices",
            _ => a,
        })));
        Add("Authentication Context", Strings(apps, "includeAuthenticationContextClassReferences") is { Count: > 0 } ctx ? Join(ctx) : null);
        Add("App Filter", apps?["applicationFilter"]?["rule"]?.GetValue<string>() is { } appRule ? $"{apps!["applicationFilter"]!["mode"]?.GetValue<string>()}: {appRule}" : null);
        Add("Platforms", Strings(c?["platforms"], "includePlatforms") is { Count: > 0 } platforms
            ? (platforms.Contains("all") ? "any device platform" : Join(platforms)) + (Strings(c!["platforms"], "excludePlatforms") is { Count: > 0 } notP ? ", except " + Join(notP) : "")
            : null);
        Add("Locations", Strings(c?["locations"], "includeLocations") is { Count: > 0 } places
            ? Join(places.Select(Place)) + (Strings(c!["locations"], "excludeLocations") is { Count: > 0 } notL ? ", except " + Join(notL.Select(Place)) : "")
            : null);
        Add("Client Apps", Join(Strings(c, "clientAppTypes").Select(t => ClientWords.TryGetValue(t, out var w) ? w : t)));
        Add("Sign-In Risk", Join(Strings(c, "signInRiskLevels")));
        Add("User Risk", Join(Strings(c, "userRiskLevels")));
        Add("App Identity Risk", Join(Strings(c, "servicePrincipalRiskLevels")));
        Add("Insider Risk", c?["insiderRiskLevels"]?.GetValue<string>());
        Add("Device Filter", c?["devices"]?["deviceFilter"]?["rule"]?.GetValue<string>() is { } rule ? $"{c!["devices"]!["deviceFilter"]!["mode"]?.GetValue<string>()}: {rule}" : null);
        // transferMethods is one comma-separated value ("deviceCodeFlow,authenticationTransfer").
        Add("Authentication Flows", c?["authenticationFlows"]?["transferMethods"]?.GetValue<string>() is { Length: > 0 } flows
            ? Join(flows.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(f => f switch
            {
                "deviceCodeFlow" => "device code sign-in",
                "authenticationTransfer" => "authentication transfer",
                _ => f,
            }))
            : null);
        Add("Then", Requires(policy, names));
        return lines;

        string Place(string id) => id switch
        {
            "All" => "any location",
            "AllTrusted" => "trusted locations",
            _ => names.Locations.TryGetValue(id, out var n) ? n : "a location no longer listed",
        };
    }

    /// <summary>What Entra's What If gives as the reason a policy doesn't apply, in words.</summary>
    public static string Reason(string flag) => flag switch
    {
        "users" => "it doesn't target this person",
        "application" => "it doesn't cover this app",
        "clientApps" => "it doesn't cover this kind of app",
        "devicePlatform" => "it doesn't cover this device platform",
        "location" => "it doesn't cover this location",
        "signInRisk" => "it needs a different sign-in risk",
        "userRisk" => "it needs a different user risk",
        "devices" => "the device filter doesn't match",
        "policyNotEnabled" => "it's off",
        "userActions" => "it covers a user action, not a sign-in",
        "authenticationContext" => "it covers an authentication context",
        "authenticationFlow" => "it covers an authentication flow",
        "time" => "it doesn't cover this time",
        "insiderRisk" => "it needs a different insider risk",
        "workloadIdentities" => "it covers app identities, not people",
        "emptyPolicy" => "it has no conditions",
        "invalidPolicy" or "invalidCondition" => "it isn't valid",
        "notEnoughInformation" => "there isn't enough information to tell",
        _ => flag,
    };
}

/// <summary>The Conditional Access list's fields.</summary>
public static class CaFields
{
    public const string Name = "name";
    public const string State = "state";
    public const string Who = "who";
    public const string Requires = "requires";
    public const string Modified = "modified";
    public const string Created = "created";

    public static IReadOnlyList<Field<CaPolicyRow>> All { get; } =
    [
        new() { Key = Name, Label = "Policy", Kind = FieldKind.Text, Searchable = true, Aliases = ["policy"], Text = p => p.Policy.Name },
        new() { Key = State, Label = "State", Kind = FieldKind.Choice, ChoiceOrder = ["On", "Report-Only", "Off"], Text = p => CaPolicy.StateName(p.Policy.State) },
        new() { Key = Who, Label = "Who", Kind = FieldKind.Text, Searchable = true, Aliases = ["users"], Text = p => p.Who },
        new() { Key = Requires, Label = "Then", Kind = FieldKind.Text, Searchable = true, Aliases = ["control", "grant"], Text = p => p.Requires },
        new() { Key = Modified, Label = "Changed", Kind = FieldKind.Date, Date = p => p.Policy.Modified ?? p.Policy.Created },
        new() { Key = Created, Label = "Created", Kind = FieldKind.Date, Date = p => p.Policy.Created },
    ];
}

/// <summary>A policy as the list shows it, with its words worked out once.</summary>
public sealed record CaPolicyRow(CaPolicy Policy, string Who, string Except, string Apps, string Requires, IReadOnlyList<(string Label, string Text)> Lines)
{
    public static CaPolicyRow Of(CaPolicy policy, CaNames names)
    {
        var node = policy.Node;
        var lines = CaText.Lines(node, names);
        string Line(string label) => lines.FirstOrDefault(l => l.Label == label).Text ?? "";
        return new CaPolicyRow(policy, Line("Who"), Line("Except"), Line("Apps"), Line("Then"), lines);
    }
}
