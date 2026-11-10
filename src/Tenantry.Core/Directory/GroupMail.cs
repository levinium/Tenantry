namespace Tenantry.Core.Directory;

/// <summary>
/// A mail-enabled group's mail settings, read live from Exchange (Get-UnifiedGroup or
/// Get-DistributionGroup, plus Get-RecipientPermission). Exchange lists people (senders,
/// moderators, delegates) by their Entra object ID (measured live).
/// </summary>
/// <param name="Addresses">Every address as Exchange lists it ("SMTP:primary", "smtp:alias", "SPO:…").</param>
/// <param name="OutsideSenders">Mail from outside the organization is accepted (RequireSenderAuthenticationEnabled off).</param>
/// <param name="AcceptFrom">Only these may send to it (empty: anyone allowed by <see cref="OutsideSenders"/>).</param>
/// <param name="ModerationNotices">Always, Internal or Never (lists only).</param>
/// <param name="SendAs">Trustees with Send As, as Exchange names them (a sign-in name or address).</param>
/// <param name="HiddenFromOutlook">Microsoft 365 groups only (null for lists), as are the subscription settings.</param>
/// <param name="JoinRestriction">Lists only: Open, Closed or ApprovalRequired (whether people can join by themselves).</param>
/// <param name="DepartRestriction">Lists only: Open or Closed (whether people can leave by themselves).</param>
public sealed record GroupMail(
    string? PrimaryAddress,
    IReadOnlyList<string> Addresses,
    bool Hidden,
    bool OutsideSenders,
    IReadOnlyList<string> AcceptFrom,
    IReadOnlyList<string> RejectFrom,
    bool Moderated,
    IReadOnlyList<string> Moderators,
    IReadOnlyList<string> BypassModeration,
    string ModerationNotices,
    string? MailTip,
    IReadOnlyList<string> SendAs,
    IReadOnlyList<string> SendOnBehalf,
    bool? HiddenFromOutlook = null,
    bool? AutoSubscribe = null,
    bool? CalendarEvents = null,
    bool? WelcomeMessage = null,
    string? JoinRestriction = null,
    string? DepartRestriction = null)
{
    /// <summary>The extra SMTP addresses (not the primary, not SharePoint's SPO: entries).</summary>
    public IReadOnlyList<string> Aliases => Addresses
        .Where(a => a.StartsWith("smtp:", StringComparison.Ordinal) && !string.Equals(a[5..], PrimaryAddress, StringComparison.OrdinalIgnoreCase))
        .Select(a => a[5..])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>
    /// The MailTip as plain text: Exchange stores it wrapped in HTML ("&lt;html&gt;&lt;body&gt;Text…",
    /// seen live), so the tags and surrounding whitespace are dropped.
    /// </summary>
    public static string? PlainTip(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        var text = System.Text.RegularExpressions.Regex.Replace(stored, "<[^>]*>", " ");
        text = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ")).Trim();
        return text.Length == 0 ? null : text;
    }
}
