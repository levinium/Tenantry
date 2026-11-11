using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tenantry.Core.Directory;

/// <summary>How a setting is edited.</summary>
public enum SettingKind { OnOff, Number, Choice, List }

/// <summary>
/// One Exchange setting the app can show and change: where it's read and written (a Get-/Set- pair,
/// with the object's Identity when it belongs to one), what it's called, what it does, and whether
/// "on" in Exchange means "on" to a person (<see cref="Inverted"/>: DisablePlusAddress… reads as
/// "plus addressing on" when false).
/// </summary>
/// <param name="Group">The card it sits on ("Sending", "Outlook"…).</param>
/// <param name="Choices">Value → words, for a choice.</param>
/// <param name="Caution">Turning it this way takes something away: the change asks to confirm.</param>
public sealed record SettingDef(string Group, string Key, string Label, string Help, string GetCmdlet, string SetCmdlet, string Parameter,
    SettingKind Kind = SettingKind.OnOff, bool Inverted = false, IReadOnlyList<(string Value, string Label)>? Choices = null,
    bool? CautionWhen = null, int Min = 0, int Max = int.MaxValue, string? Unit = null, bool? DefaultOn = null, JsonNode? Recommended = null)
{
    /// <summary>Microsoft's recommendation in words, when there is one ("Microsoft's Standard: Quarantine").</summary>
    public string? RecommendedText => Recommended is null ? null : Words(Recommended);

    /// <summary>The value differs from Microsoft's recommendation.</summary>
    public bool Differs(JsonNode? value) => Recommended is not null && value is not null && !JsonNode.DeepEquals(value, Recommended);

    /// <summary>The setting's value as the app shows it, from Exchange's row; null when Exchange leaves it unset.</summary>
    public JsonNode? Read(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(Parameter, out var v))
            return null;
        return Kind switch
        {
            SettingKind.OnOff => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? JsonValue.Create((v.ValueKind == JsonValueKind.True) != Inverted) : null,
            SettingKind.Number => v.ValueKind == JsonValueKind.Number ? JsonValue.Create(v.GetInt32()) : null,
            SettingKind.List => v.ValueKind == JsonValueKind.Array
                ? new JsonArray([.. v.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : i.ToString()).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => (JsonNode?)JsonValue.Create(i))])
                : v.ValueKind == JsonValueKind.Null ? new JsonArray() : null,
            _ => v.ValueKind == JsonValueKind.String ? JsonValue.Create(v.GetString()) : v.ValueKind == JsonValueKind.Number ? JsonValue.Create(v.ToString()) : null,
        };
    }

    /// <summary>What Exchange is sent for the value as shown.</summary>
    public object? ToExchange(JsonNode? value) => Kind switch
    {
        SettingKind.OnOff => value?.GetValue<bool>() is { } on ? on != Inverted : null,
        SettingKind.Number => value?.GetValue<int>(),
        SettingKind.List => value is JsonArray items ? items.Select(i => i!.GetValue<string>()).ToArray() : Array.Empty<string>(),
        _ => value?.GetValue<string>(),
    };

    public string Words(JsonNode? value) => value is null
        ? DefaultOn is { } d ? $"Not set (Microsoft's default: {(d ? "on" : "off")})" : "Not set (Microsoft's default)"
        : Kind switch
    {
        SettingKind.OnOff => value.GetValue<bool>() ? "On" : "Off",
        SettingKind.Number => $"{value.GetValue<int>():N0}{(Unit is null ? "" : " " + Unit)}",
        SettingKind.List => value is JsonArray { Count: > 0 } items
            ? items.Count <= 3 ? string.Join(", ", items.Select(i => i!.GetValue<string>())) : $"{items.Count:N0} entries"
            : "None",
        _ => Choices?.FirstOrDefault(c => c.Value.Equals(value.GetValue<string>(), StringComparison.OrdinalIgnoreCase)).Label ?? value.GetValue<string>(),
    };

    public bool IsCaution(JsonNode? value) => CautionWhen is { } when && Kind == SettingKind.OnOff && value?.GetValue<bool>() == when;
}

/// <summary>
/// The organization-wide mail settings Tenantry offers, grouped as the Mail Settings page shows
/// them. Each is a plain Get-/Set- parameter, so adding one is a line here.
/// </summary>
public static class OrgMailSettings
{
    const string Org = "Get-OrganizationConfig";
    const string SetOrg = "Set-OrganizationConfig";
    const string Transport = "Get-TransportConfig";
    const string SetTransport = "Set-TransportConfig";

