using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>Groups &amp; access from both sides, the people picker, and choosing a manager: all staged, never sent.</summary>
public sealed class GroupAccessViewModelTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static UserSummary Person(string id, string name, string? department = "IT") =>
        new(id, name, $"{name}@contoso.example", $"{name}@contoso.example", department, null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, []);

    static readonly UserSummary Ada = Person("u1", "zz-tenantry-test-ada");
    static readonly UserSummary Bo = Person("u2", "zz-tenantry-test-bo");
    static readonly UserSummary Cy = Person("u3", "zz-tenantry-test-cy", "Finance");

    static readonly GroupSummary Team = new("g1", "zz-tenantry-test-group", null, null, GroupKind.Microsoft365, false, true, "Private", null,
        [new GroupMember("u1", "user"), new GroupMember("u2", "user")], [new GroupMember("u1", "user")]);
    static readonly GroupSummary Dynamic = new("g2", "zz-tenantry-test-dynamic", null, null, GroupKind.Security, true, false, null, null,
        [new GroupMember("u1", "user")], []);
    static readonly GroupSummary List = new("g3", "zz-tenantry-test-list", "zz-tenantry-test-list@contoso.example", null, GroupKind.Distribution, false, false, null, null, [], []);
    static readonly GroupSummary Shared = new("m1", "zz-tenantry-test-shared", "zz-tenantry-test-shared@contoso.example", null, GroupKind.SharedMailbox, false, false, null, null,
        [new GroupMember("u1", "user", "Full access")]);

    sealed class Index(IEnumerable<UserSummary> users, IEnumerable<GroupSummary> groups) : IDirectoryIndex
    {
        readonly Dictionary<string, UserSummary> _users = users.ToDictionary(u => u.Id);
        readonly Dictionary<string, GroupSummary> _groups = groups.ToDictionary(g => g.Id);
        public UserSummary? User(string id) => _users.GetValueOrDefault(id);
        public GroupSummary? Group(string id) => _groups.GetValueOrDefault(id);
        public IEnumerable<UserSummary> AllUsers => _users.Values;
        public IEnumerable<GroupSummary> AllGroups => _groups.Values;
    }

    static readonly Index Directory = new([Ada, Bo, Cy], [Team, Dynamic, List, Shared]);

    sealed class Noop : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static ChangePipeline Pipeline() => new(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");

    static GroupPeopleEditor Editor(GroupSummary group, ChangePipeline pipeline, bool canEdit = true) =>
        new(group, Directory, pipeline, canEdit ? Availability.Yes : Availability.No("Requires the Groups Administrator role"), _ => { }, _ => { });

    // ---- Picker -----------------------------------------------------------------------------

    [Fact]
    public void The_picker_matches_every_word_and_clears_after_a_choice()
    {
        PickOption? chosen = null;
        var picker = new PersonPicker("Add", () => GroupAccess.People(Directory), o => chosen = o);

        picker.Query = "tenantry bo";
        Assert.Equal(["zz-tenantry-test-bo"], picker.Results.Select(r => r.Name));
        picker.Query = "finance";
        Assert.Equal(["zz-tenantry-test-cy"], picker.Results.Select(r => r.Name));
        picker.Query = "nobody";
        Assert.True(picker.ShowNoMatch);

        picker.Query = "cy";
        picker.PickCommand.Execute(picker.Results[0]);

        Assert.Equal("u3", chosen!.Id);
        Assert.Equal("", picker.Query);
        Assert.Empty(picker.Results);
    }

    // ---- Group side -------------------------------------------------------------------------

    [Fact]
    public void The_last_owner_cant_be_removed_until_another_is_added()
    {
        var pipeline = Pipeline();
        var editor = Editor(Team, pipeline);
        Assert.False(editor.Owners.Single().CanRemove);
        Assert.Contains("at least one owner", editor.Owners.Single().RemoveTip);

        editor.OwnerPicker.Query = "bo";
        editor.OwnerPicker.PickCommand.Execute(editor.OwnerPicker.Results.Single());

        Assert.True(editor.Owners.Single(o => o.Name == "zz-tenantry-test-ada").CanRemove);
        // Distribution lists aren't held to it: Exchange manages their owners.
        Assert.Null(GroupAccess.LastOwnerReason(List with { Owners = [new GroupMember("u1", "user")] }, pipeline));
    }

    [Fact]
    public void Removing_an_owner_is_staged_marked_and_undoable()
    {
        var pipeline = Pipeline();
        var editor = Editor(Team with { Owners = [new GroupMember("u1", "user"), new GroupMember("u3", "user")] }, pipeline);

        editor.Owners.First().RemoveCommand.Execute(null);

        var staged = Assert.IsType<SetGroupMembership>(Assert.Single(pipeline.Pending));
        Assert.Equal((GroupRole.Owner, "u1", false), (staged.Role, staged.Person.Id, staged.New));
        Assert.True(staged.IsCaution);
        Assert.Equal("Removing", editor.Owners.First().PendingText);
        // With one owner left, that one now stays.
        Assert.False(editor.Owners.Last().CanRemove);

        editor.Owners.First().RemoveCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public void Picked_members_show_as_adding_and_leave_the_picker()
    {
        var pipeline = Pipeline();
        var editor = Editor(Team, pipeline);

        editor.MemberPicker.Query = "cy";
        editor.MemberPicker.PickCommand.Execute(editor.MemberPicker.Results.Single());

        var cy = editor.Members.Single(m => m.Name == "zz-tenantry-test-cy");
        Assert.Equal("Adding", cy.PendingText);
        editor.MemberPicker.Query = "cy";
        Assert.Empty(editor.MemberPicker.Results);
        // Microsoft 365 groups take people only: no groups offered.
        editor.MemberPicker.Query = "zz";
        Assert.All(editor.MemberPicker.Results, r => Assert.Equal("user", r.Type));
    }

    [Fact]
    public void Dynamic_groups_lock_members_but_not_owners()
    {
        var editor = Editor(Dynamic, Pipeline());

        Assert.True(editor.HasMembersLock);
        Assert.False(editor.CanAddMembers);
        Assert.False(editor.Members.Single().CanRemove);
        Assert.Contains("rule", editor.Members.Single().RemoveTip);
        Assert.True(editor.HasNoOwners);
        Assert.True(editor.CanEdit);
    }

    [Fact]
    public void Distribution_list_changes_are_marked_for_exchange()
    {
        var pipeline = Pipeline();
        var editor = Editor(List, pipeline);

        editor.OwnerPicker.Query = "bo";
        editor.OwnerPicker.PickCommand.Execute(editor.OwnerPicker.Results.Single());

        Assert.True(Assert.IsType<SetGroupMembership>(pipeline.Pending.Single()).ExchangeManaged);
    }

    [Fact]
    public void Mailbox_rights_are_checkboxes_and_new_people_start_with_full_access()
    {
        var pipeline = Pipeline();
        var editor = Editor(Shared, pipeline);

        var ada = editor.Access.Single();
        Assert.Equal([true, false, false], ada.Toggles.Select(t => t.IsOn));
        ada = editor.Access.Single();
        ada.Toggles[1].IsOn = true;
        editor.AccessPicker.Query = "bo";
        editor.AccessPicker.PickCommand.Execute(editor.AccessPicker.Results.Single());

        Assert.Equal(2, pipeline.Pending.Count);
        var bo = editor.Access.Single(r => r.Name == "zz-tenantry-test-bo");
        Assert.Equal([true, false, false], bo.Toggles.Select(t => t.IsOn));
        Assert.True(bo.HasPending);
        Assert.Equal([true, true, false], editor.Access.Single(r => r.Name == "zz-tenantry-test-ada").Toggles.Select(t => t.IsOn));
    }

    [Fact]
    public void Without_the_role_nothing_can_be_changed_and_the_reason_shows()
    {
        var editor = Editor(Team, Pipeline(), canEdit: false);

        Assert.False(editor.CanEdit);
        Assert.Equal("Requires the Groups Administrator role", editor.EditReason);
        Assert.False(editor.Members.First().CanRemove);
        Assert.Equal("Requires the Groups Administrator role", editor.Members.First().RemoveTip);
    }

    // ---- User side --------------------------------------------------------------------------

    sealed class Records : IUserRecords
    {
        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserDetails(userId, new Dictionary<string, string?> { ["displayName"] = "x" }, "u2", "zz-tenantry-test-bo", null, null));

        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailboxDelegate>>(
            [
                new("zz-tenantry-test-bo", [MailboxRight.FullAccess, MailboxRight.SendAs]),
                new("Someone Departed", [MailboxRight.SendOnBehalf]),
            ]);
    }

    static UserDetailViewModel Page(UserSummary user, ChangePipeline pipeline) =>
        new(user, pipeline: pipeline, canEdit: Availability.Yes, records: new Records(), index: Directory);

    [Fact]
    public void Their_groups_show_member_and_owner_and_dynamic_ones_are_locked()
    {
        var pipeline = Pipeline();
        var tab = Page(Ada, pipeline).GroupsTab;

        Assert.Equal("2 groups · 1 shared mailbox", tab.Summary);
        var team = tab.Groups.Single(g => g.Name == "zz-tenantry-test-group");
        Assert.Equal([true, true], team.Toggles.Select(t => t.IsOn));
        // Ada is the group's only owner: she can't untick that.
        Assert.False(team.Toggles[1].CanEdit);
        var dynamic = tab.Groups.Single(g => g.Name == "zz-tenantry-test-dynamic");
        Assert.False(dynamic.Toggles[0].CanEdit);
        Assert.True(dynamic.Toggles[1].CanEdit);

        team.Toggles[0].IsOn = false;

        var staged = Assert.IsType<SetGroupMembership>(pipeline.Pending.Single());
        Assert.Equal(("g1", GroupRole.Member, false), (staged.Group.Id, staged.Role, staged.New));
        Assert.True(tab.Groups.Single(g => g.Name == "zz-tenantry-test-group").HasPending);
    }

    [Fact]
    public void Adding_to_a_group_and_a_mailbox_shows_at_once()
    {
        var pipeline = Pipeline();
        var tab = Page(Cy, pipeline).GroupsTab;
        Assert.True(tab.HasNoGroups);

        tab.GroupPicker.Query = "list";
        tab.GroupPicker.PickCommand.Execute(tab.GroupPicker.Results.Single());
        tab.MailboxPicker.Query = "shared";
        tab.MailboxPicker.PickCommand.Execute(tab.MailboxPicker.Results.Single());

        Assert.Equal([true, false], tab.Groups.Single().Toggles.Select(t => t.IsOn));
        Assert.Equal([true, false, false], tab.Mailboxes.Single().Toggles.Select(t => t.IsOn));
        // Dynamic groups aren't offered: their rule decides.
        tab.GroupPicker.Query = "dynamic";
        Assert.Empty(tab.GroupPicker.Results);
    }

    [Fact]
    public async Task Who_can_use_their_mailbox_is_read_live_and_named_where_possible()
    {
        var pipeline = Pipeline();
        var ada = Ada with { Mailbox = new MailboxInfo("User", "zz-tenantry-test-ada@contoso.example", [], ForwardingKind.None, null, false, false, false, false) };
        var tab = Page(ada, pipeline).GroupsTab;

        await tab.LoadAsync(Ct);

        Assert.Equal(["Someone Departed", "zz-tenantry-test-bo"], tab.Delegates.Select(d => d.Name));
        Assert.Equal([true, true, false], tab.Delegates.Single(d => d.Name == "zz-tenantry-test-bo").Toggles.Select(t => t.IsOn));
        tab.Delegates.Single(d => d.Name == "zz-tenantry-test-bo").Toggles[0].IsOn = false;
        var staged = Assert.IsType<SetMailboxAccess>(pipeline.Pending.Single());
        Assert.Equal(("u1", "u2", MailboxRight.FullAccess, false), (staged.Mailbox.Id, staged.Person.Id, staged.Right, staged.New));
    }

    // ---- Manager ----------------------------------------------------------------------------

    [Fact]
    public async Task A_manager_is_chosen_from_the_picker_or_cleared()
    {
        var pipeline = Pipeline();
        var account = Page(Ada, pipeline).Account;
        await account.LoadAsync(Ct);
        Assert.Equal("zz-tenantry-test-bo", account.ManagerText);

        account.ManagerPicker.Query = "test";
        Assert.DoesNotContain(account.ManagerPicker.Results, r => r.Id is "u1" or "u2");
        account.ManagerPicker.PickCommand.Execute(account.ManagerPicker.Results.Single());

        Assert.Equal("zz-tenantry-test-cy", account.ManagerText);
        Assert.Equal("Pending: was zz-tenantry-test-bo", account.PendingManagerText);
        account.UndoManagerCommand.Execute(null);
        account.ClearManagerCommand.Execute(null);
        Assert.Equal("(none)", account.ManagerText);
        Assert.Null(Assert.IsType<SetManager>(pipeline.Pending.Single()).NewId);
    }
}
