using Microsoft.Data.Sqlite;
using Tenantry.Core.Cache;

namespace Tenantry.Cache;

/// <summary>
/// An <see cref="IEntityStore"/> in an encrypted SQLite file (SQLite3 Multiple Ciphers, its
/// default ChaCha20-Poly1305 cipher). Without the key the file is noise: no SQLite header, no
/// readable text. One connection serves the store's lifetime, behind a lock, so a sync can write
/// while the UI reads.
/// </summary>
public sealed class SqliteEntityStore : IEntityStore, ILogArchive
{
    const int SchemaVersion = 1;

    static readonly Lazy<bool> Initialised = new(() =>
    {
        SQLitePCL.Batteries_V2.Init();
        return true;
    });

    readonly Lock _lock = new();
    readonly SqliteConnection _connection;

    SqliteEntityStore(SqliteConnection connection) => _connection = connection;

    public bool IsPersistent => true;

    /// <summary>
    /// Opens (or creates) the store. A file the key can't open, from a lost or reset key, is
    /// deleted and started afresh: it is only a copy, and the next sync refills it.
    /// </summary>
    public static SqliteEntityStore Open(string path, byte[] key)
    {
        _ = Initialised.Value;
        try
        {
            return new SqliteEntityStore(Connect(path, key));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 26) // SQLITE_NOTADB: wrong key or not ours
        {
            Delete(path);
            return new SqliteEntityStore(Connect(path, key));
        }
    }

    /// <summary>Deletes a store's files (used when a tenant is removed from this computer).</summary>
    public static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    static SqliteConnection Connect(string path, byte[] key)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            // Hex, so the key reaches the cipher as raw bytes rather than as a passphrase.
            Password = Convert.ToHexString(key),
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version > SchemaVersion)
                throw new SqliteException("The local database was written by a newer Tenantry.", 26);
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS entity (kind TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY (kind, id)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS mark (key TEXT PRIMARY KEY, cursor TEXT, synced_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS logevent (kind TEXT NOT NULL, id TEXT NOT NULL, at INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY (kind, id)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS logevent_at ON logevent (kind, at);
                """;
            command.ExecuteNonQuery();
            command.CommandText = $"PRAGMA user_version = {SchemaVersion};";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public IReadOnlyList<string> All(string kind)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT json FROM entity WHERE kind = $kind;";
            command.Parameters.AddWithValue("$kind", kind);
            var items = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                items.Add(reader.GetString(0));
            return items;
        }
    }

    public string? Get(string kind, string id)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT json FROM entity WHERE kind = $kind AND id = $id;";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() as string;
        }
    }

    public int Apply(string kind, IReadOnlyCollection<EntityChange> changes)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            using var read = Command(transaction, "SELECT json FROM entity WHERE kind = $kind AND id = $id;", kind);
            using var write = Command(transaction, "INSERT INTO entity (kind, id, json) VALUES ($kind, $id, $json) ON CONFLICT (kind, id) DO UPDATE SET json = excluded.json;", kind);
            using var delete = Command(transaction, "DELETE FROM entity WHERE kind = $kind AND id = $id;", kind);
            var json = write.Parameters.Add("$json", SqliteType.Text);
            var changed = 0;
            foreach (var change in changes)
            {
                if (change.Properties is null)
                {
                    delete.Parameters["$id"].Value = change.Id;
                    changed += delete.ExecuteNonQuery();
                    continue;
                }
                read.Parameters["$id"].Value = change.Id;
                var current = read.ExecuteScalar() as string;
                if (current is null && change.OnlyIfExists)
                    continue;
                if (EntityMerge.Merge(current, change.Properties) is not { } merged)
                    continue;
                write.Parameters["$id"].Value = change.Id;
                json.Value = merged;
                write.ExecuteNonQuery();
                changed++;
            }
            transaction.Commit();
            return changed;
        }
    }

    public int RemoveExcept(string kind, IReadOnlySet<string> keep)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var ids = new List<string>();
            using (var list = Command(transaction, "SELECT id FROM entity WHERE kind = $kind;", kind))
            using (var reader = list.ExecuteReader())
            {
                while (reader.Read())
                    ids.Add(reader.GetString(0));
            }
            using var delete = Command(transaction, "DELETE FROM entity WHERE kind = $kind AND id = $id;", kind);
            var removed = 0;
            foreach (var id in ids.Where(id => !keep.Contains(id)))
            {
                delete.Parameters["$id"].Value = id;
                removed += delete.ExecuteNonQuery();
            }
            transaction.Commit();
            return removed;
        }
    }

    public SyncMark? GetMark(string key)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT cursor, synced_at FROM mark WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new SyncMark(reader.IsDBNull(0) ? null : reader.GetString(0),
                    DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture))
                : null;
        }
    }

    public void SetMark(string key, SyncMark mark)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO mark (key, cursor, synced_at) VALUES ($key, $cursor, $at) ON CONFLICT (key) DO UPDATE SET cursor = excluded.cursor, synced_at = excluded.synced_at;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$cursor", (object?)mark.Cursor ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", mark.SyncedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    // ---- Log archive (10): time-indexed, added once, read by date range ------------------------

    public int AddEvents(string kind, IReadOnlyCollection<ArchivedEvent> events)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            using var insert = Command(transaction, "INSERT OR IGNORE INTO logevent (kind, id, at, json) VALUES ($kind, $id, $at, $json);", kind);
            var at = insert.Parameters.Add("$at", SqliteType.Integer);
            var json = insert.Parameters.Add("$json", SqliteType.Text);
            var added = 0;
            foreach (var e in events)
            {
                insert.Parameters["$id"].Value = e.Id;
                at.Value = e.At.ToUnixTimeMilliseconds();
                json.Value = e.Json;
                added += insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return added;
        }
    }

    public IReadOnlyList<ArchivedEvent> ReadEvents(string kind, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, at, json FROM logevent WHERE kind = $kind AND at >= $from AND at <= $to ORDER BY at DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$limit", limit);
            var events = new List<ArchivedEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                events.Add(new ArchivedEvent(reader.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), reader.GetString(2)));
            return events;
        }
    }

    public ArchiveSpan Span(string kind)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), MIN(at), MAX(at) FROM logevent WHERE kind = $kind;";
            command.Parameters.AddWithValue("$kind", kind);
            using var reader = command.ExecuteReader();
            reader.Read();
            var count = reader.GetInt32(0);
            return count == 0 ? new ArchiveSpan(0, null, null)
                : new ArchiveSpan(count, DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)));
        }
    }

    public int PruneEvents(string kind, DateTimeOffset before)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM logevent WHERE kind = $kind AND at < $before;";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$before", before.ToUnixTimeMilliseconds());
            return command.ExecuteNonQuery();
        }
    }

    SqliteCommand Command(SqliteTransaction transaction, string sql, string kind)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$kind", kind);
        if (sql.Contains("$id", StringComparison.Ordinal))
            command.Parameters.Add("$id", SqliteType.Text);
        return command;
    }

    public void Dispose()
    {
        lock (_lock)
            _connection.Dispose();
    }
}
