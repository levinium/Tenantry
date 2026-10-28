using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.CaFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>Conditional Access policies in plain words: who, what they require, and whether they're on.</summary>
public static class CaList
{
    static readonly ListQuery ByName = ListQuery.Empty with { Sort = [new SortLevel(F.Name)] };

    static readonly HashSet<string> PinnedByDefault = ["all", "on", "report", "off"];

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, ByName with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: PinnedByDefault.Contains(id));

    public static IReadOnlyList<SavedView> Views { get; } =
    [
        View("all", "All Policies"),
        View("on", "On", new ChoiceCondition(F.State, ["On"], [])),
        // Report-only policies record what they'd do: check them before switching them on.
        View("report", "Report-Only", new ChoiceCondition(F.State, ["Report-Only"], [])),
        View("off", "Off", new ChoiceCondition(F.State, ["Off"], [])),
        View("block", "Block Access", new TextCondition(F.Requires, TextOp.Contains, "Block")),
        View("mfa", "Require MFA", new TextCondition(F.Requires, TextOp.Contains, "MFA")),
    ];

    public static (string Text, BadgeKind Kind) StateBadge(CaPolicy p) => p.State switch
    {
        "enabled" => ("On", BadgeKind.Success),
        "enabledForReportingButNotEnforced" => ("Report-Only", BadgeKind.Info),
        _ => ("Off", BadgeKind.Neutral),
    };

    public static ListDefinition<CaPolicyRow> Definition(Func<CaPolicyRow, object> preview) => new()
    {
        Key = "capolicies",
        Title = "Conditional Access",
        Noun = "policies",
        Fields = F.All,
        Id = p => p.Policy.Id,
        Views = Views,
        SearchHint = "Search policies, who they cover and what they require… or try state:on",
        Preview = preview,
        Columns =
        [
            new() { Key = F.Name, Header = "Policy", Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 300,
                Text = p => p.Policy.Name, Subtitle = p => p.Apps is { Length: > 0 } apps ? $"Apps: {apps}" : "", Initials = p => Display.Initials(p.Policy.Name) },
            new() { Key = F.State, Header = "State", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 120, Badge = p => StateBadge(p.Policy) },
            new() { Key = F.Who, Header = "Who", Cell = CellKind.Text, MinWidth = 180, DefaultWidth = 240, Text = p => p.Who },
            new() { Key = F.Requires, Header = "Then", Cell = CellKind.Text, MinWidth = 200, DefaultWidth = 280, Text = p => p.Requires },
            new() { Key = F.Modified, Header = "Changed", Cell = CellKind.Date, MinWidth = 120, DefaultWidth = 130, Date = p => p.Policy.Modified ?? p.Policy.Created },
            new() { Key = F.Created, Header = "Created", Cell = CellKind.Date, MinWidth = 120, DefaultWidth = 130, DefaultVisible = false, Date = p => p.Policy.Created },
        ],
    };
}
