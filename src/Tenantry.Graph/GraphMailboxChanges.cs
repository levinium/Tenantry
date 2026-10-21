using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>Changes to what's inside a mailbox (4b-3): inbox rules, folder sharing, devices, deleted items, mailbox type.</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>
    /// Runs a cmdlet, then reads back with <paramref name="read"/> until <paramref name="done"/>
    /// holds (Exchange can take a few seconds to show a change). A read that says the object is
    /// gone counts as an empty answer, for removals.
    /// </summary>
    async Task<ApplyResult> RunCheckedAsync(string cmdlet, Dictionary<string, object?> parameters,
        Func<CancellationToken, Task<IReadOnlyList<JsonElement>>>? read, Func<IReadOnlyList<JsonElement>, bool> done, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        try
        {
            await exchange.InvokeAsync(CmdletEndpoint.Exchange, cmdlet, parameters, cancellationToken);
        }
        catch (CmdletException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, e.Explanation);
        }
        if (read is null)
            return new ApplyResult(ChangeOutcome.Applied);
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            IReadOnlyList<JsonElement> rows;
            try
            {
                rows = await read(cancellationToken);
            }
            catch (CmdletException e) when (e.IsNotFound)
            {
                rows = [];
            }
            if (done(rows))
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible,
            "Exchange accepted the change but still shows the old state; it usually appears within a minute.");
    }

    Task<IReadOnlyList<JsonElement>> Read(string cmdlet, Dictionary<string, object?> parameters, CancellationToken cancellationToken, params string[] select) =>
        exchange!.InvokeAsync(CmdletEndpoint.Exchange, cmdlet, parameters, cancellationToken, select);

    Task<ApplyResult> SetInboxRuleEnabledAsync(SetInboxRuleEnabled change, CancellationToken cancellationToken) =>
        RunCheckedAsync(change.New ? "Enable-InboxRule" : "Disable-InboxRule", new() { ["Identity"] = change.RuleIdentity, ["Confirm"] = false },
            ct => Read("Get-InboxRule", new() { ["Identity"] = change.RuleIdentity }, ct, "Enabled"),
            rows => rows.Count == 1 && rows[0].TryGetProperty("Enabled", out var on) && on.ValueKind == (change.New ? JsonValueKind.True : JsonValueKind.False),
            cancellationToken);

    Task<ApplyResult> RemoveInboxRuleAsync(RemoveInboxRule change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Remove-InboxRule", new() { ["Identity"] = change.RuleIdentity, ["Confirm"] = false },
            ct => Read("Get-InboxRule", new() { ["Identity"] = change.RuleIdentity }, ct, "Name"),
            rows => rows.Count == 0, cancellationToken);

    /// <summary>Add-, Set- or Remove-MailboxFolderPermission, depending on whether the person had an entry and keeps one.</summary>
    Task<ApplyResult> SetFolderPermissionAsync(SetFolderPermission change, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = change.FolderIdentity, ["User"] = change.Person };
        string cmdlet;
        if (change.New is null)
        {
            cmdlet = "Remove-MailboxFolderPermission";
            parameters["Confirm"] = false;
        }
        else
        {
            cmdlet = change.Old is null ? "Add-MailboxFolderPermission" : "Set-MailboxFolderPermission";
            parameters["AccessRights"] = new[] { change.New };
        }
        return RunCheckedAsync(cmdlet, parameters,
            ct => Read("Get-MailboxFolderPermission", new() { ["Identity"] = change.FolderIdentity, ["User"] = change.Person }, ct, "AccessRights"),
            rows => change.New is null
                ? rows.Count == 0
                : rows.Count == 1 && rows[0].TryGetProperty("AccessRights", out var rights) && rights.ValueKind == JsonValueKind.Array
                  && rights.EnumerateArray().Any(r => string.Equals(r.GetString(), change.New, StringComparison.OrdinalIgnoreCase)),
            cancellationToken);
    }

    /// <summary>
    /// Wiping and removing go to the device partnership; blocking and unblocking are the
    /// mailbox's list of blocked device IDs (Set-CASMailbox), read back from that list.
    /// </summary>
    Task<ApplyResult> SetMobileDeviceAsync(SetMobileDevice change, CancellationToken cancellationToken)
    {
        var identity = change.User.Address ?? change.User.Id;
        switch (change.Action)
        {
            case DeviceAction.Wipe:
            case DeviceAction.WipeAccountOnly:
            {
                var parameters = new Dictionary<string, object?> { ["Identity"] = change.DeviceIdentity, ["Confirm"] = false };
                if (change.Action == DeviceAction.WipeAccountOnly)
                    parameters["AccountOnly"] = true;
                // The device confirms only when it next connects, so there's nothing to read back yet.
                return RunCheckedAsync("Clear-MobileDevice", parameters, null, _ => true, cancellationToken);
            }
            case DeviceAction.Remove:
                return RunCheckedAsync("Remove-MobileDevice", new() { ["Identity"] = change.DeviceIdentity, ["Confirm"] = false },
                    ct => Read("Get-MobileDevice", new() { ["Identity"] = change.DeviceIdentity }, ct, "Identity"),
                    rows => rows.Count == 0, cancellationToken);
            default:
            {
                var block = change.Action == DeviceAction.Block;
                return RunCheckedAsync("Set-CASMailbox",
                    new() { ["Identity"] = identity, ["ActiveSyncBlockedDeviceIDs"] = AddOrRemove(block, change.DeviceId) },
                    ct => Read("Get-CASMailbox", new() { ["Identity"] = identity }, ct, "ActiveSyncBlockedDeviceIDs"),
                    rows => rows.Count == 1 && (rows[0].TryGetProperty("ActiveSyncBlockedDeviceIDs", out var list) && list.ValueKind == JsonValueKind.Array
                        && list.EnumerateArray().Any(d => string.Equals(d.GetString(), change.DeviceId, StringComparison.OrdinalIgnoreCase))) == block,
                    cancellationToken);
            }
        }
    }

    /// <summary>Restore-RecoverableItems puts the item back in the folder it was deleted from; gone from the list means done.</summary>
    Task<ApplyResult> RestoreDeletedItemAsync(RestoreDeletedItem change, CancellationToken cancellationToken)
    {
        var identity = change.User.Address ?? change.User.Id;
        return RunCheckedAsync("Restore-RecoverableItems", new() { ["Identity"] = identity, ["EntryID"] = change.EntryId },
            ct => Read("Get-RecoverableItems", new() { ["Identity"] = identity, ["EntryID"] = change.EntryId }, ct),
            rows => rows.Count == 0, cancellationToken);
    }

    /// <summary>
    /// Converting shows slowly: live, a user mailbox read as shared after a second, but back as a
    /// user mailbox only after 15. Read back for up to a minute before calling it "not yet visible".
    /// </summary>
    static readonly TimeSpan[] ConvertWaits = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>Set-Mailbox -Type; read back by the mailbox's kind, into the local copy so the lists follow.</summary>
    Task<ApplyResult> ConvertMailboxAsync(ConvertMailbox change, CancellationToken cancellationToken) =>
        SetMailboxAsync(change.User, new() { ["Type"] = change.NewShared ? "Shared" : "Regular" },
            row => string.Equals(Text(row, "RecipientTypeDetails"), change.NewShared ? "SharedMailbox" : "UserMailbox", StringComparison.OrdinalIgnoreCase),
            cancellationToken, waits: ConvertWaits);
}
