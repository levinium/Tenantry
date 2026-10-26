using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.GroupFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>
/// Groups &amp; shared mailboxes: Microsoft 365 groups, security groups, distribution lists, and
/// the shared, room and equipment mailboxes managed the same way (by who can use them).
/// </summary>
public static class GroupList
{
    public const string AreaTitle = "Groups & Shared Mailboxes";

    static readonly ListQuery ByName = ListQuery.Empty with { Sort = [new SortLevel(F.Name)] };
    static readonly HashSet<string> PinnedByDefault = ["all", "m365", "security", "distribution", "shared"];

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, ByName with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: PinnedByDefault.Contains(id));

    static ChoiceCondition Is(string field, string value) => new(field, [value], []);

    static string Kind(GroupKind kind) => GroupSummary.KindName(kind);

    static readonly string[] GroupKinds = [Kind(GroupKind.Microsoft365), Kind(GroupKind.Security), Kind(GroupKind.MailEnabledSecurity), Kind(GroupKind.Distribution)];

    public static IReadOnlyList<SavedView> Views { get; } =
    [
        View("all", "All"),
        View("m365", "Microsoft 365", Is(F.Type, Kind(GroupKind.Microsoft365))),
        View("security", "Security", new ChoiceCondition(F.Type, [Kind(GroupKind.Security), Kind(GroupKind.MailEnabledSecurity)], [])),
        View("distribution", "Distribution Lists", Is(F.Type, Kind(GroupKind.Distribution))),
        View("shared", "Shared Mailboxes", Is(F.Type, Kind(GroupKind.SharedMailbox))),
        View("rooms", "Rooms & Equipment", new ChoiceCondition(F.Type, [Kind(GroupKind.RoomMailbox), Kind(GroupKind.EquipmentMailbox)], [])),
        View("groups", "Groups Only", new ChoiceCondition(F.Type, GroupKinds, [])),
        View("teams", "Teams", Is(F.Team, F.Yes)),
        View("dynamic", "Dynamic Membership", Is(F.Membership, F.Dynamic)),
        View("empty", "Empty Groups", new ChoiceCondition(F.Type, GroupKinds, []), new NumberCondition(F.Members, null, 0)),
        View("nobody", "Shared Mailboxes Nobody Can Use", Is(F.Type, Kind(GroupKind.SharedMailbox)), new NumberCondition(F.Members, null, 0)),
        View("public", "Public Microsoft 365 Groups", Is(F.Type, Kind(GroupKind.Microsoft365)), Is(F.Visibility, "Public")),
    ];

    public static (string Text, BadgeKind Kind) TypeBadge(GroupKind kind) => kind switch
    {
        GroupKind.Microsoft365 => (Kind(kind), BadgeKind.Info),
        GroupKind.Distribution => (Kind(kind), BadgeKind.Warning),
        GroupKind.SharedMailbox or GroupKind.RoomMailbox or GroupKind.EquipmentMailbox => (Kind(kind), BadgeKind.Success),
        _ => (Kind(kind), BadgeKind.Neutral),
    };

    public static ListDefinition<GroupSummary> Definition(Func<GroupSummary, object> preview) => new()
    {
        Key = "groups",
        Title = AreaTitle,
        Noun = "groups and mailboxes",
        Fields = F.All,
        Id = g => g.Id,
        Views = Views,
        SearchHint = "Search names, addresses, descriptions… or try type:\"shared mailbox\" members:0",
        Preview = preview,
        Actions =
        [
            // Mailboxes: the Exchange admin center's mailbox list (it has no stable link to one mailbox).
            new RowAction<GroupSummary>("Open in Admin Center", "Globe",
                g => Platform.OpenUrl(g.IsMailbox
                    ? "https://admin.exchange.microsoft.com/#/mailboxes"
                    : $"https://entra.microsoft.com/#view/Microsoft_AAD_IAM/GroupDetailsMenuBlade/~/Overview/groupId/{g.Id}")),
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
