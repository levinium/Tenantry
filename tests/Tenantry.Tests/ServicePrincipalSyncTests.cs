using System.Net;
using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;

namespace Tenantry.Tests;

/// <summary>The local copy of enterprise apps: a first pass, changes only after, a forgotten link, publishers on their own schedule.</summary>
public sealed class ServicePrincipalSyncTests
{
    const string Base = "https://graph.microsoft.com/v1.0/";
    static readonly DateTimeOffset Start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static GraphClient Graph(FakeHttp http) => new(http.Client(), new FakeTokens(), delay: NoDelay.Instance);

    /// <summary>Two pages: Payroll and Wiki, then a deleted one (its @removed marker) and the delta link.</summary>
    static FakeHttp FirstPass(FakeHttp? http = null) => (http ?? new FakeHttp())
        .Once(HttpMethod.Get, "servicePrincipals/delta?$select", $$"""
            {"value":[{"id":"sp1","appId":"a1","displayName":"Payroll","accountEnabled":true,"tags":[]},
                      {"id":"sp2","appId":"a2","displayName":"Wiki","accountEnabled":true}],
             "@odata.nextLink":"{{Base}}servicePrincipals/delta?$skiptoken=p2"}
            """)
        .On(HttpMethod.Get, "$skiptoken=p2", $$$"""
            {"value":[{"id":"sp9","@removed":{"reason":"deleted"}}],"@odata.deltaLink":"{{{Base}}}servicePrincipals/delta?$deltatoken=t1"}
            """)
        .On(HttpMethod.Get, "servicePrincipals?$select=id,publisherName", """
            {"value":[{"id":"sp1","publisherName":"Fabrikam"},{"id":"sp2","publisherName":null,"verifiedPublisher":{"displayName":"Contoso Verified"}},
                      {"id":"sp-not-in-delta","publisherName":"Nobody"}]}
            """);

    static JsonElement App(IEntityStore store, string id) => JsonDocument.Parse(store.Get(ServicePrincipalSync.Kind, id)!).RootElement;

    [Fact]
    public async Task The_first_pass_keeps_every_app_its_publisher_and_the_delta_link()
    {
        var store = new MemoryEntityStore();
        var sync = new ServicePrincipalSync(Graph(FirstPass()), store, () => Start);

        await sync.PullAsync(cancellationToken: Ct);

        Assert.True(sync.HasCopy);
        Assert.Equal(2, store.All(ServicePrincipalSync.Kind).Count);
        Assert.Equal("Fabrikam", App(store, "sp1").GetProperty("publisherName").GetString());
        // A publisher for an app delta didn't send never invents one.
        Assert.Null(store.Get(ServicePrincipalSync.Kind, "sp-not-in-delta"));
        Assert.EndsWith("$deltatoken=t1", store.GetMark(ServicePrincipalSync.DeltaMark)!.Cursor);
    }

    [Fact]
    public async Task Later_pulls_fetch_only_changes_and_keep_the_publisher()
    {
        var http = FirstPass();
        var store = new MemoryEntityStore();
        var now = Start;
        var sync = new ServicePrincipalSync(Graph(http), store, () => now);
        await sync.PullAsync(cancellationToken: Ct);
        http.On(HttpMethod.Get, "$deltatoken=t1", $$$"""
            {"value":[{"id":"sp1","accountEnabled":false},{"id":"sp2","@removed":{"reason":"deleted"}},
                      {"id":"sp3","appId":"a3","displayName":"Timesheets","accountEnabled":true}],
             "@odata.deltaLink":"{{{Base}}}servicePrincipals/delta?$deltatoken=t2"}
            """);
        var publisherReads = http.Requests.Count(r => r.PathAndQuery.Contains("publisherName"));
        now = Start.AddMinutes(10);

        await sync.PullAsync(cancellationToken: Ct);

        var payroll = App(store, "sp1");
        Assert.False(payroll.GetProperty("accountEnabled").GetBoolean());
        Assert.Equal("Fabrikam", payroll.GetProperty("publisherName").GetString());
        Assert.Null(store.Get(ServicePrincipalSync.Kind, "sp2"));
        Assert.NotNull(store.Get(ServicePrincipalSync.Kind, "sp3"));
        // Publishers aren't due yet (hourly), and no full list was read.
        Assert.Equal(publisherReads, http.Requests.Count(r => r.PathAndQuery.Contains("publisherName")));
        Assert.DoesNotContain(http.Requests.Skip(3), r => r.PathAndQuery.Contains("servicePrincipals/delta?$select"));
        Assert.EndsWith("$deltatoken=t2", store.GetMark(ServicePrincipalSync.DeltaMark)!.Cursor);
    }

    [Fact]
    public async Task Publisher_names_are_read_again_once_due()
    {
        var http = FirstPass();
        var now = Start;
        var store = new MemoryEntityStore();
        var sync = new ServicePrincipalSync(Graph(http), store, () => now);
        await sync.PullAsync(cancellationToken: Ct);
        http.On(HttpMethod.Get, "$deltatoken=t1", $$"""{"value":[],"@odata.deltaLink":"{{Base}}servicePrincipals/delta?$deltatoken=t1"}""");
        var before = http.Requests.Count(r => r.PathAndQuery.Contains("publisherName"));

        now = Start.AddMinutes(61);
        await sync.PullAsync(cancellationToken: Ct);

        Assert.Equal(before + 1, http.Requests.Count(r => r.PathAndQuery.Contains("publisherName")));
    }

