namespace Tenantry.Core.Directory;

/// <summary>A message held in quarantine (spam, phishing, malware, a transport rule…).</summary>
/// <param name="Identity">Exchange's identity for it ("guid\guid"), for preview, release and delete.</param>
/// <param name="Reason">Why it was held, as Exchange names it ("Spam", "HighConfPhish", "Malware", "TransportRule"…).</param>
/// <param name="ReleaseStatus">"NOTRELEASED", "RELEASED", "REQUESTED", "DENIED"…</param>
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
    string? Direction)
{
    public bool IsReleased => ReleaseStatus.Equals("RELEASED", StringComparison.OrdinalIgnoreCase);

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
}

/// <summary>A quarantined message's content, for the preview.</summary>
public sealed record QuarantinePreview(string Subject, string From, string To, DateTimeOffset? Sent, string Body, bool IsHtml, IReadOnlyList<string> Attachments);

/// <summary>An organization-wide message trace search. Exchange searches up to 10 days at a time, as far back as 90 days.</summary>
public sealed record TraceSearch(DateTimeOffset Start, DateTimeOffset End, string? Sender = null, string? Recipient = null, string? Subject = null, string? Status = null)
{
    public const int MaxDays = 10;
    public const int HistoryDays = 90;
    public const int MaxResults = 5000;
}

/// <summary>Quarantine and message trace across the organization (Exchange).</summary>
public interface IMailSecurity
{
    /// <summary>Messages quarantined in the last <paramref name="days"/> days (up to 30), newest first.</summary>
    Task<IReadOnlyList<QuarantinedMessage>> GetQuarantineAsync(int days, CancellationToken cancellationToken = default);

    Task<QuarantinePreview> PreviewQuarantinedAsync(string identity, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TracedMessage>> TraceAsync(TraceSearch search, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TraceEvent>> TraceDetailAsync(TracedMessage message, CancellationToken cancellationToken = default);

    /// <summary>Every entry on the Tenant Allow/Block List: senders, links, files, IPs and spoofed senders.</summary>
    Task<IReadOnlyList<ListEntry>> GetListEntriesAsync(CancellationToken cancellationToken = default);
}
