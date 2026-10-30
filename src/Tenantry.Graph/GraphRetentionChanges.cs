using System.Globalization;
using System.Text.Json;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Retention changes (8f-3) through Purview's cmdlets. A policy is a policy (where) plus its one
/// rule (what): a retention rule is named, a rule that publishes or applies labels can't be (Purview
/// refuses the name, seen live). Purview refuses changes while a policy's last change is still
/// being put in place, which is said in plain words.
/// </summary>
public sealed partial class GraphChangeExecutor
{
    Task<ApplyResult> ApplyRetentionAsync(RetentionChange change, CancellationToken cancellationToken) => Plain(change switch
    {
        CreateRetentionPolicy c => CreateRetentionPolicyAsync(c, cancellationToken),
        UpdateRetentionPolicy u => UpdateRetentionPolicyAsync(u, cancellationToken),
        SetRetentionPolicyEnabled e => Purview("Set-RetentionCompliancePolicy", new() { ["Identity"] = e.Policy.Name, ["Enabled"] = e.New },
            ct => ReadPurview("Get-RetentionCompliancePolicy", new() { ["Identity"] = e.Policy.Name }, ct),
            rows => rows.Count == 1 && rows[0].TryGetProperty("Enabled", out var on) && on.ValueKind == (e.New ? JsonValueKind.True : JsonValueKind.False), cancellationToken),
        DeleteRetentionPolicy d => Purview("Remove-RetentionCompliancePolicy", new() { ["Identity"] = d.Policy.Name, ["Confirm"] = false },
            ct => ReadPurview("Get-RetentionCompliancePolicy", new() { ["Identity"] = d.Policy.Name }, ct), Gone, cancellationToken),
        CreateRetentionLabel c => Purview("New-ComplianceTag", LabelParameters(new() { ["Name"] = c.Tag.Name }, c.Draft, creating: true),
            ct => ReadPurview("Get-ComplianceTag", new() { ["Identity"] = c.Tag.Name }, ct), rows => rows.Count == 1, cancellationToken),
        UpdateRetentionLabel u => Purview("Set-ComplianceTag", LabelParameters(new() { ["Identity"] = u.Tag.Name, ["Confirm"] = false }, u.New, creating: false),
            ct => ReadPurview("Get-ComplianceTag", new() { ["Identity"] = u.Tag.Name }, ct), rows => rows.Count == 1, cancellationToken),
        DeleteRetentionLabel d => Purview("Remove-ComplianceTag", new() { ["Identity"] = d.Tag.Name, ["Confirm"] = false },
            ct => ReadPurview("Get-ComplianceTag", new() { ["Identity"] = d.Tag.Name }, ct), Gone, cancellationToken),
        CreateAdaptiveScope c => Purview("New-AdaptiveScope", CreateScopeParameters(c),
            ct => ReadPurview("Get-AdaptiveScope", new() { ["Identity"] = c.Scope.Name }, ct), rows => rows.Count == 1, cancellationToken),
        UpdateAdaptiveScope u => Purview("Set-AdaptiveScope", UpdateScopeParameters(u),
            ct => ReadPurview("Get-AdaptiveScope", new() { ["Identity"] = u.Scope.Name }, ct), rows => rows.Count == 1, cancellationToken),
        DeleteAdaptiveScope d => Purview("Remove-AdaptiveScope", new() { ["Identity"] = d.Scope.Name, ["Confirm"] = false },
            ct => ReadPurview("Get-AdaptiveScope", new() { ["Identity"] = d.Scope.Name }, ct), Gone, cancellationToken),
        CreateRetentionEventType c => Purview("New-ComplianceRetentionEventType", With(new() { ["Name"] = c.EventType.Name }, "Comment", c.Comment),
            ct => ReadPurview("Get-ComplianceRetentionEventType", new() { ["Identity"] = c.EventType.Name }, ct), rows => rows.Count == 1, cancellationToken),
        DeleteRetentionEventType d => Purview("Remove-ComplianceRetentionEventType", new() { ["Identity"] = d.EventType.Name, ["Confirm"] = false },
            ct => ReadPurview("Get-ComplianceRetentionEventType", new() { ["Identity"] = d.EventType.Name }, ct), Gone, cancellationToken),
        CreateRetentionEvent c => Purview("New-ComplianceRetentionEvent", RetentionEventParameters(c),
            ct => ReadPurview("Get-ComplianceRetentionEvent", new() { ["Identity"] = c.Event.Name }, ct), rows => rows.Count == 1, cancellationToken),
        _ => Task.FromResult(new ApplyResult(ChangeOutcome.Failed, $"Tenantry can't apply \"{change.Label}\" changes yet.")),
    });

