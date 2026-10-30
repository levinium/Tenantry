using Tenantry.Core.Changes;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>The PowerShell for eDiscovery (8f) and retention (8f-3) changes: the Purview cmdlets the executor sends, in order.</summary>
public sealed partial class GraphChangeExecutor
{
    static PsScript? EDiscoveryScript(ChangeOp change) => change switch
    {
        CreateCase c => PurviewText(Ps.Cmdlet("New-ComplianceCase", CreateCaseParameters(c))),
        SetCaseClosed s => PurviewText(Ps.Cmdlet("Set-ComplianceCase", CaseClosedParameters(s))),
        SetCaseDescription d => PurviewText(Ps.Cmdlet("Set-ComplianceCase", new Dictionary<string, object?> { ["Identity"] = d.Case.Name, ["Description"] = d.New ?? "" })),
        DeleteCase d => PurviewText(Ps.Cmdlet("Remove-ComplianceCase", new Dictionary<string, object?> { ["Identity"] = d.Case.Name, ["Confirm"] = false }),
            "Purview refuses while the case still has holds, including ones it is still lifting."),
        SetCaseMember m => PurviewText(Ps.Cmdlet(CaseMemberCmdlet(m), CaseMemberParameters(m))),
        // Tenantry runs a search straight after saving it, so its counts are ready.
        CreateSearch s => PurviewText(Ps.Lines(
            Ps.Cmdlet("New-ComplianceSearch", CreateSearchParameters(s)),
            Ps.Cmdlet("Start-ComplianceSearch", StartSearchParameters(s.Search.Name)))),
        UpdateSearch u => PurviewText(Ps.Lines(
            Ps.Cmdlet("Set-ComplianceSearch", UpdateSearchParameters(u)),
            Ps.Cmdlet("Start-ComplianceSearch", StartSearchParameters(u.Search.Name)))),
        DeleteSearch d => PurviewText(Ps.Cmdlet("Remove-ComplianceSearch", new Dictionary<string, object?> { ["Identity"] = d.Search.Name, ["Confirm"] = false })),
        // A hold is a policy (where) plus a rule (what), named alike.
        CreateHold h => PurviewText(Ps.Lines(
            Ps.Cmdlet("New-CaseHoldPolicy", CreateHoldParameters(h)),
            Ps.Cmdlet("New-CaseHoldRule", HoldRuleParameters(h.Hold.Name, h.Query)))),
        UpdateHold u => UpdateHoldScript(u),
        SetHoldEnabled e => PurviewText(Ps.Cmdlet("Set-CaseHoldPolicy", new Dictionary<string, object?> { ["Identity"] = e.Hold.Name, ["Enabled"] = e.New })),
        DeleteHold d => PurviewText(Ps.Cmdlet("Remove-CaseHoldPolicy", new Dictionary<string, object?> { ["Identity"] = d.Hold.Name, ["Confirm"] = false }),
            "Purview keeps the hold as pending deletion for a while as it lifts it everywhere."),
        PurgeSearch p => PurviewText(Ps.Cmdlet("New-ComplianceSearchAction", PurgeParameters(p)),
            $"This can't be undone. Purview removes at most {PurgeSearch.PerMailbox} items from each mailbox per run."),
        _ => null,
    };

    static PsScript UpdateHoldScript(UpdateHold change)
    {
        var placed = UpdateHoldParameters(change);
        var lines = new List<string>();
        if (placed.Count > 1)
            lines.Add(Ps.Cmdlet("Set-CaseHoldPolicy", placed));
        if (change.OldQuery.Trim() != change.NewQuery.Trim())
        {
            var query = change.NewQuery.Trim();
            // The hold's rule is looked up by its policy; one is made if it has none.
            lines.Add($"$rule = Get-CaseHoldRule -Policy {Ps.Quote(change.Hold.Name)} | Select-Object -First 1");
            lines.Add("if ($rule) {");
            lines.Add("    " + PurviewWithIdentity("Set-CaseHoldRule", "$rule.Name", new() { ["ContentMatchQuery"] = query }));
            lines.Add("} else {");
            lines.Add("    " + Ps.Cmdlet("New-CaseHoldRule", HoldRuleParameters(change.Hold.Name, query)));
            lines.Add("}");
        }
        return PurviewText(lines.Count > 0 ? Ps.Lines(lines) : Ps.Comment("Nothing to send: the hold is unchanged."));
    }

