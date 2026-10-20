using System.Net;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;

namespace Tenantry.Tests;

public class DirectorySyncTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    const string Base = "https://graph.microsoft.com/v1.0/";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Two pages of users (a delta link at the end), one license, sign-in times for one user and
    // for someone not in the directory (who must not be invented).
    static FakeHttp FirstRun() => new FakeHttp()
        .On(HttpMethod.Get, "/subscribedSkus", """{"value":[{"skuId":"05e9a617-0261-4cee-bb44-138d3ef5d965","skuPartNumber":"SPE_E3"}]}""")
        .On(HttpMethod.Get, "users/delta?$select", $$"""
            {"value":[
              {"id":"u1","displayName":"Ada","userPrincipalName":"ada@contoso.example","accountEnabled":true,"assignedLicenses":[{"skuId":"05e9a617-0261-4cee-bb44-138d3ef5d965"}]},
              {"id":"u2","displayName":"Bo","userPrincipalName":"bo@contoso.example","accountEnabled":true,"assignedLicenses":[]}],
             "@odata.nextLink":"{{Base}}users/delta?$skiptoken=page2"}
            """)
        .On(HttpMethod.Get, "$skiptoken=page2", $$"""
            {"value":[{"id":"u3","displayName":"Cy","userPrincipalName":"cy@contoso.example","accountEnabled":false}],
             "@odata.deltaLink":"{{Base}}users/delta?$deltatoken=d1"}
            """)
        .On(HttpMethod.Get, "users?$select=id,signInActivity", """
            {"value":[
              {"id":"u1","signInActivity":{"lastSignInDateTime":"2026-10-19T08:00:00Z"}},
              {"id":"ghost","signInActivity":{"lastSignInDateTime":"2026-10-19T08:00:00Z"}}]}
            """);

    static GraphClient Graph(FakeHttp http) => new(http.Client(), new FakeTokens(), delay: NoDelay.Instance);

    [Fact]
    public async Task First_sync_downloads_everyone_and_keeps_the_delta_link()
    {
        var http = FirstRun();
        var store = new MemoryEntityStore();
        var sync = new DirectorySync(Graph(http), store, () => Now);
        var progress = new List<string>();

        await sync.SyncAsync(new SyncProgress(progress), cancellationToken: Ct);
        var users = await new CachedTenantDirectory(store, "Contoso").GetUsersAsync(Ct);

        Assert.Equal(["Ada", "Bo", "Cy"], users.Select(u => u.DisplayName).Order().ToArray());
        var ada = users.Single(u => u.Id == "u1");
        Assert.Equal(["Microsoft 365 E3"], ada.Licenses);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), ada.LastSignIn);
        Assert.DoesNotContain(users, u => u.Id == "ghost");
        Assert.Equal($"{Base}users/delta?$deltatoken=d1", store.GetMark(DirectorySync.Users)!.Cursor);
        Assert.Equal(Now, sync.LastSyncedAt);
        Assert.Contains("Downloading users… 3", progress);
    }

    [Fact]
    public async Task Later_syncs_fetch_only_changes_and_merge_them()
    {
        var http = FirstRun()
            .On(HttpMethod.Get, "$deltatoken=d1", $$$"""
                {"value":[
                  {"id":"u1","department":"Finance"},
                  {"id":"u2","@removed":{"reason":"deleted"}}],
                 "@odata.deltaLink":"{{{Base}}}users/delta?$deltatoken=d2"}
                """);
        var store = new MemoryEntityStore();
        var sync = new DirectorySync(Graph(http), store, () => Now);
        await sync.SyncAsync(cancellationToken: Ct);
        var before = http.Requests.Count;

        var changed = await sync.SyncAsync(cancellationToken: Ct);

        var users = await new CachedTenantDirectory(store, "Contoso").GetUsersAsync(Ct);
        Assert.Equal(2, changed);
        Assert.Equal(["Ada", "Cy"], users.Select(u => u.DisplayName).Order().ToArray());
        var ada = users.Single(u => u.Id == "u1");
        Assert.Equal("Finance", ada.Department);
        Assert.Equal("ada@contoso.example", ada.UserPrincipalName);
        Assert.NotNull(ada.LastSignIn);
        // Sign-in times were read under an hour ago, so the second sync skips them.
        Assert.DoesNotContain(http.Requests.Skip(before), r => r.PathAndQuery.Contains("signInActivity", StringComparison.Ordinal));
        Assert.EndsWith("$deltatoken=d2", store.GetMark(DirectorySync.Users)!.Cursor);
    }

    [Fact]
    public async Task An_expired_delta_link_starts_over_and_sweeps_out_the_departed()
    {
        var http = FirstRun()
            .On(HttpMethod.Get, "$deltatoken=old", """{"error":{"code":"syncStateNotFound","message":"expired"}}""", HttpStatusCode.Gone);
        var store = new MemoryEntityStore();
        store.Apply(DirectorySync.Users, [new EntityChange("departed", JsonNode.Parse("""{"id":"departed","displayName":"Gone"}""")!.AsObject())]);
        store.SetMark(DirectorySync.Users, new SyncMark($"{Base}users/delta?$deltatoken=old", Now.AddDays(-40)));

        await new DirectorySync(Graph(http), store, () => Now).SyncAsync(cancellationToken: Ct);

        var users = await new CachedTenantDirectory(store, "Contoso").GetUsersAsync(Ct);
        Assert.Equal(["u1", "u2", "u3"], users.Select(u => u.Id).Order().ToArray());
        Assert.EndsWith("$deltatoken=d1", store.GetMark(DirectorySync.Users)!.Cursor);
    }

    [Fact]
    public async Task No_access_to_sign_in_activity_is_not_an_error()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "users?$select=id,signInActivity", """{"error":{"code":"Authentication_RequestFromNonPremiumTenantOrB2CTenant","message":"premium"}}""", HttpStatusCode.Forbidden)
            .On(HttpMethod.Get, "/subscribedSkus", """{"value":[]}""")
            .On(HttpMethod.Get, "users/delta?$select", $$"""{"value":[{"id":"u1","displayName":"Ada"}],"@odata.deltaLink":"{{Base}}users/delta?$deltatoken=d1"}""");
        var store = new MemoryEntityStore();

        await new DirectorySync(Graph(http), store, () => Now).SyncAsync(cancellationToken: Ct);

        var ada = (await new CachedTenantDirectory(store, "Contoso").GetUsersAsync(Ct)).Single();
        Assert.Null(ada.LastSignIn);
        Assert.NotNull(store.GetMark(DirectorySync.SignIns));
    }

    // ---- The main window around a sync ----------------------------------------------------

    static ConnectedTenant Tenant(FakeHttp http, IEntityStore store) => new(
        "Contoso", "admin@contoso.example", new CachedTenantDirectory(store, "Contoso"),
        new TenantContext(new HashSet<string>([DirectoryRoles.GlobalAdministrator]), new HashSet<string>(), true),
        Sync: new DirectorySync(Graph(http), store, () => Now));

    [Fact]
    public async Task First_open_downloads_then_shows_the_list()
    {
        var vm = new MainViewModel(Tenant(FirstRun(), new MemoryEntityStore()), now: () => Now);
        Assert.Equal("Not downloaded yet", vm.SyncText);

        await vm.InitializeAsync(Ct);

        Assert.Equal(3, vm.Users.Rows.Count);
        Assert.False(vm.Users.IsLoading);
        Assert.Null(vm.SyncError);
        Assert.StartsWith("Updated ", vm.SyncText);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task Reopening_shows_the_copy_then_applies_changes_without_losing_the_users_place()
    {
        var store = new MemoryEntityStore();
        var http = FirstRun()
            .On(HttpMethod.Get, "$deltatoken=d1", $$"""{"value":[{"id":"u3","displayName":"Cyrus"}],"@odata.deltaLink":"{{Base}}users/delta?$deltatoken=d2"}""");
        await new DirectorySync(Graph(http), store, () => Now).SyncAsync(cancellationToken: Ct);

        var vm = new MainViewModel(Tenant(http, store), now: () => Now);
        await vm.Users.LoadAsync(Ct);
        vm.Users.SearchText = "contoso";
        vm.Users.SelectedRow = vm.Users.Rows.Single(r => ((UserSummary)r).Id == "u3");

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("contoso", vm.Users.SearchText);
        var selected = Assert.IsType<UserSummary>(vm.Users.SelectedRow);
        Assert.Equal("Cyrus", selected.DisplayName);
    }

    [Fact]
    public async Task A_failed_first_download_says_so_in_the_list_and_the_status()
    {
        var http = new FakeHttp().On(HttpMethod.Get, "/subscribedSkus", """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges."}}""", HttpStatusCode.Forbidden);
        var vm = new MainViewModel(Tenant(http, new MemoryEntityStore()), now: () => Now);

        await vm.InitializeAsync(Ct);

        Assert.True(vm.HasSyncError);
        Assert.Equal("Couldn't update", vm.SyncText);
        Assert.Contains("Insufficient privileges", vm.Users.LoadError);
        Assert.Contains("Insufficient privileges", vm.SyncTip);
    }

    /// <summary>Collects progress synchronously (Progress&lt;T&gt; posts later, which a test can't wait for).</summary>
    sealed class SyncProgress(List<string> into) : IProgress<string>
    {
        public void Report(string value) => into.Add(value);
    }
}
