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
        public const string Requested = "Release Requested";
        public const string Denied = "Release Denied";
    }

    public static string StatusOf(QuarantinedMessage m) => m.ReleaseStatus.ToUpperInvariant() switch
    {
        "RELEASED" => Q.Released,
        "REQUESTED" => Q.Requested,
        "DENIED" => Q.Denied,
        _ => Q.Held,
    };

    static readonly string[] Reasons = ["Phishing", "High-Confidence Phishing", "Malware", "Spam", "High-Confidence Spam", "Bulk Mail", "Mail Flow Rule"];

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
        View("requested", "Release Requested", true, new ChoiceCondition(Q.Status, [Q.Requested], [])),
        View("phish", "Phishing", true, new ChoiceCondition(Q.Reason, ["Phishing", "High-Confidence Phishing"], [])),
        View("malware", "Malware", true, new ChoiceCondition(Q.Reason, ["Malware", "Malware in SharePoint or OneDrive"], [])),
        View("spam", "Spam and Bulk", true, new ChoiceCondition(Q.Reason, ["Spam", "High-Confidence Spam", "Bulk Mail"], [])),
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

    // ---- Block and allow lists --------------------------------------------------------------

    public static class L
    {
        public const string Value = "value";
        public const string Kind = "kind";
        public const string Action = "action";
        public const string Expires = "expires";
        public const string ExpiresInDays = "expiresin";
        public const string LastUsed = "lastused";
        public const string Notes = "notes";
        public const string ModifiedBy = "modifiedby";
        public const string Added = "added";
        public const string Source = "source";

        public const string Blocked = "Blocked";
        public const string Allowed = "Allowed";
    }

    static readonly string[] Kinds = Enum.GetValues<ListKind>().Select(AllowBlock.KindName).ToArray();

    /// <summary>Whole days until the entry expires (0 = today); null when it has no date.</summary>
    public static Func<DateTimeOffset> Today { get; set; } = () => DateTimeOffset.Now;

    static double? DaysLeft(ListEntry e) => e.Expires is { } at ? Math.Max(0, Math.Floor((at - Today()).TotalDays)) : null;

    public static IReadOnlyList<Field<ListEntry>> EntryFields { get; } =
    [
        new() { Key = L.Value, Label = "Entry", Kind = FieldKind.Text, Searchable = true, Aliases = ["sender", "domain", "url", "hash", "ip"], Text = e => e.Value },
        new() { Key = L.Kind, Label = "Kind", Kind = FieldKind.Choice, Aliases = ["type"], ChoiceOrder = Kinds, Text = e => e.KindText },
        new() { Key = L.Action, Label = "Action", Kind = FieldKind.Choice, ChoiceOrder = [L.Blocked, L.Allowed], Text = e => e.Block ? L.Blocked : L.Allowed },
        new() { Key = L.Expires, Label = "Expires", Kind = FieldKind.Date, EmptyLabel = "Never", Date = e => e.Expires },
        new() { Key = L.ExpiresInDays, Label = "Days Until It Expires", Kind = FieldKind.Number, Aliases = ["daysleft"], Number = DaysLeft },
        new() { Key = L.LastUsed, Label = "Last Matched", Kind = FieldKind.Date, Aliases = ["used"], Date = e => e.LastUsed },
        new() { Key = L.Notes, Label = "Notes", Kind = FieldKind.Text, Searchable = true, Aliases = ["note"], Text = e => e.Notes },
        new() { Key = L.ModifiedBy, Label = "Changed By", Kind = FieldKind.Text, Searchable = true, Aliases = ["by"], Text = e => e.ModifiedBy },
        new() { Key = L.Added, Label = "Added", Kind = FieldKind.Date, Aliases = ["created"], Date = e => e.Created },
        new() { Key = L.Source, Label = "Added From", Kind = FieldKind.Choice, EmptyLabel = "Not Recorded", Text = e => e.Source switch
            {
                "Submission" => "A Reported Message",
                "Tenant" => "Admin",
                "AdvancedDelivery" => "Advanced Delivery",
                var other => other,
            } },
    ];

    static readonly ListQuery ByEntry = ListQuery.Empty with { Sort = [new SortLevel(L.Kind), new SortLevel(L.Value)] };

    static SavedView EntryView(string id, string name, bool pinned, params Condition[] where) =>
        new(id, name, ByEntry with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: pinned);

    static ChoiceCondition IsKind(ListKind kind) => new(L.Kind, [AllowBlock.KindName(kind)], []);

    public static IReadOnlyList<SavedView> EntryViews { get; } =
    [
        EntryView("all", "All", true),
        EntryView("blocked", "Blocked", true, new ChoiceCondition(L.Action, [L.Blocked], [])),
        EntryView("allowed", "Allowed", true, new ChoiceCondition(L.Action, [L.Allowed], [])),
        EntryView("expiring", "Expiring Within 7 Days", true, new NumberCondition(L.ExpiresInDays, null, 7)),
        EntryView("senders", "Senders and Domains", false, IsKind(ListKind.Sender)),
        EntryView("urls", "Links", false, IsKind(ListKind.Url)),
        EntryView("files", "Files", false, IsKind(ListKind.File)),
        EntryView("ips", "IP Addresses", false, IsKind(ListKind.IP)),
        EntryView("spoof", "Spoofed Senders", false, IsKind(ListKind.Spoof)),
    ];

    public static ListDefinition<ListEntry> Entries(Func<ListEntry, object> preview) => new()
    {
        Key = "allowblock",
        Title = "Block and Allow Lists",
        Noun = "entries",
        Fields = EntryFields,
        Id = e => $"{e.Kind}:{e.Identity}",
        Views = EntryViews,
        SearchHint = "Search entries and notes… or try action:blocked kind:link",
        Preview = preview,
        Columns =
        [
            new() { Key = L.Value, Header = "Entry", Cell = CellKind.Primary, MinWidth = 280, DefaultWidth = 360,
                Text = e => e.Value, Subtitle = e => e.Kind == ListKind.Spoof ? $"Sent by {e.Infrastructure}" : e.Notes,
                Initials = e => e.Kind switch { ListKind.Sender => "@", ListKind.Url => "↗", ListKind.File => "F", ListKind.IP => "IP", _ => "S" } },
            new() { Key = L.Kind, Header = "Kind", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 160, Text = e => e.KindText },
            new() { Key = L.Action, Header = "Action", Cell = CellKind.Badge, MinWidth = 100, DefaultWidth = 110,
                Badge = e => e.Block ? (L.Blocked, BadgeKind.Danger) : (L.Allowed, BadgeKind.Success) },
            new() { Key = L.Expires, Header = "Expires", Cell = CellKind.Text, MinWidth = 150, DefaultWidth = 180, Text = e => e.ExpiryText },
            new() { Key = L.LastUsed, Header = "Last Matched", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 140, Date = e => e.LastUsed, EmptyText = "Not Yet" },
            new() { Key = L.ModifiedBy, Header = "Changed By", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 200, DefaultVisible = false, Text = e => e.ModifiedBy },
            new() { Key = L.Added, Header = "Added", Cell = CellKind.Date, MinWidth = 120, DefaultWidth = 130, DefaultVisible = false, Date = e => e.Created },
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
        "FilteredAsSpam" => "Filtered as Spam",
        "GettingStatus" => "Getting Status",
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
        TraceView("quarantined", "Quarantined or Spam", "Quarantined", "FilteredAsSpam"),
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
        Title = "Message Trace",
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
