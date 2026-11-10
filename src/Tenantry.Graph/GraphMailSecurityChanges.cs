using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;
using Tenantry.Core.MailFlow;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>Mail &amp; security changes (5a): releasing or deleting quarantined mail, blocking or allowing senders.</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>Microsoft lets an allowed sender stay this long after its last use; blocks have no end.</summary>
    public const int AllowedSenderDays = 45;

    Task<IReadOnlyList<JsonElement>> ReadQuarantined(string identity, CancellationToken ct) =>
        Read("Get-QuarantineMessage", new() { ["Identity"] = identity }, ct);

    /// <summary>
    /// Everyone (-ReleaseToAll) or chosen recipients (-User): Exchange refuses both together.
    /// Approving a release request is the same call.
    /// </summary>
    internal static Dictionary<string, object?> ReleaseParameters(ReleaseQuarantined change)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = change.Message.Id, ["Confirm"] = false };
        if (change.To is { Count: > 0 } to)
            parameters["User"] = to.ToArray();
        else
            parameters["ReleaseToAll"] = true;
        if (change.AllowSender)
            parameters["AllowSender"] = true;
        if (change.ReportFalsePositive)
            parameters["ReportFalsePositive"] = true;
        return parameters;
    }

    Task<ApplyResult> ReleaseQuarantinedAsync(ReleaseQuarantined change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Release-QuarantineMessage", ReleaseParameters(change),
            ct => ReadQuarantined(change.Message.Id, ct),
            rows => rows.Count == 1 && Released(rows[0], change.To),
            cancellationToken);

    /// <summary>Released to everyone, or (for chosen recipients) to each of them.</summary>
    static bool Released(JsonElement row, IReadOnlyList<string>? to)
    {
        var status = row.TryGetProperty("ReleaseStatus", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
        if (to is not { Count: > 0 })
            return status.Equals("RELEASED", StringComparison.OrdinalIgnoreCase);
        var done = row.TryGetProperty("ReleasedUser", out var users) && users.ValueKind == JsonValueKind.Array
            ? users.EnumerateArray().Select(u => u.GetString() ?? "").ToArray() : [];
        return status.Equals("RELEASED", StringComparison.OrdinalIgnoreCase) || to.All(t => done.Contains(t, StringComparer.OrdinalIgnoreCase));
    }

    // ---- Mail domains and blocked senders (12c) ------------------------------------------------

    internal static (string Cmdlet, Dictionary<string, object?> Parameters) DkimCall(DkimChange change) => change switch
    {
        CreateDkimKeys c => ("New-DkimSigningConfig", new() { ["DomainName"] = c.Domain.Id, ["Enabled"] = false, ["KeySize"] = 2048 }),
        SetDkimSigning s => ("Set-DkimSigningConfig", new() { ["Identity"] = s.Domain.Id, ["Enabled"] = s.New }),
        RotateDkimKeys r => ("Rotate-DkimSigningConfig", new() { ["Identity"] = r.Domain.Id, ["KeySize"] = r.KeySize }),
        _ => throw new NotSupportedException(change.GetType().Name),
    };

    Task<ApplyResult> DkimAsync(DkimChange change, CancellationToken cancellationToken)
    {
        var (cmdlet, parameters) = DkimCall(change);
        return RunCheckedAsync(cmdlet, parameters, ct => Read("Get-DkimSigningConfig", new() { ["Identity"] = change.Domain.Id }, ct),
            rows => change switch
            {
                SetDkimSigning s => rows.Count == 1 && rows[0].TryGetProperty("Enabled", out var e) && (e.ValueKind == JsonValueKind.True) == s.New,
                _ => rows.Count == 1,
            }, cancellationToken);
    }

    internal static Dictionary<string, object?> UnblockParameters(UnblockSender change)
    {
        var parameters = new Dictionary<string, object?> { ["SenderAddress"] = change.Sender.Id };
        if (change.Reason is { Length: > 0 } reason)
            parameters["Reason"] = reason;
        return parameters;
    }

    Task<ApplyResult> UnblockSenderAsync(UnblockSender change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Remove-BlockedSenderAddress", UnblockParameters(change),
            ct => Read("Get-BlockedSenderAddress", new() { ["SenderAddress"] = change.Sender.Id }, ct),
            rows => rows.Count == 0, cancellationToken);

    /// <summary>Deny is only in the bulk form, which needs a placeholder Identity (Exchange ignores its value).</summary>
    internal static Dictionary<string, object?> DenyParameters(DenyQuarantineRelease change) => new()
    {
        ["Identities"] = new[] { change.Message.Id }, ["Identity"] = "000", ["ActionType"] = "Deny", ["Confirm"] = false,
    };

    Task<ApplyResult> DenyQuarantineReleaseAsync(DenyQuarantineRelease change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Release-QuarantineMessage", DenyParameters(change),
            ct => ReadQuarantined(change.Message.Id, ct),
            rows => rows.Count == 1 && rows[0].TryGetProperty("ReleaseStatus", out var s) && s.ValueKind == JsonValueKind.String
                    && s.GetString()!.Equals("DENIED", StringComparison.OrdinalIgnoreCase),
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

    /// <summary>
    /// A whole new rule order: the moves it needs, sent one after another (Exchange moves one rule per
    /// call), then the order read back. A move that fails stops the rest and says how far it got.
    /// </summary>
    async Task<ApplyResult> ReorderRulesAsync(ReorderRules change, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        var moves = change.Moves();
        for (var i = 0; i < moves.Count; i++)
        {
            // Exchange takes about half a minute for each move (seen live), so say which one is going.
            ApplyProgress.Report(moves.Count == 1
                ? $"Moving \"{Name(moves[i].Identity)}\" (Exchange takes about half a minute)"
                : $"Moving rule {i + 1} of {moves.Count}: \"{Name(moves[i].Identity)}\" (about half a minute each)");
            try
            {
                await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Set-TransportRule",
                    new Dictionary<string, object?> { ["Identity"] = moves[i].Identity, ["Priority"] = moves[i].Priority }, cancellationToken);
            }
            catch (CmdletException e)
            {
                return new ApplyResult(i == 0 ? ChangeOutcome.Failed : ChangeOutcome.AppliedNotYetVisible,
                    $"{(i == 0 ? "Nothing moved" : $"{i} of {moves.Count} moves went through")}; moving \"{Name(moves[i].Identity)}\" failed: {e.Explanation} Refresh the rules to see where they stand.");
            }
        }
        string Name(string identity) => change.New.FirstOrDefault(r => r.Identity == identity)?.Name ?? identity;
        ApplyProgress.Report("Checking the new order");
        var wanted = change.New.Select(r => r.Identity).ToArray();
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            // The rules' identities are their Guids (as GetRulesAsync reads them), so read the same field.
            var rows = await Read("Get-TransportRule", new() { ["ResultSize"] = "Unlimited" }, cancellationToken, "Guid", "Identity", "Priority");
            var order = rows.Select(GraphUserRecords.ToMailFlowRule).OrderBy(r => r.Priority).Select(r => r.Identity).ToArray();
            if (order.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
                return new ApplyResult(ChangeOutcome.Applied, moves.Count == 1 ? "1 move." : $"{moves.Count} moves.");
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "Exchange took every move but still shows another order; refresh the rules in a minute.");
    }

    internal static PsScript ReorderScript(ReorderRules change) => change.Moves() is { Count: > 0 } moves
        ? PsScript.Exchange(Ps.Lines([.. moves.Select(m => Ps.Cmdlet("Set-TransportRule",
                new Dictionary<string, object?> { ["Identity"] = m.Identity, ["Priority"] = m.Priority }))]),
            "Exchange moves one rule at a time and renumbers the others each time, so these run in this order.")
        : PsScript.Exchange("# Nothing to move: the rules are already in this order.");

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
