namespace Tenantry.Core.Directory;

/// <summary>
/// A message, link or file sent to Microsoft for a verdict (Graph threat submissions, beta): by a
/// person with Outlook's Report button (<see cref="ByUser"/>), or by an admin.
/// </summary>
/// <param name="Kind">"email", "url" or "file".</param>
/// <param name="Category">What it was reported as: "notJunk", "spam", "phishing" or "malware".</param>
/// <param name="Status">"notStarted", "running", "succeeded", "failed", "skipped".</param>
/// <param name="ResultCategory">Microsoft's verdict, once there is one.</param>
/// <param name="ResultDetail">Microsoft's reason code for the verdict ("itemFoundClean", "underInvestigation"…).</param>
/// <param name="ReviewedAs">An admin's review of a person's report ("phishing", "notSpam"…); null until reviewed.</param>
public sealed record ThreatSubmission(string Id, string Kind, bool ByUser, string Category, string Status, string? ResultCategory, string? ResultDetail,
    string Subject, string? Sender, string? Recipient, DateTimeOffset? Received, DateTimeOffset Created, string? CreatedBy,
    string? ReviewedAs, string? ReviewedBy, DateTimeOffset? ReviewedAt)
{
    public bool IsReviewed => ReviewedAs is not null;

    /// <summary>A person's report of an email: the kind an admin can review and answer.</summary>
    public bool CanReview => ByUser && Kind == "email";

    public static string CategoryText(string? category) => category switch
    {
        "notJunk" or "notSpam" => "Not Junk",
        "spam" or "junk" => "Spam",
        "phishing" => "Phishing",
        "malware" => "Malware",
        "bulk" => "Bulk",
        "spoof" or "spoofedBlocked" or "spoofedAllowed" => "Spoofing",
        "allowedByPolicy" or "allowedDueToOrganizationOverride" => "Allowed by Your Policy",
        "blockedByPolicy" or "blockedDueToOrganizationOverride" => "Blocked by Your Policy",
        "allowedDueToUserOverride" => "Allowed by the Person's Settings",
        "blockedDueToUserOverride" => "Blocked by the Person's Settings",
        "noThreatsFound" => "No Threats Found",
        "threatsFound" => "Threats Found",
        "phishingSimulation" => "Phishing Simulation",
        "domainImpersonation" or "userImpersonation" or "brandImpersonation" => "Impersonation",
        "authenticationFailure" => "Failed Sender Checks",
        "beingAnalyzed" => "Being Analyzed",
        "unknown" or "noResultAvailable" or null or "" => "No Result Yet",
        _ => category,
    };

    /// <summary>Microsoft's reason for its verdict, said as a sentence.</summary>
    public static string DetailText(string? detail) => detail switch
    {
        null or "" => "",
        "underInvestigation" => "Microsoft is still looking at it.",
        "willNotifyOnceDone" => "Microsoft will say once it has looked.",
        "checkUserReportedSettings" => "People's reports aren't going to Microsoft: see Where Reports Go.",
        "messageNotFound" or "itemNotfound" or "itemDeleted" => "Microsoft couldn't find the message (deleted, or older than 30 days).",
        "allowedByTenantAllowBlockList" or "allowedByTenant" => "It was let through by your Tenant Allow/Block List.",
        "blockedByTenantAllowBlockList" => "It was blocked by your Tenant Allow/Block List.",
        "blockedByExchangeTransportRule" => "A mail flow rule blocked it.",
        "allowedByExchangeTransportRule" => "A mail flow rule let it through.",
        "allowedByUserSetting" => "The person's own safe senders or settings let it through.",
        "blockedByUserSetting" => "The person's own blocked senders or settings stopped it.",
        "itemFoundClean" => "Microsoft found nothing wrong with it.",
        "itemFoundMalicious" => "Microsoft found it malicious.",
        "itemFoundSpam" => "Microsoft found it to be spam.",
        "itemFoundBulk" => "Microsoft found it to be bulk mail (newsletters, marketing).",
        "unableToMakeDecision" => "Microsoft couldn't reach a verdict.",
        "simulatedThreat" => "It was part of a phishing simulation.",
        _ => System.Text.RegularExpressions.Regex.Replace(detail, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant() is var words && words.Length > 0
            ? char.ToUpperInvariant(words[0]) + words[1..] + "." : "",
    };

    /// <summary>The categories a submission can be sent as, and an admin review's verdicts.</summary>
    public static IReadOnlyList<(string Value, string Label)> SubmitAs { get; } =
        [("notJunk", "Not Junk (Wrongly Blocked)"), ("spam", "Spam"), ("phishing", "Phishing"), ("malware", "Malware")];
}

/// <summary>Where Outlook's Report button sends people's reports.</summary>
public enum ReportMode
{
    /// <summary>To Microsoft, and a copy to the organization's reporting mailbox (Microsoft's default).</summary>
    MicrosoftAndMailbox,
    MailboxOnly,
    MicrosoftOnly,
    /// <summary>The Report button isn't offered.</summary>
    Off,
}

/// <summary>The report settings (Exchange's DefaultReportSubmissionPolicy and DefaultReportSubmissionRule).</summary>
/// <param name="Mailbox">The reporting mailbox; null when reports go only to Microsoft.</param>
/// <param name="NotifyReporters">Email the person when an admin has reviewed their report.</param>
/// <param name="AskFirst">Ask "Report this message?" before sending.</param>
/// <param name="ThankAfter">Say thank you after sending.</param>
/// <param name="PolicyExists">Exchange has the policy (it doesn't until the settings are first saved).</param>
/// <param name="RuleExists">Exchange has the rule that names the mailbox.</param>
public sealed record ReportRouting(ReportMode Mode, string? Mailbox, bool NotifyReporters, bool AskFirst, bool ThankAfter, bool PolicyExists, bool RuleExists)
{
    public bool UsesMailbox => Mode is ReportMode.MicrosoftAndMailbox or ReportMode.MailboxOnly;

    public static string ModeText(ReportMode mode) => mode switch
    {
        ReportMode.MicrosoftAndMailbox => "Microsoft and Our Reporting Mailbox",
        ReportMode.MailboxOnly => "Our Reporting Mailbox Only",
        ReportMode.MicrosoftOnly => "Microsoft Only",
        _ => "Nowhere (Report Button Off)",
    };

    /// <summary>"Microsoft and reports@…; reporters told when reviewed".</summary>
    public string Describe()
    {
        var where = Mode switch
        {
            ReportMode.MicrosoftAndMailbox => $"Microsoft and {Mailbox ?? "a reporting mailbox"}",
            ReportMode.MailboxOnly => Mailbox ?? "a reporting mailbox",
            ReportMode.MicrosoftOnly => "Microsoft",
            _ => "Report button off",
        };
        var extras = new List<string>();
        if (Mode != ReportMode.Off && NotifyReporters)
            extras.Add("reporters told when reviewed");
        if (Mode != ReportMode.Off && !AskFirst)
            extras.Add("no confirmation first");
        return extras.Count == 0 ? where : $"{where}; {string.Join(", ", extras)}";
    }

    /// <summary>Why it can't be saved as it is; null when it can.</summary>
    public string? Problem => UsesMailbox && (string.IsNullOrWhiteSpace(Mailbox) || !Mailbox.Contains('@'))
        ? "Give the reporting mailbox's address (an Exchange Online mailbox; not a group or an outside address)." : null;
}

/// <summary>Reported messages and submissions (Graph, beta) and where reports go (Exchange).</summary>
public interface ISubmissionRecords
{
    /// <summary>Email, link and file submissions from the last 30 days, people's and admins', newest first.</summary>
    Task<IReadOnlyList<ThreatSubmission>> GetSubmissionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a quarantined message (downloaded from quarantine) to Microsoft as <paramref name="category"/>.</summary>
    Task<ThreatSubmission> SubmitQuarantinedAsync(string identity, string recipient, string category, CancellationToken cancellationToken = default);

    Task<ThreatSubmission> SubmitUrlAsync(string url, string category, CancellationToken cancellationToken = default);

    Task<ThreatSubmission> SubmitFileAsync(string fileName, byte[] content, string category, CancellationToken cancellationToken = default);

    Task<ReportRouting> GetReportRoutingAsync(CancellationToken cancellationToken = default);
}
