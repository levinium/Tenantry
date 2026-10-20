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

    // The bug: open a tenant you'd just made the default, come back, and it wasn't the default.
    // Recording the sign-in wrote back the copy loaded before the default was set.
    [Fact]
    public void Recording_a_sign_in_keeps_the_default_set_since_the_profile_was_loaded()
    {
        var store = new TenantProfileStore(Path.Combine(_folder, "tenants.json"));
        store.Upsert(Profile("1", "Zeta"));
        var loadedEarlier = store.Load().Single();

        store.SetDefault("1");
        store.MarkUsed(loadedEarlier.Id, "admin@zeta.example", DateTimeOffset.UnixEpoch.AddDays(1));

        var now = store.Load().Single();
        Assert.True(now.IsDefault);
        Assert.Equal("admin@zeta.example", now.LastAccount);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(1), now.LastUsedAt);
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

        var u = CachedTenantDirectory.ToSummary(json, new Dictionary<string, string> { ["sku-a"] = "SPE_E3" });

        Assert.Equal("Ada", u.DisplayName);
        Assert.Null(u.Mail);
        Assert.False(u.AccountEnabled);
        Assert.Equal(UserKind.Guest, u.Kind);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), u.LastSignIn);
        Assert.Equal(["SPE_E3", "sku-unknown"], u.Licenses);
    }
}
