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

    [Fact]
    public async Task Setting_a_list_detail_skips_people_who_already_have_it()
    {
        var actions = BulkActions.ForUsers([Ada, Bo, Cy], new Index(), new Records());
        var set = Action(actions, "Set a detail");
        set.Choice = set.Choices.Single(c => (string)c.Value == "department");
        set.Text = "IT";

        var plan = await set.PlanAsync(Ct);

        var change = Assert.IsType<SetUserText>(Assert.Single(plan.Changes));
        Assert.Equal(("u2", "Finance", "IT"), (change.User.Id, change.Old, change.New));
        Assert.Equal(2, plan.Skipped.Count);
    }

    [Fact]
    public async Task Other_details_are_read_live_for_a_true_before()
    {
        var records = new Records();
        var set = Action(BulkActions.ForUsers([Ada, Bo], new Index(), records), "Set a detail");
        set.Choice = set.Choices.Single(c => (string)c.Value == "officeLocation");
        set.Text = "Floor 3";

        var plan = await set.PlanAsync(Ct);

        Assert.Equal(2, records.DetailReads);
        Assert.Equal(["Floor 2", null], plan.Changes.Cast<SetUserText>().OrderBy(c => c.User.Id).Select(c => c.Old));
    }

    [Fact]
    public async Task A_bad_usage_location_stages_nothing()
    {
        var set = Action(BulkActions.ForUsers([Ada], new Index(), new Records()), "Set a detail");
        set.Choice = set.Choices.Single(c => (string)c.Value == "usageLocation");
        set.Text = "usa";

        var plan = await set.PlanAsync(Ct);

        Assert.Empty(plan.Changes);
        Assert.Contains("country code", plan.Error);
    }

    [Fact]
    public async Task Licenses_are_added_only_where_missing_and_shortfalls_are_flagged()
    {
        var add = Action(BulkActions.ForUsers([Ada, Bo, Cy], new Index(), new Records()), "Add a license");
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
        var remove = Action(BulkActions.ForUsers([Ada, Bo], new Index(), new Records()), "Remove a license");
        await remove.LoadAsync(Ct);

        Assert.Equal(["Microsoft 365 E3"], remove.Choices.Select(c => c.Label));
        Assert.True(remove.IsCaution);
    }

    [Fact]
    public async Task Group_and_mailbox_actions_skip_people_already_there()
    {
        var actions = BulkActions.ForUsers([Ada, Bo, Cy], new Index(), new Records());
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
        var block = Action(BulkActions.ForUsers([Ada, Bo], new Index(), new Records()), "Block or allow sign-in");
        block.Choice = block.Choices.Single(c => c.Label == "Block sign-in");

        var plan = await block.PlanAsync(Ct);

        Assert.Equal("u1", Assert.Single(plan.Changes).Target.Id);
        Assert.Equal(["zz-tenantry-test-bo: already blocked"], plan.Skipped);
    }

    // ---- Groups -----------------------------------------------------------------------------

    [Fact]
    public async Task Group_bulk_skips_mailboxes_dynamic_groups_and_last_owners()
    {
        var actions = BulkActions.ForGroups([Team, Second, Dynamic, Shared], new Index());
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
        var editor = new BulkEditorViewModel("Edit 3 users", BulkActions.ForUsers([Ada, Bo, Cy], new Index(), new Records()), pipeline, () => closed = true);
        Assert.False(editor.StageCommand.CanExecute(null));

        editor.Selected = Action(editor.Actions, "Sign out everywhere");
        Assert.True(Action(editor.Actions, "Sign out everywhere").IsSelected);
        Assert.Equal("Stage (takes something away)", editor.StageText);
        await editor.StageCommand.ExecuteAsync(null);

        Assert.Equal(3, pipeline.Pending.Count);
        Assert.StartsWith("Staged 3 changes", editor.Result);
        editor.DoneCommand.Execute(null);
        Assert.True(closed);
    }

    [Fact]
    public async Task Checked_rows_survive_a_filter_that_keeps_them_and_drive_the_bar()
    {
        var list = DataListViewModel.Create(UserList.Definition(() => DateTimeOffset.UnixEpoch, _ => null!),
            _ => Task.FromResult<IReadOnlyList<UserSummary>>([Ada, Bo, Cy]), _ => { });
        await list.LoadAsync(Ct);
        var ran = new List<object>();
        list.BulkAction = new ListBulkAction("users", true, "", rows => ran.AddRange(rows));

        list.SelectedRows = list.Rows.Take(2).ToArray();

        Assert.True(list.HasSelection);
        Assert.Equal("Edit 2 users…", list.BulkText);
        list.SearchText = "ada";
        Assert.Equal([Ada], list.SelectedRows);
        Assert.False(list.HasSelection);
        list.SearchText = "";
        list.SelectedRows = list.Rows.ToArray();
        list.BulkEditCommand.Execute(null);
        Assert.Equal(3, ran.Count);
        list.ClearSelectionCommand.Execute(null);
        Assert.False(list.HasSelection);
    }
}
