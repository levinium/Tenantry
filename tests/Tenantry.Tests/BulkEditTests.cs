using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>Bulk edit: many rows, one change each, staged together; skips explained.</summary>
public sealed class BulkEditTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static UserSummary Person(string id, string name, string? department, bool enabled = true, params string[] licenses) =>
        new(id, name, $"{name}@contoso.example", $"{name}@contoso.example", department, null, enabled, UserKind.Member, DateTimeOffset.UnixEpoch, null, licenses);

    static readonly UserSummary Ada = Person("u1", "zz-tenantry-test-ada", "IT", true, "Microsoft 365 E3");
    static readonly UserSummary Bo = Person("u2", "zz-tenantry-test-bo", "Finance", false);
    static readonly UserSummary Cy = Person("u3", "zz-tenantry-test-cy", "IT");

    static readonly GroupSummary Team = new("g1", "zz-tenantry-test-group", null, null, GroupKind.Microsoft365, false, false, null, null,
        [new GroupMember("u1", "user")], [new GroupMember("u1", "user")]);
    static readonly GroupSummary Second = new("g2", "zz-tenantry-test-second", null, null, GroupKind.Security, false, false, null, null,
        [], [new GroupMember("u1", "user"), new GroupMember("u2", "user")]);
    static readonly GroupSummary Dynamic = new("g3", "zz-tenantry-test-dynamic", null, null, GroupKind.Security, true, false, null, null, [], []);
    static readonly GroupSummary Shared = new("m1", "zz-tenantry-test-shared", "zz-tenantry-test-shared@contoso.example", null, GroupKind.SharedMailbox,
        false, false, null, null, [new GroupMember("u2", "user", "Full access")]);

    sealed class Index : IDirectoryIndex
    {
        readonly Dictionary<string, UserSummary> _users = new[] { Ada, Bo, Cy }.ToDictionary(u => u.Id);
        readonly Dictionary<string, GroupSummary> _groups = new[] { Team, Second, Dynamic, Shared }.ToDictionary(g => g.Id);
        public UserSummary? User(string id) => _users.GetValueOrDefault(id);
        public GroupSummary? Group(string id) => _groups.GetValueOrDefault(id);
        public IEnumerable<UserSummary> AllUsers => _users.Values;
        public IEnumerable<GroupSummary> AllGroups => _groups.Values;
    }

    sealed class Records : IUserRecords
    {
        public int DetailReads;

        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DetailReads);
            return Task.FromResult(new UserDetails(userId, new Dictionary<string, string?> { ["officeLocation"] = userId == "u1" ? "Floor 2" : null }, null, null, null, null));
        }

        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserLicensing("US", [], [new LicenseSku("sku-e3", "Microsoft 365 E3", 5, 4, []), new LicenseSku("sku-basic", "Business Basic", 10, 0, [])], []));

        public Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailboxDelegate>>([]);

        public Task<MailDetails> GetMailAsync(string userId, string address, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    sealed class Noop : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static ChangePipeline Pipeline() => new(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");

    static BulkAction Action(IReadOnlyList<BulkAction> actions, string title) => actions.Single(a => a.Title == title);

    // ---- Pipeline ---------------------------------------------------------------------------

    [Fact]
    public async Task Staging_many_tells_listeners_once_and_slots_stay_findable()
    {
        var pipeline = Pipeline();
        var events = 0;
        pipeline.Changed += (_, _) => events++;
        var changes = Enumerable.Range(0, 300)
            .Select(i => new SetSignInAllowed(new ChangeTarget("user", $"u{i}", $"zz-tenantry-test-{i}", $"zz-tenantry-test-{i}@contoso.example"), true, false))
            .ToArray();

        pipeline.StageAll(changes);

        Assert.Equal(1, events);
        Assert.Equal(300, pipeline.Pending.Count);
        Assert.Same(changes[150], pipeline.PendingFor("u150", "signin"));
        // Staging one back to where it started drops it, and its slot.
        pipeline.Stage(new SetSignInAllowed(changes[150].User, false, true));
        Assert.Null(pipeline.PendingFor("u150", "signin"));

        await pipeline.ApplyAsync(cancellationToken: Ct);

        Assert.Empty(pipeline.Pending);
        Assert.Null(pipeline.PendingFor("u1", "signin"));
    }

    // ---- Users ------------------------------------------------------------------------------

    static async Task<BulkDetailsViewModel> Details(ChangePipeline pipeline, Records records, params UserSummary[] users)
    {
        var details = new BulkDetailsViewModel(() => users, records, pipeline);
        // The first read of details outside the list runs in the background; wait for it.
        for (var i = 0; details.IsReading && i < 100; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        return details;
    }

    static MixedField Field(BulkDetailsViewModel details, string property) => details.Fields.Single(f => f.Property == property);

    [Fact]
    public async Task Details_show_a_shared_value_or_mixed_and_stage_for_everyone_who_differs()
    {
        var pipeline = Pipeline();
        var details = await Details(pipeline, new Records(), Ada, Bo, Cy);
        var department = Field(details, "department");
        Assert.Equal("Mixed", department.Value);
        Assert.Contains("IT (2)", department.ValuesTip);

        department.EditCommand.Execute(null);
        department.Draft = "IT";
        await department.StageCommand.ExecuteAsync(null);

        var change = Assert.IsType<SetUserText>(Assert.Single(pipeline.Pending));
        Assert.Equal(("u2", "Finance", "IT"), (change.User.Id, change.Old, change.New));
        Assert.Equal("IT", department.Value);
        Assert.Equal("Pending for 1", department.PendingText);
        Assert.False(department.IsEditing);
    }

    [Fact]
    public async Task Leaving_a_mixed_detail_untouched_never_clears_it_but_clear_for_everyone_does()
    {
        var pipeline = Pipeline();
        var details = await Details(pipeline, new Records(), Ada, Bo, Cy);
        var department = Field(details, "department");

        department.EditCommand.Execute(null);
        await department.StageCommand.ExecuteAsync(null);
        Assert.Empty(pipeline.Pending);
        Assert.False(department.IsEditing);

        Assert.True(department.CanClear);
        await department.ClearForEveryoneCommand.ExecuteAsync(null);
        Assert.Equal(3, pipeline.Pending.Count);
        Assert.All(pipeline.Pending.Cast<SetUserText>(), c => Assert.Null(c.New));
    }

    [Fact]
    public async Task A_pause_stages_a_detail_for_everyone_and_esc_puts_them_back()
    {
        var pipeline = Pipeline();
        var details = await Details(pipeline, new Records(), Ada, Bo, Cy);
        var department = Field(details, "department");

        department.EditCommand.Execute(null);
        department.Draft = "Sales";
        await department.StageDraftCommand.ExecuteAsync(null);
        Assert.True(department.IsEditing);
        Assert.Equal(3, pipeline.Pending.Count);

        department.Draft = "IT";
        await department.StageDraftCommand.ExecuteAsync(null);
        Assert.Equal("u2", Assert.Single(pipeline.Pending).Target.Id);

        department.CancelCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public async Task Details_outside_the_list_are_read_once_and_reused_when_staging()
    {
        var records = new Records();
        var pipeline = Pipeline();
        var details = await Details(pipeline, records, Ada, Bo);
        var office = Field(details, "officeLocation");
        Assert.Equal("Mixed", office.Value);
        Assert.Equal(2, records.DetailReads);

        office.EditCommand.Execute(null);
        office.Draft = "Floor 3";
        await office.StageCommand.ExecuteAsync(null);

        Assert.Equal(2, records.DetailReads);
        Assert.Equal(["Floor 2", null], pipeline.Pending.Cast<SetUserText>().OrderBy(c => c.User.Id).Select(c => c.Old));
    }

    [Fact]
    public async Task A_bad_usage_location_stages_nothing()
    {
        var pipeline = Pipeline();
        var location = Field(await Details(pipeline, new Records(), Ada), "usageLocation");
        location.EditCommand.Execute(null);
        location.Draft = "usa";

        await location.StageCommand.ExecuteAsync(null);

        Assert.Empty(pipeline.Pending);
        Assert.Contains("country code", location.Message);
        Assert.True(location.IsEditing);
    }
    [Fact]
    public async Task Licenses_are_added_only_where_missing_and_shortfalls_are_flagged()
    {
        var add = Action(BulkActions.ForUsers(() => [Ada, Bo, Cy], new Index(), new Records()), "Add a license");
        await add.LoadAsync(Ct);
        add.Choice = add.Choices.Single(c => ((LicenseSku)c.Value).SkuId == "sku-e3");

        var plan = await add.PlanAsync(Ct);

        Assert.Equal(["u2", "u3"], plan.Changes.Select(c => c.Target.Id));
        Assert.StartsWith("Only 1 left for 2 people", plan.Skipped[0]);
        Assert.Contains("zz-tenantry-test-ada: already has it", plan.Skipped);
    }

    [Fact]
    public async Task Removing_a_license_only_offers_ones_they_hold()
    {
        var remove = Action(BulkActions.ForUsers(() => [Ada, Bo], new Index(), new Records()), "Remove a license");
        await remove.LoadAsync(Ct);

        Assert.Equal(["Microsoft 365 E3"], remove.Choices.Select(c => c.Label));
        Assert.True(remove.IsCaution);
    }

    [Fact]
    public async Task Group_and_mailbox_actions_skip_people_already_there()
    {
        var actions = BulkActions.ForUsers(() => [Ada, Bo, Cy], new Index(), new Records());
        var join = Action(actions, "Add to a group");
        join.Picker!.Query = "test";
        Assert.DoesNotContain(join.Picker.Results, r => r.Id == "g3");
        join.Picker.PickCommand.Execute(join.Picker.Results.Single(r => r.Id == "g1"));
        var access = Action(actions, "Give access to a shared mailbox");
        access.Picked = PickOption.Of(Shared);
        access.Choice = access.Choices.Single(c => (MailboxRight)c.Value == MailboxRight.FullAccess);

        var joined = await join.PlanAsync(Ct);
        var granted = await access.PlanAsync(Ct);

        Assert.Equal(["u2", "u3"], joined.Changes.Cast<SetGroupMembership>().Select(c => c.Person.Id));
        Assert.Equal(["u1", "u3"], granted.Changes.Cast<SetMailboxAccess>().Select(c => c.Person.Id));
        Assert.Contains("zz-tenantry-test-bo: already has Full access", granted.Skipped);
    }

    [Fact]
    public async Task Sign_in_skips_accounts_already_that_way()
    {
        var block = Action(BulkActions.ForUsers(() => [Ada, Bo], new Index(), new Records()), "Block or allow sign-in");
        block.Choice = block.Choices.Single(c => c.Label == "Block sign-in");

        var plan = await block.PlanAsync(Ct);

        Assert.Equal("u1", Assert.Single(plan.Changes).Target.Id);
        Assert.Equal(["zz-tenantry-test-bo: already blocked"], plan.Skipped);
    }

    // ---- Groups -----------------------------------------------------------------------------

    [Fact]
    public async Task Group_bulk_skips_mailboxes_dynamic_groups_and_last_owners()
    {
        var actions = BulkActions.ForGroups(() => [Team, Second, Dynamic, Shared], new Index());
        var addMember = Action(actions, "Add a member");
        addMember.Picked = PickOption.Of(Cy);
        var removeOwner = Action(actions, "Remove an owner");
        removeOwner.Picker!.Query = "ada";
        removeOwner.Picker.PickCommand.Execute(removeOwner.Picker.Results.Single());

        var added = await addMember.PlanAsync(Ct);
        var removed = await removeOwner.PlanAsync(Ct);

        Assert.Equal(["g1", "g2"], added.Changes.Select(c => c.Target.Id));
        Assert.Contains("zz-tenantry-test-dynamic: dynamic, follows its rule", added.Skipped);
        Assert.Contains("zz-tenantry-test-shared: a mailbox (use mailbox access)", added.Skipped);
        Assert.Equal(["g2"], removed.Changes.Select(c => c.Target.Id));
        Assert.Contains("zz-tenantry-test-group: they're its only owner", removed.Skipped);
    }

    // ---- Editor and list --------------------------------------------------------------------

    [Fact]
    public async Task The_editor_stages_once_and_reports_what_it_skipped()
    {
        var pipeline = Pipeline();
        var closed = false;
        var editor = new BulkEditorViewModel("Edit 3 users", BulkActions.ForUsers(() => [Ada, Bo, Cy], new Index(), new Records()), pipeline, () => closed = true);
        Assert.False(editor.StageCommand.CanExecute(null));

        editor.Selected = Action(editor.Actions, "Sign out everywhere");
        Assert.True(Action(editor.Actions, "Sign out everywhere").IsSelected);
        Assert.Equal("Add to Pending changes (takes something away)", editor.StageText);
        await editor.StageCommand.ExecuteAsync(null);

        Assert.Equal(3, pipeline.Pending.Count);
        Assert.StartsWith("Added 3 changes", editor.Result);
        editor.DoneCommand.Execute(null);
        Assert.True(closed);
    }

    static async Task<DataListViewModel> UserListWithPanel(ChangePipeline pipeline)
    {
        var list = DataListViewModel.Create(UserList.Definition(() => DateTimeOffset.UnixEpoch, _ => null!),
            _ => Task.FromResult<IReadOnlyList<UserSummary>>([Ada, Bo, Cy]), _ => { });
        await list.LoadAsync(TestContext.Current.CancellationToken);
        list.SelectionPanel = (rows, uncheck) => SelectionPanelViewModel.ForUsers(rows, uncheck, new Index(), new Records(), pipeline,
            Tenantry.Core.Capabilities.Availability.Yes);
        return list;
    }

    [Fact]
    public async Task Checking_several_rows_turns_the_pane_into_a_selection_panel()
    {
        var list = await UserListWithPanel(Pipeline());
        Assert.False(list.IsPaneVisible);

        list.SelectedRows = list.Rows.Take(2).ToArray();

        Assert.True(list.HasSelection);
        Assert.True(list.IsPaneVisible);
        Assert.Equal("SELECTION", list.PaneTitle);
        var panel = Assert.IsType<SelectionPanelViewModel>(list.PreviewModel);
        Assert.Equal("2 users selected", panel.Heading);
        Assert.Equal("Sign-in", panel.Summary[1].Label);
        Assert.Equal("1 allowed · 1 blocked", panel.Summary[1].Value);
        Assert.DoesNotContain(panel.Bulk!.Actions, a => a.Title == "Set a detail");

        // The same panel follows the selection (what's typed into it survives).
        list.SelectedRows = list.Rows.ToArray();
        Assert.Same(panel, list.PreviewModel);
        Assert.Equal("3 users selected", panel.Heading);

        // A chip's × unchecks that person.
        panel.Chips[0].RemoveCommand.Execute(null);
        Assert.Equal(2, list.SelectedRows.Count);

        list.ClearSelectionCommand.Execute(null);
        Assert.False(list.HasSelection);
        Assert.IsNotType<SelectionPanelViewModel>(list.PreviewModel);
    }

    [Fact]
    public async Task Checked_rows_survive_a_filter_that_keeps_them()
    {
        var list = await UserListWithPanel(Pipeline());
        list.SelectedRows = list.Rows.Take(2).ToArray();

        list.SearchText = "ada";

        Assert.Equal([Ada], list.SelectedRows);
        Assert.False(list.HasSelection);
    }

    [Fact]
    public async Task Pasting_addresses_checks_the_matching_rows_and_names_the_rest()
    {
        var list = await UserListWithPanel(Pipeline());
        list.PasteText = "zz-tenantry-test-ada@contoso.example\nBo <zz-tenantry-test-bo@contoso.example>; nobody@contoso.example";

        list.SelectFromPasteCommand.Execute(null);

        Assert.Equal(["u1", "u2"], list.SelectedRows.Cast<UserSummary>().Select(u => u.Id).Order());
        Assert.Equal("Selected 2 of 3. Not found: nobody@contoso.example", list.PasteResult);
        Assert.Equal("Select all 3 matching rows, or clear the selection", list.SelectAllTip);
    }
}