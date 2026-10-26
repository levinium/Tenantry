using System.Text.Json;
using Tenantry.Core.Changes;

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