    [Fact]
    public async Task A_forgotten_delta_link_starts_over_and_sweeps_out_what_it_no_longer_sees()
    {
        var http = FirstPass();
        var store = new MemoryEntityStore();
        var sync = new ServicePrincipalSync(Graph(http), store, () => Start);
        await sync.PullAsync(cancellationToken: Ct);
        http.On(HttpMethod.Get, "$deltatoken=t1", """{"error":{"code":"syncStateNotFound","message":"gone"}}""", HttpStatusCode.Gone);
        // The new full pass sees only Payroll: Wiki left while the link was forgotten.
        http.Once(HttpMethod.Get, "servicePrincipals/delta?$select", $$"""
            {"value":[{"id":"sp1","appId":"a1","displayName":"Payroll","accountEnabled":true}],"@odata.deltaLink":"{{Base}}servicePrincipals/delta?$deltatoken=t5"}
            """);

        await sync.PullAsync(cancellationToken: Ct);

        Assert.NotNull(store.Get(ServicePrincipalSync.Kind, "sp1"));
        Assert.Null(store.Get(ServicePrincipalSync.Kind, "sp2"));
        Assert.EndsWith("$deltatoken=t5", store.GetMark(ServicePrincipalSync.DeltaMark)!.Cursor);
    }

    [Fact]
    public async Task An_interrupted_first_pass_keeps_no_link_so_the_next_starts_again()
    {
        var http = new FakeHttp()
            .Once(HttpMethod.Get, "servicePrincipals/delta?$select", $$"""
                {"value":[{"id":"sp1","appId":"a1","displayName":"Payroll"}],"@odata.nextLink":"{{Base}}servicePrincipals/delta?$skiptoken=p2"}
                """)
            .On(HttpMethod.Get, "$skiptoken=p2", """{"error":{"code":"serviceUnavailable","message":"down"}}""", HttpStatusCode.InternalServerError);
        var store = new MemoryEntityStore();
        var sync = new ServicePrincipalSync(Graph(http), store, () => Start);

        await Assert.ThrowsAnyAsync<Exception>(() => sync.PullAsync(cancellationToken: Ct));

        Assert.False(sync.HasCopy);
    }

    [Fact]
    public async Task A_slow_changes_pull_shows_the_copy_now_and_says_when_the_changes_are_in()
    {
        var http = FirstPass();
        var store = new MemoryEntityStore();
        var graph = Graph(http);
        await new ServicePrincipalSync(graph, store).PullAsync(cancellationToken: Ct);
        // Entra takes its time over the changes (as with a run of empty pages), then sends one.
        http.On(HttpMethod.Get, "$deltatoken=t1", _ =>
        {
            Thread.Sleep(2500);
            return FakeHttp.Json($$"""{"value":[{"id":"sp3","appId":"a3","displayName":"Timesheets"}],"@odata.deltaLink":"{{Base}}servicePrincipals/delta?$deltatoken=t2"}""");
        });
        var records = new GraphUserRecords(graph, store);
        var changed = new TaskCompletionSource();
        ((IAppRecords)records).EnterpriseAppsChanged += (_, _) => changed.TrySetResult();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var apps = await ((IAppRecords)records).GetAppsAsync([], Ct);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2.2), $"the copy showed without waiting ({clock.Elapsed.TotalSeconds:0.0} s)");
        Assert.Equal(2, apps.EnterpriseApps.Count);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.NotNull(store.Get(ServicePrincipalSync.Kind, "sp3"));
    }

    [Fact]
    public async Task Enterprise_Apps_come_from_the_copy_once_there_is_one_after_pulling_changes()
    {
        var http = FirstPass();
        http.On(HttpMethod.Get, "$deltatoken=t1", $$"""{"value":[],"@odata.deltaLink":"{{Base}}servicePrincipals/delta?$deltatoken=t1"}""");
        var store = new MemoryEntityStore();
        var graph = Graph(http);
        await new ServicePrincipalSync(graph, store).PullAsync(cancellationToken: Ct);
        var records = new GraphUserRecords(graph, store);
        AppRegistration Ours(string appId) => new("r1", appId, "Payroll", "AzureADMyOrg", null, [], [], [], []);

        var apps = await ((IAppRecords)records).GetAppsAsync([Ours("a1")], Ct);

        Assert.Equal(["Payroll", "Wiki"], apps.EnterpriseApps.Select(e => e.Name).Order());
        Assert.Equal(AppSource.ThisOrganization, apps.EnterpriseApps.Single(e => e.Name == "Payroll").Source);
        Assert.Equal("Fabrikam", apps.EnterpriseApps.Single(e => e.Name == "Payroll").Publisher);
        Assert.Equal("Contoso Verified", apps.EnterpriseApps.Single(e => e.Name == "Wiki").Publisher);
        // No slow full list: only the changes-only pull.
        Assert.DoesNotContain(http.Requests, r => r.PathAndQuery.Contains("servicePrincipals?$select=id,appId"));
        Assert.Contains(http.Requests, r => r.PathAndQuery.Contains("$deltatoken=t1"));
    }
}
