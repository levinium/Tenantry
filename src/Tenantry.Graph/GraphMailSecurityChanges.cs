using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.MailFlow;

namespace Tenantry.Graph;

/// <summary>Mail &amp; security changes (5a): releasing or deleting quarantined mail, blocking or allowing senders.</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>Microsoft lets an allowed sender stay this long after its last use; blocks have no end.</summary>
    public const int AllowedSenderDays = 45;

    Task<IReadOnlyList<JsonElement>> ReadQuarantined(string identity, CancellationToken ct) =>
        Read("Get-QuarantineMessage", new() { ["Identity"] = identity }, ct);

    static Dictionary<string, object?> ReleaseParameters(ReleaseQuarantined change)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = change.Message.Id, ["ReleaseToAll"] = true, ["Confirm"] = false };
        if (change.AllowSender)
            parameters["AllowSender"] = true;
        return parameters;
    }

    Task<ApplyResult> ReleaseQuarantinedAsync(ReleaseQuarantined change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Release-QuarantineMessage", ReleaseParameters(change),
            ct => ReadQuarantined(change.Message.Id, ct),
            rows => rows.Count == 1 && rows[0].TryGetProperty("ReleaseStatus", out var s) && s.ValueKind == JsonValueKind.String
                    && s.GetString()!.Equals("RELEASED", StringComparison.OrdinalIgnoreCase),
            cancellationToken);

    Task<ApplyResult> DeleteQuarantinedAsync(DeleteQuarantined change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Delete-QuarantineMessage", new() { ["Identity"] = change.Message.Id, ["Confirm"] = false },
            ct => ReadQuarantined(change.Message.Id, ct), rows => rows.Count == 0, cancellationToken);

    static void AddExpiry(Dictionary<string, object?> parameters, ListExpiry expiry)
    {
        if (expiry.On is { } on)
            parameters["ExpirationDate"] = on.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        else if (expiry.AfterLastUseDays is { } days)
            parameters["RemoveAfter"] = days;
        else
            parameters["NoExpiration"] = true;
    }

    Task<IReadOnlyList<JsonElement>> ReadEntry(ListKind kind, string value, CancellationToken ct) => kind == ListKind.Spoof
        ? Read("Get-TenantAllowBlockListSpoofItems", new(), ct)
        : Read("Get-TenantAllowBlockListItems", new() { ["ListType"] = AllowBlock.ListType(kind), ["Entry"] = value }, ct);

    static bool Matches(JsonElement row, ListKind kind, string value) =>
        kind != ListKind.Spoof
        || row.TryGetProperty("SpoofedUser", out var user) && string.Equals(user.GetString(), value, StringComparison.OrdinalIgnoreCase);

    /// <summary>The cmdlet and parameters that add a block or allow entry (spoofed senders have their own cmdlet).</summary>
    static (string Cmdlet, Dictionary<string, object?> Parameters) AddListEntryCall(AddListEntry change)
    {
        var value = change.Entry.Name;
        if (change.Kind == ListKind.Spoof)
            return ("New-TenantAllowBlockListSpoofItems", new()
            {
                ["Identity"] = "Default", ["SpoofedUser"] = value, ["SendingInfrastructure"] = change.Infrastructure,
                ["SpoofType"] = change.SpoofType ?? "External", ["Action"] = change.Block ? "Block" : "Allow",
            });
        var parameters = new Dictionary<string, object?> { ["ListType"] = AllowBlock.ListType(change.Kind), ["Entries"] = new[] { value } };
        parameters[change.Block ? "Block" : "Allow"] = true;
        AddExpiry(parameters, change.Expiry);
        if (change.Notes is { Length: > 0 } notes)
            parameters["Notes"] = notes;
        return ("New-TenantAllowBlockListItems", parameters);
    }

    Task<ApplyResult> AddListEntryAsync(AddListEntry change, CancellationToken cancellationToken)
    {
        var value = change.Entry.Name;
        var (cmdlet, parameters) = AddListEntryCall(change);
        return RunCheckedAsync(cmdlet, parameters, ct => ReadEntry(change.Kind, value, ct),
            rows => rows.Any(r => Matches(r, change.Kind, value) && r.TryGetProperty("Action", out var a)
                                  && string.Equals(a.GetString(), change.Block ? "Block" : "Allow", StringComparison.OrdinalIgnoreCase)),
            cancellationToken);
    }

    static (string Cmdlet, Dictionary<string, object?> Parameters) RemoveListEntryCall(RemoveListEntry change) => change.Kind == ListKind.Spoof
        ? ("Remove-TenantAllowBlockListSpoofItems", new() { ["Identity"] = "Default", ["Ids"] = new[] { change.Identity } })
        : ("Remove-TenantAllowBlockListItems", new() { ["ListType"] = AllowBlock.ListType(change.Kind), ["Ids"] = new[] { change.Identity } });

    Task<ApplyResult> RemoveListEntryAsync(RemoveListEntry change, CancellationToken cancellationToken)
    {
        var (cmdlet, parameters) = RemoveListEntryCall(change);
        return RunCheckedAsync(cmdlet, parameters, ct => ReadEntry(change.Kind, change.Entry.Name, ct),
            rows => change.Kind == ListKind.Spoof ? !rows.Any(r => Matches(r, change.Kind, change.Entry.Name)) : rows.Count == 0, cancellationToken);
    }

    /// <summary>Set-TenantAllowBlockListItems with only what changed (expiry, notes).</summary>
    static Dictionary<string, object?> UpdateListEntryParameters(UpdateListEntry change)
    {
        var parameters = new Dictionary<string, object?> { ["ListType"] = AllowBlock.ListType(change.Kind), ["Ids"] = new[] { change.Identity } };
        if (change.NewExpiry != change.OldExpiry)
            AddExpiry(parameters, change.NewExpiry);
        if ((change.NewNotes ?? "") != (change.OldNotes ?? ""))
            parameters["Notes"] = change.NewNotes ?? "";
        return parameters;
    }

    Task<ApplyResult> UpdateListEntryAsync(UpdateListEntry change, CancellationToken cancellationToken)
    {
        var parameters = UpdateListEntryParameters(change);
        return RunCheckedAsync("Set-TenantAllowBlockListItems", parameters, ct => ReadEntry(change.Kind, change.Entry.Name, ct),
            rows => rows.Any(r => (r.TryGetProperty("Notes", out var n) ? n.GetString() ?? "" : "") == (change.NewNotes ?? "")),
            cancellationToken);
    }

    Task<IReadOnlyList<JsonElement>> ReadRule(string identity, CancellationToken ct) =>
        Read("Get-TransportRule", new() { ["Identity"] = identity }, ct);

    static bool RuleHas(IReadOnlyList<JsonElement> rows, Func<MailFlowRule, bool> done) =>
        rows.Count == 1 && done(GraphUserRecords.ToMailFlowRule(rows[0]));

    Task<ApplyResult> SetRuleEnabledAsync(SetRuleEnabled change, CancellationToken cancellationToken) =>
        RunCheckedAsync(change.New ? "Enable-TransportRule" : "Disable-TransportRule", new() { ["Identity"] = change.Rule.Id, ["Confirm"] = false },
            ct => ReadRule(change.Rule.Id, ct), rows => RuleHas(rows, r => r.Enabled == change.New), cancellationToken);

    Task<ApplyResult> SetRuleModeAsync(SetRuleMode change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Set-TransportRule", new() { ["Identity"] = change.Rule.Id, ["Mode"] = MailFlowRule.ExchangeMode(change.New) },
            ct => ReadRule(change.Rule.Id, ct), rows => RuleHas(rows, r => r.Mode == change.New), cancellationToken);

    Task<ApplyResult> SetRulePriorityAsync(SetRulePriority change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Set-TransportRule", new() { ["Identity"] = change.Rule.Id, ["Priority"] = change.New },
            ct => ReadRule(change.Rule.Id, ct), rows => RuleHas(rows, r => r.Priority == change.New), cancellationToken);

    Task<ApplyResult> RemoveRuleAsync(RemoveRule change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Remove-TransportRule", new() { ["Identity"] = change.Rule.Id, ["Confirm"] = false },
            ct => ReadRule(change.Rule.Id, ct), rows => rows.Count == 0, cancellationToken);

    static Dictionary<string, object?> RuleParameters(string json) =>
        JsonNode.Parse(json) is JsonObject o ? o.ToDictionary(p => p.Key, p => (object?)p.Value?.DeepClone(), StringComparer.OrdinalIgnoreCase) : [];

    static ApplyResult? NotAsAsked(IReadOnlyList<JsonElement> rows, IEnumerable<KeyValuePair<string, JsonNode?>> expected) =>
        rows.Count == 1 && RuleCodec.Mismatches(rows[0], expected) is { Count: > 0 } wrong
            ? new ApplyResult(ChangeOutcome.Failed, $"Exchange saved the rule, but these settings didn't read back as asked: {string.Join(", ", wrong)}.")
            : null;

    static Dictionary<string, object?> CreateRuleParameters(CreateRule change)
    {
        var parameters = RuleParameters(change.Parameters);
        parameters["Name"] = change.Rule.Name;
        return parameters;
    }

    static Dictionary<string, object?> UpdateRuleParameters(UpdateRule change)
    {
        var parameters = RuleParameters(change.Changes);
        parameters["Identity"] = change.Rule.Id;
        return parameters;
    }

    async Task<ApplyResult> CreateRuleAsync(CreateRule change, CancellationToken cancellationToken)
    {
        var parameters = CreateRuleParameters(change);
        var result = await RunCheckedAsync("New-TransportRule", parameters, ct => ReadRule(change.Rule.Name, ct), rows => rows.Count == 1, cancellationToken);
        if (result.Outcome != ChangeOutcome.Applied)
            return result;
        var expected = (JsonObject)JsonNode.Parse(change.Parameters)!;
        expected.Remove("Mode");
        expected.Remove("Enabled");
        return NotAsAsked(await ReadRule(change.Rule.Name, cancellationToken), expected) ?? result;
    }

    async Task<ApplyResult> UpdateRuleAsync(UpdateRule change, CancellationToken cancellationToken)
    {
        var parameters = UpdateRuleParameters(change);
        var result = await RunCheckedAsync("Set-TransportRule", parameters, ct => ReadRule(change.Rule.Id, ct), rows => rows.Count == 1, cancellationToken);
        if (result.Outcome != ChangeOutcome.Applied)
            return result;
        var expected = (JsonObject)JsonNode.Parse(change.Changes)!;
        expected.Remove("Mode");
        return NotAsAsked(await ReadRule(change.Rule.Id, cancellationToken), expected) ?? result;
    }

    static Dictionary<string, object?> SenderListedParameters(SetSenderListed change)
    {
        var parameters = new Dictionary<string, object?> { ["ListType"] = "Sender", ["Entries"] = new[] { change.Sender.Address ?? change.Sender.Id } };
        parameters[change.Block ? "Block" : "Allow"] = true;
        if (change.Block)
            parameters["NoExpiration"] = true;
        else
            parameters["RemoveAfter"] = change.RemoveAfterDays ?? AllowedSenderDays;
        if (change.Notes is { Length: > 0 } notes)
            parameters["Notes"] = notes;
        return parameters;
    }

    Task<ApplyResult> SetSenderListedAsync(SetSenderListed change, CancellationToken cancellationToken)
    {
        var entry = change.Sender.Address ?? change.Sender.Id;
        return RunCheckedAsync("New-TenantAllowBlockListItems", SenderListedParameters(change),
            ct => Read("Get-TenantAllowBlockListItems", new() { ["ListType"] = "Sender", ["Entry"] = entry }, ct),
            rows => rows.Any(r => r.TryGetProperty("Action", out var a) && a.ValueKind == JsonValueKind.String
                                  && a.GetString()!.Equals(change.Block ? "Block" : "Allow", StringComparison.OrdinalIgnoreCase)),
            cancellationToken);
    }
}
