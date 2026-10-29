using System.Globalization;
using Tenantry.Core.Changes;
using Tenantry.Core.Lists;

namespace Tenantry.ViewModels.Lists;

/// <summary>
/// The Change Log (9a): every change Tenantry tried in this tenant, newest first, from the tenant's
/// encrypted local log. Searchable and filterable like any list, and exportable as CSV.
/// </summary>
public static class ChangeLogList
{
    public const string When = "when";
    public const string Who = "who";
    public const string Object = "object";
    public const string Kind = "kind";
    public const string Change = "change";
    public const string Before = "before";
    public const string After = "after";
    public const string Outcome = "outcome";
    public const string Detail = "detail";
    public const string Caution = "caution";

    public const string Yes = "Yes";
    public const string No = "No";

    /// <summary>What kind of thing a change touched, in words.</summary>
    public static string KindText(string kind) => kind switch
    {
        "user" => "Person",
        "group" => "Group",
        "mailbox" or "recipient" => "Mailbox",
        "device" or "manageddevice" => "Device",
        "autopilot" => "Autopilot Device",
        "site" => "Site",
        "role" or "customrole" => "Admin Role",
        "rolerequest" => "Role Request",
        "capolicy" => "Conditional Access Policy",
        "location" or "namedlocation" => "Named Location",
        "authmethod" or "authpolicy" => "Sign-In Methods",
        "securitydefaults" => "Security Defaults",
        AppChange.Registration => "App Registration",
        AppChange.Enterprise or "app" => "Enterprise App",
        "rule" => "Mail Flow Rule",
        "quarantine" => "Quarantined Message",
        "listentry" => "Block or Allow Entry",
        "tenant" => "Tenant",
        EdChange.CaseKind => "eDiscovery Case",
        EdChange.SearchKind => "Content Search",
        EdChange.HoldKind => "eDiscovery Hold",
        RetentionChange.PolicyKind => "Retention Policy",
        RetentionChange.LabelKind => "Retention Label",
        RetentionChange.ScopeKind => "Adaptive Scope",
        RetentionChange.EventTypeKind => "Event Type",
        RetentionChange.EventKind => "Retention Event",
        _ => kind.Length == 0 ? "Other" : char.ToUpper(kind[0], CultureInfo.InvariantCulture) + kind[1..],
    };

    public static (string Text, BadgeKind Kind) OutcomeBadge(ChangeOutcome outcome) => outcome switch
    {
        ChangeOutcome.Applied => ("Applied", BadgeKind.Success),
        ChangeOutcome.AppliedNotYetVisible => ("Applied, Not Yet Showing", BadgeKind.Info),
        ChangeOutcome.Refused => ("Stopped by Test Mode", BadgeKind.Neutral),
        _ => ("Failed", BadgeKind.Danger),
    };

    static readonly IReadOnlyList<string> Outcomes = ["Applied", "Applied, Not Yet Showing", "Failed", "Stopped by Test Mode"];

    public static IReadOnlyList<Field<ChangeLogEntry>> Fields { get; } =
    [
        new() { Key = When, Label = "When", Kind = FieldKind.Date, Date = e => e.At },
        new() { Key = Who, Label = "Who", Kind = FieldKind.Text, Searchable = true, Aliases = ["by"], Text = e => e.Account },
        new() { Key = Object, Label = "Changed", Kind = FieldKind.Text, Searchable = true, Aliases = ["target"], Text = e => $"{e.Change.Target.Name} {e.Change.Target.Address}" },
        new() { Key = Kind, Label = "Kind", Kind = FieldKind.Choice, Text = e => KindText(e.Change.Target.Kind) },
        new() { Key = Change, Label = "Change", Kind = FieldKind.Text, Searchable = true, Text = e => e.Change.Label },
        new() { Key = Before, Label = "Before", Kind = FieldKind.Text, Searchable = true, Text = e => e.Change.Before },
        new() { Key = After, Label = "After", Kind = FieldKind.Text, Searchable = true, Text = e => e.Change.After },
        new() { Key = Outcome, Label = "Outcome", Kind = FieldKind.Choice, ChoiceOrder = Outcomes, Text = e => OutcomeBadge(e.Outcome).Text },
        new() { Key = Detail, Label = "Detail", Kind = FieldKind.Text, Searchable = true, Text = e => e.Detail },
        new() { Key = Caution, Label = "Caution Change", Kind = FieldKind.Choice, ChoiceOrder = [Yes, No], Text = e => e.Change.IsCaution ? Yes : No },
    ];

    static readonly ListQuery Newest = ListQuery.Empty with { Sort = [new SortLevel(When, Descending: true)] };

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, Newest with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: true);

    public static ListDefinition<ChangeLogEntry> Definition(Func<ChangeLogEntry, object> preview) => new()
    {
        Key = "changelog",
        Title = "Change Log",
        Noun = "changes",
        Fields = Fields,
        Id = e => e.Id,
        Views =
        [
            View("all", "All Changes"),
            View("week", "Last 7 Days", new DateCondition(When, DateOp.WithinLastDays, 7)),
            View("failed", "Failed or Stopped", new ChoiceCondition(Outcome, ["Failed", "Stopped by Test Mode"], [])),
            View("caution", "Caution Changes", new ChoiceCondition(Caution, [Yes], [])),
        ],
        SearchHint = "Search who, what changed and why… or try outcome:failed",
        Preview = preview,
        Columns =
        [
            new() { Key = When, Header = "When", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 160, Date = e => e.At },
            new() { Key = Object, Header = "Changed", Cell = CellKind.Primary, MinWidth = 200, DefaultWidth = 260, Text = e => e.Change.Target.Name,
                Subtitle = e => KindText(e.Change.Target.Kind), Initials = e => Display.Initials(e.Change.Target.Name) },
            new() { Key = Change, Header = "Change", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 240, Text = e => e.Change.Label },
            new() { Key = After, Header = "To", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 260, Text = e => e.Change.After },
            new() { Key = Outcome, Header = "Outcome", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 160, Badge = e => OutcomeBadge(e.Outcome) },
            new() { Key = Who, Header = "Who", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 200, Text = e => e.Account },
            new() { Key = Before, Header = "From", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 220, Text = e => e.Change.Before, DefaultVisible = false },
            new() { Key = Kind, Header = "Kind", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 160, Text = e => KindText(e.Change.Target.Kind), DefaultVisible = false },
            new() { Key = Detail, Header = "Detail", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 300, Text = e => e.Detail ?? "", DefaultVisible = false },
        ],
    };
}
