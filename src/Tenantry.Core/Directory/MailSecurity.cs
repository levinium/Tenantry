namespace Tenantry.Core.Directory;

/// <summary>A message held in quarantine (spam, phishing, malware, a transport rule…).</summary>
/// <param name="Identity">Exchange's identity for it ("guid\guid"), for preview, release and delete.</param>
/// <param name="Reason">Why it was held, as Exchange names it ("Spam", "HighConfPhish", "Malware", "TransportRule"…).</param>
/// <param name="ReleaseStatus">"NOTRELEASED", "RELEASED", "REQUESTED", "DENIED"…</param>
/// <param name="ReleasedTo">The recipients it has been released to so far (Exchange's ReleasedUser).</param>
public sealed record QuarantinedMessage(
    string Identity,
    DateTimeOffset Received,
    string Sender,
    IReadOnlyList<string> Recipients,
    string Subject,
    long? Size,
    string Reason,
    string? Policy,
    DateTimeOffset? Expires,
    string ReleaseStatus,
    string? Direction,
    IReadOnlyList<string>? ReleasedTo = null)
{
    public bool IsReleased => ReleaseStatus.Equals("RELEASED", StringComparison.OrdinalIgnoreCase);

    /// <summary>A recipient asked for it to be released (their quarantine policy lets them ask, not release).</summary>
    public bool IsRequested => ReleaseStatus.Equals("REQUESTED", StringComparison.OrdinalIgnoreCase);

    /// <summary>Recipients it hasn't been released to yet.</summary>
    public IReadOnlyList<string> NotReleasedTo => ReleasedTo is { Count: > 0 } done
        ? [.. Recipients.Where(r => !done.Contains(r, StringComparer.OrdinalIgnoreCase))]
        : Recipients;

    /// <summary>Exchange takes a "wrongly caught" report to Microsoft with a release of spam only.</summary>
    public bool CanReportFalsePositive => Reason is "Spam" or "HighConfSpam" or "Bulk";

    /// <summary>Exchange's reason as a person reads it.</summary>
    public string ReasonText => Reason switch
    {
        "Spam" => "Spam",
        "HighConfSpam" => "High-Confidence Spam",
        "Phish" => "Phishing",
        "HighConfPhish" => "High-Confidence Phishing",
        "Malware" => "Malware",
        "Bulk" => "Bulk Mail",
        "TransportRule" => "Mail Flow Rule",
        "SPOMalware" => "Malware in SharePoint or OneDrive",
        "FileTypeBlock" => "Blocked File Type",
        "DataLossPrevention" => "Data Loss Prevention",
        _ => Reason,
    };

    /// <summary>Malware and high-confidence phishing are the riskiest to release: the preview warns before releasing them.</summary>
    public bool IsDangerous => Reason is "Malware" or "HighConfPhish" or "SPOMalware";

    /// <summary>The most messages one quarantine read returns (Exchange pages 1,000 at a time; ten pages).</summary>
    public const int MaxRead = 10_000;
}

/// <summary>A quarantined message's content, for the preview.</summary>
public sealed record QuarantinePreview(string Subject, string From, string To, DateTimeOffset? Sent, string Body, bool IsHtml, IReadOnlyList<string> Attachments);

/// <summary>An organization-wide message trace search. Exchange searches up to 10 days at a time, as far back as 90 days.</summary>
public sealed record TraceSearch(DateTimeOffset Start, DateTimeOffset End, string? Sender = null, string? Recipient = null, string? Subject = null, string? Status = null)
{
    public const int MaxDays = 10;
    public const int HistoryDays = 90;
    public const int MaxResults = 5000;

    /// <summary>The range cut into the 10-day windows Exchange searches, newest first.</summary>
    public static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Windows(DateTimeOffset start, DateTimeOffset end)
    {
        for (var to = end; to > start; to = to.AddDays(-MaxDays))
        {
            var from = to.AddDays(-MaxDays);
            yield return (from < start ? start : from, to);
        }
    }
}

/// <summary>The kinds of trace report Exchange builds in the background (Start-HistoricalSearch's report types).</summary>
public sealed record TraceReportKind(string Type, string Label, string Description)
{
    public override string ToString() => Label;

    public static IReadOnlyList<TraceReportKind> All { get; } =
    [
        new("MessageTrace", "Message Trace Summary", "Every message: who sent it, who it went to, when, and what happened."),
        new("MessageTraceDetail", "Message Trace with Every Step", "The summary plus each step a message went through, for working out where one went."),
        new("SPAM", "Spam", "Messages Microsoft filtered as spam."),
        new("Spoof", "Spoofed Senders", "Messages whose sender looked forged."),
        new("TransportRule", "Mail Flow Rules", "Messages your mail flow rules acted on."),
    ];
}

