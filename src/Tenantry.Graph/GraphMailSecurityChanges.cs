using System.Text.Json;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>Mail &amp; security changes (5a): releasing or deleting quarantined mail, blocking or allowing senders.</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>Microsoft lets an allowed sender stay this long after its last use; blocks have no end.</summary>
    public const int AllowedSenderDays = 45;

    Task<IReadOnlyList<JsonElement>> ReadQuarantined(string identity, CancellationToken ct) =>
        Read("Get-QuarantineMessage", new() { ["Identity"] = identity }, ct);

    Task<ApplyResult> ReleaseQuarantinedAsync(ReleaseQuarantined change, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = change.Message.Id, ["ReleaseToAll"] = true, ["Confirm"] = false };
        if (change.AllowSender)
            parameters["AllowSender"] = true;
        return RunCheckedAsync("Release-QuarantineMessage", parameters,
            ct => ReadQuarantined(change.Message.Id, ct),
            rows => rows.Count == 1 && rows[0].TryGetProperty("ReleaseStatus", out var s) && s.ValueKind == JsonValueKind.String
                    && s.GetString()!.Equals("RELEASED", StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

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

    Task<ApplyResult> AddListEntryAsync(AddListEntry change, CancellationToken cancellationToken)
    {
        var value = change.Entry.Name;
        string cmdlet;
        Dictionary<string, object?> parameters;
        if (change.Kind == ListKind.Spoof)
        {
            cmdlet = "New-TenantAllowBlockListSpoofItems";
            parameters = new()
            {
                ["Identity"] = "Default", ["SpoofedUser"] = value, ["SendingInfrastructure"] = change.Infrastructure,
                ["SpoofType"] = change.SpoofType ?? "External", ["Action"] = change.Block ? "Block" : "Allow",
            };
        }
        else
        {
            cmdlet = "New-TenantAllowBlockListItems";
            parameters = new() { ["ListType"] = AllowBlock.ListType(change.Kind), ["Entries"] = new[] { value } };
            parameters[change.Block ? "Block" : "Allow"] = true;
            AddExpiry(parameters, change.Expiry);
            if (change.Notes is { Length: > 0 } notes)
                parameters["Notes"] = notes;
        }
        return RunCheckedAsync(cmdlet, parameters, ct => ReadEntry(change.Kind, value, ct),
            rows => rows.Any(r => Matches(r, change.Kind, value) && r.TryGetProperty("Action", out var a)
                                  && string.Equals(a.GetString(), change.Block ? "Block" : "Allow", StringComparison.OrdinalIgnoreCase)),
            cancellationToken);
    }

    Task<ApplyResult> RemoveListEntryAsync(RemoveListEntry change, CancellationToken cancellationToken) =>
        change.Kind == ListKind.Spoof
            ? RunCheckedAsync("Remove-TenantAllowBlockListSpoofItems", new() { ["Identity"] = "Default", ["Ids"] = new[] { change.Identity } },
                ct => ReadEntry(change.Kind, change.Entry.Name, ct), rows => !rows.Any(r => Matches(r, change.Kind, change.Entry.Name)), cancellationToken)
            : RunCheckedAsync("Remove-TenantAllowBlockListItems", new() { ["ListType"] = AllowBlock.ListType(change.Kind), ["Ids"] = new[] { change.Identity } },
                ct => ReadEntry(change.Kind, change.Entry.Name, ct), rows => rows.Count == 0, cancellationToken);

    Task<ApplyResult> UpdateListEntryAsync(UpdateListEntry change, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?> { ["ListType"] = AllowBlock.ListType(change.Kind), ["Ids"] = new[] { change.Identity } };
        if (change.NewExpiry != change.OldExpiry)
            AddExpiry(parameters, change.NewExpiry);
        if ((change.NewNotes ?? "") != (change.OldNotes ?? ""))
            parameters["Notes"] = change.NewNotes ?? "";
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

    Task<ApplyResult> SetSenderListedAsync(SetSenderListed change, CancellationToken cancellationToken)
    {
        var entry = change.Sender.Address ?? change.Sender.Id;
        var parameters = new Dictionary<string, object?> { ["ListType"] = "Sender", ["Entries"] = new[] { entry } };
        parameters[change.Block ? "Block" : "Allow"] = true;
        if (change.Block)
            parameters["NoExpiration"] = true;
        else
            parameters["RemoveAfter"] = change.RemoveAfterDays ?? AllowedSenderDays;
        if (change.Notes is { Length: > 0 } notes)
            parameters["Notes"] = notes;
        return RunCheckedAsync("New-TenantAllowBlockListItems", parameters,
            ct => Read("Get-TenantAllowBlockListItems", new() { ["ListType"] = "Sender", ["Entry"] = entry }, ct),
            rows => rows.Any(r => r.TryGetProperty("Action", out var a) && a.ValueKind == JsonValueKind.String
                                  && a.GetString()!.Equals(change.Block ? "Block" : "Allow", StringComparison.OrdinalIgnoreCase)),
            cancellationToken);
    }
}
