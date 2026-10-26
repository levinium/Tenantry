using System.Net;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>The OneDrive and Activity tabs (4d-1): SharePoint admin calls, the tabs' states and what they stage.</summary>
public sealed class OneDriveActivityTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string Root = """{"siteCollection":{"hostname":"contoso.sharepoint.com"}}""";

    static GraphClient Graph(FakeHttp http) => new(http.Client(), new FakeTokens(), delay: NoDelay.Instance);

    static SharePointAdmin SharePoint(FakeHttp http) => new(http.Client(), new FakeTokens(), Graph(http), NoDelay.Instance);

    // ---- SharePoint admin -------------------------------------------------------------------

    [Fact]
    public async Task A_profile_page_answer_means_no_OneDrive_yet()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/sites/root", Root)
            .Once(HttpMethod.Post, "contoso-admin.sharepoint.com/_vti_bin/client.svc/ProcessQuery",
                """[{"SchemaVersion":"15.0.0.0","ErrorInfo":null},2,{"IsNull":false},3,"https://contoso-my.sharepoint.com/Person.aspx?accountname=x"]""")
            .Once(HttpMethod.Post, "contoso-admin.sharepoint.com/_vti_bin/client.svc/ProcessQuery",
                """[{"SchemaVersion":"15.0.0.0","ErrorInfo":null},2,{"IsNull":false},3,"https://contoso-my.sharepoint.com/personal/ada_contoso_example/"]""");
        var sharePoint = SharePoint(http);

        Assert.Null(await sharePoint.GetPersonalSiteUrlAsync("ada@contoso.example", Ct));
        Assert.Equal("https://contoso-my.sharepoint.com/personal/ada_contoso_example", await sharePoint.GetPersonalSiteUrlAsync("ada@contoso.example", Ct));
        Assert.Contains("TypeId=\"{268004ae-ef6b-4e9b-8425-127220d84719}\"", http.Requests[1].Body);
        // The host is read from Graph once.
        Assert.Single(http.Requests, r => r.PathAndQuery.Contains("/sites/root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Giving_access_sends_the_login_and_a_refusal_carries_SharePoints_message()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/sites/root", Root)
            .Once(HttpMethod.Post, "ProcessQuery", """[{"SchemaVersion":"15.0.0.0","ErrorInfo":null},2,{}]""")
            .Once(HttpMethod.Post, "ProcessQuery", """[{"SchemaVersion":"15.0.0.0","ErrorInfo":{"ErrorMessage":"The user does not exist.","ErrorCode":-2146232832}}]""");
        var sharePoint = SharePoint(http);

        await sharePoint.SetSiteAdminAsync("https://contoso-my.sharepoint.com/personal/bo", SetOneDriveAccess.LoginFor("ada@contoso.example"), true, Ct);
        var refused = await Assert.ThrowsAsync<SharePointException>(() =>
            sharePoint.SetSiteAdminAsync("https://contoso-my.sharepoint.com/personal/bo", "i:0#.f|membership|nobody@contoso.example", true, Ct));

        var body = http.Requests[1].Body!;
        Assert.Contains("<Parameter Type=\"String\">i:0#.f|membership|ada@contoso.example</Parameter>", body);
        Assert.Contains("<Parameter Type=\"Boolean\">true</Parameter>", body);
        Assert.Equal("The user does not exist.", refused.Message);
    }

    [Fact]
    public async Task Setting_up_a_OneDrive_calls_the_profile_loader()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/sites/root", Root)
            .On(HttpMethod.Post, "ProcessQuery", """[{"SchemaVersion":"15.0.0.0","ErrorInfo":null},2,{},3,[]]""");
        var executor = new GraphChangeExecutor(Graph(http), new MemoryEntityStore(), NoDelay.Instance, sharePoint: SharePoint(http));

        var result = await executor.ApplyAsync(new SetUpOneDrive(new ChangeTarget("user", "u2", "Bo", "zz-tenantry-test-bo@contoso.example")), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var body = http.Requests[1].Body!;
        Assert.Contains("Name=\"GetProfileLoader\" TypeId=\"{9c42543a-91b3-4902-b2fe-14ccdefb6e2b}\"", body);
        Assert.Contains("<Object Type=\"String\">zz-tenantry-test-bo@contoso.example</Object>", body);
    }

    [Fact]
    public async Task Access_changes_are_read_back_from_the_admin_list()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/sites/root", Root)
            .On(HttpMethod.Post, "ProcessQuery", """[{"SchemaVersion":"15.0.0.0","ErrorInfo":null},2,{}]""")
            .Once(HttpMethod.Post, "SPO.Tenant/GetSiteAdministrators", """{"value":[{"loginName":"i:0#.f|membership|bo@contoso.example","name":"Bo"}]}""")
            .Once(HttpMethod.Post, "SPO.Tenant/GetSiteAdministrators",
                """{"value":[{"loginName":"i:0#.f|membership|bo@contoso.example","name":"Bo"},{"loginName":"i:0#.f|membership|ada@contoso.example","name":"Ada"}]}""");
        var executor = new GraphChangeExecutor(Graph(http), new MemoryEntityStore(), NoDelay.Instance, sharePoint: SharePoint(http));
        var bo = new ChangeTarget("user", "u2", "Bo", "bo@contoso.example");
        var ada = new ChangeTarget("user", "u1", "Ada", "ada@contoso.example");

        var result = await executor.ApplyAsync(new SetOneDriveAccess(bo, "https://contoso-my.sharepoint.com/personal/bo", "site-1", ada,
            SetOneDriveAccess.LoginFor("ada@contoso.example"), false, true), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Contains(http.Requests, r => r.PathAndQuery.Contains("GetSiteAdministrators?siteId='site-1'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_OneDrive_reads_as_not_set_up_and_a_drive_reads_its_quota()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/users/u1/drive", """{"error":{"code":"itemNotFound","message":"mysite not found"}}""", HttpStatusCode.NotFound)
            .On(HttpMethod.Get, "/users/u2/drive", """
                {"webUrl":"https://contoso-my.sharepoint.com/personal/bo/Documents","sharePointIds":{"siteId":"site-2"},
                 "quota":{"used":1073741824,"total":1099511627776,"deleted":0,"state":"normal"},"lastModifiedDateTime":"2026-10-20T10:00:00Z"}
                """);
        var records = new GraphUserRecords(Graph(http), new MemoryEntityStore());

        var none = await records.GetOneDriveAsync("u1", Ct);
        var drive = (await records.GetOneDriveAsync("u2", Ct)).Value!;

        Assert.Equal((null, null), (none.Value, none.Unavailable));
        Assert.Equal(("site-2", 1073741824L, false), (drive.SiteId, drive.Used, drive.IsNearlyFull));
    }

    [Fact]
    public void Giving_someone_OneDrive_access_is_guarded_on_both_people()
    {
        var change = new SetOneDriveAccess(new ChangeTarget("user", "u2", "zz-tenantry-test-bo", "zz-tenantry-test-bo@contoso.example"), "url", "site",
            new ChangeTarget("user", "u9", "Pat", "pat@contoso.example"), SetOneDriveAccess.LoginFor("pat@contoso.example"), false, true);

        Assert.Equal(2, change.Guarded.Count);
        Assert.False(change.IsCaution);
        Assert.True((change with { Old = true, New = false }).IsCaution);
    }

    // ---- The OneDrive tab -------------------------------------------------------------------

    sealed class Fake : IUserRecords, IOneDriveRecords, IActivityRecords
    {
        public OneDriveInfo? Drive { get; set; }
        public List<SiteAdmin> Admins { get; } = [];
        public int AuditReads { get; private set; }

        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailboxDelegate>>([]);
        public Task<MailDetails> GetMailAsync(string userId, string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Available<OneDriveInfo>> GetOneDriveAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(new Available<OneDriveInfo>(Drive));
        public Task<IReadOnlyList<SiteAdmin>> GetOneDriveAdminsAsync(OneDriveInfo drive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SiteAdmin>>(Admins.ToArray());

        public Task<Available<IReadOnlyList<AuditEvent>>> GetAuditAsync(string userId, bool byThem, CancellationToken cancellationToken = default)
        {
            AuditReads++;
            return Task.FromResult(new Available<IReadOnlyList<AuditEvent>>(byThem ? [] :
                [new AuditEvent(DateTimeOffset.UnixEpoch, "Update user", "UserManagement", false, "Pat", "Bo", "Insufficient privileges")]));
        }

        public Task<Available<AppActivity>> GetAppActivityAsync(string userPrincipalName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Available<AppActivity>(new AppActivity(new DateOnly(2026, 10, 24), [("Teams", new DateOnly(2026, 10, 25)), ("OneDrive", null)])));
    }

    sealed class Noop : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static readonly UserSummary Bo = new("u2", "zz-tenantry-test-bo", "zz-tenantry-test-bo@contoso.example", null, null, null, true, UserKind.Member,
        DateTimeOffset.UnixEpoch, null, ["Microsoft 365 Business Basic"]);

    static readonly UserSummary Ada = new("u1", "zz-tenantry-test-ada", "zz-tenantry-test-ada@contoso.example", null, null, null, true, UserKind.Member,
        DateTimeOffset.UnixEpoch, null, []);

    sealed class Index(params UserSummary[] users) : IDirectoryIndex
    {
        public UserSummary? User(string id) => users.FirstOrDefault(u => u.Id == id);
        public GroupSummary? Group(string id) => null;
        public IEnumerable<UserSummary> AllUsers => users;
        public IEnumerable<GroupSummary> AllGroups => [];
    }

    static (UserDetailViewModel Page, ChangePipeline Pipeline, Fake Records) Page(UserSummary? person = null)
    {
        var pipeline = new ChangePipeline(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var records = new Fake();
        var page = new UserDetailViewModel(person ?? Bo, pipeline: pipeline, canEdit: Availability.Yes, records: records, index: new Index(Bo, Ada));
        return (page, pipeline, records);
    }

    static SiteAdmin AdminOf(UserSummary u) => new(SetOneDriveAccess.LoginFor(u.UserPrincipalName), u.DisplayName, null, u.UserPrincipalName);

    [Fact]
    public async Task A_missing_OneDrive_can_be_set_up_when_they_have_a_license()
    {
        var (page, pipeline, _) = Page();
        var tab = page.Sections.OfType<OneDriveSection>().Single();
        await tab.LoadAsync(Ct);

        Assert.True(tab.IsNotSetUp);
        Assert.True(tab.CanSetUp);
        tab.SetUpCommand.Execute(null);
        Assert.IsType<SetUpOneDrive>(Assert.Single(pipeline.Pending));
        Assert.Equal("Undo set up", tab.SetUpText);
        tab.SetUpCommand.Execute(null);
        Assert.Empty(pipeline.Pending);

        var (unlicensedPage, _, _) = Page(Ada);
        var unlicensed = unlicensedPage.Sections.OfType<OneDriveSection>().Single();
        await unlicensed.LoadAsync(Ct);
        Assert.False(unlicensed.CanSetUp);
    }

    [Fact]
    public async Task The_owner_is_listed_first_and_can_not_be_removed_and_others_can_be_added_and_removed()
    {
        var (page, pipeline, records) = Page();
        records.Drive = new OneDriveInfo("https://contoso-my.sharepoint.com/personal/bo", "site-2", 900L << 30, 1L << 40, 0, "nearing", null);
        records.Admins.AddRange([new SiteAdmin("c:0t.c|tenant|abc", "Company Administrator", null, null), AdminOf(Bo)]);
        var tab = page.Sections.OfType<OneDriveSection>().Single();
        await tab.LoadAsync(Ct);

        Assert.True(tab.IsNearlyFull);
        Assert.Equal(("zz-tenantry-test-bo", true, false), (tab.Access[0].Name, tab.Access[0].IsOwner, tab.Access[0].CanRemove));

        tab.Access[1].RemoveCommand.Execute(null);
        var removal = Assert.IsType<SetOneDriveAccess>(Assert.Single(pipeline.Pending));
        Assert.Equal(("c:0t.c|tenant|abc", false, true), (removal.Login, removal.New, removal.IsCaution));
        Assert.Equal("Removing", tab.Access[1].PendingText);
        tab.Access[1].RemoveCommand.Execute(null);
        Assert.Empty(pipeline.Pending);

        tab.AccessPicker.Query = "ada";
        tab.AccessPicker.PickCommand.Execute(tab.AccessPicker.Results.Single());
        var grant = Assert.IsType<SetOneDriveAccess>(Assert.Single(pipeline.Pending));
        Assert.Equal((SetOneDriveAccess.LoginFor(Ada.UserPrincipalName), true, "site-2"), (grant.Login, grant.New, grant.SiteId));
        Assert.Contains(tab.Access, r => r.Name == Ada.DisplayName && r.PendingText == "Adding");
        // Someone already listed (or being added) isn't offered again.
        tab.AccessPicker.Query = "ada";
        Assert.Empty(tab.AccessPicker.Results);
    }

    // ---- The Activity tab -------------------------------------------------------------------

    [Fact]
    public async Task Activity_lists_Tenantrys_changes_involving_them_and_both_directions_of_the_audit_log()
    {
        var (page, pipeline, records) = Page();
        var group = new ChangeTarget("group", "g1", "zz-tenantry-test-group", null);
        pipeline.Stage(new SetGroupMembership(group, new ChangeTarget("user", "u2", "Bo", Bo.UserPrincipalName), "user", GroupRole.Member, false, false, true));
        pipeline.Stage(new SetUserText(new ChangeTarget("user", "u1", "Ada", Ada.UserPrincipalName), "department", "Department", null, "IT"));
        await pipeline.ApplyAsync(cancellationToken: Ct);
        var tab = page.Sections.OfType<ActivitySection>().Single();

        await tab.LoadAsync(Ct);

        var change = Assert.Single(tab.Changes);
        Assert.Equal(("Member Bo", "Done"), (change.What, change.OutcomeText));
        var audit = Assert.Single(tab.Audit);
        Assert.Equal(("By Pat", true, true), (audit.Party, audit.Failed, audit.HasReason));
        Assert.Equal(("Teams", true), (tab.Apps[0].App, tab.Apps[0].IsActive));
        Assert.Equal("No activity in the last 30 days", tab.Apps[1].LastText);

        tab.AuditByThem = true;
        await Task.Yield();
        Assert.Equal(2, records.AuditReads);
        Assert.True(tab.HasNoAudit);
        Assert.False(tab.AuditAboutThem);
    }
}