/// <summary>A trace report to ask for: a range up to 90 days and at least one sender or recipient.</summary>
/// <param name="NotifyAddress">An address in the organization to email when it's ready; null for none.</param>
public sealed record TraceReportRequest(string Title, string Type, DateTimeOffset Start, DateTimeOffset End,
    IReadOnlyList<string> Senders, IReadOnlyList<string> Recipients, string? NotifyAddress)
{
    /// <summary>What's wrong with it, as Exchange would refuse it; empty when it can go.</summary>
    public IReadOnlyList<string> Problems(DateTimeOffset now)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Title))
            problems.Add("Give the report a name.");
        if (Senders.Count == 0 && Recipients.Count == 0)
            problems.Add("Add at least one sender or recipient: Exchange needs one of them.");
        if (Senders.Count > MaxAddresses || Recipients.Count > MaxAddresses)
            problems.Add($"Up to {MaxAddresses} senders and {MaxAddresses} recipients.");
        if (End <= Start)
            problems.Add("The range has to end after it starts.");
        if (Start < now.AddDays(-TraceSearch.HistoryDays))
            problems.Add($"Reports go back {TraceSearch.HistoryDays} days at most.");
        return problems;
    }

    public const int MaxAddresses = 100;
}

/// <summary>A trace report Exchange is building or has built (Get-HistoricalSearch, kept 10 days).</summary>
/// <param name="Status">"NotStarted", "InProgress", "Done", "Failed", "Cancelled"…</param>
/// <param name="FileUrl">Where the finished CSV is, for a browser signed in as an admin.</param>
public sealed record TraceReport(string JobId, string Title, string Type, string Status, DateTimeOffset? Submitted, DateTimeOffset? Completed,
    DateTimeOffset? Start, DateTimeOffset? End, long Rows, string? FileUrl, string? Error, IReadOnlyList<string> Senders, IReadOnlyList<string> Recipients)
{
    public bool IsDone => Status.Equals("Done", StringComparison.OrdinalIgnoreCase);

    public bool IsWaiting => Status is "NotStarted" or "InProgress" or "Queued";

    public string KindLabel => TraceReportKind.All.FirstOrDefault(k => k.Type.Equals(Type, StringComparison.OrdinalIgnoreCase))?.Label ?? Type;

    /// <summary>Exchange allows this many report requests a day (cancelled ones count).</summary>
    public const int DailyLimit = 250;
}

/// <summary>Quarantine and message trace across the organization (Exchange).</summary>
public interface IMailSecurity
{
    /// <summary>Messages quarantined in the last <paramref name="days"/> days (up to 30), newest first.</summary>
    Task<IReadOnlyList<QuarantinedMessage>> GetQuarantineAsync(int days, CancellationToken cancellationToken = default);

    Task<QuarantinePreview> PreviewQuarantinedAsync(string identity, CancellationToken cancellationToken = default);

    /// <summary>One message read fresh, with who it has been released to (the list read doesn't say).</summary>
    Task<QuarantinedMessage?> GetQuarantinedAsync(string identity, CancellationToken cancellationToken = default);

    /// <summary>A quarantined message's headers, as text.</summary>
    Task<string> GetQuarantineHeadersAsync(string identity, CancellationToken cancellationToken = default);

    /// <summary>A quarantined message as an .eml file's bytes (Microsoft records each download).</summary>
    Task<byte[]> ExportQuarantinedAsync(string identity, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TracedMessage>> TraceAsync(TraceSearch search, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TraceEvent>> TraceDetailAsync(TracedMessage message, CancellationToken cancellationToken = default);

    /// <summary>The trace reports asked for in the last 10 days, newest first.</summary>
    Task<IReadOnlyList<TraceReport>> GetTraceReportsAsync(CancellationToken cancellationToken = default);

    Task<TraceReport> StartTraceReportAsync(TraceReportRequest request, CancellationToken cancellationToken = default);

    Task CancelTraceReportAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Every entry on the Tenant Allow/Block List: senders, links, files, IPs and spoofed senders.</summary>
    Task<IReadOnlyList<ListEntry>> GetListEntriesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every mail flow rule, in the order they run.</summary>
    Task<IReadOnlyList<MailFlowRule>> GetRulesAsync(CancellationToken cancellationToken = default);

    /// <summary>One rule with every setting, read fresh, for the rule editor.</summary>
    Task<MailFlow.RuleDraft> GetRuleDraftAsync(string identity, CancellationToken cancellationToken = default);
}
