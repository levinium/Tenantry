using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's threat policies (12f): each kind's default (with a few settings looser than
/// Microsoft's Standard, to show the recommendations), a custom anti-spam and anti-phishing policy,
/// a Safe Links policy and the built-in protection, as editable rows so changes play out.
/// </summary>
public sealed partial class DemoUserRecords : IThreatPolicyRecords
{
    sealed record DemoPolicy(ThreatPolicyKind Kind, JsonObject Policy, JsonObject? Rule);

    List<DemoPolicy>? _threat;

    List<DemoPolicy> Threat() => _threat ??= Seed();

    static List<DemoPolicy> Seed()
    {
        JsonObject Defaults(ThreatPolicyKind kind, string name, bool isDefault)
        {
            var row = new JsonObject { ["Name"] = name, ["IsDefault"] = isDefault };
            foreach (var s in ThreatPolicyCatalog.Settings(kind, ["AdminOnlyAccessPolicy", "DefaultFullAccessPolicy", "DefaultFullAccessWithNotificationPolicy"]))
                row[s.Parameter] = s.Kind switch
                {
                    SettingKind.List => new JsonArray(),
                    _ => s.Recommended?.DeepClone() is { } rec
                        ? s.Inverted && rec is JsonValue v && v.TryGetValue<bool>(out var b) ? JsonValue.Create(!b) : rec
                        : s.Kind == SettingKind.OnOff ? JsonValue.Create(false) : s.Kind == SettingKind.Number ? JsonValue.Create(s.Min) : JsonValue.Create(s.Choices?.FirstOrDefault().Value),
                };
            return row;
        }
        JsonObject Rule(string name, string policyNoun, int priority, string state, params string[] domains) =>
            new() { ["Name"] = name, [policyNoun] = name, ["Priority"] = priority, ["State"] = state, ["SentTo"] = new JsonArray(), ["SentToMemberOf"] = new JsonArray(),
                ["RecipientDomainIs"] = new JsonArray([.. domains.Select(d => (JsonNode?)JsonValue.Create(d))]) };

        var spamDefault = Defaults(ThreatPolicyCatalog.AntiSpam, "Default", true);
        spamDefault["BulkThreshold"] = 7;
        spamDefault["PhishSpamAction"] = "MoveToJmf";
        spamDefault["QuarantineRetentionPeriod"] = 15;
        var finance = Defaults(ThreatPolicyCatalog.AntiSpam, "Finance team", false);
        finance["AllowedSenderDomains"] = new JsonArray("invoices.example");
        var testSpam = Defaults(ThreatPolicyCatalog.AntiSpam, "zz-tenantry-test-spam", false);
        var phishDefault = Defaults(ThreatPolicyCatalog.AntiPhish, "Office365 AntiPhish Default", true);
        phishDefault["DmarcRejectAction"] = "Quarantine";
        var outbound = Defaults(ThreatPolicyCatalog.Outbound, "Default", true);
        outbound["ActionWhenThresholdReached"] = "BlockUserForToday";
        outbound["RecipientLimitExternalPerHour"] = 0;
        return
        [
            new(ThreatPolicyCatalog.AntiSpam, finance, Rule("Finance team", "HostedContentFilterPolicy", 0, "Enabled", "northgate.example")),
            new(ThreatPolicyCatalog.AntiSpam, testSpam, Rule("zz-tenantry-test-spam", "HostedContentFilterPolicy", 1, "Disabled")),
            new(ThreatPolicyCatalog.AntiSpam, spamDefault, null),
            new(ThreatPolicyCatalog.Outbound, outbound, null),
            new(ThreatPolicyCatalog.AntiPhish, Defaults(ThreatPolicyCatalog.AntiPhish, "Leaders", false), Rule("Leaders", "AntiPhishPolicy", 0, "Enabled", "northgate.example")),
            new(ThreatPolicyCatalog.AntiPhish, phishDefault, null),
            new(ThreatPolicyCatalog.AntiMalware, Defaults(ThreatPolicyCatalog.AntiMalware, "Default", true), null),
            new(ThreatPolicyCatalog.SafeLinks, Defaults(ThreatPolicyCatalog.SafeLinks, "Everyone", false), Rule("Everyone", "SafeLinksPolicy", 0, "Enabled", "northgate.example")),
            new(ThreatPolicyCatalog.SafeLinks, Defaults(ThreatPolicyCatalog.SafeLinks, "Built-In Protection Policy", false), null),
            new(ThreatPolicyCatalog.SafeAttachments, Defaults(ThreatPolicyCatalog.SafeAttachments, "Built-In Protection Policy", false), null),
        ];
    }

    public Task<IReadOnlyList<ThreatPolicy>?> GetThreatPoliciesAsync(ThreatPolicyKind kind, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ThreatPolicy>?>([.. Threat().Where(t => t.Kind.Key == kind.Key).Select(t => new ThreatPolicy(t.Kind, t.Policy["Name"]!.GetValue<string>(),
                t.Policy["IsDefault"]!.GetValue<bool>(), t.Policy["Name"]!.GetValue<string>().StartsWith("Built-In", StringComparison.Ordinal),
                JsonSerializer.SerializeToElement(t.Policy), t.Rule is null ? null : JsonSerializer.SerializeToElement(t.Rule)))
            .OrderBy(t => t.IsDefault || t.ReadOnly).ThenBy(t => t.Priority ?? int.MaxValue)]);

    List<QuarantinePolicy>? _quarantinePolicies;

    List<QuarantinePolicy> QuarantinePolicyList() => _quarantinePolicies ??=
    [
        new("AdminOnlyAccessPolicy", true, QuarantinePermissions.All.ToDictionary(p => p.Key, _ => false), false),
        new("DefaultFullAccessPolicy", true, QuarantinePermissions.All.ToDictionary(p => p.Key, p => p.Key is "PermissionToRelease" or "PermissionToPreview" or "PermissionToDelete" or "PermissionToDownload"), false),
        new("DefaultFullAccessWithNotificationPolicy", true, QuarantinePermissions.All.ToDictionary(p => p.Key, p => p.Key is "PermissionToRelease" or "PermissionToPreview" or "PermissionToDelete" or "PermissionToDownload"), true),
        new("Ask before release", false, QuarantinePermissions.All.ToDictionary(p => p.Key, p => p.Key is "PermissionToRequestRelease" or "PermissionToPreview" or "PermissionToDelete"), true),
    ];

    public Task<IReadOnlyList<QuarantinePolicy>> GetQuarantinePoliciesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<QuarantinePolicy>>([.. QuarantinePolicyList()]);

    JsonObject _quarantineGlobal = new() { ["Name"] = "DefaultGlobalTag", ["EndUserSpamNotificationFrequency"] = "P1D", ["OrganizationBrandingEnabled"] = false };

    public Task<JsonElement> GetQuarantineGlobalAsync(CancellationToken cancellationToken = default) => Task.FromResult(JsonSerializer.SerializeToElement(_quarantineGlobal));

    JsonObject _connection = new() { ["IPAllowList"] = new JsonArray("198.51.100.20"), ["IPBlockList"] = new JsonArray(), ["EnableSafeList"] = false };

    public Task<JsonElement> GetConnectionFilterAsync(CancellationToken cancellationToken = default) => Task.FromResult(JsonSerializer.SerializeToElement(_connection));

    /// <summary>Applies a threat policy change to the demo; null when it isn't one.</summary>
    public ApplyResult? ApplyThreatPolicy(ChangeOp change)
    {
        DemoPolicy? Find(string noun, string? name) => Threat().FirstOrDefault(t => (t.Kind.PolicyNoun == noun || t.Kind.RuleNoun == noun)
            && string.Equals(t.Policy["Name"]!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
        switch (change)
        {
            case SetExchangeSetting { SetCmdlet: "Set-HostedConnectionFilterPolicy" } s:
                _connection[s.Parameter] = s.ExchangeValue?.DeepClone();
                return new ApplyResult(ChangeOutcome.Applied);
            case SetExchangeSetting { SetCmdlet: "Set-QuarantinePolicy" } s:
                _quarantineGlobal[s.Parameter] = s.ExchangeValue?.DeepClone();
                return new ApplyResult(ChangeOutcome.Applied);
            case SetExchangeSetting s when s.SetCmdlet.StartsWith("Set-", StringComparison.Ordinal) && Find(s.SetCmdlet[4..], s.Identity) is { } t:
                var target = s.SetCmdlet[4..] == t.Kind.RuleNoun ? t.Rule : t.Policy;
                if (target is null)
                    return new ApplyResult(ChangeOutcome.Failed, "The default policy has no rule.");
                target[s.Parameter] = s.ExchangeValue?.DeepClone();
                return new ApplyResult(ChangeOutcome.Applied);
            case CreateThreatPolicy c:
                var kind = ThreatPolicyCatalog.Kinds.First(k => k.PolicyNoun == c.PolicyNoun);
                var policy = new JsonObject { ["Name"] = c.Policy.Name, ["IsDefault"] = false };
                foreach (var s in ThreatPolicyCatalog.Settings(kind, ["AdminOnlyAccessPolicy", "DefaultFullAccessPolicy"]))
                    policy[s.Parameter] = s.Kind == SettingKind.List ? new JsonArray() : s.Recommended?.DeepClone();
                var rule = new JsonObject { ["Name"] = c.Policy.Name, [c.PolicyNoun] = c.Policy.Name, ["State"] = "Enabled",
                    ["Priority"] = Threat().Count(t => t.Kind.Key == kind.Key && t.Rule is not null) };
                foreach (var (parameter, values) in c.WhoParameters.Zip(new[] { c.People, c.Groups, c.Domains }))
                    rule[parameter] = new JsonArray([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);
                Threat().Insert(0, new DemoPolicy(kind, policy, rule));
                return new ApplyResult(ChangeOutcome.Applied);
            case RemoveThreatPolicy r:
                Threat().RemoveAll(t => t.Kind.PolicyNoun == r.PolicyNoun && t.Policy["Name"]!.GetValue<string>() == r.Policy.Name);
                return new ApplyResult(ChangeOutcome.Applied);
            case SetThreatRuleEnabled e when Find(e.RuleNoun, e.Policy.Name)?.Rule is { } r2:
                r2["State"] = e.New ? "Enabled" : "Disabled";
                return new ApplyResult(ChangeOutcome.Applied);
            case SetQuarantinePermissions q:
                var at = QuarantinePolicyList().FindIndex(p => p.Name == q.QuarantinePolicy.Name);
                if (at >= 0)
                    _quarantinePolicies![at] = _quarantinePolicies[at] with { Permissions = QuarantinePermissions.All.ToDictionary(p => p.Key, p => (q.New & p.Bit) != 0) };
                return new ApplyResult(ChangeOutcome.Applied);
            default:
                return null;
        }
    }
}
