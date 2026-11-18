using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;

namespace Tenantry.Graph;

/// <summary>
/// The local copy of enterprise apps (service principals). Listing them all takes Entra 10 to 15
/// seconds for several hundred (measured live), so they're kept like users and groups:
/// <list type="bullet">
/// <item><c>servicePrincipals/delta</c>: the first pass pages through everything (with
/// <c>@removed</c> markers for deleted ones) and ends with a delta link; later pulls send that
/// link and get only what changed (about 0.1 s). An expired link starts a full pass, which also
/// sweeps out whatever it no longer sees. The link is saved only once every page is in, so an
/// interrupted pass starts again.</item>
/// <item>Delta refuses <c>publisherName</c> and leaves <c>verifiedPublisher</c> empty (both seen
/// live), so those two come from the ordinary list on a slower schedule, merged into what's stored
/// (never creating an app).</item>
/// </list>
/// The tenant's sync and the Enterprise Apps page both pull through here: one pull at a time per
/// local copy, so two never interleave delta pages.
/// </summary>
public sealed class ServicePrincipalSync(GraphClient graph, IEntityStore store, Func<DateTimeOffset>? now = null)
{
    /// <summary>The kind the apps are stored under.</summary>
    public const string Kind = "serviceprincipal";

    /// <summary>Where the delta link is kept; bump the version when <see cref="DeltaSelect"/> grows.</summary>
    public const string DeltaMark = "serviceprincipal.v1";

    /// <summary>When the publisher names were last read.</summary>
    public const string PublishersMark = "serviceprincipal.publishers";

    /// <summary>What delta returns (all of the list's fields except the two it refuses or leaves empty).</summary>
    public const string DeltaSelect = "id,appId,displayName,servicePrincipalType,accountEnabled,appRoleAssignmentRequired,tags,homepage,"
        + "preferredSingleSignOnMode,notes,createdDateTime,appOwnerOrganizationId";

    const string PublisherSelect = "id,publisherName,verifiedPublisher";

    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    static readonly ConditionalWeakTable<IEntityStore, SemaphoreSlim> Gates = new();

    SemaphoreSlim Gate => Gates.GetValue(store, _ => new SemaphoreSlim(1, 1));

    /// <summary>True once a full pass has finished: the stored apps can be shown.</summary>
    public bool HasCopy => store.GetMark(DeltaMark)?.Cursor is not null;

    /// <summary>When the apps were last brought up to date.</summary>
    public DateTimeOffset? SyncedAt => store.GetMark(DeltaMark)?.SyncedAt;

    /// <summary>
    /// Brings the copy up to date: changes only when there's a delta link (a full pass otherwise,
    /// or when <paramref name="startOver"/>); then the publisher names when they're due, or when
    /// this was a full pass. Returns how many apps changed.
    /// </summary>
    public async Task<int> PullAsync(bool startOver = false, TimeSpan? publishersEvery = null, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var cursor = startOver ? null : store.GetMark(DeltaMark)?.Cursor;
            var full = cursor is null;
            int changed;
            if (cursor is not null)
            {
                try
                {
                    changed = await DeltaAsync(cursor, sweep: false, progress, cancellationToken);
                }
                catch (GraphException e) when (IsExpired(e))
                {
                    // Entra forgot the delta state (they expire after a while): start over.
                    full = true;
                    changed = await DeltaAsync($"servicePrincipals/delta?$select={DeltaSelect}", sweep: true, progress, cancellationToken);
                }
            }
            else
                changed = await DeltaAsync($"servicePrincipals/delta?$select={DeltaSelect}", sweep: true, progress, cancellationToken);

            var every = publishersEvery ?? TimeSpan.FromHours(1);
            if (full || store.GetMark(PublishersMark) is not { } last || _now() - last.SyncedAt >= every)
                changed += await PublishersAsync(progress, cancellationToken);
            return changed;
        }
        finally
        {
            Gate.Release();
        }
    }

    async Task<int> DeltaAsync(string url, bool sweep, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var seen = sweep ? new HashSet<string>(StringComparer.Ordinal) : null;
        var changed = 0;
        string? next = url;
        string? deltaLink = null;
        while (next is not null)
        {
            var page = await graph.GetAsync(next, cancellationToken);
            var changes = new List<EntityChange>();
            if (page.TryGetProperty("value", out var items))
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
                    changes.Add(new EntityChange(id, JsonNode.Parse(item.GetRawText())!.AsObject()));
                }
            changed += store.Apply(Kind, changes);
            if (sweep)
                progress?.Report($"Downloading enterprise apps… {seen!.Count:N0}");
            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            deltaLink = page.TryGetProperty("@odata.deltaLink", out var delta) ? delta.GetString() : deltaLink;
        }
        if (seen is not null)
            changed += store.RemoveExcept(Kind, seen);
        store.SetMark(DeltaMark, new SyncMark(deltaLink, _now()));
        return changed;
    }

    /// <summary>Publisher names (the slow ordinary list), merged into the stored apps.</summary>
    async Task<int> PublishersAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Reading enterprise app publishers…");
        var changes = new List<EntityChange>();
        await foreach (var s in graph.GetAllAsync($"servicePrincipals?$select={PublisherSelect}&$top=999", cancellationToken))
        {
            var properties = new JsonObject
            {
                ["publisherName"] = s.TryGetProperty("publisherName", out var p) ? JsonNode.Parse(p.GetRawText()) : null,
                ["verifiedPublisher"] = s.TryGetProperty("verifiedPublisher", out var v) ? JsonNode.Parse(v.GetRawText()) : null,
            };
            changes.Add(new EntityChange(s.GetProperty("id").GetString()!, properties, OnlyIfExists: true));
        }
        var changed = store.Apply(Kind, changes);
        store.SetMark(PublishersMark, new SyncMark(null, _now()));
        return changed;
    }

    /// <summary>Every stored app, as Graph returned it.</summary>
    public IEnumerable<JsonElement> Stored()
    {
        foreach (var json in store.All(Kind))
        {
            using var doc = JsonDocument.Parse(json);
            yield return doc.RootElement.Clone();
        }
    }

    static bool IsExpired(GraphException e) =>
        e.Status == System.Net.HttpStatusCode.Gone
        || e.Code is "syncStateNotFound" or "resyncRequired" or "syncStateInvalid";
}
