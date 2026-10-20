using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.GroupFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>The Groups list: Microsoft 365 groups, security groups and distribution lists together.</summary>
public static class GroupList
{
    static readonly ListQuery ByName = ListQuery.Empty with { Sort = [new SortLevel(F.Name)] };
    static readonly HashSet<string> PinnedByDefault = ["all", "m365", "security", "distribution"];

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, ByName with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: PinnedByDefault.Contains(id));

    static ChoiceCondition Is(string field, string value) => new(field, [value], []);

    static string Kind(GroupKind kind) => GroupSummary.KindName(kind);

    public static IReadOnlyList<SavedView> Views { get; } =
    [
        View("all", "All groups"),
        View("m365", "Microsoft 365", Is(F.Type, Kind(GroupKind.Microsoft365))),
        View("security", "Security", new ChoiceCondition(F.Type, [Kind(GroupKind.Security), Kind(GroupKind.MailEnabledSecurity)], [])),
        View("distribution", "Distribution lists", Is(F.Type, Kind(GroupKind.Distribution))),
        View("teams", "Teams", Is(F.Team, F.Yes)),
        View("dynamic", "Dynamic membership", Is(F.Membership, F.Dynamic)),
        View("empty", "Empty groups", new NumberCondition(F.Members, null, 0)),
        View("public", "Public Microsoft 365 groups", Is(F.Type, Kind(GroupKind.Microsoft365)), Is(F.Visibility, "Public")),
    ];

    public static (string Text, BadgeKind Kind) TypeBadge(GroupKind kind) => kind switch
    {
        GroupKind.Microsoft365 => (Kind(kind), BadgeKind.Info),
        GroupKind.Distribution => (Kind(kind), BadgeKind.Warning),
        _ => (Kind(kind), BadgeKind.Neutral),
    };

    public static ListDefinition<GroupSummary> Definition(Func<GroupSummary, object> preview) => new()
    {
        Key = "groups",
        Title = "Groups",
        Noun = "groups",
        Fields = F.All,
        Id = g => g.Id,
        Views = Views,
        SearchHint = "Search names, addresses, descriptions… or try type:security members:0",
        Preview = preview,
        Actions =
        [
            new RowAction<GroupSummary>("Open in Entra admin center", "Globe",
                g => Platform.OpenUrl($"https://entra.microsoft.com/#view/Microsoft_AAD_IAM/GroupDetailsMenuBlade/~/Overview/groupId/{g.Id}")),
        ],
        Columns =
        [
            new() { Key = F.Name, Header = "Name", Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 300,
                Text = g => g.DisplayName,
                Subtitle = g => g.Mail ?? g.Description,
                Initials = g => Display.Initials(g.DisplayName) },
            new() { Key = F.Type, Header = "Type", Cell = CellKind.Badge, MinWidth = 150, DefaultWidth = 180, Badge = g => TypeBadge(g.Kind) },
            new() { Key = F.Members, Header = "Members", Cell = CellKind.Number, MinWidth = 100, DefaultWidth = 110, Number = g => g.Members.Count },
            new() { Key = F.Membership, Header = "Membership", Cell = CellKind.Badge, MinWidth = 120, DefaultWidth = 130,
                Badge = g => g.IsDynamic ? (F.Dynamic, BadgeKind.Info) : (F.Assigned, BadgeKind.Neutral) },
            new() { Key = F.Team, Header = "Team", Cell = CellKind.Text, MinWidth = 80, DefaultWidth = 90, Text = g => g.IsTeam ? F.Yes : null },
            new() { Key = F.Visibility, Header = "Visibility", Cell = CellKind.Text, MinWidth = 110, DefaultWidth = 120, Text = g => g.Visibility },
            new() { Key = F.Created, Header = "Created", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 140, Date = g => g.CreatedAt },
            new() { Key = F.Email, Header = "Email", Cell = CellKind.Text, MinWidth = 200, DefaultWidth = 240, DefaultVisible = false, Text = g => g.Mail },
            new() { Key = F.Description, Header = "Description", Cell = CellKind.Text, MinWidth = 200, DefaultWidth = 300, DefaultVisible = false, Text = g => g.Description },
        ],
    };
}
