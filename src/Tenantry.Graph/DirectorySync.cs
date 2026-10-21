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
    public const string MailboxAccess = "mailboxaccess";
    public const string SignIns = "signin";

    const string UserSelect = "id,displayName,userPrincipalName,mail,department,jobTitle,accountEnabled,userType,createdDateTime,assignedLicenses";
    const string GroupSelect = "id,displayName,mail,mailEnabled,securityEnabled,groupTypes,description,visibility,createdDateTime,members";

    /// <summary>The few Get-Mailbox properties the lists use, of the 300-odd it returns.</summary>
    static readonly string[] MailboxSelect =
    [
        "ExternalDirectoryObjectId", "RecipientTypeDetails", "PrimarySmtpAddress", "EmailAddresses", "ForwardingSmtpAddress",
        "ForwardingAddress", "DeliverToMailboxAndForward", "LitigationHoldEnabled", "ArchiveStatus", "HiddenFromAddressListsEnabled",
        "GrantSendOnBehalfTo",
    ];

    /// <summary>Mailboxes whose access lists are read: the ones shared between people.</summary>
    static readonly HashSet<string> SharedTypes = ["SharedMailbox", "RoomMailbox", "EquipmentMailbox"];

    const int AccessReadsAtOnce = 4;

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
            var shared = new List<(string Id, string Address)>();
            foreach (var row in rows)
            {
                // Keyed by the mailbox's Entra object, so it joins its user.
                if (!row.TryGetProperty("ExternalDirectoryObjectId", out var owner) || owner.GetString() is not { Length: > 0 } id)
                    continue;
                seen.Add(id);
                changes.Add(new EntityChange(id, ToObject(row)));
                if (row.TryGetProperty("RecipientTypeDetails", out var type) && SharedTypes.Contains(type.GetString() ?? "")
                    && row.TryGetProperty("PrimarySmtpAddress", out var address) && address.GetString() is { Length: > 0 } a)
                    shared.Add((id, a));
            }
            changed = store.Apply(Mailboxes, changes) + store.RemoveExcept(Mailboxes, seen);
            changed += await SyncMailboxAccessAsync(shared, notes, progress, cancellationToken);
        }
        catch (CmdletException e)
        {
            // Typically no Exchange role. Users still sync; mailbox columns stay empty.
            notes.Add($"Mailbox details weren't read: {e.Detail}");
        }
        store.SetMark(Mailboxes, new SyncMark(null, _now()));
        return changed;
    }

    /// <summary>
    /// Who can use each shared, room and equipment mailbox: Full access (Get-MailboxPermission)
    /// and Send as (Get-RecipientPermission). Exchange has no tenant-wide or delta form of these
    /// that maps cleanly to mailboxes, so it is two calls per mailbox, a few at a time. Trustees
    /// come back as addresses; the mailbox's own SELF entry and inherited or deny entries are
    /// dropped. Send on behalf comes from Get-Mailbox itself.
    /// </summary>
    async Task<int> SyncMailboxAccessAsync(List<(string Id, string Address)> mailboxes, List<string> notes,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (mailboxes.Count == 0)
            return store.RemoveExcept(MailboxAccess, new HashSet<string>());
        progress?.Report($"Reading who can use {mailboxes.Count:N0} shared mailboxes…");
        var access = new System.Collections.Concurrent.ConcurrentDictionary<string, JsonObject>(StringComparer.Ordinal);
        var failed = 0;
        await Parallel.ForEachAsync(mailboxes, new ParallelOptions { MaxDegreeOfParallelism = AccessReadsAtOnce, CancellationToken = cancellationToken },
            async (mailbox, ct) =>
            {
                try
                {
                    var identity = new Dictionary<string, object?> { ["Identity"] = mailbox.Address };
                    var full = await exchange!.InvokeAsync(CmdletEndpoint.Exchange, "Get-MailboxPermission", identity, ct,
                        ["User", "AccessRights", "IsInherited", "Deny"]);
                    var sendAs = await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-RecipientPermission", identity, ct,
                        ["Trustee", "AccessRights", "IsInherited", "AccessControlType"]);
                    access[mailbox.Id] = new JsonObject
                    {
                        ["id"] = mailbox.Id,
                        ["fullAccess"] = Trustees(full, "User", "FullAccess"),
                        ["sendAs"] = Trustees(sendAs, "Trustee", "SendAs"),
                    };
                }
                catch (CmdletException)
                {
                    // Typically a mailbox removed between the two reads. Keep what was stored.
                    Interlocked.Increment(ref failed);
                }
            });
        if (failed > 0)
            notes.Add($"Access for {failed:N0} shared mailboxes couldn't be read; their previous access is shown.");
        var changes = access.Select(a => new EntityChange(a.Key, a.Value)).ToArray();
        return store.Apply(MailboxAccess, changes)
            + store.RemoveExcept(MailboxAccess, mailboxes.Select(m => m.Id).ToHashSet(StringComparer.Ordinal));
    }

    static JsonArray Trustees(IReadOnlyList<JsonElement> entries, string trusteeProperty, string right)
    {
        var trustees = new JsonArray();
        foreach (var e in entries)
        {
            var who = e.TryGetProperty(trusteeProperty, out var t) ? t.GetString() : null;
            if (who is null || who.StartsWith("NT AUTHORITY", StringComparison.OrdinalIgnoreCase) || who.StartsWith("S-1-", StringComparison.Ordinal))
                continue;
            if (e.TryGetProperty("IsInherited", out var inherited) && inherited.ValueKind == JsonValueKind.True)
                continue;
            if ((e.TryGetProperty("Deny", out var deny) && deny.ValueKind == JsonValueKind.True)
                || (e.TryGetProperty("AccessControlType", out var control) && string.Equals(control.GetString(), "Deny", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (e.TryGetProperty("AccessRights", out var rights) && rights.ValueKind == JsonValueKind.Array
                && rights.EnumerateArray().Any(r => string.Equals(r.GetString(), right, StringComparison.OrdinalIgnoreCase)))
                trustees.Add(who);
        }
        return trustees;
    }

    static bool IsExpired(GraphException e) =>
        e.Status == HttpStatusCode.Gone
        || e.Code is "syncStateNotFound" or "resyncRequired" or "syncStateInvalid";

    static string Id(JsonElement item) => item.GetProperty("id").GetString()!;

    static JsonObject ToObject(JsonElement element) => JsonNode.Parse(element.GetRawText())!.AsObject();
}
