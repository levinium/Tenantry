using System.Globalization;
using System.Text.RegularExpressions;

namespace Tenantry.Core.Directory;

public enum AutoReplyState { Off, On, Scheduled }

/// <summary>Who outside the organization gets the automatic reply (Exchange: None, Known, All).</summary>
public enum AutoReplyAudience { None, Known, All }

/// <summary>A mailbox's automatic replies. Times are only used when <see cref="State"/> is Scheduled.</summary>
public sealed record AutoReply(AutoReplyState State, DateTimeOffset? Start, DateTimeOffset? End, string Internal, string External, AutoReplyAudience Audience)
{
    public static readonly AutoReply Off = new(AutoReplyState.Off, null, null, "", "", AutoReplyAudience.All);

    /// <summary>"Off", "On", or "Scheduled: Oct 22, 9:00 AM – Oct 30, 5:00 PM".</summary>
    public string Describe() => State switch
    {
        AutoReplyState.Off => "Off",
        AutoReplyState.On => "On",
        _ => $"Scheduled: {Format(Start)} – {Format(End)}",
    };

    static string Format(DateTimeOffset? at) => at?.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture) ?? "?";

    public static string AudienceName(AutoReplyAudience audience) => audience switch
    {
        AutoReplyAudience.None => "Nobody Outside the Organization",
        AutoReplyAudience.Known => "Only Their Contacts Outside",
        _ => "Everyone Outside the Organization",
    };

    /// <summary>Exchange stores replies as HTML; this is the text a person typed.</summary>
    public static string PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        // Line breaks in HTML source are just spacing (Exchange adds them around its tags); the
        // real breaks are <br>, </p> and </div>.
        var text = Regex.Replace(html, @"\r?\n", " ");
        text = Regex.Replace(text, @"<\s*(br|/p|/div)\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t ]*\n[ \t ]*", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return Regex.Replace(text, @"[ \t]{2,}", " ").Trim();
    }
}

/// <summary>
/// The ways a mailbox can be reached, each on or off (Get-CASMailbox). SMTP sending (client
/// SMTP authentication) can be left to the organization's default, given in
/// <see cref="OrgSmtpSendOn"/>.
/// </summary>
public sealed record EmailApps(bool Owa, bool Outlook, bool Mobile, bool Pop, bool Imap, bool Ews, bool? SmtpSend, bool OrgSmtpSendOn)
{
    public bool SmtpSendEffective => SmtpSend ?? OrgSmtpSendOn;

    /// <summary>The Set-CASMailbox parameter behind each app, with how it reads to a person.</summary>
    public static readonly IReadOnlyList<(string Parameter, string Name, string Explanation)> All =
    [
        ("OWAEnabled", "Outlook on the Web", "Mail in a browser"),
        ("MAPIEnabled", "Outlook Desktop", "The Outlook app on Windows and Mac"),
        ("ActiveSyncEnabled", "Mobile Mail", "Phones' built-in mail apps (Exchange ActiveSync)"),
        ("EwsEnabled", "Exchange Web Services", "Older apps and add-ins that connect through EWS"),
        ("PopEnabled", "POP", "Older mail apps; often signs in without multi-factor sign-in"),
        ("ImapEnabled", "IMAP", "Older mail apps; often signs in without multi-factor sign-in"),
        ("SmtpClientAuthenticationDisabled", "SMTP Sending", "Devices and apps that send mail by signing in with a password (scanners, older apps)"),
    ];

    public bool IsOn(string parameter) => parameter switch
    {
        "OWAEnabled" => Owa,
        "MAPIEnabled" => Outlook,
        "ActiveSyncEnabled" => Mobile,
        "EwsEnabled" => Ews,
        "PopEnabled" => Pop,
        "ImapEnabled" => Imap,
        _ => SmtpSendEffective,
    };
}

/// <summary>
/// A mailbox's settings beyond the everyday ones (the Mail tab's 4b-2 part): limits, who may send
/// to it, email apps, archive, litigation hold and retention. Read live from Exchange.
/// </summary>
/// <param name="RecipientLimit">Most recipients in one message ("500" or "Unlimited", as Exchange says it).</param>
/// <param name="AllowedSenders">When not empty, only these people and groups may send to it (as Exchange names them).</param>
/// <param name="HoldDays">How long held items are kept; null means indefinitely.</param>
/// <param name="RetentionPolicies">The tenant's retention policies, to choose from.</param>
public sealed record MailboxSettings(
    string RecipientLimit,
    long? MaxSendBytes,
    long? MaxReceiveBytes,
    bool OnlyInternalSenders,
    IReadOnlyList<string> AllowedSenders,
    EmailApps Apps,
    bool ArchiveOn,
    bool AutoExpandingArchive,
    long? ArchiveUsedBytes,
    bool HoldOn,
    int? HoldDays,
    string? HoldNote,
    DateTimeOffset? HoldSince,
    string? HoldBy,
    string? RetentionPolicy,
    IReadOnlyList<string> RetentionPolicies);

/// <summary>
/// A mailbox's everyday settings as the Mail tab reads them live from Exchange: addresses,
/// forwarding, automatic replies, address-list visibility and storage against the quota.
/// </summary>
/// <param name="ForwardingIsRecipient">Forwarding set to a directory recipient (ForwardingAddress)
/// rather than an SMTP address; shown as is, replaced when edited.</param>
/// <param name="Domains">The tenant's domains, for new aliases.</param>
public sealed record MailDetails(
    string PrimaryAddress,
    IReadOnlyList<string> Aliases,
    string? ForwardingTo,
    bool ForwardingIsRecipient,
    bool KeepCopy,
    bool HiddenFromAddressList,
    AutoReply AutoReply,
    long? UsedBytes,
    long? QuotaBytes,
    long? WarningBytes,
    long? Items,
    IReadOnlyList<string> Domains)
{
    /// <summary>Limits, email apps, archive, hold and retention (null where they couldn't be read).</summary>
    public MailboxSettings? Settings { get; init; }

    /// <summary>Used / quota as a fraction, when both are known (an unlimited quota gives null).</summary>
    public double? UsedFraction => UsedBytes is { } used && QuotaBytes is { } quota && quota > 0 ? (double)used / quota : null;

    /// <summary>
    /// Exchange gives sizes as text: "1.234 GB (1,325,000,000 bytes)", or "Unlimited". Returns
    /// the byte count, or null when unlimited or unreadable.
    /// </summary>
    public static long? ParseBytes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = Regex.Match(text, @"\(([\d,\.\s]+)\s*bytes\)", RegexOptions.IgnoreCase);
        var digits = match.Success ? match.Groups[1].Value : text;
        digits = new string(digits.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : null;
    }

    /// <summary>"3.1 GB", "820 MB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.##} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} bytes",
    };
}
