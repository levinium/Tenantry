namespace Tenantry.Core.Directory;

/// <summary>One Microsoft 365 service's health right now (Exchange Online, Microsoft Teams…), from Graph's health overviews.</summary>
public sealed record ServiceStatus(string Id, string Service, string Status)
{
    public string StatusText => ServiceHealth.StatusText(Status);

    public bool IsHealthy => ServiceHealth.IsHealthy(Status);
}

/// <summary>An update Microsoft posted on an incident or advisory.</summary>
public sealed record IssuePost(DateTimeOffset? When, string Text);

/// <summary>
/// An incident (something is broken) or advisory (something is degraded or limited) Microsoft
/// posted for a service, with its updates. Graph keeps them for about a month after they end.
/// </summary>
public sealed record ServiceIssue(
    string Id, string Title, string Service, string? Feature, string? FeatureGroup, string? Impact, string Status,
    bool IsIncident, bool IsResolved, bool HighImpact, DateTimeOffset? Start, DateTimeOffset? End, DateTimeOffset? Modified,
    IReadOnlyList<IssuePost> Posts)
{
    public string StatusText => ServiceHealth.StatusText(Status);

    public string KindText => IsIncident ? "Incident" : "Advisory";

    /// <summary>The newest update, for the list's subtitle and the preview's top.</summary>
    public IssuePost? Latest => Posts.OrderByDescending(p => p.When).FirstOrDefault();
}

/// <summary>A Message Center post: a change Microsoft is making, or something admins should know or do.</summary>
/// <param name="Category">planForChange, stayInformed or preventOrFixIssue.</param>
/// <param name="Severity">normal, high or critical.</param>
/// <param name="Body">The post as plain text (Graph sends HTML; it's never rendered).</param>
public sealed record MessageCenterPost(
    string Id, string Title, IReadOnlyList<string> Services, string Category, string Severity, IReadOnlyList<string> Tags, bool IsMajorChange,
    DateTimeOffset? ActBy, DateTimeOffset? Start, DateTimeOffset? End, DateTimeOffset? Modified, string Body,
    IReadOnlyList<KeyValuePair<string, string>> Details, bool IsRead, bool IsArchived, bool IsFavorited)
{
    public string CategoryText => Category switch
    {
        "planForChange" => "Plan for Change",
        "preventOrFixIssue" => "Prevent or Fix Issues",
        "stayInformed" => "Stay Informed",
        _ => ServiceHealth.Words(Category),
    };

    public string SeverityText => Severity switch
    {
        "high" => "High",
        "critical" => "Critical",
        _ => "Normal",
    };

    /// <summary>Updated after it was first posted (Microsoft revises dates and details).</summary>
    public bool IsUpdated => Modified is { } m && Start is { } s && m - s > TimeSpan.FromHours(1);
}

/// <summary>A change to how Message Center posts show for the signed-in admin (only their own view; nothing in the tenant changes).</summary>
public enum MessageView
{
    Read,
    Unread,
    Archive,
    Unarchive,
    Favorite,
    Unfavorite,
}

/// <summary>Service health as read: each service's status and the open and recent issues, or why it couldn't be read.</summary>
public sealed record ServiceHealthRead(IReadOnlyList<ServiceStatus> Services, IReadOnlyList<ServiceIssue> Issues, string? Note = null);

/// <summary>Message Center as read, or why it couldn't be.</summary>
public sealed record MessageCenterRead(IReadOnlyList<MessageCenterPost> Posts, string? Note = null);

/// <summary>Service health and Message Center (15a), from Graph's service announcements.</summary>
public interface IServiceHealthRecords
{
    Task<ServiceHealthRead> GetServiceHealthAsync(CancellationToken cancellationToken = default);

    Task<MessageCenterRead> GetMessageCenterAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks posts read, archived or favorite for the signed-in admin.</summary>
    Task SetMessageViewAsync(IReadOnlyList<string> ids, MessageView view, CancellationToken cancellationToken = default);
}

/// <summary>Service health in words.</summary>
public static class ServiceHealth
{
    public static bool IsHealthy(string status) => status is "serviceOperational" or "serviceRestored" or "falsePositive" or "resolved"
        or "resolvedExternal" or "postIncidentReviewPublished" or "mitigated" or "mitigatedExternal";

    public static string StatusText(string status) => status switch
    {
        "serviceOperational" => "Healthy",
        "investigating" => "Investigating",
        "restoringService" => "Restoring Service",
        "verifyingService" => "Verifying the Fix",
        "serviceRestored" => "Restored",
        "postIncidentReviewPublished" => "Review Published",
        "serviceDegradation" => "Degraded",
        "serviceInterruption" => "Interrupted",
        "extendedRecovery" => "Extended Recovery",
        "falsePositive" => "False Alarm",
        "investigationSuspended" => "Investigation Paused",
        "resolved" => "Resolved",
        "mitigatedExternal" or "mitigated" => "Mitigated",
        "resolvedExternal" => "Resolved",
        "confirmed" => "Confirmed",
        "reported" => "Reported",
        "" => "Unknown",
        _ => Words(status),
    };

    /// <summary>How much a status needs attention, for ordering (interrupted first).</summary>
    public static int Severity(string status) => status switch
    {
        "serviceInterruption" => 0,
        "serviceDegradation" => 1,
        "restoringService" or "extendedRecovery" => 2,
        "investigating" or "confirmed" or "reported" or "verifyingService" or "investigationSuspended" => 3,
        _ => IsHealthy(status) ? 9 : 4,
    };

    /// <summary>camelCase as words: "planForChange" → "Plan For Change".</summary>
    public static string Words(string value)
    {
        if (value.Length == 0)
            return value;
        var text = new System.Text.StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]))
                text.Append(' ');
            text.Append(i == 0 ? char.ToUpperInvariant(value[i]) : value[i]);
        }
        return text.ToString();
    }
}
