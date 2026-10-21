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
        AutoReplyAudience.None => "Nobody outside the organization",
        AutoReplyAudience.Known => "Only their contacts outside",
        _ => "Everyone outside the organization",
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
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} bytes",
    };
}
