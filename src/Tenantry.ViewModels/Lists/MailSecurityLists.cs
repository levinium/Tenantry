using Tenantry.Core.Directory;
using Tenantry.Core.Lists;

namespace Tenantry.ViewModels.Lists;

/// <summary>Mail &amp; security's lists (5a): quarantined mail and message trace results.</summary>
public static class MailSecurityLists
{
    // ---- Quarantine -------------------------------------------------------------------------

    public static class Q
    {
        public const string Received = "received";
        public const string Sender = "sender";
        public const string Recipients = "recipients";
        public const string Subject = "subject";
        public const string Reason = "reason";
        public const string Policy = "policy";
        public const string Status = "status";
        public const string Expires = "expires";
        public const string Size = "size";

        public const string Held = "Held";
        public const string Released = "Released";
        public const string Requested = "Release requested";
        public const string Denied = "Release denied";
    }

    public static string StatusOf(QuarantinedMessage m) => m.ReleaseStatus.ToUpperInvariant() switch
    {
        "RELEASED" => Q.Released,
        "REQUESTED" => Q.Requested,
        "DENIED" => Q.Denied,
        _ => Q.Held,
    };

    static readonly string[] Reasons = ["Phishing", "High-confidence phishing", "Malware", "Spam", "High-confidence spam", "Bulk mail", "Mail flow rule"];

    public static IReadOnlyList<Field<QuarantinedMessage>> QuarantineFields { get; } =
    [
        new() { Key = Q.Received, Label = "Received", Kind = FieldKind.Date, Aliases = ["date", "when"], Date = m => m.Received },
        new() { Key = Q.Sender, Label = "Sender", Kind = FieldKind.Text, Searchable = true, Aliases = ["from"], Text = m => m.Sender },
        new() { Key = Q.Recipients, Label = "Recipients", Kind = FieldKind.Text, Searchable = true, Aliases = ["to", "recipient"], Text = m => string.Join(", ", m.Recipients) },
        new() { Key = Q.Subject, Label = "Subject", Kind = FieldKind.Text, Searchable = true, Text = m => m.Subject },
        new() { Key = Q.Reason, Label = "Reason", Kind = FieldKind.Choice, Aliases = ["type"], ChoiceOrder = Reasons, Text = m => m.ReasonText },
        new() { Key = Q.Policy, Label = "Policy", Kind = FieldKind.Choice, EmptyLabel = "None", Text = m => m.Policy },
        new() { Key = Q.Status, Label = "Status", Kind = FieldKind.Choice, ChoiceOrder = [Q.Held, Q.Requested, Q.Released, Q.Denied], Text = StatusOf },
        new() { Key = Q.Expires, Label = "Expires", Kind = FieldKind.Date, Date = m => m.Expires },
        new() { Key = Q.Size, Label = "Size (KB)", Kind = FieldKind.Number, Number = m => m.Size is { } s ? Math.Round(s / 1024.0) : null },
    ];

    static readonly ListQuery Newest = ListQuery.Empty with { Sort = [new SortLevel(Q.Received, Descending: true)] };

    static SavedView View(string id, string name, bool pinned, params Condition[] where) =>
        new(id, name, Newest with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: pinned);

    public static IReadOnlyList<SavedView> QuarantineViews { get; } =
    [
        View("held", "Held", true, new ChoiceCondition(Q.Status, [Q.Held], [])),
        View("requested", "Release requested", true, new ChoiceCondition(Q.Status, [Q.Requested], [])),
        View("phish", "Phishing", true, new ChoiceCondition(Q.Reason, ["Phishing", "High-confidence phishing"], [])),
        View("malware", "Malware", true, new ChoiceCondition(Q.Reason, ["Malware", "Malware in SharePoint or OneDrive"], [])),
        View("spam", "Spam and bulk", true, new ChoiceCondition(Q.Reason, ["Spam", "High-confidence spam", "Bulk mail"], [])),
        View("released", "Released", false, new ChoiceCondition(Q.Status, [Q.Released], [])),
        View("all", "All", false),
    ];

    static (string, BadgeKind) ReasonBadge(QuarantinedMessage m) => m.Reason switch
    {
        "Malware" or "SPOMalware" or "HighConfPhish" => (m.ReasonText, BadgeKind.Danger),
        "Phish" => (m.ReasonText, BadgeKind.Warning),
        _ => (m.ReasonText, BadgeKind.Neutral),
    };

    static (string, BadgeKind) StatusBadge(QuarantinedMessage m) => StatusOf(m) switch
    {
        Q.Released => (Q.Released, BadgeKind.Success),
        Q.Requested => (Q.Requested, BadgeKind.Info),
        Q.Denied => (Q.Denied, BadgeKind.Neutral),
        _ => (Q.Held, BadgeKind.Neutral),
    };

