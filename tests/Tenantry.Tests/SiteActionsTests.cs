using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>Site actions (7c): what they stage, what the write guard judges, what SharePoint is sent, and the site page's editing.</summary>
public sealed class SiteActionsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string Root = """{"siteCollection":{"hostname":"contoso.sharepoint.com"}}""";
    const string Ok = """[{"SchemaVersion":"15.0.0.0","ErrorInfo":null},2,{},4,{}]""";

    static GraphClient Graph(FakeHttp http) => new(http.Client(), new FakeTokens(), delay: NoDelay.Instance);

    static SharePointAdmin SharePoint(FakeHttp http) => new(http.Client(), new FakeTokens(), Graph(http), NoDelay.Instance);

    static SiteSummary Site(string alias, SiteKind kind = SiteKind.TeamSite, string? groupId = null, int sharing = 0, long usedMb = 2048, string? title = null) =>
        new("aaaaaaaa-0000-0000-0000-000000000001", $"https://contoso.sharepoint.com/sites/{alias}", title ?? alias, kind == SiteKind.TeamSite ? "GROUP#0" : "SITEPAGEPUBLISHING#0",
            kind, "Owner", null, null, groupId, false, false, null, usedMb, 25 * 1024, sharing, "Unlock", "NotArchived", null, null);

    static async Task<BulkPlan> Plan(IReadOnlyList<BulkAction> actions, string title, Action<BulkAction>? fill = null)
    {
        var action = actions.Single(a => a.Title == title);
        await action.LoadAsync(Ct);
        fill?.Invoke(action);
        return await action.PlanAsync(Ct);
    }

    // ---- The write guard -----------------------------------------------------------------------

    [Fact]
    public void The_guard_judges_a_site_by_its_address_not_its_title()
    {
        var renamed = SiteChange.TargetFor(Site("finance", title: WriteGuard.Marker + "finance"));
        var test = SiteChange.TargetFor(Site(WriteGuard.Marker + "site", title: "Anything"));
        var lockIt = (ChangeTarget t) => new SetSiteSetting(t, "u", "lock", "Lock", "Unlocked", "Read-Only", """{"LockState":"ReadOnly"}""", true);

        Assert.NotNull(WriteGuard.Refusal(lockIt(renamed), testMode: true));
        Assert.Null(WriteGuard.Refusal(lockIt(test), testMode: true));
    }

    [Fact]
    public void Site_changes_round_trip_through_the_change_log()
    {
        ChangeOp change = new SetSiteSetting(SiteChange.TargetFor(Site("a")), "https://contoso.sharepoint.com/sites/a", "quota", "Storage Limit", "25 GB", "50 GB",
            """{"StorageMaximumLevel":51200}""");

        var back = JsonSerializer.Deserialize<ChangeOp>(JsonSerializer.Serialize(change));

        Assert.Equal(change, back);
    }

    // ---- What the actions stage ------------------------------------------------------------------

    [Fact]
    public async Task Sharing_choices_stop_at_what_the_organization_allows_and_opening_up_is_a_caution()
    {
        var site = Site("a", sharing: 0);
        var actions = BulkActions.ForSites(() => [site], tenantSharing: _ => Task.FromResult(1));
        var sharing = actions.Single(a => a.Title == "External Sharing");
        await sharing.LoadAsync(Ct);

        Assert.Equal([0, 3, 1], sharing.Choices.Select(c => (int)c.Value));
        sharing.Choice = sharing.Choices.Last();
        var change = Assert.IsType<SetSiteSetting>(Assert.Single((await sharing.PlanAsync(Ct)).Changes));
        Assert.True(change.IsCaution);
        Assert.Equal("""{"SharingCapability":1}""", change.Values);
    }

    [Fact]
    public async Task A_storage_limit_below_what_a_site_holds_is_skipped()
    {
        var full = Site("full", usedMb: 30 * 1024);
        var roomy = Site("roomy", usedMb: 100);
        var plan = await Plan(BulkActions.ForSites(() => [full, roomy]), "Storage Limit", a => a.Text = "20");

        var change = Assert.IsType<SetSiteSetting>(Assert.Single(plan.Changes));
        Assert.Equal("roomy", change.Site.Address);
        Assert.Contains("already holds", Assert.Single(plan.Skipped), StringComparison.Ordinal);
        Assert.True(change.IsCaution);
    }

    [Fact]
    public async Task Guest_expiry_sets_the_switch_and_the_days_together()
    {
        var plan = await Plan(BulkActions.ForSites(() => [Site("a")]), "Guest Access Expiry", a => a.Choice = a.Choices.Single(c => c.Value is 90));

        Assert.Equal("""{"OverrideTenantExternalUserExpirationPolicy":true,"ExternalUserExpirationInDays":90}""", Assert.IsType<SetSiteSetting>(Assert.Single(plan.Changes)).Values);
    }

    [Fact]
    public async Task Deleting_takes_a_groups_site_with_its_group_and_skips_what_cant_be_deleted()
    {
        var group = Site("finance", groupId: "g1");
        var channel = Site("finance-leads", SiteKind.ChannelSite, groupId: "g1");
        var drive = Site("ada", SiteKind.OneDrive);
        var plan = await Plan(BulkActions.ForSites(() => [group, channel, drive], one: true), "Delete Site");

        var delete = Assert.IsType<DeleteSite>(Assert.Single(plan.Changes));
        Assert.Equal("g1", delete.GroupId);
        Assert.Equal("finance", delete.ConfirmPhrase);
        Assert.Equal(2, plan.Skipped.Count);
    }

    [Fact]
    public async Task Adding_yourself_stages_a_site_admin_change_for_your_sign_in()
    {
        var plan = await Plan(BulkActions.ForSites(() => [Site("a")], me: "admin@contoso.example"), "Add Yourself as Site Admin");

        var add = Assert.IsType<SetSiteAdmin>(Assert.Single(plan.Changes));
        Assert.Equal("i:0#.f|membership|admin@contoso.example", add.Login);
        Assert.True(add.New);
    }

    // ---- What SharePoint is sent ---------------------------------------------------------------------

    [Fact]
    public async Task A_setting_is_sent_typed_saved_and_read_back_into_the_local_copy()
    {
        var url = $"https://contoso.sharepoint.com/sites/{WriteGuard.Marker}a";
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/sites/root", Root)
            .Once(HttpMethod.Post, "ProcessQuery", Ok)
            .Once(HttpMethod.Post, "ProcessQuery", $$"""
                [{"SchemaVersion":"15.0.0.0","ErrorInfo":null},2,{},4,{},5,{"SiteId":"/Guid(aaaaaaaa-0000-0000-0000-000000000001)/","Url":"{{url}}",
                  "Template":"GROUP#0","SharingCapability":1,"LockState":"Unlock"}]
                """);
        var store = new MemoryEntityStore();
        var executor = new GraphChangeExecutor(Graph(http), store, NoDelay.Instance, sharePoint: SharePoint(http));
        var site = Site(WriteGuard.Marker + "a") with { Url = url };

        var result = await executor.ApplyAsync(new SetSiteSetting(SiteChange.TargetFor(site), url, "sharing", "External Sharing", "Off", "Guests",
            """{"SharingCapability":1}"""), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var body = http.Requests[1].Body!;
        Assert.Contains("<SetProperty Id=\"10\" ObjectPathId=\"3\" Name=\"SharingCapability\"><Parameter Type=\"Enum\">1</Parameter></SetProperty>", body);
        Assert.Contains("<Method Name=\"Update\"", body);
        Assert.Contains("\"sharing\":1", store.Get(DirectorySync.Sites, "aaaaaaaa-0000-0000-0000-000000000001"));
    }

    [Fact]
    public async Task Removing_from_a_site_group_escapes_the_login_and_a_refusal_says_to_become_an_admin()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/sites/root", Root)
            .Once(HttpMethod.Post, "removeByLoginName", "{}")
            .Once(HttpMethod.Get, "sitegroups/GetById(5)/users", """{"value":[]}""")
            .Once(HttpMethod.Post, "removeByLoginName", """{"error":{"message":"Access denied."}}""", System.Net.HttpStatusCode.Forbidden);
        var executor = new GraphChangeExecutor(Graph(http), new MemoryEntityStore(), NoDelay.Instance, sharePoint: SharePoint(http));
        var site = SiteChange.TargetFor(Site("a"));
        var ada = new ChangeTarget("user", "u1", "Ada", "ada@contoso.example");
        SetSiteGroupMember Remove() => new(site, "https://contoso.sharepoint.com/sites/a", 5, "Members", ada, "i:0#.f|membership|ada@contoso.example", true, false);

        var done = await executor.ApplyAsync(Remove(), Ct);
        var refused = await executor.ApplyAsync(Remove(), Ct);

        Assert.Equal(ChangeOutcome.Applied, done.Outcome);
        Assert.Contains("@v='i%3A0%23.f%7Cmembership%7Cada%40contoso.example'", http.Requests[1].Url.Query, StringComparison.Ordinal);
        Assert.Equal(ChangeOutcome.Failed, refused.Outcome);
        Assert.Contains("Add yourself as a site admin", refused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_group_connected_site_is_deleted_by_deleting_its_group()
    {
        var http = new FakeHttp().On(HttpMethod.Delete, "/groups/g1", "{}");
        var executor = new GraphChangeExecutor(Graph(http), new MemoryEntityStore(), NoDelay.Instance, sharePoint: SharePoint(http));

        var result = await executor.ApplyAsync(new DeleteSite(SiteChange.TargetFor(Site("a")), "https://contoso.sharepoint.com/sites/a", "g1", "Team Site"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.DoesNotContain(http.Requests, r => r.PathAndQuery.Contains("ProcessQuery", StringComparison.Ordinal));
    }

    // ---- The site page ------------------------------------------------------------------------------

    sealed class Index(IEnumerable<UserSummary> users, IEnumerable<GroupSummary> groups) : IDirectoryIndex
    {
        readonly Dictionary<string, UserSummary> _users = users.ToDictionary(u => u.Id);
        readonly Dictionary<string, GroupSummary> _groups = groups.ToDictionary(g => g.Id);
        public UserSummary? User(string id) => _users.GetValueOrDefault(id);
        public GroupSummary? Group(string id) => _groups.GetValueOrDefault(id);
        public IEnumerable<UserSummary> AllUsers => _users.Values;
        public IEnumerable<GroupSummary> AllGroups => _groups.Values;
    }

    /// <param name="withGroups">False: the local copy has no groups yet (a team site's group just made).</param>
    static async Task<(SiteDetailViewModel Page, ChangePipeline Pipeline, DemoTenantDirectory Directory)> DemoPage(Func<SiteSummary, bool> pick, bool withGroups = true)
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false),
            DemoUserRecords.Account);
        var index = new Index(await directory.GetUsersAsync(Ct), withGroups ? await directory.GetGroupsAsync(Ct) : []);
        var site = (await directory.GetSitesAsync(Ct)).First(pick);
        return (new SiteDetailViewModel(site, records, index, pipeline: pipeline, canEdit: Availability.Yes, me: DemoUserRecords.Account), pipeline, directory);
    }

    [Fact]
    public async Task A_site_you_cant_see_inside_offers_to_add_you_and_reads_again_once_applied()
    {
        var (page, pipeline, _) = await DemoPage(s => s.OwnerName is null && s.Kind == SiteKind.TeamSite && !s.IsArchived);
        var permissions = page.Sections.OfType<SitePermissionsSection>().Single();
        await permissions.LoadAsync(Ct);
        Assert.Equal("Add Yourself as Site Admin", permissions.NoteActionText);

        permissions.NoteActionCommand.Execute(null);
        Assert.IsType<SetSiteAdmin>(Assert.Single(pipeline.Pending));
        Assert.Null(permissions.NoteActionText);

        await pipeline.ApplyAsync(cancellationToken: Ct);
        for (var i = 0; i < 50 && (permissions.IsLoading || permissions.Groups.Count == 0); i++)
            await Task.Delay(10, Ct);
        Assert.Null(permissions.Note);
        Assert.NotEmpty(permissions.Groups);
    }

    [Fact]
    public async Task A_groups_site_lists_the_groups_owners_and_keeps_its_last_owner()
    {
        var (page, pipeline, directory) = await DemoPage(s => s.GroupId is not null && s.Kind == SiteKind.TeamSite && s.OwnerName is not null && !s.IsArchived);
        var group = (await directory.GetGroupsAsync(Ct)).Single(g => g.Id == page.Site.GroupId);
        var permissions = page.Sections.OfType<SitePermissionsSection>().Single();
        await permissions.LoadAsync(Ct);

        var owners = permissions.Groups.Single(g => g.Name == "Owners");
        Assert.Equal(group.OwnerList.Count, owners.Members.Count(m => m.Principal.Kind == PrincipalKind.Person));
        if (group.OwnerList.Count == 1)
            Assert.True(owners.Members.Single(m => m.Principal.Kind == PrincipalKind.Person).IsLocked);

        var members = permissions.Groups.Single(g => g.Name == "Members");
        members.Picker!.Query = "a";
        var pick = members.Picker.Results.First();
        members.Picker.PickCommand.Execute(pick);

        var staged = Assert.IsType<SetGroupMembership>(Assert.Single(pipeline.Pending));
        Assert.Equal((group.Id, GroupRole.Member, true), (staged.Group.Id, staged.Role, staged.New));
        Assert.Contains(permissions.Groups.Single(g => g.Name == "Members").Members, m => m.StateText == "Adding");
    }

    [Fact]
    public async Task Site_pickers_label_people_SharePoint_wont_take()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var users = await directory.GetUsersAsync(Ct);
        var index = new Index(users, []);

        var options = GroupAccess.SitePeople(index).ToDictionary(o => o.Id);

        var unlicensed = users.FirstOrDefault(u => UserFields.TypeOf(u) == UserFields.Member && u.Licenses.Count == 0);
        var licensed = users.First(u => UserFields.TypeOf(u) == UserFields.Member && u.Licenses.Count > 0);
        Assert.DoesNotContain(GroupAccess.NoSharePointLicense, options[licensed.Id].Detail, StringComparison.Ordinal);
        if (unlicensed is not null)
            Assert.EndsWith(GroupAccess.NoSharePointLicense, options[unlicensed.Id].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_groups_site_whose_group_isnt_in_the_local_copy_shows_its_people_without_changing_them()
    {
        var (page, _, _) = await DemoPage(s => s.GroupId is not null && s.Kind == SiteKind.TeamSite && s.OwnerName is not null && !s.IsArchived, withGroups: false);
        var permissions = page.Sections.OfType<SitePermissionsSection>().Single();
        await permissions.LoadAsync(Ct);

        foreach (var card in permissions.Groups.Where(g => g.Name is "Owners" or "Members"))
        {
            Assert.False(card.HasPicker);
            Assert.All(card.Members, m => Assert.False(m.HasAction));
            Assert.Contains("after the next refresh", card.Explanation!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task With_one_owner_leaving_the_others_keep_the_last_owner_in_place()
    {
        var (page, pipeline, directory) = await DemoPage(s => s.GroupId is not null && s.Kind == SiteKind.TeamSite && s.OwnerName is not null && !s.IsArchived);
        var group = (await directory.GetGroupsAsync(Ct)).Single(g => g.Id == page.Site.GroupId);
        Assert.SkipWhen(group.OwnerList.Count != 2, "the demo site's group needs exactly two owners");
        var permissions = page.Sections.OfType<SitePermissionsSection>().Single();
        await permissions.LoadAsync(Ct);

        permissions.Groups.Single(g => g.Name == "Owners").Members.First(m => m.HasAction).DoCommand.Execute(null);

        Assert.Single(pipeline.Pending);
        var owners = permissions.Groups.Single(g => g.Name == "Owners").Members.Where(m => m.Principal.Kind == PrincipalKind.Person).ToArray();
        Assert.Single(owners, m => m.StateText == "Removing");
        Assert.Single(owners, m => m.Locked == "The group's last owner");
    }
}
