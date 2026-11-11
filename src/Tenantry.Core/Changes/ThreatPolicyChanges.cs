namespace Tenantry.Core.Changes;

/// <summary>
/// A threat policy's change (12f). The target is the policy (its name), so in Test mode only
/// test-named policies change; Microsoft's default policies never can be created or removed.
/// </summary>
/// <param name="PolicyNoun">"HostedContentFilterPolicy" and so on.</param>
/// <param name="RuleNoun">"HostedContentFilterRule" and so on.</param>
public abstract record ThreatPolicyChange(ChangeTarget Policy, string PolicyNoun, string RuleNoun, string KindTitle) : ChangeOp
{
    public static ChangeTarget TargetFor(string kindKey, string name) => new("threatpolicy", $"{kindKey}:{name}", name, null);

    public override ChangeTarget Target => Policy;
    public override string Label => $"{KindTitle} Policy";
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>A new policy (its settings start as Microsoft's defaults) and its rule naming who it covers.</summary>
/// <param name="WhoParameters">The rule's who parameters for people, groups and domains ("SentTo", "SentToMemberOf", "RecipientDomainIs" or the sender ones).</param>
public sealed record CreateThreatPolicy(ChangeTarget Policy, string PolicyNoun, string RuleNoun, string KindTitle, IReadOnlyList<string> WhoParameters,
    IReadOnlyList<string> People, IReadOnlyList<string> Groups, IReadOnlyList<string> Domains) : ThreatPolicyChange(Policy, PolicyNoun, RuleNoun, KindTitle)
{
    public override string Key => "policy";
    public override string Before => "None";
    public override string After
    {
        get
        {
            var who = new[] { People.Count > 0 ? $"{People.Count} {(People.Count == 1 ? "person" : "people")}" : null,
                Groups.Count > 0 ? $"{Groups.Count} {(Groups.Count == 1 ? "group" : "groups")}" : null,
                Domains.Count > 0 ? string.Join(", ", Domains) : null }.OfType<string>();
            return $"Created for {string.Join(", ", who)} (Microsoft's default settings to start)";
        }
    }
    public override bool IsNoChange => false;
}

/// <summary>Removes a policy and its rule (Exchange keeps one if only the other is removed).</summary>
public sealed record RemoveThreatPolicy(ChangeTarget Policy, string PolicyNoun, string RuleNoun, string KindTitle, bool HasRule) : ThreatPolicyChange(Policy, PolicyNoun, RuleNoun, KindTitle)
{
    public override string Key => "policy";
    public override string Before => "In use";
    public override string After => "Removed (the next policy, or the default, covers its people)";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
}

/// <summary>Turns a policy's rule on or off.</summary>
public sealed record SetThreatRuleEnabled(ChangeTarget Policy, string PolicyNoun, string RuleNoun, string KindTitle, bool Old, bool New) : ThreatPolicyChange(Policy, PolicyNoun, RuleNoun, KindTitle)
{
    public override string Key => "state";
    public override string Before => Old ? "On" : "Off";
    public override string After => New ? "On" : "Off";
    public override bool IsCaution => !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetThreatRuleEnabled e ? this with { Old = e.Old } : this;
}

/// <summary>What people can do with their quarantined mail under a quarantine policy (the permission bits).</summary>
public sealed record SetQuarantinePermissions(ChangeTarget QuarantinePolicy, int Old, int New, string OldText, string NewText) : ChangeOp
{
    public override ChangeTarget Target => QuarantinePolicy;
    public override string Key => "permissions";
    public override string Label => "Quarantine Policy";
    public override string Before => OldText;
    public override string After => NewText;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetQuarantinePermissions e ? this with { Old = e.Old, OldText = e.OldText } : this;
}