    public static ListDefinition<QuarantinedMessage> Quarantine(Func<QuarantinedMessage, object> preview) => new()
    {
        Key = "quarantine",
        Title = "Quarantine",
        Noun = "messages",
        Fields = QuarantineFields,
        Id = m => m.Identity,
        Views = QuarantineViews,
        SearchHint = "Search senders, recipients, subjects… or try reason:phishing status:held",
        Preview = preview,
        Columns =
        [
            new() { Key = Q.Subject, Header = "Message", Cell = CellKind.Primary, MinWidth = 280, DefaultWidth = 380,
                Text = m => m.Subject, Subtitle = m => $"From {m.Sender}", Initials = m => Display.Initials(m.Sender) },
            new() { Key = Q.Recipients, Header = "To", Cell = CellKind.Text, MinWidth = 180, DefaultWidth = 240, Text = m => string.Join(", ", m.Recipients) },
            new() { Key = Q.Reason, Header = "Reason", Cell = CellKind.Badge, MinWidth = 150, DefaultWidth = 190, Badge = ReasonBadge },
            new() { Key = Q.Status, Header = "Status", Cell = CellKind.Badge, MinWidth = 130, DefaultWidth = 150, Badge = StatusBadge },
            new() { Key = Q.Received, Header = "Received", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = m => m.Received },
            new() { Key = Q.Expires, Header = "Expires", Cell = CellKind.Date, MinWidth = 120, DefaultWidth = 140, DefaultVisible = false, Date = m => m.Expires },
            new() { Key = Q.Policy, Header = "Policy", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 180, DefaultVisible = false, Text = m => m.Policy },
            new() { Key = Q.Size, Header = "Size (KB)", Cell = CellKind.Number, MinWidth = 90, DefaultWidth = 100, DefaultVisible = false,
                Number = m => m.Size is { } s ? Math.Round(s / 1024.0) : null },
        ],
    };

    // ---- Message trace ----------------------------------------------------------------------

    public static class T
    {
        public const string Received = "received";
        public const string Sender = "sender";
        public const string Recipient = "recipient";
        public const string Subject = "subject";
        public const string Status = "status";
        public const string Size = "size";
    }

    /// <summary>Message trace statuses, in the order an admin usually looks for them.</summary>
    public static readonly string[] TraceStatuses = ["Delivered", "Failed", "Pending", "Quarantined", "FilteredAsSpam", "Expanded", "Resolved", "GettingStatus"];

    public static string StatusText(string status) => status switch
    {
        "FilteredAsSpam" => "Filtered as spam",
        "GettingStatus" => "Getting status",
        _ => status,
    };

    public static IReadOnlyList<Field<TracedMessage>> TraceFields { get; } =
    [
        new() { Key = T.Received, Label = "Received", Kind = FieldKind.Date, Aliases = ["date", "when"], Date = m => m.Received },
        new() { Key = T.Sender, Label = "Sender", Kind = FieldKind.Text, Searchable = true, Aliases = ["from"], Text = m => m.Sender },
        new() { Key = T.Recipient, Label = "Recipient", Kind = FieldKind.Text, Searchable = true, Aliases = ["to"], Text = m => m.Recipient },
        new() { Key = T.Subject, Label = "Subject", Kind = FieldKind.Text, Searchable = true, Text = m => m.Subject },
        new() { Key = T.Status, Label = "Status", Kind = FieldKind.Choice, ChoiceOrder = TraceStatuses.Select(StatusText).ToArray(), Text = m => StatusText(m.Status) },
        new() { Key = T.Size, Label = "Size (KB)", Kind = FieldKind.Number, Number = m => m.Size is { } s ? Math.Round(s / 1024.0) : null },
    ];

    static readonly ListQuery TraceNewest = ListQuery.Empty with { Sort = [new SortLevel(T.Received, Descending: true)] };

    static SavedView TraceView(string id, string name, params string[] statuses) =>
        new(id, name, statuses.Length == 0 ? TraceNewest : TraceNewest with { Where = ConditionGroup.All(new ChoiceCondition(T.Status, statuses.Select(StatusText).ToArray(), [])) },
            BuiltIn: true, Pinned: true);

    public static IReadOnlyList<SavedView> TraceViews { get; } =
    [
        TraceView("all", "All"),
        TraceView("failed", "Failed", "Failed"),
        TraceView("quarantined", "Quarantined or spam", "Quarantined", "FilteredAsSpam"),
        TraceView("pending", "Pending", "Pending", "GettingStatus"),
        TraceView("delivered", "Delivered", "Delivered"),
    ];

    static (string, BadgeKind) TraceBadge(TracedMessage m) => m.Status switch
    {
        "Delivered" or "Expanded" or "Resolved" => (StatusText(m.Status), BadgeKind.Success),
        "Failed" => (StatusText(m.Status), BadgeKind.Danger),
        "Quarantined" or "FilteredAsSpam" => (StatusText(m.Status), BadgeKind.Warning),
        _ => (StatusText(m.Status), BadgeKind.Info),
    };

    public static ListDefinition<TracedMessage> Trace(Func<TracedMessage, object> preview) => new()
    {
        Key = "trace",
        Title = "Message trace",
        Noun = "messages",
        Fields = TraceFields,
        Id = m => m.TraceId + "|" + m.Recipient,
        Views = TraceViews,
        SearchHint = "Filter these results… or try status:failed",
        Preview = preview,
        Columns =
        [
            new() { Key = T.Subject, Header = "Message", Cell = CellKind.Primary, MinWidth = 280, DefaultWidth = 380,
                Text = m => m.Subject, Subtitle = m => $"From {m.Sender}", Initials = m => Display.Initials(m.Sender) },
            new() { Key = T.Recipient, Header = "To", Cell = CellKind.Text, MinWidth = 180, DefaultWidth = 240, Text = m => m.Recipient },
            new() { Key = T.Status, Header = "Status", Cell = CellKind.Badge, MinWidth = 130, DefaultWidth = 150, Badge = TraceBadge },
            new() { Key = T.Received, Header = "Received", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = m => m.Received },
            new() { Key = T.Size, Header = "Size (KB)", Cell = CellKind.Number, MinWidth = 90, DefaultWidth = 100, DefaultVisible = false,
                Number = m => m.Size is { } s ? Math.Round(s / 1024.0) : null },
        ],
    };
}
