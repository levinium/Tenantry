using System.Text.Json.Nodes;
using System.Xml;

namespace Tenantry.Core.Directory;

/// <summary>Someone who approves activations of a role.</summary>
public sealed record RoleApprover(string Id, string Name);

/// <summary>
/// A role's PIM settings, as the Entra admin center's Role settings page shows them, read from the
/// role's management policy. <see cref="Rules"/> keeps each rule's JSON as Graph sent it, so a change
/// rewrites only the fields it means to (<see cref="RolePolicyRules"/>).
/// </summary>
/// <param name="MaxActivation">The longest an activation lasts.</param>
/// <param name="EligibleMax">How long an eligible assignment can last; null allows permanent ones.</param>
/// <param name="ActiveMax">How long an active assignment can last; null allows permanent ones.</param>
public sealed record RoleSettings(
    string PolicyId,
    TimeSpan MaxActivation,
    bool ActivationMfa,
    bool ActivationJustification,
    bool ActivationTicket,
    bool ApprovalRequired,
    IReadOnlyList<RoleApprover> Approvers,
    TimeSpan? EligibleMax,
    TimeSpan? ActiveMax,
    bool ActiveMfa,
    bool ActiveJustification,
    IReadOnlyDictionary<string, string> Rules);

/// <summary>A request waiting for someone's approval: who wants which role, for how long, and why.</summary>
/// <param name="ApprovalId">The approval to answer (its steps).</param>
public sealed record RoleRequest(string Id, string ApprovalId, string RoleId, string RoleName, string PrincipalId, string PrincipalName, string? PrincipalUpn,
    string? Justification, DateTimeOffset? Requested, TimeSpan? Duration, string ScopeName);

/// <summary>
/// The rule IDs Entra uses in every directory role's PIM policy, and how Tenantry rewrites them.
/// "EndUser" rules are about activating an eligible role; "Admin" rules about assignments an admin makes.
/// </summary>
public static class RolePolicyRules
{
    public const string ActivationExpiry = "Expiration_EndUser_Assignment";
    public const string ActivationRequirements = "Enablement_EndUser_Assignment";
    public const string Approval = "Approval_EndUser_Assignment";
    public const string EligibleExpiry = "Expiration_Admin_Eligibility";
    public const string ActiveExpiry = "Expiration_Admin_Assignment";
    public const string ActiveRequirements = "Enablement_Admin_Assignment";

    public const string Mfa = "MultiFactorAuthentication";
    public const string Justification = "Justification";
    public const string Ticket = "Ticketing";

    public static TimeSpan Duration(string? iso) => string.IsNullOrEmpty(iso) ? TimeSpan.Zero : XmlConvert.ToTimeSpan(iso);

    public static string Iso(TimeSpan span) => XmlConvert.ToString(span);

    static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>An expiration rule: required (with a maximum) or not.</summary>
    public static string WithExpiration(string rule, TimeSpan? maximum)
    {
        var r = Parse(rule);
        r["isExpirationRequired"] = maximum is not null;
        if (maximum is { } max)
            r["maximumDuration"] = Iso(max);
        return r.ToJsonString();
    }

    /// <summary>An enablement rule's requirements (MFA, justification, ticket).</summary>
    public static string WithRequirements(string rule, IEnumerable<string> required)
    {
        var r = Parse(rule);
        r["enabledRules"] = new JsonArray([.. required.Select(x => (JsonNode)x)]);
        return r.ToJsonString();
    }

    /// <summary>The approval rule: required or not, with one stage of named approvers.</summary>
    public static string WithApproval(string rule, bool required, IReadOnlyList<RoleApprover> approvers)
    {
        var r = Parse(rule);
        var setting = r["setting"] as JsonObject ?? [];
        setting["isApprovalRequired"] = required;
        setting["approvalMode"] = "SingleStage";
        var stage = (setting["approvalStages"] as JsonArray)?.FirstOrDefault()?.DeepClone().AsObject()
                    ?? new JsonObject { ["approvalStageTimeOutInDays"] = 1, ["isApproverJustificationRequired"] = true, ["escalationTimeInMinutes"] = 0, ["isEscalationEnabled"] = false };
        stage["primaryApprovers"] = new JsonArray([.. approvers.Select(a => (JsonNode)new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.singleUser",
            ["userId"] = a.Id,
            ["description"] = a.Name,
        })]);
        stage["escalationApprovers"] ??= new JsonArray();
        setting["approvalStages"] = new JsonArray(stage);
        r["setting"] = setting;
        return r.ToJsonString();
    }

    /// <summary>Reads a policy's rules (Graph's JSON, keyed by rule ID) into settings.</summary>
    public static RoleSettings Read(string policyId, IReadOnlyDictionary<string, string> rules)
    {
        JsonObject? Rule(string id) => rules.TryGetValue(id, out var json) ? Parse(json) : null;
        static bool Required(JsonObject? r) => r?["isExpirationRequired"]?.GetValue<bool>() ?? false;
        static TimeSpan Max(JsonObject? r) => Duration(r?["maximumDuration"]?.GetValue<string>());
        static string[] Enabled(JsonObject? r) => r?["enabledRules"] is JsonArray a ? [.. a.Select(x => x?.GetValue<string>() ?? "")] : [];
        var activation = Enabled(Rule(ActivationRequirements));
        var active = Enabled(Rule(ActiveRequirements));
        var approval = Rule(Approval)?["setting"] as JsonObject;
        var approvers = (approval?["approvalStages"] as JsonArray)?.FirstOrDefault()?["primaryApprovers"] as JsonArray;
        var eligible = Rule(EligibleExpiry);
        var activeExpiry = Rule(ActiveExpiry);
        return new RoleSettings(policyId,
            Max(Rule(ActivationExpiry)),
            activation.Contains(Mfa), activation.Contains(Justification), activation.Contains(Ticket),
            approval?["isApprovalRequired"]?.GetValue<bool>() ?? false,
            [.. (approvers ?? []).OfType<JsonObject>().Select(a => new RoleApprover(a["userId"]?.GetValue<string>() ?? a["groupId"]?.GetValue<string>() ?? "",
                a["description"]?.GetValue<string>() ?? "Someone"))],
            Required(eligible) ? Max(eligible) : null,
            Required(activeExpiry) ? Max(activeExpiry) : null,
            active.Contains(Mfa), active.Contains(Justification),
            rules);
    }

    /// <summary>"8 hours", "1 day", "1 year", for a duration.</summary>
    public static string Words(TimeSpan span) => span.TotalDays >= 365 && span.TotalDays % 365 == 0 ? Plural((int)(span.TotalDays / 365), "year")
        : span.TotalDays >= 1 && span.TotalHours % 24 == 0 ? Plural((int)span.TotalDays, "day")
        : span.TotalHours >= 1 ? Plural((int)Math.Round(span.TotalHours), "hour")
        : Plural((int)span.TotalMinutes, "minute");

    static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
