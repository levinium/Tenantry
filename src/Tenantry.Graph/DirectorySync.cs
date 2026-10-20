using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>
/// Keeps a tenant's local copy current from Graph and Exchange.
/// <list type="bullet">
/// <item>Users and groups: <c>users/delta</c> and <c>groups/delta</c>. The first run pages
/// through everything and ends with a delta link; later runs send that link and get only what
/// changed (partial objects merged into what is stored, <c>@removed</c> markers, and for groups
/// <c>members@delta</c> additions and removals). An expired link starts a full pass, which also
/// sweeps out whatever it no longer sees.</item>
/// <item>Licenses: <c>subscribedSkus</c>, read whole every time (a handful of rows).</item>
/// <item>Slow reads, at most hourly (or on Refresh): last sign-in (delta can't return
/// <c>signInActivity</c>), mailboxes (<c>Get-Mailbox</c>; Exchange has no delta), the tenant's
/// domains (to tell internal forwarding from external) and which groups are Teams.</item>
/// </list>
/// A slow read the admin isn't allowed (no Entra ID P1, no Exchange role) is skipped with a
/// note, never failing the sync.
/// </summary>
public sealed class DirectorySync(GraphClient graph, IEntityStore store, ICmdletRunner? exchange = null, Func<DateTimeOffset>? now = null) : ITenantSync
{
    public const string Users = "user";
    public const string Groups = "group";
    public const string Skus = "sku";
    public const string Domains = "domain";
    public const string Teams = "team";
    public const string Mailboxes = "mailbox";
    public const string SignIns = "signin";

    const string UserSelect = "id,displayName,userPrincipalName,mail,department,jobTitle,accountEnabled,userType,createdDateTime,assignedLicenses";
    const string GroupSelect = "id,displayName,mail,mailEnabled,securityEnabled,groupTypes,description,visibility,createdDateTime,members";

    /// <summary>The few Get-Mailbox properties the lists use, of the 300-odd it returns.</summary>
    static readonly string[] MailboxSelect =
    [
        "ExternalDirectoryObjectId", "RecipientTypeDetails", "PrimarySmtpAddress", "EmailAddresses", "ForwardingSmtpAddress",
        "ForwardingAddress", "DeliverToMailboxAndForward", "LitigationHoldEnabled", "ArchiveStatus", "HiddenFromAddressListsEnabled",
    ];

    static readonly TimeSpan SlowInterval = TimeSpan.FromHours(1);

    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    readonly SemaphoreSlim _running = new(1, 1);
    IReadOnlyList<string> _notes = [];

    public DateTimeOffset? LastSyncedAt => store.GetMark(Users)?.SyncedAt;

    public bool IsPersistent => store.IsPersistent;

    public IReadOnlyList<string> Notes => _notes;

    public async Task<int> SyncAsync(IProgress<string>? progress = null, SyncScope scope = SyncScope.Changes, CancellationToken cancellationToken = default)
    {
        // One sync at a time: a timer tick and a Refresh click must not interleave delta pages.
        await _running.WaitAsync(cancellationToken);
        try
        {
            var notes = new List<string>();
            var changed = await ReadWholeAsync(Skus, "subscribedSkus?$select=skuId,skuPartNumber", "skuId", cancellationToken);
            changed += await SyncDeltaAsync(Users, $"users/delta?$select={UserSelect}", "users", scope, _ => item => new EntityChange(Id(item), ToObject(item)), progress, cancellationToken);
            changed += await SyncDeltaAsync(Groups, $"groups/delta?$select={GroupSelect}", "groups", scope, GroupMapper, progress, cancellationToken);

            if (Due(Domains, scope))
                changed += await ReadWholeAsync(Domains, "domains?$select=id,isVerified", "id", cancellationToken);
            if (Due(Teams, scope))
                changed += await ReadWholeAsync(Teams, "groups?$filter=resourceProvisioningOptions/Any(x:x eq 'Team')&$select=id&$top=999", "id", cancellationToken);
            if (Due(SignIns, scope))
                changed += await SyncSignInsAsync(progress, cancellationToken);
            if (exchange is not null && Due(Mailboxes, scope))
                changed += await SyncMailboxesAsync(notes, progress, cancellationToken);

            _notes = notes;
            return changed;
        }
        finally
        {
            _running.Release();
        }
    }

    bool Due(string key, SyncScope scope) =>
        scope != SyncScope.Changes || store.GetMark(key) is not { } last || _now() - last.SyncedAt >= SlowInterval;