    static PsScript? RetentionScript(ChangeOp change) => change switch
    {
        // The policy (where), then its one rule (what).
        CreateRetentionPolicy c => PurviewText(Ps.Lines(
            Ps.Cmdlet("New-RetentionCompliancePolicy", CreatePolicyParameters(c)),
            Ps.Cmdlet("New-RetentionComplianceRule", RuleParameters(new() { ["Policy"] = c.Policy.Name }, c.Policy.Name, c.Draft, creating: true))),
            "Purview takes a while to put a new policy in place, and refuses changes to it until it has."),
        UpdateRetentionPolicy u => UpdatePolicyScript(u),
        SetRetentionPolicyEnabled e => PurviewText(Ps.Cmdlet("Set-RetentionCompliancePolicy", new Dictionary<string, object?> { ["Identity"] = e.Policy.Name, ["Enabled"] = e.New })),
        DeleteRetentionPolicy d => PurviewText(Ps.Cmdlet("Remove-RetentionCompliancePolicy", new Dictionary<string, object?> { ["Identity"] = d.Policy.Name, ["Confirm"] = false })),
        CreateRetentionLabel c => PurviewText(Ps.Cmdlet("New-ComplianceTag", LabelParameters(new() { ["Name"] = c.Tag.Name }, c.Draft, creating: true))),
        UpdateRetentionLabel u => PurviewText(Ps.Cmdlet("Set-ComplianceTag", LabelParameters(new() { ["Identity"] = u.Tag.Name, ["Confirm"] = false }, u.New, creating: false))),
        DeleteRetentionLabel d => PurviewText(Ps.Cmdlet("Remove-ComplianceTag", new Dictionary<string, object?> { ["Identity"] = d.Tag.Name, ["Confirm"] = false })),
        CreateAdaptiveScope c => PurviewText(Ps.Cmdlet("New-AdaptiveScope", CreateScopeParameters(c))),
        UpdateAdaptiveScope u => PurviewText(Ps.Cmdlet("Set-AdaptiveScope", UpdateScopeParameters(u))),
        DeleteAdaptiveScope d => PurviewText(Ps.Cmdlet("Remove-AdaptiveScope", new Dictionary<string, object?> { ["Identity"] = d.Scope.Name, ["Confirm"] = false })),
        CreateRetentionEventType c => PurviewText(Ps.Cmdlet("New-ComplianceRetentionEventType", With(new() { ["Name"] = c.EventType.Name }, "Comment", c.Comment))),
        DeleteRetentionEventType d => PurviewText(Ps.Cmdlet("Remove-ComplianceRetentionEventType", new Dictionary<string, object?> { ["Identity"] = d.EventType.Name, ["Confirm"] = false })),
        CreateRetentionEvent c => PurviewText(Ps.Cmdlet("New-ComplianceRetentionEvent", RetentionEventParameters(c))),
        _ => null,
    };

    static PsScript UpdatePolicyScript(UpdateRetentionPolicy change)
    {
        var placed = UpdatePolicyParameters(change);
        var lines = new List<string>();
        if (placed.Count > 1)
            lines.Add(Ps.Cmdlet("Set-RetentionCompliancePolicy", placed));
        if (RuleChanged(change))
        {
            // A policy has one rule; it's found by its policy.
            lines.Add($"$rule = Get-RetentionComplianceRule -Policy {Ps.Quote(change.Policy.Name)} | Select-Object -First 1");
            lines.Add(PurviewWithIdentity("Set-RetentionComplianceRule", "$rule.Guid", RuleParameters(new(), change.Policy.Name, change.New, creating: false)));
        }
        return PurviewText(lines.Count > 0 ? Ps.Lines(lines) : Ps.Comment("Nothing to send: the policy is unchanged."),
            "Purview refuses changes while the policy's last change is still being put in place.");
    }

    static PsScript PurviewText(string text, string? note = null) => PsScript.Purview(text, note);

    /// <summary>A cmdlet whose -Identity is a PowerShell expression (a rule just looked up), not a quoted name.</summary>
    static string PurviewWithIdentity(string cmdlet, string identityExpression, Dictionary<string, object?> parameters)
    {
        var rest = Ps.Cmdlet(cmdlet, parameters.Where(p => p.Key != "Identity"));
        return cmdlet + " -Identity " + identityExpression + rest[cmdlet.Length..];
    }
}
