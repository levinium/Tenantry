namespace Tenantry.Core.Directory;

/// <summary>
/// One inbox rule. <see cref="Identity"/> is how Exchange finds it again ("mailbox\ruleId"); the
/// actions that matter to an admin are pulled out so suspicious rules stand out.
/// </summary>
public sealed record InboxRule(
    string Identity,
    string Name,
    bool Enabled,
    int Priority,
    string Description,
    bool InError,
    IReadOnlyList<string> ForwardsTo,
    bool Deletes,
    string? MovesTo)
{
    /// <summary>Forwards or redirects mail anywhere: the classic sign of a compromised account.</summary>
    public bool Forwards => ForwardsTo.Count > 0;

    /// <summary>
    /// What the rule does in one sentence, from Exchange's description ("If the message: … Take the
    /// following actions: …"): "When a message includes 'invoice' in the subject: delete the message."
    /// </summary>
    public string Summary => Sentence(Description);

    public static string Sentence(string? description)
    {
        // Only Exchange's own layout is rewritten; anything else shows as it came.
        if (description is null || !description.Contains("Take the following action", StringComparison.OrdinalIgnoreCase))
            return (description ?? "").Trim();
        var (conditions, exceptions, actions) = MailFlowRule.ParseDescription(description);
        var when = conditions.Count == 0 || conditions is ["(all messages)"] ? "Every message" : "When a message " + string.Join(" and ", conditions.Select(Lower));
        var except = exceptions.Count == 0 ? "" : ", except when it " + string.Join(" or ", exceptions.Select(Lower));
        var then = actions.Count == 0 ? "" : ": " + string.Join(", then ", actions.Select(Lower));
        var sentence = when + except + then;
        return sentence.EndsWith('.') ? sentence : sentence + ".";
    }

    /// <summary>Exchange sometimes starts each part with a capital ("Is received from…"); mid-sentence it doesn't.</summary>
    static string Lower(string part) => part.Length > 1 && char.IsUpper(part[0]) && !char.IsUpper(part[1]) ? char.ToLowerInvariant(part[0]) + part[1..] : part;
}

/// <summary>Someone's access to a mailbox folder. "Default" (everyone in the organization) and "Anonymous" are Exchange's own entries.</summary>
public sealed record FolderPermission(string User, string AccessRight, bool IsDelegate)
{
    public bool IsBuiltIn => User is "Default" or "Anonymous";
}

/// <summary>A folder's sharing, with the identity Exchange takes for changes ("address:\Calendar").</summary>
public sealed record FolderSharing(string FolderIdentity, string FolderName, IReadOnlyList<FolderPermission> Permissions);

/// <summary>Exchange's folder roles, from most to least access, with what each lets someone do.</summary>
public static class FolderRoles
{
    public static readonly IReadOnlyList<(string Role, string Name, string Explanation)> Calendar =
    [
        ("Editor", "Can Edit", "See and change everything, and add or delete events"),
        ("Author", "Can Edit Their Own", "See everything, add events, change or delete only their own"),
        ("Reviewer", "Can View All Details", "See every event with its details"),
        ("LimitedDetails", "Can View Titles and Locations", "See when they're busy, with each event's title and location"),
        ("AvailabilityOnly", "Can See When They're Busy", "See only free or busy times"),
        ("None", "No Access", "Can't see the calendar"),
    ];

    public static string NameOf(string role) =>
        Calendar.FirstOrDefault(r => string.Equals(r.Role, role, StringComparison.OrdinalIgnoreCase)).Name ?? role;
}

/// <summary>A phone or tablet that syncs this mailbox (Exchange ActiveSync or Outlook mobile).</summary>
/// <param name="Identity">Exchange's identity for the device partnership, used to wipe or remove it.</param>
/// <param name="DeviceId">The device's own ID, used to block or allow it.</param>
/// <param name="AccessState">"Allowed", "Blocked", "Quarantined" or "DeviceDiscovery".</param>
/// <param name="WipeStatus">Set once a wipe was sent: when, and whether the device confirmed it.</param>
public sealed record MobileDevice(
    string Identity,
    string DeviceId,
    string Name,
    string Model,
    string Os,
    string ClientType,
    DateTimeOffset? FirstSync,
    DateTimeOffset? LastSync,
    string AccessState,
    string? WipeStatus)
{
    public bool IsBlocked => string.Equals(AccessState, "Blocked", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An item someone deleted that Exchange still keeps (Deleted Items or Recoverable Items).</summary>
public sealed record DeletedItem(string EntryId, string Subject, string ItemClass, string? FolderPath, DateTimeOffset? Modified)
{
    /// <summary>"Email", "Meeting", "Contact"… from Exchange's item class.</summary>
    public string Kind => ItemClass switch
    {
        _ when ItemClass.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase) => "Email",
        _ when ItemClass.StartsWith("IPM.Appointment", StringComparison.OrdinalIgnoreCase) => "Calendar Event",
        _ when ItemClass.StartsWith("IPM.Schedule", StringComparison.OrdinalIgnoreCase) => "Meeting Message",
        _ when ItemClass.StartsWith("IPM.Contact", StringComparison.OrdinalIgnoreCase) => "Contact",
        _ when ItemClass.StartsWith("IPM.Task", StringComparison.OrdinalIgnoreCase) => "Task",
        _ => "Item",
    };
}

public enum TraceDirection
{
    Received,
    Sent,
}

/// <summary>One message as message trace saw it, to or from this mailbox.</summary>
public sealed record TracedMessage(string TraceId, DateTimeOffset Received, string Sender, string Recipient, string Subject, string Status, long? Size);

/// <summary>One step a traced message went through (received, spam filtering, delivered…).</summary>
public sealed record TraceEvent(DateTimeOffset Date, string Event, string Action, string Detail);

/// <summary>
/// What's inside a mailbox, read live from Exchange when the Mail tab's pages ask for it. Each
/// read is separate so a page shows as soon as its own data arrives.
/// </summary>
public interface IMailboxContents
{
    Task<IReadOnlyList<InboxRule>> GetInboxRulesAsync(string address, CancellationToken cancellationToken = default);

    Task<FolderSharing> GetCalendarSharingAsync(string address, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MobileDevice>> GetMobileDevicesAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>The most deleted items one search lists; past it the page says to narrow by subject.</summary>
    const int DeletedItemsLimit = 200;

    /// <summary>The most messages one trace lists (Get-MessageTraceV2's most); past it the page says so.</summary>
    const int TraceLimit = 5000;

    /// <param name="subject">Only items whose subject contains this, when given.</param>
    Task<IReadOnlyList<DeletedItem>> GetDeletedItemsAsync(string address, string? subject, CancellationToken cancellationToken = default);

    /// <summary>Messages to or from the mailbox over the last <paramref name="days"/> days (up to 10 per search).</summary>
    Task<IReadOnlyList<TracedMessage>> TraceAsync(string address, TraceDirection direction, int days, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TraceEvent>> TraceDetailAsync(TracedMessage message, CancellationToken cancellationToken = default);
}
