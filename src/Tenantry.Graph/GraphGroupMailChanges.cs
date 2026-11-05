using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>A group's mail settings (G2): Exchange only, for Microsoft 365 groups and lists alike.</summary>
public sealed partial class GraphChangeExecutor
{
    static string GroupIdentity(GroupChange change) => change.Group.Address ?? change.Group.Id;

    static string ReadCmdlet(GroupKind kind) => kind == GroupKind.Microsoft365 ? "Get-UnifiedGroup" : "Get-DistributionGroup";

    /// <summary>The property that reads back a parameter (the welcome switch is named differently when read).</summary>
    internal static string ReadProperty(string parameter) => parameter == "UnifiedGroupWelcomeMessageEnabled" ? "WelcomeMessageEnabled" : parameter;

    /// <summary>Lists need the manager check bypassed, as the admin is changing them from outside.</summary>
    internal static Dictionary<string, object?> GroupMailParameters(SetGroupMailValue change)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = GroupIdentity(change), [change.Parameter] = change.Value?.DeepClone() };
        if (change.Kind != GroupKind.Microsoft365)
            parameters["BypassSecurityGroupManagerCheck"] = true;
        return parameters;
    }

    async Task<ApplyResult> SetGroupMailValueAsync(SetGroupMailValue change, CancellationToken cancellationToken)
    {
        var read = ReadProperty(change.Parameter);
        return await RunCheckedAsync(change.Cmdlet, GroupMailParameters(change),
            ct => Read(ReadCmdlet(change.Kind), new() { ["Identity"] = GroupIdentity(change) }, ct, read),
            rows => rows.Count == 1 && rows[0].TryGetProperty(read, out var shown) && ShowsGroupValue(shown, change.Parameter, change.Value),
            cancellationToken);
    }

    /// <summary>
    /// Whether Exchange now shows what was sent: booleans as booleans, a MailTip as its text (Exchange
    /// wraps it in HTML), a list by its length (Exchange answers with Entra IDs whatever was sent).
    /// </summary>
    internal static bool ShowsGroupValue(JsonElement shown, string parameter, JsonNode? sent) => sent switch
    {
        null => shown.ValueKind is JsonValueKind.Null || (shown.ValueKind == JsonValueKind.String && GroupMail.PlainTip(shown.GetString()) is null)
                || (shown.ValueKind == JsonValueKind.Array && shown.GetArrayLength() == 0),
        JsonArray list => shown.ValueKind == JsonValueKind.Array && shown.GetArrayLength() == list.Count,
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => shown.ValueKind == v.GetValueKind(),
        JsonValue v when parameter == "MailTip" => GroupMail.PlainTip(shown.GetString()) == GroupMail.PlainTip(v.GetValue<string>()),
        JsonValue v => shown.ValueKind == JsonValueKind.String && string.Equals(shown.GetString(), v.ToString(), StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    internal static Dictionary<string, object?> GroupAddressParameters(SetGroupAddress change)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["Identity"] = GroupIdentity(change),
            ["EmailAddresses"] = AddOrRemove(change.New, "smtp:" + change.Address),
        };
        if (change.Kind != GroupKind.Microsoft365)
            parameters["BypassSecurityGroupManagerCheck"] = true;
        return parameters;
    }

    internal static Dictionary<string, object?> GroupPrimaryParameters(SetGroupPrimaryAddress change)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = GroupIdentity(change), ["PrimarySmtpAddress"] = change.New };
        if (change.Kind != GroupKind.Microsoft365)
            parameters["BypassSecurityGroupManagerCheck"] = true;
        return parameters;
    }

    static string SetCmdlet(GroupKind kind) => kind == GroupKind.Microsoft365 ? "Set-UnifiedGroup" : "Set-DistributionGroup";

    async Task<ApplyResult> SetGroupAddressAsync(SetGroupAddress change, CancellationToken cancellationToken)
    {
        var result = await RunCheckedAsync(SetCmdlet(change.Kind), GroupAddressParameters(change),
            ct => Read(ReadCmdlet(change.Kind), new() { ["Identity"] = GroupIdentity(change) }, ct, "EmailAddresses"),
            rows => rows.Count == 1 && rows[0].TryGetProperty("EmailAddresses", out var a) && a.ValueKind == JsonValueKind.Array
                    && a.EnumerateArray().Any(x => string.Equals(x.GetString()?.Split(':', 2).Last(), change.Address, StringComparison.OrdinalIgnoreCase)) == change.New,
            cancellationToken);
        if (result.Succeeded)
            await IntoLocalCopyAsync(sync => sync.RefreshGroupAsync(change.Group.Id, cancellationToken));
        return result;
    }

    async Task<ApplyResult> SetGroupPrimaryAddressAsync(SetGroupPrimaryAddress change, CancellationToken cancellationToken)
    {
        var result = await RunCheckedAsync(SetCmdlet(change.Kind), GroupPrimaryParameters(change),
            ct => Read(ReadCmdlet(change.Kind), new() { ["Identity"] = change.Group.Id }, ct, "PrimarySmtpAddress"),
            rows => rows.Count == 1 && string.Equals(rows[0].GetProperty("PrimarySmtpAddress").GetString(), change.New, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
        if (result.Succeeded)
            await IntoLocalCopyAsync(sync => sync.RefreshGroupAsync(change.Group.Id, cancellationToken));
        return result;
    }

    internal static (string Cmdlet, Dictionary<string, object?> Parameters) GroupSendAsCall(SetGroupSendAs change) =>
        (change.New ? "Add-RecipientPermission" : "Remove-RecipientPermission", new Dictionary<string, object?>
        {
            ["Identity"] = change.Group.Id, ["Trustee"] = change.Person.Address ?? change.Person.Id, ["AccessRights"] = new[] { "SendAs" }, ["Confirm"] = false,
        });

    Task<ApplyResult> SetGroupSendAsAsync(SetGroupSendAs change, CancellationToken cancellationToken)
    {
        var (cmdlet, parameters) = GroupSendAsCall(change);
        return RunCheckedAsync(cmdlet, parameters,
            ct => Read("Get-RecipientPermission", new() { ["Identity"] = change.Group.Id }, ct, "Trustee", "AccessRights"),
            rows => rows.Any(r => Names(r.TryGetProperty("Trustee", out var t) ? t.GetString() : null, change.Person)) == change.New,
            cancellationToken);
    }

    static PsScript? GroupMailScript(ChangeOp change) => change switch
    {
        SetGroupMailValue v => PsScript.Exchange(Ps.Cmdlet(v.Cmdlet, GroupMailParameters(v))),
        SetGroupAddress a => PsScript.Exchange(Ps.Cmdlet(SetCmdlet(a.Kind), GroupAddressParameters(a))),
        SetGroupPrimaryAddress p => PsScript.Exchange(Ps.Cmdlet(SetCmdlet(p.Kind), GroupPrimaryParameters(p))),
        SetGroupSendAs s => GroupSendAsCall(s) is var (cmdlet, parameters) ? PsScript.Exchange(Ps.Cmdlet(cmdlet, parameters)) : null,
        _ => null,
    };
}

public sealed partial class GraphUserRecords
{
    static readonly string[] GroupMailSelect =
    [
        "PrimarySmtpAddress", "EmailAddresses", "HiddenFromAddressListsEnabled", "RequireSenderAuthenticationEnabled", "AcceptMessagesOnlyFromSendersOrMembers",
        "RejectMessagesFromSendersOrMembers", "ModerationEnabled", "ModeratedBy", "BypassModerationFromSendersOrMembers", "SendModerationNotifications",
        "MailTip", "GrantSendOnBehalfTo",
    ];

    static readonly string[] UnifiedSelect = ["HiddenFromExchangeClientsEnabled", "AutoSubscribeNewMembers", "AlwaysSubscribeMembersToCalendarEvents", "WelcomeMessageEnabled"];

    /// <summary>The group's mail settings, and who can send as it, read together.</summary>
    public async Task<GroupMail> GetGroupMailAsync(GroupSummary group, CancellationToken cancellationToken = default)
    {
        if (exchange is null)
            throw new InvalidOperationException("A group's mail settings come from Exchange Online, which isn't connected for this tenant.");
        var unified = group.Kind == GroupKind.Microsoft365;
        var who = new Dictionary<string, object?> { ["Identity"] = group.Id };
        var settingsRead = exchange.InvokeAsync(CmdletEndpoint.Exchange, unified ? "Get-UnifiedGroup" : "Get-DistributionGroup", who, cancellationToken,
            unified ? [.. GroupMailSelect, .. UnifiedSelect] : GroupMailSelect);
        var sendAsRead = exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-RecipientPermission", who, cancellationToken, ["Trustee", "AccessRights"]);
        var rows = await settingsRead;
        if (rows.Count == 0)
            throw new InvalidOperationException("Exchange didn't find this group. A new group can take a minute to reach Exchange.");
        var r = rows[0];
        string[] List(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(x => x.GetString()).OfType<string>()]
            : [];
        bool Flag(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        bool? Maybe(string name) => unified ? Flag(name) : null;
        string[] sendAs = [];
        try
        {
            sendAs = [.. (await sendAsRead)
                .Where(p => p.TryGetProperty("AccessRights", out var a) && a.ToString().Contains("SendAs", StringComparison.Ordinal))
                .Select(p => p.TryGetProperty("Trustee", out var t) ? t.GetString() : null)
                .OfType<string>()
                .Where(t => !t.StartsWith("NT AUTHORITY", StringComparison.OrdinalIgnoreCase))];
        }
        catch (CmdletException)
        {
            // Send As stays unknown-empty rather than failing the whole read.
        }
        return new GroupMail(
            r.TryGetProperty("PrimarySmtpAddress", out var p) ? p.GetString() : group.Mail,
            List("EmailAddresses"),
            Flag("HiddenFromAddressListsEnabled"),
            !Flag("RequireSenderAuthenticationEnabled"),
            List("AcceptMessagesOnlyFromSendersOrMembers"),
            List("RejectMessagesFromSendersOrMembers"),
            Flag("ModerationEnabled"),
            List("ModeratedBy"),
            List("BypassModerationFromSendersOrMembers"),
            r.TryGetProperty("SendModerationNotifications", out var n) ? n.GetString() ?? "Always" : "Always",
            GroupMail.PlainTip(r.TryGetProperty("MailTip", out var tip) && tip.ValueKind == JsonValueKind.String ? tip.GetString() : null),
            sendAs,
            List("GrantSendOnBehalfTo"),
            Maybe("HiddenFromExchangeClientsEnabled"),
            Maybe("AutoSubscribeNewMembers"),
            Maybe("AlwaysSubscribeMembersToCalendarEvents"),
            Maybe("WelcomeMessageEnabled"));
    }
}