    /// <summary>A small collection read in full: every row upserted, anything missing removed.</summary>
    async Task<int> ReadWholeAsync(string kind, string path, string idProperty, CancellationToken cancellationToken)
    {
        var changes = new List<EntityChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in graph.GetAllAsync(path, cancellationToken))
        {
            var id = item.GetProperty(idProperty).GetString()!;
            seen.Add(id);
            changes.Add(new EntityChange(id, ToObject(item)));
        }
        var changed = store.Apply(kind, changes) + store.RemoveExcept(kind, seen);
        store.SetMark(kind, new SyncMark(null, _now()));
        return changed;
    }

    async Task<int> SyncDeltaAsync(string kind, string firstUrl, string noun, SyncScope scope,
        Func<bool, Func<JsonElement, EntityChange>> mapper, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var cursor = scope == SyncScope.StartOver ? null : store.GetMark(kind)?.Cursor;
        if (cursor is not null)
        {
            try
            {
                return await PullAsync(kind, cursor, noun, sweep: false, mapper(false), progress, cancellationToken);
            }
            catch (GraphException e) when (IsExpired(e))
            {
                // The service forgot the delta state (they expire after a while): start over.
            }
        }
        return await PullAsync(kind, firstUrl, noun, sweep: true, mapper(true), progress, cancellationToken);
    }

    async Task<int> PullAsync(string kind, string url, string noun, bool sweep, Func<JsonElement, EntityChange> map,
        IProgress<string>? progress, CancellationToken cancellationToken)
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
            {
                foreach (var item in items.EnumerateArray())
                {
                    var id = Id(item);
                    if (item.TryGetProperty("@removed", out _))
                    {
                        changes.Add(EntityChange.Removed(id));
                        seen?.Remove(id);
                        continue;
                    }
                    seen?.Add(id);
                    changes.Add(map(item));
                }
            }
            // Each page is written as it arrives: a first sync of a large tenant shows progress
            // and holds one page in memory, not the whole directory.
            changed += store.Apply(kind, changes);
            if (sweep)
                progress?.Report($"Downloading {noun}… {seen!.Count:N0}");
            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            deltaLink = page.TryGetProperty("@odata.deltaLink", out var delta) ? delta.GetString() : deltaLink;
        }
        if (seen is not null)
            changed += store.RemoveExcept(kind, seen);
        // Saved only once every page is in: an interrupted sync starts that pass again.
        store.SetMark(kind, new SyncMark(deltaLink, _now()));
        return changed;
    }

    /// <summary>
    /// Groups carry their members as <c>members@delta</c>: on a full pass, the complete list
    /// (a large group arrives spread over several pages); afterwards, only who joined and who
    /// left. Members are stored as a <c>members</c> map of id → type, built up across pages.
    /// </summary>
    Func<JsonElement, EntityChange> GroupMapper(bool sweep)
    {
        var members = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        return item =>
        {
            var id = Id(item);
            var properties = ToObject(item);
            if (sweep && !members.ContainsKey(id))
            {
                // A full pass lists every member, so a group with none sends no members@delta.
                members[id] = [];
                properties["members"] = new JsonObject();
            }
            if (item.TryGetProperty("members@delta", out var delta))
            {
                if (!members.TryGetValue(id, out var current))
                    members[id] = current = StoredMembers(id);
                foreach (var member in delta.EnumerateArray())
                {
                    var memberId = Id(member);
                    if (member.TryGetProperty("@removed", out _))
                        current.Remove(memberId);
                    else
                        current[memberId] = TypeName(member);
                }
                properties["members"] = current.DeepClone();
            }
            return new EntityChange(id, properties);
        };
    }

    JsonObject StoredMembers(string groupId) =>
        store.Get(Groups, groupId) is { } json && JsonNode.Parse(json)?["members"] is JsonObject stored
            ? stored.DeepClone().AsObject()
            : [];

    /// <summary>"#microsoft.graph.user" → "user".</summary>
    static string TypeName(JsonElement member) =>
        member.TryGetProperty("@odata.type", out var type) && type.GetString() is { } t ? t[(t.LastIndexOf('.') + 1)..] : "unknown";

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
                changes.Add(new EntityChange(Id(item), properties, OnlyIfExists: true));
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

    async Task<int> SyncMailboxesAsync(List<string> notes, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var changed = 0;
        try
        {
            progress?.Report("Reading mailboxes…");
            var rows = await exchange!.InvokeAsync(CmdletEndpoint.Exchange, "Get-Mailbox",
                new Dictionary<string, object?> { ["ResultSize"] = "Unlimited" }, cancellationToken, MailboxSelect);
            var changes = new List<EntityChange>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                // Keyed by the mailbox's Entra object, so it joins its user.
                if (!row.TryGetProperty("ExternalDirectoryObjectId", out var owner) || owner.GetString() is not { Length: > 0 } id)
                    continue;
                seen.Add(id);
                changes.Add(new EntityChange(id, ToObject(row)));
            }
            changed = store.Apply(Mailboxes, changes) + store.RemoveExcept(Mailboxes, seen);
        }
        catch (CmdletException e)
        {
            // Typically no Exchange role. Users still sync; mailbox columns stay empty.
            notes.Add($"Mailbox details weren't read: {e.Detail}");
        }
        store.SetMark(Mailboxes, new SyncMark(null, _now()));
        return changed;
    }

    static bool IsExpired(GraphException e) =>
        e.Status == HttpStatusCode.Gone
        || e.Code is "syncStateNotFound" or "resyncRequired" or "syncStateInvalid";

    static string Id(JsonElement item) => item.GetProperty("id").GetString()!;

    static JsonObject ToObject(JsonElement element) => JsonNode.Parse(element.GetRawText())!.AsObject();
}
