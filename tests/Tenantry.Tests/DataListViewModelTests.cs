using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;
using F = Tenantry.Core.Directory.UserFields;

namespace Tenantry.Tests;

public class DataListViewModelTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static UserSummary U(string name, string? dept = null, bool enabled = true, UserKind kind = UserKind.Member,
        int? signedInDaysAgo = null, params string[] licenses) =>
        new(name, name, $"{name.ToLowerInvariant()}@contoso.example", $"{name.ToLowerInvariant()}@contoso.example", dept, null,
            enabled, kind, Now.AddDays(-400), signedInDaysAgo is { } d ? Now.AddDays(-d) : null, licenses);

    static readonly UserSummary[] People =
    [
        U("Ada", "Finance", signedInDaysAgo: 2, licenses: ["E3"]),
        U("Alan", "IT", signedInDaysAgo: 120, licenses: ["E5", "Visio"]),
        U("Grace", "IT", enabled: false, signedInDaysAgo: 200, licenses: ["E3"]),
        U("Guest, Pat", kind: UserKind.Guest),
        U("Linus", "IT", signedInDaysAgo: 40),
    ];

    static async Task<(DataListViewModel List, List<UserSummary> Opened)> Loaded(IListStateStore? store = null)
    {
        var opened = new List<UserSummary>();
        var list = DataListViewModel.Create(UserList.Definition(() => Now),
            _ => Task.FromResult<IReadOnlyList<UserSummary>>(People), opened.Add, store, () => Now);
        await list.LoadAsync(Ct);
        return (list, opened);
    }

    static string[] Names(DataListViewModel list) => list.Rows.Cast<UserSummary>().Select(u => u.DisplayName).ToArray();

    static FieldOption FieldOf(DataListViewModel list, string key) => list.FilterFields.Single(f => f.Key == key);

    [Fact]
    public async Task Starts_on_the_first_built_in_view_sorted_by_name_with_view_counts()
    {
        var (list, _) = await Loaded();

        Assert.Equal(["Ada", "Alan", "Grace", "Guest, Pat", "Linus"], Names(list));
        Assert.Equal("All users", list.CurrentViewName);
        var counts = list.Views.ToDictionary(v => v.View.Id, v => v.Count);
        Assert.Equal(5, counts["all"]);
        Assert.Equal(1, counts["blocked"]);
        Assert.Equal(1, counts["licensed-blocked"]);
        Assert.Equal(2, counts["inactive"]);          // Alan (120 days) and the guest (never); Grace is blocked
        Assert.Equal(1, counts["unlicensed"]);        // Linus
        Assert.Equal(1, counts["guests-never"]);
    }

    [Fact]
    public async Task Hiding_blocked_users_with_the_choice_editor()
    {
        var (list, _) = await Loaded();

        list.EditFilter(FieldOf(list, F.Status));
        var editor = Assert.IsType<ChoiceFilterEditor>(list.Editor);
        Assert.Equal([("Active", 4), ("Blocked", 1)], editor.Values.Select(v => (v.Value, v.Count)));
        editor.Values.Single(v => v.Value == "Blocked").IsChecked = true;
        editor.HideSelected = true;
        list.ApplyFilter();

        Assert.DoesNotContain("Grace", Names(list));
        Assert.Equal("Status: not Blocked", list.Pills.Single().Text);
        Assert.Equal("All users (changed)", list.CurrentViewName);

        list.Pills.Single().RemoveCommand.Execute(null);
        Assert.Contains("Grace", Names(list));
        Assert.False(list.HasFilters);
    }

    [Fact]
    public async Task Filtering_by_license_including_unlicensed()
    {
        var (list, _) = await Loaded();

        list.EditFilter(FieldOf(list, F.Licenses));
        var editor = (ChoiceFilterEditor)list.Editor!;
        Assert.Equal(F.Unlicensed, editor.Values[^1].Value);
        editor.Values.Single(v => v.Value == "E5").IsChecked = true;
        editor.Values.Single(v => v.Value == F.Unlicensed).IsChecked = true;
        list.ApplyFilter();

        Assert.Equal(["Alan", "Guest, Pat", "Linus"], Names(list));
        Assert.Equal("Licenses: E5, Unlicensed", list.Pills.Single().Text);
    }

    [Fact]
    public async Task Search_shortcuts_filter_and_say_what_they_understood()
    {
        var (list, _) = await Loaded();

        list.SearchText = "dept:it -status:blocked";

        Assert.Equal(["Alan", "Linus"], Names(list));
        Assert.Equal("Filtering: Department: it · Status: not blocked", list.SearchUnderstood);
        Assert.Empty(list.Pills);

        list.SearchText = "signin:soon";
        Assert.NotNull(list.SearchProblem);
    }

    [Fact]
    public async Task Header_sort_toggles_and_multi_level_shows_priorities()
    {
        var (list, _) = await Loaded();
        var dept = list.Columns.Single(c => c.Key == F.Department);
        var name = list.Columns.Single(c => c.Key == F.Name);

        list.SortByColumn(dept);
        Assert.Equal(SortState.Ascending, dept.Sort);
        Assert.Equal(SortState.None, name.Sort);
        Assert.False(dept.ShowPriority);

        list.SortByColumn(dept);
        Assert.Equal(SortState.Descending, dept.Sort);

        list.AddSortByColumn(name);
        Assert.Equal((1, true), (dept.SortPriority, dept.ShowPriority));
        Assert.Equal((2, "2"), (name.SortPriority, name.PriorityText));
        Assert.Equal(["Alan", "Grace", "Linus", "Ada", "Guest, Pat"], Names(list));
        Assert.Equal("Sort: 2 levels", list.SortSummary);
    }

    [Fact]
    public async Task Sort_panel_edits_reorders_and_applies_levels()
    {
        var (list, _) = await Loaded();

        list.OpenSortPanel();
        Assert.Single(list.SortLevels);                      // the view's name sort
        list.AddSortLevel();
        list.SortLevels[1].Field = list.SortFields.Single(f => f.Key == F.LastSignIn);
        list.SortLevels[1].Descending = true;
        list.MoveSortLevelUpCommand.Execute(list.SortLevels[1]);
        Assert.Equal(("Sort by", "Then by"), (list.SortLevels[0].LevelText, list.SortLevels[1].LevelText));
        Assert.Equal("Newest first", list.SortLevels[0].DirectionText);
        list.ApplySort();

        Assert.Equal(["Ada", "Linus", "Alan", "Grace", "Guest, Pat"], Names(list));
        Assert.False(list.IsSortPanelOpen);
    }

    [Fact]
    public async Task Grouping_puts_each_group_together()
    {
        var (list, _) = await Loaded();

        list.SelectedGroup = list.GroupOptions.Single(g => g.Key == F.Status);

        Assert.True(list.IsGrouped);
        Assert.Equal("Blocked", list.GroupKeyOf(list.Rows[^1]));
        Assert.Equal("Grace", Names(list)[^1]);
    }

    [Fact]
    public async Task Query_builder_or_rules_become_one_advanced_pill()
    {
        var (list, _) = await Loaded();

        list.OpenBuilder();
        var root = list.Builder!;
        root.MatchAny = true;
        root.Rules[0].Field = root.Rules[0].Fields.Single(f => f.Key == F.LastSignIn);
        root.Rules[0].Op = root.Rules[0].Ops.Single(o => o.Key == "never");
        root.AddRule();
        root.Rules[1].Field = root.Rules[1].Fields.Single(f => f.Key == F.Licenses);
        root.Rules[1].Op = root.Rules[1].Ops.Single(o => o.Key == "is");
        root.Rules[1].Value = "Visio";
        list.ApplyBuilder();

        Assert.Equal(["Alan", "Guest, Pat"], Names(list));
        Assert.Equal("Advanced: 2 rules", list.Pills.Single().Text);

        // Reopening shows the same rules.
        list.Pills.Single().EditCommand.Execute(null);
        Assert.True(list.Builder!.MatchAny);
        Assert.Equal(2, list.Builder.Rules.Count);
    }

    [Fact]
    public async Task Date_and_number_editors()
    {
        var (list, _) = await Loaded();

        list.EditFilter(FieldOf(list, F.LastSignIn));
        var date = (DateFilterEditor)list.Editor!;
        date.Preset = DateFilterEditor.Presets.Single(p => p.CustomDays && p.Op == DateOp.OlderThanDays);
        date.DaysText = "100";
        list.ApplyFilter();
        Assert.Equal(["Alan", "Grace"], Names(list));
        Assert.Equal("Last sign-in: over 100 days ago", list.Pills.Single().Text);

        list.EditFilter(FieldOf(list, F.LicenseCount));
        ((NumberFilterEditor)list.Editor!).MinText = "2";
        list.ApplyFilter();
        Assert.Equal(["Alan"], Names(list));
    }

    [Fact]
    public async Task Views_save_apply_and_survive_a_restart()
    {
        var store = new MemoryListStateStore();
        var (list, _) = await Loaded(store);

        list.SearchText = "";
        list.EditFilter(FieldOf(list, F.Department));
        ((ChoiceFilterEditor)list.Editor!).Values.Single(v => v.Value == "IT").IsChecked = true;
        list.ApplyFilter();
        list.ToggleColumn(list.Columns.Single(c => c.Key == F.Title));
        list.MoveColumnUp(list.Columns.Single(c => c.Key == F.Licenses));
        list.ToggleDensity();
        list.NewViewName = "IT people";
        list.SaveViewAs();
        Assert.Equal("IT people", list.CurrentViewName);
        Assert.Equal(3, list.Views.Single(v => v.Name == "IT people").Count);

        list.ApplyView(list.Views.Single(v => v.View.Id == "guests"));
        Assert.Equal(["Guest, Pat"], Names(list));

        // A fresh list over the same store comes back as it was left, with the saved view.
        var (again, _) = await Loaded(store);
        Assert.Equal(["Guest, Pat"], Names(again));
        Assert.Equal("Guests", again.CurrentViewName);
        Assert.Contains(again.Views, v => v.Name == "IT people" && !v.IsBuiltIn);
        Assert.True(again.IsCompact);
        Assert.False(again.Columns.Single(c => c.Key == F.Title).IsVisible);
        var order = again.Columns.Select(c => c.Key).ToList();
        Assert.True(order.IndexOf(F.Licenses) < order.IndexOf(F.Type));

        again.ApplyView(again.Views.Single(v => v.Name == "IT people"));
        Assert.Equal(["Alan", "Grace", "Linus"], Names(again));
    }

    [Fact]
    public async Task Primary_column_cannot_be_hidden_or_moved()
    {
        var (list, _) = await Loaded();
        var name = list.Columns[0];

        list.ToggleColumn(name);
        list.MoveColumnDown(name);
        list.MoveColumnUp(list.Columns[1]);

        Assert.Same(name, list.Columns[0]);
        Assert.True(name.IsVisible);
    }

    [Fact]
    public async Task Preview_expand_and_row_menu()
    {
        var (list, opened) = await Loaded();
        var alan = list.Rows[1];

        list.PreviewRow(alan);
        Assert.True(list.IsPreviewOpen);
        Assert.IsType<Tenantry.ViewModels.UserDetailViewModel>(list.PreviewModel);
        list.ExpandPreview();
        Assert.Equal("Alan", opened.Single().DisplayName);

        var menu = list.MenuFor(alan);
        Assert.Equal("Preview", menu[0].Label);
        Assert.Contains(menu, m => m.Label == "Open in Entra admin center");
        Assert.Equal("E5; Visio", menu.Single(m => m.Label == "Copy licenses").CopyText);
        Assert.Equal("alan@contoso.example", menu.Single(m => m.Label == "Copy address").CopyText);
    }

    [Fact]
    public async Task Csv_export_is_what_is_shown_with_quoting()
    {
        var (list, _) = await Loaded();
        list.SearchText = "guest";

        using var writer = new StringWriter();
        list.ExportCsv(writer);
        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Name,Name (detail),Department,Job title,Status,Type,Licenses,Last sign-in", lines[0]);
        Assert.Equal(2, lines.Length);
        // Commas inside values are quoted; empty department, title, licenses and sign-in stay empty.
        Assert.Equal("\"Guest, Pat\",\"guest, pat@contoso.example\",,,Active,Guest,,", lines[1]);
    }

    [Fact]
    public async Task Stale_sign_ins_get_warning_tones()
    {
        var (list, _) = await Loaded();
        var signIn = list.Columns.Single(c => c.Key == F.LastSignIn);

        Assert.Null(signIn.DateCell!(list.Rows[0]).Tone);                                       // Ada, 2 days
        Assert.Equal(Tenantry.ViewModels.BadgeKind.Warning, signIn.DateCell!(list.Rows[1]).Tone); // Alan, 120 days
        Assert.Equal(Tenantry.ViewModels.BadgeKind.Danger, signIn.DateCell!(list.Rows[2]).Tone);  // Grace, 200 days
        Assert.Equal(("Never", Tenantry.ViewModels.BadgeKind.Warning), (signIn.DateCell!(list.Rows[3]).Relative, signIn.DateCell!(list.Rows[3]).Tone));
    }
}
