using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Keeps a tenant's local copy current from Graph.
/// <list type="bullet">
/// <item>Users: <c>users/delta</c>. The first run pages through everyone and ends with a delta
/// link; later runs send that link and get only what changed (partial objects, merged into
/// what is stored, and <c>@removed</c> markers). An expired link starts a full pass, which also
/// sweeps out anyone it no longer sees.</item>
/// <item>Licenses: <c>subscribedSkus</c>, a handful of rows, read whole every time.</item>
/// <item>Last sign-in: delta can't return <c>signInActivity</c>, so it is read separately and
/// merged in, at most hourly. It needs Entra ID P1 and a reports role; without them the column
/// stays empty and nothing else is affected.</item>
/// </list>
/// </summary>
public sealed class DirectorySync(GraphClient graph, IEntityStore store, Func<DateTimeOffset>? now = null) : ITenantSync
{
    public const string Users = "user";
    public const string Skus = "sku";
    public const string SignIns = "signin";

    const string UserSelect = "id,displayName,userPrincipalName,mail,department,jobTitle,accountEnabled,userType,createdDateTime,assignedLicenses";
    static readonly TimeSpan SignInInterval = TimeSpan.FromHours(1);

    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    readonly SemaphoreSlim _running = new(1, 1);

    public DateTimeOffset? LastSyncedAt => store.GetMark(Users)?.SyncedAt;

    public bool IsPersistent => store.IsPersistent;

    public async Task<int> SyncAsync(IProgress<string>? progress = null, bool full = false, CancellationToken cancellationToken = default)
    {
        // One sync at a time: a timer tick and a Refresh click must not interleave delta pages.
        await _running.WaitAsync(cancellationToken);
        try
        {
            var changed = await SyncSkusAsync(cancellationToken);
            changed += await SyncUsersAsync(progress, full, cancellationToken);
            if (full || store.GetMark(SignIns) is not { } last || _now() - last.SyncedAt >= SignInInterval)
                changed += await SyncSignInsAsync(progress, cancellationToken);
            return changed;
        }
        finally
        {
            _running.Release();
        }
    }

    async Task<int> SyncSkusAsync(CancellationToken cancellationToken)
    {
        var changes = new List<EntityChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var sku in graph.GetAllAsync("subscribedSkus?$select=skuId,skuPartNumber", cancellationToken))
        {
            var id = sku.GetProperty("skuId").GetString()!;
            seen.Add(id);
            changes.Add(new EntityChange(id, ToObject(sku)));
        }
        var changed = store.Apply(Skus, changes) + store.RemoveExcept(Skus, seen);
        store.SetMark(Skus, new SyncMark(null, _now()));
        return changed;
    }

    async Task<int> SyncUsersAsync(IProgress<string>? progress, bool full, CancellationToken cancellationToken)
    {
        var cursor = full ? null : store.GetMark(Users)?.Cursor;
        if (cursor is not null)
        {
            try
            {
                return await PullUsersAsync(cursor, sweep: false, progress, cancellationToken);
            }
            catch (GraphException e) when (IsExpired(e))
            {
                // The service forgot the delta state (they expire after a while): start over.
            }
        }
        return await PullUsersAsync($"users/delta?$select={UserSelect}", sweep: true, progress, cancellationToken);
    }

    async Task<int> PullUsersAsync(string url, bool sweep, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var seen = sweep ? new HashSet<string>(StringComparer.Ordinal) : null;
        var changed = 0;
        var received = 0;
        string? next = url;
        string? deltaLink = null;
        while (next is not null)
        {
            var page = await graph.GetAsync(next, cancellationToken);
            var changes = new List<EntityChange>();
            if (page.TryGetProperty("value", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString()!;
                    if (item.TryGetProperty("@removed", out _))
                    {
                        changes.Add(EntityChange.Removed(id));
                        seen?.Remove(id);
                        continue;
                    }
                    seen?.Add(id);
                    changes.Add(new EntityChange(id, ToObject(item)));
                }
            }
            // Each page is written as it arrives: a first sync of a large tenant shows progress
            // and holds one page in memory, not the whole directory.
            changed += store.Apply(Users, changes);
            received += changes.Count;
            if (sweep)
                progress?.Report($"Downloading users… {received:N0}");
            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            deltaLink = page.TryGetProperty("@odata.deltaLink", out var delta) ? delta.GetString() : deltaLink;
        }
        if (seen is not null)
            changed += store.RemoveExcept(Users, seen);
        // Saved only once every page is in: an interrupted sync starts that pass again.
        store.SetMark(Users, new SyncMark(deltaLink, _now()));
        return changed;
    }

    async Task<int> SyncSignInsAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var changed = 0;
        try
        {
            progress?.Report("Reading last sign-in times…");
            var changes = new List<EntityChange>();
            await foreach (var item in graph.GetAllAsync("users?$select=id,signInActivity&$top=999", cancellationToken))
            {
                if (!item.TryGetProperty("signInActivity", out var activity))
                    continue;
                var properties = new JsonObject { ["signInActivity"] = JsonNode.Parse(activity.GetRawText()) };
                changes.Add(new EntityChange(item.GetProperty("id").GetString()!, properties, OnlyIfExists: true));
                if (changes.Count >= 999)
                {
                    changed += store.Apply(Users, changes);
                    changes.Clear();
                }
            }
            changed += store.Apply(Users, changes);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            // No Entra ID P1, or no reports role: last sign-in stays unknown. Not an error.
        }
        store.SetMark(SignIns, new SyncMark(null, _now()));
        return changed;
    }

    static bool IsExpired(GraphException e) =>
        e.Status == HttpStatusCode.Gone
        || e.Code is "syncStateNotFound" or "resyncRequired" or "syncStateInvalid";

    static JsonObject ToObject(JsonElement element) => JsonNode.Parse(element.GetRawText())!.AsObject();
}