    /// <summary>Gone, or marked for deletion while Purview takes it away everywhere (seen live, for minutes).</summary>
    static bool Gone(IReadOnlyList<JsonElement> rows) => rows.Count == 0
        || rows[0].TryGetProperty("Mode", out var mode) && string.Equals(mode.GetString(), "PendingDeletion", StringComparison.OrdinalIgnoreCase);

    /// <summary>Purview's "still being deployed" refusal, said plainly.</summary>
    static async Task<ApplyResult> Plain(Task<ApplyResult> applying)
    {
        var result = await applying;
        var detail = result.Detail ?? "";
        if (result.Outcome != ChangeOutcome.Failed)
            return result;
        if (detail.Contains("are being deployed", StringComparison.OrdinalIgnoreCase) || detail.Contains("pending deletion", StringComparison.OrdinalIgnoreCase))
            return result with { Detail = "Purview is still putting this policy's last change in place (it can take a while); try again later." };
        if (detail.Contains("not licensed", StringComparison.OrdinalIgnoreCase))
            return result with { Detail = "This organization isn't licensed for this (adaptive scopes and auto-applied labels need Microsoft 365 E5 or E5 Compliance)." };
        return result;
    }

    static string Duration(int? days) => days is { } d ? d.ToString(CultureInfo.InvariantCulture) : "Unlimited";

    static Dictionary<string, object?> LabelParameters(Dictionary<string, object?> parameters, RetentionLabelDraft draft, bool creating)
    {
        With(parameters, "Comment", draft.Comment ?? (creating ? null : ""));
        With(parameters, "Notes", draft.Notes ?? (creating ? null : ""));
        if (draft.Action is not null)
            parameters["RetentionDuration"] = Duration(draft.Days);
        if (creating)
        {
            if (draft.Action is { } action)
            {
                parameters["RetentionAction"] = action;
                parameters["RetentionType"] = draft.BasedOn;
            }
            if (draft.IsRecord)
                parameters["IsRecordLabel"] = true;
            With(parameters, "EventType", draft.EventType);
        }
        if (draft.Reviewers.Count > 0 || !creating)
            parameters["ReviewerEmail"] = draft.Reviewers.ToArray();
        return parameters;
    }

    static string ParameterFor(string kind) => kind + "Location";

    /// <summary>The policy (where), then its rule (what); if the rule can't be made the policy goes again.</summary>
    async Task<ApplyResult> CreateRetentionPolicyAsync(CreateRetentionPolicy change, CancellationToken cancellationToken)
    {
        var draft = change.Draft;
        var made = await Purview("New-RetentionCompliancePolicy", CreatePolicyParameters(change),
            ct => ReadPurview("Get-RetentionCompliancePolicy", new() { ["Identity"] = change.Policy.Name }, ct), rows => rows.Count == 1, cancellationToken);
        if (made.Outcome != ChangeOutcome.Applied)
            return made;
        try
        {
            await exchange!.InvokeAsync(Tenantry.Core.Exchange.CmdletEndpoint.Compliance, "New-RetentionComplianceRule",
                RuleParameters(new() { ["Policy"] = change.Policy.Name }, change.Policy.Name, draft, creating: true), cancellationToken);
        }
        catch (Tenantry.Core.Exchange.CmdletException e)
        {
            try
            {
                await exchange!.InvokeAsync(Tenantry.Core.Exchange.CmdletEndpoint.Compliance, "Remove-RetentionCompliancePolicy",
                    new Dictionary<string, object?> { ["Identity"] = change.Policy.Name, ["Confirm"] = false }, cancellationToken);
            }
            catch (Tenantry.Core.Exchange.CmdletException)
            {
            }
            return new ApplyResult(ChangeOutcome.Failed, $"Purview couldn't set what the policy does, so it wasn't made: {e.Explanation}");
        }
        return made with { Detail = "Made; Purview takes a while to put it in place everywhere, and the policy can't be changed until it has." };
    }

    /// <summary>
    /// A retention rule has a name and a duration; a rule that publishes or applies labels has
    /// neither (Purview's parameter sets, seen live).
    /// </summary>
    static Dictionary<string, object?> RuleParameters(Dictionary<string, object?> parameters, string policyName, RetentionPolicyDraft draft, bool creating)
    {
        var teams = draft.Places.Keys.Any(RetentionPlaces.IsTeams);
        switch (draft.Kind)
        {
            case RetentionPolicyKind.PublishLabels:
                parameters["PublishComplianceTag"] = string.Join(",", draft.Labels);
                break;
            case RetentionPolicyKind.AutoApplyLabel:
                if (creating)
                    parameters["ApplyComplianceTag"] = draft.Labels.FirstOrDefault() ?? "";
                parameters["ContentMatchQuery"] = draft.Query ?? "";
                break;
            default:
                if (creating)
                    parameters["Name"] = policyName + "-rule";
                var setting = draft.Setting ?? new RetentionSetting("Keep", null, "CreationAgeInDays");
                parameters["RetentionDuration"] = Duration(setting.Days);
                parameters["RetentionComplianceAction"] = setting.Action;
                // Teams rules take no expiration option (Purview's rule).
                if (!teams)
                    parameters["ExpirationDateOption"] = setting.BasedOn;
                break;
        }
        return parameters;
    }

