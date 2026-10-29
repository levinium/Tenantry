using System.Globalization;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;
using F = Tenantry.Core.Directory.EdFields;

namespace Tenantry.ViewModels.Records;

/// <summary>The eDiscovery lists (8f): cases, and every search; with their previews.</summary>
public static class EdLists
{
    static SavedView View(ListQuery sort, string id, string name, params Condition[] where) =>
        new(id, name, sort with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: true);

    static readonly ListQuery NewestChanged = ListQuery.Empty with { Sort = [new SortLevel(F.Modified, Descending: true)] };
    static readonly ListQuery NewestCreated = ListQuery.Empty with { Sort = [new SortLevel(F.Created, Descending: true)] };

    static string Day(DateTimeOffset? when) => when is { } w ? w.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "Unknown";

    public static (string Text, BadgeKind Kind) CaseBadge(EdCase c) => c.IsClosed ? (F.Closed, BadgeKind.Neutral) : (F.Active, BadgeKind.Success);

    public static (string Text, BadgeKind Kind) SearchBadge(string status) => (EDiscovery.SearchStatus(status), status switch
    {
        "Completed" => BadgeKind.Success,
        "Starting" or "InProgress" => BadgeKind.Info,
        "Failed" or "PartiallySucceeded" => BadgeKind.Warning,
        _ => BadgeKind.Neutral,
    });

    public static ListDefinition<EdCase> Cases(Func<EdCase, object> preview) => new()
    {
        Key = "ediscoverycases",
        Title = "eDiscovery Cases",
        Noun = "cases",
        Fields = F.Cases,
        Id = c => c.Id,
        Views =
        [
            View(NewestChanged, "active", "Active Cases", new ChoiceCondition(F.Status, [F.Active], [])),
            View(NewestChanged, "closed", "Closed Cases", new ChoiceCondition(F.Status, [F.Closed], [])),
            View(NewestChanged, "all", "All Cases"),
        ],
        SearchHint = "Search cases… or try status:active",
        Preview = preview,
        Columns =
        [
            new() { Key = F.Name, Header = "Case", Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 320, Text = c => c.Name, Subtitle = c => c.Description ?? "",
                Initials = c => Display.Initials(c.Name) },
            new() { Key = F.Status, Header = "Status", Cell = CellKind.Badge, MinWidth = 100, DefaultWidth = 110, Badge = CaseBadge },
            new() { Key = F.Modified, Header = "Last Changed", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = c => c.Modified },
            new() { Key = F.ModifiedBy, Header = "Last Changed By", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 180, Text = c => c.ModifiedBy ?? "" },
            new() { Key = F.Created, Header = "Created", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = c => c.Created },
        ],
    };

    public static ListDefinition<EdSearch> Searches(Func<EdSearch, object> preview) => new()
    {
        Key = "ediscoverysearches",
        Title = "Content Searches",
        Noun = "searches",
        Fields = F.Searches,
        Id = s => (s.CaseName ?? "") + "/" + s.Name,
        Views =
        [
            View(NewestCreated, "all", "All Searches"),
            View(NewestCreated, "nocase", "Not in a Case", new ChoiceCondition(F.Case, [F.NoCase], [])),
            View(NewestCreated, "notrun", "Not Run", new ChoiceCondition(F.Status, ["Not Run"], [])),
        ],
        SearchHint = "Search searches and cases… or try case:\"not in a case\"",
        Preview = preview,
        Columns =
        [
            new() { Key = F.Name, Header = "Search", Cell = CellKind.Primary, MinWidth = 220, DefaultWidth = 280, Text = s => s.Name, Subtitle = s => s.Description ?? "",
                Initials = s => Display.Initials(s.Name) },
            new() { Key = F.Case, Header = "Case", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 220, Text = s => s.CaseName ?? F.NoCase },
            new() { Key = F.Status, Header = "Status", Cell = CellKind.Badge, MinWidth = 100, DefaultWidth = 110, Badge = s => SearchBadge(s.Status) },
            new() { Key = F.LastRun, Header = "Last Run", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = s => s.LastRun },
            new() { Key = F.CreatedBy, Header = "Created By", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 170, Text = s => s.CreatedBy ?? "" },
            new() { Key = F.Created, Header = "Created", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = s => s.Created, DefaultVisible = false },
        ],
    };

    public static AppPreviewViewModel CasePreview(EdCase c, Action<EdCase>? open)
    {
        var (text, kind) = CaseBadge(c);
        return new AppPreviewViewModel(c.Name, c.Description, [new HealthChip(text, kind, c.IsClosed ? "Its holds no longer keep anything" : "Open for work")],
            [
                new("Created", Day(c.Created)),
                new("Last Changed", c.ModifiedBy is { } by ? $"{Day(c.Modified)} by {by}" : Day(c.Modified)),
                .. c.IsClosed ? [new SummaryLine("Closed", c.ClosedBy is { } closer ? $"{Day(c.Closed)} by {closer}" : Day(c.Closed))] : Array.Empty<SummaryLine>(),
            ],
            "Open Case", open is null ? null : () => open(c));
    }

    public static AppPreviewViewModel SearchPreview(EdSearch s, Action<EdSearch>? open)
    {
        var (text, kind) = SearchBadge(s.Status);
        return new AppPreviewViewModel(s.Name, s.CaseName is { } c ? $"In case {c}" : F.NoCase, [new HealthChip(text, kind, "When it last ran, and how that went")],
            [
                .. s.Description is { } d ? [new SummaryLine("Description", d)] : Array.Empty<SummaryLine>(),
                new("Created", s.CreatedBy is { } by ? $"{Day(s.Created)} by {by}" : Day(s.Created)),
                new("Last Run", s.LastRun is null ? "Never" : s.RunBy is { } runner ? $"{Day(s.LastRun)} by {runner}" : Day(s.LastRun)),
            ],
            "Open Search", open is null ? null : () => open(s));
    }
}
