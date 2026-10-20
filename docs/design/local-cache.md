# Local cache and sync

Lists read a local copy of each tenant's directory instead of calling Microsoft 365, so they open
and filter instantly. This note covers how that copy is stored, protected and kept current.

## Storage

- **One encrypted SQLite file per tenant profile**, `cache.db` in the profile's folder under the
  app-data directory (never in a repository).
- **Encryption:** [SQLite3 Multiple Ciphers](https://utelle.github.io/SQLite3MultipleCiphers/)
  through `SQLite3MC.PCLRaw.bundle` (MIT), using its default cipher, ChaCha20-Poly1305. The
  SQLitePCLRaw SQLCipher bundle was the original plan, but its last release was in 2024 and it
  is not part of SQLitePCLRaw 3.
- **Key:** 32 random bytes per profile, held in the OS secret store that also protects the token
  cache: DPAPI on Windows, the login Keychain on macOS, the Secret Service keyring on Linux
  (`CacheKeyStore`).
- **No secret store** (for example a Linux machine without a keyring): the copy lives in memory
  for the session and is downloaded again next time. Tenant data is never written to disk
  unencrypted.
- **Lost or reset key:** the file can't be opened, so it is deleted and rebuilt by the next sync.
  It is only a copy.
- **Removing a tenant** from this computer deletes the database, its key and the token cache.

## Shape

`IEntityStore` (in Core) holds JSON objects grouped by kind (`user`, `sku`), plus a sync mark
(cursor and time) per kind. Objects are stored as Graph returned them. Changes merge top-level
properties into the stored object, so a delta that carries only the changed properties keeps
everything else. `MemoryEntityStore` and `SqliteEntityStore` follow the same rules, and the tests
run every behaviour against both.

`CachedTenantDirectory` maps the stored JSON to `UserSummary` for the lists. Lists keep their
in-memory engine (`ListEngine`), which is fast enough without a SQL query layer. At 100,000 users:

| Step | Time |
|---|---|
| Read and map the copy (background thread) | ~0.7 s |
| Prepare the list | ~0.7 s |
| A search | ~15 ms |
| Apply a 500-object delta | ~45 ms |

Translating `ListQuery` to SQL stays possible if a much larger tenant ever needs it.

## Sync (`DirectorySync`)

| Data | Source | When |
|---|---|---|
| Licenses | `subscribedSkus`, read whole | Every sync |
| Users | `users/delta` | Every sync. The first sync pages through everyone and saves the delta link; later syncs fetch only changes and `@removed` markers. |
| Last sign-in | `users?$select=id,signInActivity` | At most hourly; delta can't return it. Needs Entra ID P1 and a reports role. Without them the column stays empty and nothing else is affected. |

- **Expired delta link** (410 or `syncStateNotFound`): the sync starts a full pass, which also
  removes anyone it no longer sees.
- **Writes and resumes:** each page is written as it arrives. The delta link is saved only after
  the last page, so an interrupted sync repeats that pass.
- **Concurrency:** syncs never overlap (one semaphore), and they run off the UI thread.

## In the app

- **First open of a tenant:** the list shows download progress, then the list.
- **Later opens:** the list shows the local copy at once, checks for changes behind it, and
  reloads in place if any arrive. Search, filters, sort, selection and preview are kept.
- **While open:** a check runs every 10 minutes, and the sidebar's Refresh button runs one on
  demand.
- **Sidebar status:** shows "Updated at 10:42 AM" (a clock time, which can't go stale on screen),
  "Checking for changes…", or "Couldn't update" with the reason in the tooltip.

## Not yet

Groups, mailboxes (from Exchange) and Ctrl+K global search come next, on the same store.
