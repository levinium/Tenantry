using System.Text;
using System.Text.Json.Nodes;
using Tenantry.Cache;
using Tenantry.Core.Cache;

namespace Tenantry.Tests;

/// <summary>
/// Both stores must behave identically, so every behaviour runs against each. The SQLite store
/// also proves the file on disk is encrypted.
/// </summary>
public sealed class EntityStoreTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "tenantry-tests-" + Guid.NewGuid().ToString("N"));
    readonly byte[] _key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    public EntityStoreTests() => Directory.CreateDirectory(_folder);

    string DbPath => Path.Combine(_folder, "cache.db");

    public static TheoryData<string> Kinds => ["memory", "sqlite"];

    IEntityStore Open(string kind) => kind == "memory" ? new MemoryEntityStore() : SqliteEntityStore.Open(DbPath, _key);

    static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Changes_merge_property_by_property_and_count_only_real_changes(string kind)
    {
        using var store = Open(kind);

        Assert.Equal(2, store.Apply("user", [new("u1", Obj("""{"id":"u1","displayName":"Ada","department":"IT"}""")), new("u2", Obj("""{"id":"u2","displayName":"Bo"}"""))]));
        // A delta update carries only what changed; the rest of the object stays.
        Assert.Equal(1, store.Apply("user", [new("u1", Obj("""{"id":"u1","department":"Finance","@odata.type":"#microsoft.graph.user"}"""))]));
        // The same values again are not a change.
        Assert.Equal(0, store.Apply("user", [new("u1", Obj("""{"department":"Finance"}"""))]));

        var ada = JsonNode.Parse(store.All("user").Single(j => j.Contains("u1")))!;
        Assert.Equal("Ada", (string?)ada["displayName"]);
        Assert.Equal("Finance", (string?)ada["department"]);
        Assert.Null(ada["@odata.type"]);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Removal_sweeps_and_only_if_exists_never_create_objects(string kind)
    {
        using var store = Open(kind);
        store.Apply("user", [new("u1", Obj("""{"id":"u1"}""")), new("u2", Obj("""{"id":"u2"}""")), new("u3", Obj("""{"id":"u3"}"""))]);

        Assert.Equal(1, store.Apply("user", [EntityChange.Removed("u1"), EntityChange.Removed("missing")]));
        Assert.Equal(1, store.RemoveExcept("user", new HashSet<string> { "u2" }));
        Assert.Equal(0, store.Apply("user", [new("ghost", Obj("""{"signInActivity":{}}"""), OnlyIfExists: true)]));
        Assert.Equal(1, store.Apply("user", [new("u2", Obj("""{"signInActivity":{"lastSignInDateTime":"2026-01-01T00:00:00Z"}}"""), OnlyIfExists: true)]));

        Assert.Equal(["u2"], store.All("user").Select(j => (string)JsonNode.Parse(j)!["id"]!).ToArray());
        Assert.Empty(store.All("sku"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Sync_marks_round_trip(string kind)
    {
        using var store = Open(kind);
        var at = new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.FromHours(-4));

        Assert.Null(store.GetMark("user"));
        store.SetMark("user", new SyncMark("https://graph.example/delta?token=1", at));
        store.SetMark("user", new SyncMark("https://graph.example/delta?token=2", at));
        store.SetMark("sku", new SyncMark(null, at));

        Assert.Equal(new SyncMark("https://graph.example/delta?token=2", at), store.GetMark("user"));
        Assert.Null(store.GetMark("sku")!.Cursor);
    }

    [Fact]
    public void The_database_file_is_encrypted_and_survives_a_reopen()
    {
        using (var store = SqliteEntityStore.Open(DbPath, _key))
        {
            store.Apply("user", [new("u1", Obj("""{"id":"u1","displayName":"Ada Lovelace"}"""))]);
            store.SetMark("user", new SyncMark("cursor", DateTimeOffset.UnixEpoch));
        }

        var bytes = File.ReadAllBytes(DbPath);
        Assert.False(Encoding.ASCII.GetString(bytes, 0, 15) == "SQLite format 3", "the file has a plain SQLite header");
        Assert.DoesNotContain("Lovelace", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        using var reopened = SqliteEntityStore.Open(DbPath, _key);
        Assert.Contains("Ada Lovelace", reopened.All("user").Single(), StringComparison.Ordinal);
        Assert.Equal("cursor", reopened.GetMark("user")!.Cursor);
    }

    [Fact]
    public void A_file_the_key_cannot_open_is_replaced_by_an_empty_store()
    {
        using (var store = SqliteEntityStore.Open(DbPath, _key))
            store.Apply("user", [new("u1", Obj("""{"id":"u1"}"""))]);

        var otherKey = Enumerable.Repeat((byte)7, 32).ToArray();
        using var fresh = SqliteEntityStore.Open(DbPath, otherKey);

        Assert.Empty(fresh.All("user"));
        Assert.Null(fresh.GetMark("user"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is harmless.
        }
    }
}
