using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.DeletedSiteFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>Deleted sites, newest first, with how long each can still be restored.</summary>
public static class DeletedSiteList
{
    static readonly ListQuery Newest = ListQuery.Empty with { Sort = [new SortLevel(F.Deleted, Descending: true)] };

    static readonly HashSet<string> PinnedByDefault = ["all", "expiring"];

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, Newest with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: PinnedByDefault.Contains(id));

    public static IReadOnlyList<SavedView> Views { get; } =
    [
        View("all", "All Deleted Sites"),
        // Gone for good soon: restore now or never.
        View("expiring", "Gone Within 7 Days", new NumberCondition(F.DaysLeft, null, 7)),
        View("groups", "Microsoft 365 Groups", new ChoiceCondition(F.Kind, [F.Group], [])),
        View("onedrives", "OneDrives", new ChoiceCondition(F.Kind, [F.OneDrive], [])),
    ];

    public static (string Text, BadgeKind Kind) DaysBadge(DeletedSite s) => s.DaysLeft switch
    {
        null => ("Unknown", BadgeKind.Neutral),
        <= 7 and var d => (d == 1 ? "1 Day Left" : $"{d} Days Left", BadgeKind.Danger),
        var d => ($"{d} Days Left", BadgeKind.Neutral),
    };

    public static ListDefinition<DeletedSite> Definition(Func<DeletedSite, object> preview) => new()
    {
        Key = "deletedsites",
        Title = "Deleted Sites",
        Noun = "deleted sites",
        Fields = F.All,
        Id = s => s.Id,
        Views = Views,
        SearchHint = "Search names and addresses…",
        Preview = preview,
        Columns =
        [
            new() { Key = F.Name, Header = "Name", Cell = CellKind.Primary, MinWidth = 220, DefaultWidth = 280,
                Text = s => s.Name, Subtitle = s => s.Url, Initials = s => Display.Initials(s.Name) },
            new() { Key = F.Kind, Header = "Type", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 220, Text = F.KindOf },
            new() { Key = F.Deleted, Header = "Deleted", Cell = CellKind.Date, MinWidth = 140, DefaultWidth = 160, Date = s => s.DeletedAt },
            new() { Key = F.DaysLeft, Header = "Can Be Restored For", Cell = CellKind.Badge, MinWidth = 150, DefaultWidth = 170, Badge = DaysBadge },
        ],
    };
}
