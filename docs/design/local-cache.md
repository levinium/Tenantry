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
| Groups and members | `groups/delta` with `members` | Every sync. Members arrive as `members@delta` (a large group's list is spread over several pages on a full pass; afterwards, only joins and leaves) and are stored as an id-to-type map. |
| Last sign-in | `users?$select=id,signInActivity` | Hourly, or on Refresh; delta can't return it. Needs Entra ID P1 and a reports role. Without them the column stays empty and nothing else is affected. |
| Mailboxes | Exchange `Get-Mailbox` over REST, with `$select` (about 13 of 338 properties, roughly 24x less data) | Hourly, or on Refresh; Exchange has no delta. Keyed by `ExternalDirectoryObjectId`, so each mailbox joins its user. Without an Exchange role the sync skips it and notes why in the status tooltip. |
| Domains, Teams | `domains`, `groups?$filter=resourceProvisioningOptions/Any(x:x eq 'Team')` | Hourly, or on Refresh. Domains decide whether forwarding is internal or external. |

**Sync scopes:**
- **Changes** (the 10-minute timer) runs the delta reads and any hourly read that is due.
- **Everything** (the Refresh button, and the first download) also redoes the hourly reads now.
- **StartOver** forgets the delta links.

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

## Search everything (Ctrl+K)

The palette searches the loaded copy: people (name, sign-in name, email, aliases, department,
title), groups (name, email, description, type) and every list's views. Every typed word must
appear. Names that start with the query rank first, then names containing a word that starts
with it, then everything else that matches; each kind shows at most six results. Choosing a
person or group opens its record page. Choosing a view switches to that list and applies the view.

## Mailbox details and sync settings (2026-10-21)

- **Mailbox size and automatic replies** feed the "Mailbox nearly full" and "Automatic reply on" views. Exchange offers neither tenant-wide, so they take two reads per mailbox (`Get-MailboxStatistics`, `Get-MailboxAutoReplyConfiguration`). The reads are **single calls, 32 at a time** (`DirectorySync.RunAllAsync`). Measured live on 605 calls, with no throttling at any setting: `$batch` requests (Exchange allows at most 10 calls each, and runs them one after another) took 56 s at their best; single calls took 47 s at 32 in flight and 43 s at 64. 32 leaves headroom. `ICmdletRunner.InvokeManyAsync` (batched) stays available; its sub-requests need absolute URLs, and the response is JSON only when `Accept: application/json` is sent. Exchange answers roughly 13 to 14 such calls a second for one admin.
- The read runs **in the background**, outside the main sync's lock, so checking for changes and Refresh never wait for it. `BackgroundUpdated` tells the lists to re-read.
- Measured on a tenant with a few hundred mailboxes:
  - one call at a time: about 11 minutes;
  - batched with 4 requests in flight: about 6 minutes;
  - batched with 8 in flight: under 4 minutes, with no throttling;
  - single calls, 32 in flight (now): about 70 s for sizes and replies together.

### Sync speed (2026-10-21)

Profiled live, a step at a time, on a tenant with a few hundred mailboxes:

| | Before | After |
|---|---|---|
| Refresh button | 136 s | 42 s |
| First download (lists usable) | 200 s | 48 s |
| Background sizes and replies | 3–4 min | about 70 s |

What changed:
- **Shared mailbox access is read in parallel.** It was two calls per mailbox, one at a time, and about 80% of a Refresh.
- **The Exchange reads start at the beginning of a sync and run alongside the Graph reads**, which need nothing from them. The status line shows Exchange's step once Graph is done.
- **`SyncScope.Refresh`** (the button) re-reads mailboxes and sign-ins but leaves mailbox sizes and replies to their schedule. `Everything` (Settings' "Check everything now") reads them too.
- **Sizes and replies have separate schedules.** Replies default to hourly; sizes default to every 4 hours, since they change slowly.
- **`Get-Mailbox` is left as one call.** Splitting it by type or by name range saved only about 3 of its 24 seconds.
- Microsoft's mailbox usage report (Graph, one call) has the sizes, but it was several days old when checked and has no automatic replies, so it isn't used.
- **Sync settings** are per tenant, stored in `sync.json`, and edited in Settings: how often to check for changes, how often to re-read mailboxes and sign-ins, and how often (or never) to read mailbox sizes and replies. Settings also lists each part of the local copy, when it was last read, and "Check everything now" and "Download everything again".