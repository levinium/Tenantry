namespace Tenantry.Core.Directory;

/// <summary>
/// One day's count of one kind of mail (Get-MailFlowStatusReport): delivered, spam, phishing,
/// malware or blocked at the edge, in one direction.
/// </summary>
/// <param name="EventType">Exchange's name: GoodMail, SpamDetections, EmailPhish, EmailMalware, EdgeBlockSpam…</param>
/// <param name="Direction">Inbound, Outbound or IntraOrg.</param>
public sealed record MailFlowCount(DateOnly Day, string EventType, string Direction, int Count);

/// <summary>A "top" entry (Get-MailTrafficSummaryReport): an address, a malware name or a mail flow rule, and how many messages.</summary>
public sealed record MailFlowTop(string Name, int Count);

/// <summary>What caught threats (Get-MailTrafficATPReport): a verdict and the filter or technology behind it.</summary>
/// <param name="Verdict">Spam, Phish, Malware, NotSpam, Allow.</param>
/// <param name="Technology">"URL detonation", "IP reputation", "Impersonation brand"…</param>
public sealed record ThreatCatch(string Verdict, string Technology, string Direction, int Count);

/// <summary>A kind of mail as the report shows it, in the order the chart stacks them.</summary>
public sealed record MailFlowKind(string Key, string Title, string Explanation);

/// <summary>The words and arithmetic for mail flow reports (kept here so they can be tested).</summary>
public static class MailFlowReport
{
    /// <summary>The longest the reports reach back.</summary>
    public const int HistoryDays = 90;

    /// <summary>Defender's breakdown is slow (about 10 s for 7 days, over a minute for 30), so it covers the last week.</summary>
    public const int ThreatDays = 7;

    /// <summary>The kinds of incoming mail, bottom of the bar first.</summary>
    public static IReadOnlyList<MailFlowKind> Kinds { get; } =
    [
        new("GoodMail", "Delivered", "Mail that passed the filters and reached mailboxes."),
        new("SpamDetections", "Spam", "Caught as spam or bulk mail: sent to Junk or quarantined, depending on your anti-spam policies."),
        new("EmailPhish", "Phishing", "Caught as phishing (fake sign-in pages, impersonation, spoofing)."),
        new("EmailMalware", "Malware", "Carried a virus or a dangerous file or link."),
        new("EdgeBlockSpam", "Blocked at the Edge", "Turned away before it arrived, usually because the sending server has a bad reputation."),
    ];

    /// <summary>The words for an event type, or the type itself when it's one Microsoft adds later.</summary>
    public static string KindTitle(string eventType) => Kinds.FirstOrDefault(k => k.Key.Equals(eventType, StringComparison.OrdinalIgnoreCase))?.Title ?? eventType;

    public static string DirectionText(string direction) => direction switch
    {
        "Inbound" => "Incoming",
        "Outbound" => "Sent Outside",
        "IntraOrg" => "Internal",
        _ => direction,
    };

    /// <summary>The total for one kind and direction (null direction: every direction).</summary>
    public static int Total(IEnumerable<MailFlowCount> counts, string eventType, string? direction = null) =>
        counts.Where(c => c.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase) && (direction is null || c.Direction == direction)).Sum(c => c.Count);

    /// <summary>The share of incoming mail that wasn't wanted (everything incoming but delivered), as a whole percent.</summary>
    public static int UnwantedPercent(IReadOnlyCollection<MailFlowCount> counts)
    {
        var incoming = counts.Where(c => c.Direction == "Inbound").ToArray();
        var all = incoming.Sum(c => c.Count);
        return all == 0 ? 0 : (int)Math.Round(100.0 * (all - Total(incoming, "GoodMail")) / all);
    }

    /// <summary>Incoming mail per day, oldest first, with a count for every kind (0 when none) and every day in the range.</summary>
    public static IReadOnlyList<(DateOnly Day, IReadOnlyList<(string Kind, int Count)> Counts)> IncomingByDay(IEnumerable<MailFlowCount> counts, DateOnly first, DateOnly last)
    {
        var byDay = counts.Where(c => c.Direction == "Inbound").ToLookup(c => c.Day);
        var kinds = Kinds.Select(k => k.Key).Concat(byDay.SelectMany(d => d).Select(c => c.EventType).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(t => !Kinds.Any(k => k.Key.Equals(t, StringComparison.OrdinalIgnoreCase)))).ToArray();
        var days = new List<(DateOnly, IReadOnlyList<(string, int)>)>();
        for (var day = first; day <= last; day = day.AddDays(1))
            days.Add((day, [.. kinds.Select(k => (k, Total(byDay[day], k)))]));
        return days;
    }

    /// <summary>The same entry across several report rows (an address in different cases) counted once, biggest first.</summary>
    public static IReadOnlyList<MailFlowTop> Combine(IEnumerable<MailFlowTop> rows, int take = 20) =>
        [.. rows.Where(r => r.Name.Length > 0).GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(g => new MailFlowTop(g.First().Name, g.Sum(r => r.Count)))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Take(take)];

    /// <summary>The top lists the page offers, as Get-MailTrafficSummaryReport categories.</summary>
    public static IReadOnlyList<(string Category, string Title, string Explanation, bool IsAddress)> TopLists { get; } =
    [
        ("TopMailSender", "Top Senders", "Who in your organization sent the most mail.", true),
        ("TopMailRecipient", "Top Recipients", "Who received the most mail.", true),
        ("TopSpamRecipient", "Most Spam Received", "Who was sent the most spam.", true),
        ("TopPhishRecipient", "Most Phishing Received", "Who was sent the most phishing: the people attackers aim at.", true),
        ("TopMalwareRecipient", "Most Malware Received", "Who was sent the most malware.", true),
        ("TopMalware", "Malware Seen", "The malware Microsoft found, by name.", false),
        ("InboundTransportRuleHits", "Mail Flow Rules: Incoming", "How often each mail flow rule acted on incoming mail.", false),
        ("OutboundTransportRuleHits", "Mail Flow Rules: Outgoing", "How often each mail flow rule acted on outgoing mail.", false),
    ];

    /// <summary>Defender's verdicts in words.</summary>
    public static string VerdictText(string verdict) => verdict switch
    {
        "Spam" => "Spam",
        "Phish" => "Phishing",
        "Malware" => "Malware",
        "NotSpam" => "Clean",
        "Allow" => "Allowed by You",
        _ => verdict,
    };
}

/// <summary>Mail flow reports: daily counts, top lists and what caught threats.</summary>
public interface IMailFlowReports
{
    /// <summary>Mail per day and kind over the last <paramref name="days"/> days (up to 90), today included.</summary>
    Task<IReadOnlyList<MailFlowCount>> GetMailFlowStatusAsync(int days, CancellationToken cancellationToken = default);

    /// <summary>One top list (a <see cref="MailFlowReport.TopLists"/> category) over the last <paramref name="days"/> days.</summary>
    Task<IReadOnlyList<MailFlowTop>> GetTopTrafficAsync(string category, int days, CancellationToken cancellationToken = default);

    /// <summary>What caught threats over the last <paramref name="days"/> days, totalled per verdict, technology and direction.</summary>
    Task<IReadOnlyList<ThreatCatch>> GetThreatCatchesAsync(int days, CancellationToken cancellationToken = default);
}