    async Task<ApplyResult> UpdateRetentionPolicyAsync(UpdateRetentionPolicy change, CancellationToken cancellationToken)
    {
        var parameters = UpdatePolicyParameters(change);
        if (parameters.Count > 1)
        {
            var placed = await Purview("Set-RetentionCompliancePolicy", parameters, null, _ => true, cancellationToken);
            if (placed.Outcome != ChangeOutcome.Applied)
                return placed;
        }
        if (!RuleChanged(change))
            return new ApplyResult(ChangeOutcome.Applied);
        var rules = await ReadPurview("Get-RetentionComplianceRule", new() { ["Policy"] = change.Policy.Name }, cancellationToken);
        if (rules.FirstOrDefault() is not { ValueKind: JsonValueKind.Object } rule
            || (rule.TryGetProperty("Guid", out var g) ? g.GetString() : rule.TryGetProperty("Name", out var n) ? n.GetString() : null) is not { } ruleId)
            return new ApplyResult(ChangeOutcome.Failed, "The policy's rule wasn't found, so what it does couldn't be changed.");
        return await Purview("Set-RetentionComplianceRule", RuleParameters(new() { ["Identity"] = ruleId }, change.Policy.Name, change.New, creating: false),
            null, _ => true, cancellationToken);
    }

    // ---- Parameters (shared with the PowerShell shown for each change) ---------------------------

    static Dictionary<string, object?> CreateScopeParameters(CreateAdaptiveScope change) =>
        With(new() { ["Name"] = change.Scope.Name, ["LocationType"] = change.LocationType, ["RawQuery"] = change.Query }, "Comment", change.Comment);

    static Dictionary<string, object?> UpdateScopeParameters(UpdateAdaptiveScope change) =>
        new() { ["Identity"] = change.Scope.Name, ["RawQuery"] = change.NewQuery, ["Comment"] = change.NewComment ?? "" };

    static Dictionary<string, object?> RetentionEventParameters(CreateRetentionEvent change) =>
        With(With(With(new()
        {
            ["Name"] = change.Event.Name, ["EventType"] = change.EventType,
            ["EventDateTime"] = change.When.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
        }, "ExchangeAssetIdQuery", change.ExchangeQuery), "SharePointAssetIdQuery", change.SharePointQuery), "Comment", change.Comment);

    static Dictionary<string, object?> CreatePolicyParameters(CreateRetentionPolicy change)
    {
        var draft = change.Draft;
        var parameters = With(new() { ["Name"] = change.Policy.Name }, "Comment", draft.Comment);
        foreach (var (kind, places) in draft.Places)
            if (places.Count > 0)
                parameters[ParameterFor(kind)] = Places(places);
        if (draft.Scopes.Count > 0)
            parameters["AdaptiveScopeLocation"] = draft.Scopes.ToArray();
        return parameters;
    }

    /// <summary>Set-RetentionCompliancePolicy's comment, places and scopes that changed; only Identity when none did.</summary>
    static Dictionary<string, object?> UpdatePolicyParameters(UpdateRetentionPolicy change)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = change.Policy.Name };
        if ((change.Old.Comment ?? "") != (change.New.Comment ?? ""))
            parameters["Comment"] = change.New.Comment ?? "";
        foreach (var kind in RetentionPlaces.All)
            ChangePlaces(parameters, kind, change.Old.Places.GetValueOrDefault(kind) ?? [], change.New.Places.GetValueOrDefault(kind) ?? []);
        var addScopes = change.New.Scopes.Except(change.Old.Scopes, StringComparer.OrdinalIgnoreCase).ToArray();
        var removeScopes = change.Old.Scopes.Except(change.New.Scopes, StringComparer.OrdinalIgnoreCase).ToArray();
        if (addScopes.Length > 0)
            parameters["AddAdaptiveScopeLocation"] = addScopes;
        if (removeScopes.Length > 0)
            parameters["RemoveAdaptiveScopeLocation"] = removeScopes;
        return parameters;
    }

    /// <summary>Whether what the policy does (its rule) changed, not only where it applies.</summary>
    static bool RuleChanged(UpdateRetentionPolicy change) =>
        change.Old.Setting != change.New.Setting || change.Old.Query != change.New.Query
        || !change.Old.Labels.SequenceEqual(change.New.Labels, StringComparer.OrdinalIgnoreCase);
}
