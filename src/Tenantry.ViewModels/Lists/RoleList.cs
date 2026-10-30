using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.RoleFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>Admin roles: every directory role with who holds it, and views for a privileged-access review.</summary>
public static class RoleList
{
    static readonly ListQuery ByName = ListQuery.Empty with { Sort = [new SortLevel(F.Name)] };

    static readonly HashSet<string> PinnedByDefault = ["in-use", "privileged", "permanent", "custom"];

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, ByName with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: PinnedByDefault.Contains(id));

    static readonly Condition InUse = ConditionGroup.Any(new NumberCondition(F.Active, 1, null), new NumberCondition(F.Eligible, 1, null));

    public static IReadOnlyList<SavedView> Views { get; } =
    [
        View("in-use", "Roles in Use", InUse),
        View("privileged", "Privileged Roles in Use", InUse, new ChoiceCondition(F.Privileged, [F.Yes], [])),
        // People holding a privileged role permanently: what to move to eligible (PIM) or remove.
        View("permanent", "Permanent Privileged Admins", new ChoiceCondition(F.Privileged, [F.Yes], []), new NumberCondition(F.Permanent, 1, null)),
        View("custom", "Custom Roles", new ChoiceCondition(F.Type, [F.Custom], [])),
        View("all", "All Roles"),
    ];

    public static (string Text, BadgeKind Kind)? PrivilegedBadge(RoleSummary r) => r.Role.IsPrivileged ? (F.Yes, BadgeKind.Warning) : null;

    /// <summary>"Ada Lovelace, Bo Diaz and 3 more".</summary>
    public static string Holders(RoleSummary r)
    {
        var names = r.Grants.Select(g => g.PrincipalName).Distinct().ToArray();
        return names.Length switch
        {
            0 => "Nobody",
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{names[0]}, {names[1]} and {names.Length - 2} more",
        };
    }

    public static ListDefinition<RoleSummary> Definition(Func<RoleSummary, object> preview) => new()
    {
        Key = "roles",
        Title = "Admin Roles",
        Noun = "roles",
        Fields = F.All,
        Id = r => r.Role.Id,
        Views = Views,
        SearchHint = "Search roles and who holds them, like Exchange or a person's name",
        Preview = preview,
        Columns =
        [
            new() { Key = F.Name, Header = "Role", Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 300,
                Text = r => r.Role.Name, Subtitle = r => r.Role.Description, Initials = r => Display.Initials(r.Role.Name) },
            new() { Key = F.Privileged, Header = "Privileged", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 120,
                Badge = r => PrivilegedBadge(r) ?? ("", BadgeKind.Neutral) },
            new() { Key = F.Active, Header = "Active", Cell = CellKind.Number, MinWidth = 80, DefaultWidth = 90, Number = r => r.Active },
            new() { Key = F.Eligible, Header = "Eligible", Cell = CellKind.Number, MinWidth = 80, DefaultWidth = 90, Number = r => r.Eligible },
            new() { Key = F.Holders, Header = "Held By", Cell = CellKind.Text, MinWidth = 220, DefaultWidth = 320, Text = Holders },
            new() { Key = F.Type, Header = "Type", Cell = CellKind.Text, MinWidth = 90, DefaultWidth = 100, Text = r => r.Role.IsBuiltIn ? F.BuiltIn : F.Custom },
            new() { Key = F.Permanent, Header = "Permanent Assignments", Cell = CellKind.Number, MinWidth = 130, DefaultWidth = 140, DefaultVisible = false,
                Number = r => r.PermanentPeople },
        ],
    };
}
