using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.AutopilotFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>Windows Autopilot devices: serial, group tag, profile and enrollment, with clean-up views.</summary>
public static class AutopilotList
{
    static readonly ListQuery BySerial = ListQuery.Empty with { Sort = [new SortLevel(F.Serial)] };

    static readonly HashSet<string> PinnedByDefault = ["all", "no-profile", "not-contacted"];

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, BySerial with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: PinnedByDefault.Contains(id));

    static ChoiceCondition Is(string field, string value) => new(field, [value], []);

    public static IReadOnlyList<SavedView> Views { get; } =
    [
        View("all", "All Autopilot Devices"),
        // Without a profile, Autopilot won't set it up the organization's way.
        View("no-profile", "No Profile Assigned", new ChoiceCondition(F.ProfileStatus, ["Not Assigned", "Failed"], [])),
        View("not-contacted", "Never Set Up", Is(F.Enrollment, "Not Contacted")),
        View("enrolled", "Enrolled", Is(F.Enrollment, "Enrolled")),
        View("no-tag", "No Group Tag", new TextCondition(F.GroupTag, TextOp.IsEmpty)),
        View("stale", "Not Contacted in 180+ Days",
            ConditionGroup.Any(new DateCondition(F.LastContacted, DateOp.OlderThanDays, 180), new DateCondition(F.LastContacted, DateOp.Never))),
    ];

    public static (string Text, BadgeKind Kind) ProfileBadge(AutopilotDevice d) => d.ProfileStatus switch
    {
        "Assigned" => ("Assigned", BadgeKind.Success),
        "Pending" => ("Pending", BadgeKind.Info),
        "Failed" => ("Failed", BadgeKind.Danger),
        "Not Assigned" => ("Not Assigned", BadgeKind.Warning),
        var other => (other, BadgeKind.Neutral),
    };

    public static (string Text, BadgeKind Kind) EnrollmentBadge(AutopilotDevice d) => d.EnrollmentState switch
    {
        "Enrolled" => ("Enrolled", BadgeKind.Success),
        "Failed" or "Blocked" => (d.EnrollmentState, BadgeKind.Danger),
        "Not Contacted" => ("Not Contacted", BadgeKind.Neutral),
        var other => (other, BadgeKind.Info),
    };

    public static ListDefinition<AutopilotDevice> Definition(Func<AutopilotDevice, object> preview) => new()
    {
        Key = "autopilot",
        Title = "Autopilot",
        Noun = "Autopilot devices",
        Fields = F.All,
        Id = d => d.Id,
        Views = Views,
        SearchHint = "Search serial numbers, models, group tags, profiles… or try grouptag:sales",
        Preview = preview,
        Actions =
        [
            new RowAction<AutopilotDevice>("Open in Intune Admin Center", "Globe",
                _ => Platform.OpenUrl("https://intune.microsoft.com/#view/Microsoft_Intune_Enrollment/AutopilotDevices.ReactView")),
        ],
        Columns =
        [
            new() { Key = F.Serial, Header = "Serial Number", Cell = CellKind.Primary, MinWidth = 200, DefaultWidth = 230,
                Text = d => d.Serial,
                Subtitle = d => string.Join(' ', new[] { d.Manufacturer, d.Model }.Where(s => !string.IsNullOrEmpty(s)).Distinct()),
                Initials = _ => "A" },
            new() { Key = F.GroupTag, Header = "Group Tag", Cell = CellKind.Text, MinWidth = 130, DefaultWidth = 150, Text = d => d.GroupTag, EmptyText = "None" },
            new() { Key = F.Profile, Header = "Profile", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 200, Text = d => d.ProfileName },
            new() { Key = F.ProfileStatus, Header = "Profile Status", Cell = CellKind.Badge, MinWidth = 130, DefaultWidth = 140, Badge = ProfileBadge },
            new() { Key = F.Enrollment, Header = "Enrollment", Cell = CellKind.Badge, MinWidth = 130, DefaultWidth = 140, Badge = EnrollmentBadge },
            new() { Key = F.LastContacted, Header = "Last Contacted", Cell = CellKind.Date, MinWidth = 140, DefaultWidth = 150, Date = d => d.LastContacted, EmptyText = "Never" },
            new() { Key = F.User, Header = "Assigned User", Cell = CellKind.Text, MinWidth = 180, DefaultWidth = 220, DefaultVisible = false, Text = d => d.AssignedUser },
            new() { Key = F.PurchaseOrder, Header = "Purchase Order", Cell = CellKind.Text, MinWidth = 130, DefaultWidth = 150, DefaultVisible = false, Text = d => d.PurchaseOrder },
            new() { Key = F.Model, Header = "Model", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 200, DefaultVisible = false,
                Text = d => string.Join(' ', new[] { d.Manufacturer, d.Model }.Where(s => !string.IsNullOrEmpty(s)).Distinct()) },
        ],
    };
}
