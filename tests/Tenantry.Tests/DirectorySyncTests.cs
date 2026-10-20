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
    // for someone not in the directory (who must not be invented). Two pages of groups: g1's
    // members arrive split across both pages, g2 has none; g1 is a Team. groupChanges is what
    // the groups delta link returns on the next sync (nothing, by default).
    static FakeHttp FirstRun(string groupChanges = """{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=gd2"}""") => new FakeHttp()
        .On(HttpMethod.Get, "groups/delta?$select", $$$"""
            {"value":[
              {"id":"g1","displayName":"Finance","mailEnabled":true,"securityEnabled":false,"groupTypes":["Unified"],
               "members@delta":[{"@odata.type":"#microsoft.graph.user","id":"u1"},{"@odata.type":"#microsoft.graph.user","id":"u2"}]}],
             "@odata.nextLink":"{{{Base}}}groups/delta?$skiptoken=gpage2"}
            """)
        .On(HttpMethod.Get, "$skiptoken=gpage2", $$$"""
            {"value":[
              {"id":"g1","members@delta":[{"@odata.type":"#microsoft.graph.group","id":"g2"}]},
              {"id":"g2","displayName":"Old Printer Access","mailEnabled":false,"securityEnabled":true,"groupTypes":[]}],
             "@odata.deltaLink":"{{{Base}}}groups/delta?$deltatoken=gd1"}
            """)
        .On(HttpMethod.Get, "$deltatoken=gd1", groupChanges)
        .On(HttpMethod.Get, "/domains?", """{"value":[{"id":"contoso.example","isVerified":true}]}""")
        .On(HttpMethod.Get, "resourceProvisioningOptions", """{"value":[{"id":"g1"}]}""")
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
        var sync = new DirectorySync(Graph(http), store, now: () => Now);
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
        var sync = new DirectorySync(Graph(http), store, now: () => Now);
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

        await new DirectorySync(Graph(http), store, now: () => Now).SyncAsync(cancellationToken: Ct);

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
            .On(HttpMethod.Get, "users/delta?$select", $$"""{"value":[{"id":"u1","displayName":"Ada"}],"@odata.deltaLink":"{{Base}}users/delta?$deltatoken=d1"}""")
            .On(HttpMethod.Get, "groups/delta?$select", $$"""{"value":[],"@odata.deltaLink":"{{Base}}groups/delta?$deltatoken=gd1"}""")
            .On(HttpMethod.Get, "/domains?", """{"value":[]}""")
            .On(HttpMethod.Get, "resourceProvisioningOptions", """{"value":[]}""");
        var store = new MemoryEntityStore();

        await new DirectorySync(Graph(http), store, now: () => Now).SyncAsync(cancellationToken: Ct);

        var ada = (await new CachedTenantDirectory(store, "Contoso").GetUsersAsync(Ct)).Single();
        Assert.Null(ada.LastSignIn);
        Assert.NotNull(store.GetMark(DirectorySync.SignIns));
    }

    // ---- Groups -----------------------------------------------------------------------------

    [Fact]
    public async Task Groups_collect_members_spread_over_pages_and_know_their_kind()
    {
        var store = new MemoryEntityStore();
        await new DirectorySync(Graph(FirstRun()), store, now: () => Now).SyncAsync(cancellationToken: Ct);

        var groups = (await new CachedTenantDirectory(store, "Contoso").GetGroupsAsync(Ct)).ToDictionary(g => g.Id);

        var finance = groups["g1"];
        Assert.Equal(GroupKind.Microsoft365, finance.Kind);
        Assert.True(finance.IsTeam);
        Assert.Equal([("u1", "user"), ("u2", "user"), ("g2", "group")], finance.Members.Select(m => (m.Id, m.Type)).ToArray());
        Assert.Equal(GroupKind.Security, groups["g2"].Kind);
        Assert.False(groups["g2"].IsTeam);
        Assert.Empty(groups["g2"].Members);
    }

    [Fact]
    public async Task Group_deltas_apply_joins_and_leaves_and_removals()
    {
        var http = FirstRun($$$"""
            {"value":[
              {"id":"g1","members@delta":[
                {"@odata.type":"#microsoft.graph.user","id":"u2","@removed":{"reason":"deleted"}},
                {"@odata.type":"#microsoft.graph.device","id":"d9"}]},
              {"id":"g2","@removed":{"reason":"changed"}}],
             "@odata.deltaLink":"{{{Base}}}groups/delta?$deltatoken=gd2"}
            """)
            .On(HttpMethod.Get, "users/delta?$deltatoken=d1", $$"""{"value":[],"@odata.deltaLink":"{{Base}}users/delta?$deltatoken=d2"}""");
        var store = new MemoryEntityStore();
        var sync = new DirectorySync(Graph(http), store, now: () => Now);
        await sync.SyncAsync(cancellationToken: Ct);

        await sync.SyncAsync(cancellationToken: Ct);

        var finance = (await new CachedTenantDirectory(store, "Contoso").GetGroupsAsync(Ct)).Single();
        Assert.Equal("Finance", finance.DisplayName);
        Assert.Equal([("u1", "user"), ("g2", "group"), ("d9", "device")], finance.Members.Select(m => (m.Id, m.Type)).ToArray());
    }

    // ---- Mailboxes ----------------------------------------------------------------------------

    sealed class FakeExchange(Func<IReadOnlyList<System.Text.Json.JsonElement>> answer) : Tenantry.Core.Exchange.ICmdletRunner
    {
        public List<(string Cmdlet, IReadOnlyList<string>? Select)> Calls { get; } = [];

        public Task<IReadOnlyList<System.Text.Json.JsonElement>> InvokeAsync(Tenantry.Core.Exchange.CmdletEndpoint endpoint, string cmdlet,
            IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default, IReadOnlyList<string>? select = null)
        {
            Calls.Add((cmdlet, select));
            return Task.FromResult(answer());
        }
    }

    static IReadOnlyList<System.Text.Json.JsonElement> Rows(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();

    [Fact]
    public async Task Mailboxes_join_their_users_and_forwarding_is_judged_against_the_tenants_domains()
    {
        var exchange = new FakeExchange(() => Rows("""
            [
              {"ExternalDirectoryObjectId":"u1","RecipientTypeDetails":"UserMailbox","PrimarySmtpAddress":"ada@contoso.example",
               "EmailAddresses":["SMTP:ada@contoso.example","smtp:a.l@contoso.example","SIP:ada@contoso.example"],
               "ForwardingSmtpAddress":"smtp:ada@personal.example","DeliverToMailboxAndForward":true,"ArchiveStatus":"Active"},
              {"ExternalDirectoryObjectId":"u2","RecipientTypeDetails":"SharedMailbox","PrimarySmtpAddress":"bo@contoso.example",
               "ForwardingSmtpAddress":"smtp:team@sub.contoso.example","LitigationHoldEnabled":true,"ArchiveStatus":"None"},
              {"ExternalDirectoryObjectId":"","RecipientTypeDetails":"DiscoveryMailbox"}
            ]
            """));
        var store = new MemoryEntityStore();

        await new DirectorySync(Graph(FirstRun()), store, exchange, () => Now).SyncAsync(cancellationToken: Ct);

        var users = (await new CachedTenantDirectory(store, "Contoso").GetUsersAsync(Ct)).ToDictionary(u => u.Id);
        var ada = users["u1"].Mailbox!;
        Assert.Equal("User", ada.Type);
        Assert.Equal(["a.l@contoso.example"], ada.Aliases);
        Assert.Equal(ForwardingKind.External, ada.Forwarding);
        Assert.Equal("ada@personal.example", ada.ForwardingTo);
        Assert.True(ada.KeepsCopyWhenForwarding);
        Assert.True(ada.ArchiveEnabled);
        var bo = users["u2"].Mailbox!;
        Assert.Equal("Shared", bo.Type);
        Assert.Equal(ForwardingKind.Internal, bo.Forwarding);
        Assert.True(bo.LitigationHold);
        Assert.Null(users["u3"].Mailbox);
        // Only the properties the lists use are asked for, not all 300-odd.
        Assert.Contains("ExternalDirectoryObjectId", exchange.Calls.Single().Select!);
    }

    [Fact]
    public async Task No_exchange_role_leaves_a_note_and_the_rest_syncs()
    {
        var exchange = new FakeExchange(() => throw new Tenantry.Core.Exchange.CmdletException("Get-Mailbox", 403, "The user isn't assigned to any management roles."));
        var store = new MemoryEntityStore();
        var sync = new DirectorySync(Graph(FirstRun()), store, exchange, () => Now);

        await sync.SyncAsync(cancellationToken: Ct);

        Assert.Equal(3, store.All(DirectorySync.Users).Count);
        Assert.Contains("management roles", Assert.Single(sync.Notes));
    }

    // ---- The main window around a sync ----------------------------------------------------

    static ConnectedTenant Tenant(FakeHttp http, IEntityStore store) => new(
        "Contoso", "admin@contoso.example", new CachedTenantDirectory(store, "Contoso"),
        new TenantContext(new HashSet<string>([DirectoryRoles.GlobalAdministrator]), new HashSet<string>(), true),
        Sync: new DirectorySync(Graph(http), store, now: () => Now));

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
        await new DirectorySync(Graph(http), store, now: () => Now).SyncAsync(cancellationToken: Ct);

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