    public static IReadOnlyList<SettingDef> All { get; } =
    [
        new("Safety", "externaltag", "Mark Mail from Outside in Outlook",
            "Outlook shows an \"External\" tag on mail from outside the organization, so people think twice about it.", "Get-ExternalInOutlook", "Set-ExternalInOutlook", "Enabled"),
        new("Safety", "smtpauth", "Let Apps and Devices Sign In to Send (SMTP AUTH)",
            "Older apps, printers and scanners that send mail with a username and password. Off blocks that for everyone except those turned on one by one; attackers use it to send spam from stolen passwords.",
            Transport, SetTransport, "SmtpClientAuthenticationDisabled", Inverted: true),
        new("Safety", "replyallstorm", "Stop Reply-All Storms",
            "When many people reply-all to a big list at once, Exchange blocks further reply-alls to it for a while.", Transport, SetTransport, "ReplyAllStormProtectionEnabled", CautionWhen: false),
        new("Safety", "ews", "Exchange Web Services (EWS)",
            "An older way apps read and send mail. Some old apps still need it; Microsoft is retiring it for Exchange Online.", Org, SetOrg, "EwsEnabled", CautionWhen: false),
        new("Safety", "audit", "Mailbox Auditing",
            "Records who opened, sent or deleted what in each mailbox. Needed to investigate a hacked account.", Org, SetOrg, "AuditDisabled", Inverted: true, CautionWhen: false),

        new("Sending", "plus", "Plus Addressing",
            "Mail to name+anything@your domain reaches name@. People use it to sign up to sites and filter what comes back.", Org, SetOrg, "DisablePlusAddressInRecipients", Inverted: true),
        new("Sending", "sendfromalias", "Send from Aliases",
            "People can send from any of their addresses, not only their main one.", Org, SetOrg, "SendFromAliasEnabled"),
        new("Sending", "recall", "Message Recall",
            "People can recall a message they sent to someone in the organization.", Org, SetOrg, "MessageRecallEnabled", DefaultOn: true),
        new("Sending", "recallread", "Recall Messages Already Read",
            "Recall works even when the recipient has opened it.", Org, SetOrg, "RecallReadMessagesEnabled", DefaultOn: true),
        new("Sending", "readtracking", "Read Status in Message Trace",
            "Message trace can show whether a message was read.", Org, SetOrg, "ReadTrackingEnabled"),

        new("Outlook", "tipsall", "MailTips",
            "Outlook warns before sending: an out-of-office reply, a full mailbox, a big list.", Org, SetOrg, "MailTipsAllTipsEnabled"),
        new("Outlook", "tipsexternal", "MailTip for Outside Recipients",
            "Outlook warns when a message is going to someone outside the organization.", Org, SetOrg, "MailTipsExternalRecipientsTipsEnabled"),
        new("Outlook", "tipslarge", "Warn When a List Is Bigger Than",
            "Outlook warns before sending to a list with more members than this.", Org, SetOrg, "MailTipsLargeAudienceThreshold", SettingKind.Number, Min: 1, Max: 10000, Unit: "people"),
        new("Outlook", "focused", "Focused Inbox",
            "Outlook splits the inbox into Focused and Other. Each person can still turn it off.", Org, SetOrg, "FocusedInboxOn", DefaultOn: true),
        new("Outlook", "linkpreview", "Link Previews",
            "Outlook on the web shows a preview of links in messages.", Org, SetOrg, "LinkPreviewEnabled"),
        new("Outlook", "actionable", "Actionable Messages",
            "Messages with buttons that act in other apps (approvals, surveys).", Org, SetOrg, "SmtpActionableMessagesEnabled"),
        new("Outlook", "connectors", "Connectors in Outlook",
            "Office 365 connectors that post updates from other services into mailboxes and groups.", Org, SetOrg, "ConnectorsEnabled"),
        new("Outlook", "bookings", "Bookings",
            "People can publish booking pages for appointments.", Org, SetOrg, "BookingsEnabled"),
    ];

    /// <summary>The Get- cmdlets to read, once each.</summary>
    public static IEnumerable<string> Reads => All.Select(s => s.GetCmdlet).Distinct();
}

