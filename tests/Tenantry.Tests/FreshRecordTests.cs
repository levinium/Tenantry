using System.Net;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>Opening a record checks it against Microsoft 365: the one-object re-read, what counts as a change, and how the page says so.</summary>
public sealed class FreshRecordTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- What changed -----------------------------------------------------------------------

    [Fact]
    public void A_changed_job_title_is_named_and_a_new_last_sign_in_is_kept_quiet()
    {
        var old = FakeDirectory.User("1", "Ada Lovelace", "Finance", "Controller");
        var fresh = old with { JobTitle = "CFO", LastSignIn = DateTimeOffset.UnixEpoch.AddDays(9000) };

        var changes = RecordChanges.Between(old, fresh);

        Assert.Equal([("title", "Job title", false), ("signin", "Last sign-in", true)], changes.Select(c => (c.Key, c.Label, c.Quiet)));
        Assert.Empty(RecordChanges.Between(old, old with { Licenses = [.. old.Licenses] }));
    }

    [Fact]
    public void Swapping_one_member_for_another_counts_though_the_count_is_the_same()
    {
        var group = new GroupSummary("g1", "Finance", "finance@example.org", null, GroupKind.Microsoft365, false, false, null, null,
            [new GroupMember("1", "user")]);

        var changes = RecordChanges.Between(group, group with { Members = [new GroupMember("2", "user")] });

        Assert.Equal("Members", Assert.Single(changes).Label);
    }

    // ---- How the page says it ---------------------------------------------------------------

    [Fact]
    public async Task Updated_names_the_fields_tints_them_then_settles_to_up_to_date()
    {
        RecordFreshness.SettleAfter = TimeSpan.FromMilliseconds(50);
        var freshness = new RecordFreshness();
        IReadOnlySet<string>? tinted = null;
        freshness.Highlight += (_, keys) => tinted = keys;

        freshness.Updated([new FieldChange("title", "Job title"), new FieldChange("signin", "Last sign-in", Quiet: true)], DateTimeOffset.Now);

        Assert.Equal("Updated from Microsoft 365: Job title", freshness.Text);
        Assert.Equal(["signin", "title"], tinted!.Order());
        for (var i = 0; i < 100 && freshness.State != FreshnessState.UpToDate; i++)
            await Task.Delay(10, Ct);
        Assert.StartsWith("Up to date · checked", freshness.Text);
    }

    [Fact]
    public void Only_quiet_changes_go_straight_to_up_to_date()
    {
        var freshness = new RecordFreshness();

        freshness.Updated([new FieldChange("signin", "Last sign-in", Quiet: true)], DateTimeOffset.Now);

        Assert.Equal(FreshnessState.UpToDate, freshness.State);
    }

    // ---- The whole flow ---------------------------------------------------------------------

    /// <summary>A directory whose users can change, and a refresher that applies a change when asked (as Microsoft 365 would show it).</summary>
    sealed class ChangingTenant : ITenantDirectory, ITenantSync, IRecordRefresher
    {
        public List<UserSummary> People { get; } = [FakeDirectory.User("1", "Ada Lovelace", "Finance", "Controller"), FakeDirectory.User("2", "Alan Turing", "IT", "Engineer")];
        public Func<UserSummary, UserSummary>? InMicrosoft365 { get; set; }
        public int Refreshes { get; private set; }

        public string TenantName => "Test tenant";
        public Task<IReadOnlyList<UserSummary>> GetUsersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UserSummary>>(People.ToArray());
        public Task<IReadOnlyList<GroupSummary>> GetGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GroupSummary>>([]);

        public DateTimeOffset? LastSyncedAt => DateTimeOffset.Now;
        public bool IsPersistent => false;
        public IReadOnlyList<string> Notes => [];
        public Task<int> SyncAsync(IProgress<string>? progress = null, SyncScope scope = SyncScope.Changes, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task RefreshUserAsync(string userId, CancellationToken cancellationToken = default)
        {
            Refreshes++;
            var index = People.FindIndex(u => u.Id == userId);
            if (InMicrosoft365 is { } change && index >= 0)
                People[index] = change(People[index]);
            return Task.CompletedTask;
        }

        public Task RefreshGroupAsync(string groupId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    static (MainViewModel Vm, ChangingTenant Tenant) Open()
    {
        var tenant = new ChangingTenant();
        var vm = new MainViewModel(new ConnectedTenant("Test tenant", "admin@example.org", tenant,
            new TenantContext(new HashSet<string>([DirectoryRoles.GlobalAdministrator]), new HashSet<string>(), true), Sync: tenant));
        return (vm, tenant);
    }

    [Fact]
    public async Task Opening_a_record_shows_the_saved_copy_then_what_changed_in_Microsoft_365()
    {
        var (vm, tenant) = Open();
        await vm.Users.LoadAsync(Ct);
        tenant.InMicrosoft365 = u => u with { JobTitle = "CFO" };

        vm.OpenUser(tenant.People[0]);
        var page = (UserDetailViewModel)vm.SelectedTab!;
        for (var i = 0; i < 100 && page.Freshness.State is FreshnessState.None or FreshnessState.Checking; i++)
            await Task.Delay(10, Ct);

        Assert.Equal("CFO", page.User.JobTitle);
        Assert.Equal(FreshnessState.Updated, page.Freshness.State);
        Assert.Contains("Job title", page.Freshness.Text);
        Assert.Equal(1, tenant.Refreshes);
    }

    [Fact]
    public async Task Reopening_within_a_minute_doesnt_read_again_but_refresh_does()
    {
        var (vm, tenant) = Open();
        await vm.Users.LoadAsync(Ct);
        vm.OpenUser(tenant.People[1]);
        var page = (UserDetailViewModel)vm.SelectedTab!;
        for (var i = 0; i < 100 && page.Freshness.State != FreshnessState.UpToDate; i++)
            await Task.Delay(10, Ct);

        vm.OpenUser(tenant.People[1]);
        Assert.Equal(1, tenant.Refreshes);

        await page.Freshness.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, tenant.Refreshes);
    }

    [Fact]
    public async Task A_preview_is_checked_only_once_it_stays_on_screen()
    {
        MainViewModel.PreviewSettle = TimeSpan.FromMilliseconds(80);
        var (vm, tenant) = Open();
        await vm.Users.LoadAsync(Ct);

        vm.Users.IsPreviewOpen = true;
        // Skimming: two rows in quick succession, then resting on the second.
        vm.Users.SelectedRow = vm.Users.Rows[0];
        vm.Users.SelectedRow = vm.Users.Rows[1];
        await Task.Delay(400, Ct);

        Assert.Equal(1, tenant.Refreshes);
    }

    [Fact]
    public async Task Without_a_refresher_nothing_is_shown()
    {
        var vm = new MainViewModel(FakeConnector.Tenant());
        await vm.Users.LoadAsync(Ct);

        vm.OpenUser((await FakeDirectory.Standard().GetUsersAsync(Ct))[0]);

        Assert.False(((UserDetailViewModel)vm.SelectedTab!).Freshness.IsVisible);
    }

    // ---- The one-object re-read --------------------------------------------------------------

    [Fact]
    public async Task A_user_is_reread_into_the_local_copy_and_a_deleted_one_leaves_it()
    {
        var store = new MemoryEntityStore();
        store.Apply(DirectorySync.Users, [new EntityChange("u1", System.Text.Json.Nodes.JsonNode.Parse("""{"id":"u1","displayName":"Old name","userPrincipalName":"ada@example.org"}""")!.AsObject())]);
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/users/u1?", """{"id":"u1","displayName":"New name","userPrincipalName":"ada@example.org","accountEnabled":true}""")
            .On(HttpMethod.Get, "/users/gone?", """{"error":{"code":"Request_ResourceNotFound","message":"Not found."}}""", HttpStatusCode.NotFound)
            .On(HttpMethod.Get, "/userRegistrationDetails/u1", """{"id":"u1","isAdmin":false,"isMfaRegistered":true,"methodsRegistered":["microsoftAuthenticatorPush"]}""");
        var sync = new DirectorySync(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store);
        store.Apply(DirectorySync.Users, [new EntityChange("gone", System.Text.Json.Nodes.JsonNode.Parse("""{"id":"gone","displayName":"Gone"}""")!.AsObject())]);

        await sync.RefreshUserAsync("u1", Ct);
        await sync.RefreshUserAsync("gone", Ct);

        var directory = new CachedTenantDirectory(store, "Test");
        var refreshed = (await ((ITenantDirectory)directory).GetUserAsync("u1", Ct))!;
        Assert.Equal("New name", refreshed.DisplayName);
        Assert.True(refreshed.Security!.MfaRegistered);
        Assert.Null(store.Get(DirectorySync.Users, "gone"));
        Assert.Contains(http.Requests, r => r.PathAndQuery.Contains("signInActivity", StringComparison.Ordinal));
    }
}
