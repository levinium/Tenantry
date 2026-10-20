using System.Text.Json;
using Tenantry.Core.Directory;
using Tenantry.Core.Tenancy;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;

namespace Tenantry.Tests;

public sealed class TenantStoreAndDirectoryTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "tenantry-tests-" + Guid.NewGuid().ToString("N"));

    public TenantStoreAndDirectoryTests() => System.IO.Directory.CreateDirectory(_folder);

    public void Dispose() => System.IO.Directory.Delete(_folder, recursive: true);

    static TenantProfile Profile(string id, string name) =>
        new(id, name, $"tid-{id}", $"{name.ToLowerInvariant()}.example", $"app-{id}", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Profiles_round_trip_and_upsert_replaces_by_id()
    {
        var store = new TenantProfileStore(Path.Combine(_folder, "tenants.json"));
        Assert.Empty(store.Load());

        store.Upsert(Profile("1", "Zeta"));
        store.Upsert(Profile("2", "Alpha"));
        store.Upsert(Profile("1", "Zeta") with { LastAccount = "admin@zeta.example" });

        var loaded = store.Load();
        Assert.Equal(["Alpha", "Zeta"], loaded.Select(p => p.DisplayName));
        Assert.Equal("admin@zeta.example", loaded.Single(p => p.Id == "1").LastAccount);

        store.Remove("2");
        Assert.Equal(["1"], store.Load().Select(p => p.Id));
    }

    [Fact]
    public void Graph_user_maps_to_a_summary()
    {
        var json = JsonDocument.Parse("""
            {"id":"u1","displayName":"Ada","userPrincipalName":"ada@contoso.example","mail":null,
             "accountEnabled":false,"userType":"Guest","createdDateTime":"2024-01-02T03:04:05Z",
             "assignedLicenses":[{"skuId":"sku-a"},{"skuId":"sku-unknown"}],
             "signInActivity":{"lastSignInDateTime":"2026-01-01T00:00:00Z","lastNonInteractiveSignInDateTime":"2026-02-01T00:00:00Z"}}
            """).RootElement;

        var u = GraphTenantDirectory.ToSummary(json, new Dictionary<string, string> { ["sku-a"] = "SPE_E3" });

        Assert.Equal("Ada", u.DisplayName);
        Assert.Null(u.Mail);
        Assert.False(u.AccountEnabled);
        Assert.Equal(UserKind.Guest, u.Kind);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), u.LastSignIn);
        Assert.Equal(["SPE_E3", "sku-unknown"], u.Licenses);
    }

    [Fact]
    public async Task Directory_falls_back_when_sign_in_activity_is_refused()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/subscribedSkus", """{"value":[]}""")
            .On(HttpMethod.Get, "signInActivity", """{"error":{"code":"Authentication_RequestFromNonPremiumTenantOrB2CTenant","message":"premium"}}""", System.Net.HttpStatusCode.Forbidden)
            .On(HttpMethod.Get, "/users?", """{"value":[{"id":"u1","displayName":"Ada","userPrincipalName":"ada@contoso.example"}]}""");
        var directory = new GraphTenantDirectory(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), "Contoso");

        var users = await directory.GetUsersAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Ada", users.Single().DisplayName);
        Assert.Null(users.Single().LastSignIn);
    }
}