/// <summary>The per-domain settings on a remote domain (Set-RemoteDomain).</summary>
public static class RemoteDomainSettings
{
    public static IReadOnlyList<SettingDef> All { get; } =
    [
        new("Replies", "autoreply", "Out-of-Office Replies", "Send people's automatic replies to this domain.", "Get-RemoteDomain", "Set-RemoteDomain", "AutoReplyEnabled"),
        new("Replies", "ooftype", "Which Automatic Replies", "Which of a person's automatic replies go out: the one for outside senders, or the one for inside too.",
            "Get-RemoteDomain", "Set-RemoteDomain", "AllowedOOFType", SettingKind.Choice,
            Choices: [("External", "The Reply for Outside Senders"), ("InternalLegacy", "Inside and Outside Replies"), ("ExternalLegacy", "Outside Reply (Older Clients)"), ("None", "None")]),
        new("Forwarding", "autoforward", "Automatic Forwarding",
            "Let people's forwarding rules and settings send mail to this domain. The outbound spam policy can still block it.", "Get-RemoteDomain", "Set-RemoteDomain", "AutoForwardEnabled",
            CautionWhen: true),
        new("Reports", "ndr", "Bounce Messages (NDRs)", "Send bounce messages to senders at this domain.", "Get-RemoteDomain", "Set-RemoteDomain", "NDREnabled"),
        new("Reports", "ndrdiag", "Technical Details in Bounces", "Include server details in bounce messages.", "Get-RemoteDomain", "Set-RemoteDomain", "NDRDiagnosticInfoEnabled"),
        new("Reports", "delivery", "Delivery Reports", "Send delivery and read receipts to this domain.", "Get-RemoteDomain", "Set-RemoteDomain", "DeliveryReportEnabled"),
        new("Reports", "meetingfwd", "Meeting Forward Notices", "Tell organizers at this domain when someone forwards their meeting.", "Get-RemoteDomain", "Set-RemoteDomain", "MeetingForwardNotificationEnabled"),
        new("Format", "tnef", "Rich Text (TNEF)", "Send Outlook rich text (winmail.dat). Off is safest for mail systems that aren't Outlook.",
            "Get-RemoteDomain", "Set-RemoteDomain", "TNEFEnabled"),
        new("Format", "content", "Message Format", "The format messages go out in.", "Get-RemoteDomain", "Set-RemoteDomain", "ContentType", SettingKind.Choice,
            Choices: [("MimeHtmlText", "HTML and Plain Text"), ("MimeHtml", "HTML"), ("MimeText", "Plain Text")]),
    ];
}

/// <summary>A remote domain: settings for mail to one outside domain (or "*" for every other one).</summary>
public sealed record RemoteDomain(string Name, string DomainName, IReadOnlyDictionary<string, JsonNode?> Values)
{
    public bool IsDefault => Name.Equals("Default", StringComparison.OrdinalIgnoreCase) || DomainName == "*";
}

/// <summary>An inbound or outbound connector, in words.</summary>
public sealed record MailConnector(string Name, bool Inbound, bool Enabled, string Kind, string? Comment, IReadOnlyList<string> Facts, DateTimeOffset? Changed)
{
    public string Direction => Inbound ? "Into Microsoft 365" : "Out of Microsoft 365";
}

/// <summary>A sender Microsoft's spoof intelligence has seen sending as another domain.</summary>
public sealed record SpoofedSender(string SpoofedUser, string Infrastructure, int Messages, DateTimeOffset? LastSeen, string SpoofType, string Action)
{
    public bool IsAllowed => Action.Equals("Allow", StringComparison.OrdinalIgnoreCase);
    public bool IsInternal => SpoofType.Equals("Internal", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A journal rule: copies of matching mail sent to an outside archive.</summary>
public sealed record JournalRule(string Name, bool Enabled, string Scope, string? Recipient, string JournalAddress);

/// <summary>An accepted domain: one the organization receives mail for.</summary>
public sealed record AcceptedDomain(string Name, string DomainName, string Type, bool IsDefault, bool MatchSubDomains, bool SendingDisabled);

/// <summary>Exchange's organization settings, remote domains, connectors, spoofing and journaling (12e).</summary>
public interface IExchangeOrgRecords
{
    /// <summary>Each Get- cmdlet's single row, by cmdlet name.</summary>
    Task<IReadOnlyDictionary<string, JsonElement>> GetOrgSettingsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteDomain>> GetRemoteDomainsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailConnector>> GetConnectorsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SpoofedSender>> GetSpoofedSendersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JournalRule>> GetJournalRulesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AcceptedDomain>> GetAcceptedDomainsAsync(CancellationToken cancellationToken = default);
}
