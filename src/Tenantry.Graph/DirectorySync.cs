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
/// <param name="settings">How often each part is re-read (Settings); the defaults when null.</param>
public sealed class DirectorySync(GraphClient graph, IEntityStore store, ICmdletRunner? exchange = null, Func<DateTimeOffset>? now = null,
    Func<SyncSettings>? settings = null) : ITenantSync
{
    /// <summary>Each mailbox's size and automatic-reply state (one Exchange read per mailbox for each).</summary>
    public const string MailboxDetails = "mailboxdetails";

    public const string Users = "user";
    public const string Groups = "group";
    public const string Skus = "sku";
    public const string Domains = "domain";
    public const string Teams = "team";
    public const string Mailboxes = "mailbox";
    public const string MailboxAccess = "mailboxaccess";
    public const string SignIns = "signin";

    const string UserSelect = "id,displayName,userPrincipalName,mail,department,jobTitle,accountEnabled,userType,createdDateTime,assignedLicenses";
    /// <summary>Licenses with counts and their apps, for the license picker and per-app switches.</summary>
    public const string SkuSelect = "skuId,skuPartNumber,prepaidUnits,consumedUnits,servicePlans,appliesTo,capabilityStatus";

    const string GroupSelect = "id,displayName,mail,mailEnabled,securityEnabled,groupTypes,description,visibility,createdDateTime,members,owners";

    /// <summary>
    /// Where the groups' delta link is kept. Bumped when the selected properties grow (owners,
    /// 2026-10-21), so the next sync does one full pass instead of waiting for every group to change.
    /// </summary>
    public const string GroupsMark = "group.v2";

    /// <summary>The few Get-Mailbox properties the lists use, of the 300-odd it returns.</summary>
    internal static readonly string[] MailboxSelect =
    [
        "ExternalDirectoryObjectId", "RecipientTypeDetails", "PrimarySmtpAddress", "EmailAddresses", "ForwardingSmtpAddress",
        "ForwardingAddress", "DeliverToMailboxAndForward", "LitigationHoldEnabled", "ArchiveStatus", "HiddenFromAddressListsEnabled",
        "GrantSendOnBehalfTo", "ProhibitSendReceiveQuota",
    ];

    /// <summary>Mailboxes whose access lists are read: the ones shared between people.</summary>
    static readonly HashSet<string> SharedTypes = ["SharedMailbox", "RoomMailbox", "EquipmentMailbox"];

    const int AccessReadsAtOnce = 4;

    SyncSettings Settings => settings?.Invoke() ?? SyncSettings.Default;

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
            // Each step says what it's doing, so a long Refresh never looks stuck.
            progress?.Report("Reading licenses…");
            var changed = await ReadWholeAsync(Skus, $"subscribedSkus?$select={SkuSelect}", "skuId", cancellationToken);
            progress?.Report("Checking users for changes…");
            changed += await SyncDeltaAsync(Users, Users, $"users/delta?$select={UserSelect}", "users", scope, _ => item => new EntityChange(Id(item), ToObject(item)), progress, cancellationToken);
            progress?.Report("Checking groups for changes…");
            changed += await SyncDeltaAsync(Groups, GroupsMark, $"groups/delta?$select={GroupSelect}", "groups", scope, GroupMapper, progress, cancellationToken);

            if (Due(Domains, scope))
            {
                progress?.Report("Reading domains…");
                changed += await ReadWholeAsync(Domains, "domains?$select=id,isVerified", "id", cancellationToken);
            }
            if (Due(Teams, scope))
            {
                progress?.Report("Reading which groups are Teams…");
                changed += await ReadWholeAsync(Teams, "groups?$filter=resourceProvisioningOptions/Any(x:x eq 'Team')&$select=id&$top=999", "id", cancellationToken);
            }
            if (Due(SignIns, scope))
                changed += await SyncSignInsAsync(progress, cancellationToken);
            if (exchange is not null && Due(Mailboxes, scope))
                changed += await SyncMailboxesAsync(notes, progress, cancellationToken);
            // Mailbox sizes and replies take minutes for a big tenant: read in the background, so
            // checking for changes (and Refresh) never waits for them.
            if (exchange is not null && Settings.MailboxDetailsOn && Due(MailboxDetails, scope, Settings.MailboxDetailsMinutes))
                StartMailboxDetails();

            _notes = [.. notes, .. _detailNotes];
            return changed;
        }
        finally
        {
            _running.Release();
        }
    }

    bool Due(string key, SyncScope scope, int? minutes = null) =>
        scope != SyncScope.Changes || store.GetMark(key) is not { } last
        || _now() - last.SyncedAt >= TimeSpan.FromMinutes(Math.Max(1, minutes ?? Settings.DetailsMinutes));

    /// <summary>Each part of the local copy, when it was last read and how often (for Settings).</summary>
    public IReadOnlyList<SyncArea> Areas
    {
        get
        {
            var s = Settings;
            string Every(int minutes, string none) => minutes <= 0 ? none : minutes % 60 == 0 && minutes >= 60
                ? minutes == 60 ? "Every hour" : minutes == 1440 ? "Once a day" : $"Every {minutes / 60} hours"
                : $"Every {minutes} minutes";
            var changes = Every(s.ChangesMinutes, "When the tenant opens, and on Refresh");
            var details = Every(s.DetailsMinutes, "On Refresh");
            List<SyncArea> areas =
            [
                new("Users", "Everyone in the directory, their licenses and status (changes only)", store.GetMark(Users)?.SyncedAt, changes),
                new("Groups", "Groups with their members and owners (changes only)", store.GetMark(GroupsMark)?.SyncedAt, changes),
                new("Licenses", "The tenant's licenses and how many are left", store.GetMark(Skus)?.SyncedAt, changes),
                new("Last sign-in times", "When each person last signed in", store.GetMark(SignIns)?.SyncedAt, details),
                new("Domains and Teams", "The tenant's domains, and which groups are Teams", store.GetMark(Domains)?.SyncedAt, details),
            ];
            if (exchange is not null)
            {
                areas.Add(new("Mailboxes", "Mailbox types, addresses, forwarding, address list; who can use shared mailboxes", store.GetMark(Mailboxes)?.SyncedAt, details));
                areas.Add(new("Mailbox size and automatic replies", "Two reads per mailbox, in the background",
                    store.GetMark(MailboxDetails)?.SyncedAt, BackgroundStatus ?? Every(s.MailboxDetailsMinutes, "Off")));
            }
            return areas;
        }
    }

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

    async Task<int> SyncDeltaAsync(string kind, string markKey, string firstUrl, string noun, SyncScope scope,
        Func<bool, Func<JsonElement, EntityChange>> mapper, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var cursor = scope == SyncScope.StartOver ? null : store.GetMark(markKey)?.Cursor;
        if (cursor is not null)
        {
            try
            {
                return await PullAsync(kind, markKey, cursor, noun, sweep: false, mapper(false), progress, cancellationToken);
            }
            catch (GraphException e) when (IsExpired(e))
            {
                // The service forgot the delta state (they expire after a while): start over.
            }
        }
        return await PullAsync(kind, markKey, firstUrl, noun, sweep: true, mapper(true), progress, cancellationToken);
    }

    async Task<int> PullAsync(string kind, string markKey, string url, string noun, bool sweep, Func<JsonElement, EntityChange> map,
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
        store.SetMark(markKey, new SyncMark(deltaLink, _now()));
        return changed;
    }

    /// <summary>The group relations that arrive as <c>{relation}@delta</c> lists.</summary>
    static readonly string[] Relations = ["members", "owners"];

    /// <summary>
    /// Groups carry their members and owners as <c>members@delta</c> / <c>owners@delta</c>: on a
    /// full pass, the complete lists (a large group arrives spread over several pages);
    /// afterwards, only who joined and who left. Each is stored as a map of id → type, built up
    /// across pages.
    /// </summary>
    Func<JsonElement, EntityChange> GroupMapper(bool sweep)
    {
        var lists = new Dictionary<(string Group, string Relation), JsonObject>();
        return item =>
        {
            var id = Id(item);
            var properties = ToObject(item);
            foreach (var relation in Relations)
            {
                if (sweep && !lists.ContainsKey((id, relation)))
                {
                    // A full pass lists everyone, so a group with no owners sends no owners@delta.
                    lists[(id, relation)] = [];
                    properties[relation] = new JsonObject();
                }
                if (!item.TryGetProperty($"{relation}@delta", out var delta))
                    continue;
                if (!lists.TryGetValue((id, relation), out var current))
                    lists[(id, relation)] = current = Stored(id, relation);
                foreach (var entry in delta.EnumerateArray())
                {
                    var entryId = Id(entry);
                    if (entry.TryGetProperty("@removed", out _))
                        current.Remove(entryId);
                    else
                        current[entryId] = TypeName(entry);
                }
                properties[relation] = current.DeepClone();
            }
            return new EntityChange(id, properties);
        };
    }

    JsonObject Stored(string groupId, string relation) =>
        store.Get(Groups, groupId) is { } json && JsonNode.Parse(json)?[relation] is JsonObject stored
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
        progress?.Report($"Shared mailbox access: 0 of {mailboxes.Count:N0}…");
        var access = new System.Collections.Concurrent.ConcurrentDictionary<string, JsonObject>(StringComparer.Ordinal);
        var failed = 0;
        var done = 0;
        await Parallel.ForEachAsync(mailboxes, new ParallelOptions { MaxDegreeOfParallelism = AccessReadsAtOnce, CancellationToken = cancellationToken },
            async (mailbox, ct) =>
            {
                try
                {
                    access[mailbox.Id] = await ReadMailboxAccessAsync(exchange!, mailbox.Id, mailbox.Address, ct);
                }
                catch (CmdletException)
                {
                    // Typically a mailbox removed between the two reads. Keep what was stored.
                    Interlocked.Increment(ref failed);
                }
                progress?.Report($"Shared mailbox access: {Interlocked.Increment(ref done):N0} of {mailboxes.Count:N0}…");
            });
        if (failed > 0)
            notes.Add($"Access for {failed:N0} shared mailboxes couldn't be read; their previous access is shown.");
        var changes = access.Select(a => new EntityChange(a.Key, a.Value)).ToArray();
        return store.Apply(MailboxAccess, changes)
            + store.RemoveExcept(MailboxAccess, mailboxes.Select(m => m.Id).ToHashSet(StringComparer.Ordinal));
    }

    // ---- Mailbox details, in the background ---------------------------------------------------

    /// <summary>Mailboxes whose two reads go in one $batch request (ten calls).</summary>
    const int MailboxesPerBatch = 5;

    /// <summary>Batches in flight at once.</summary>
    const int BatchesAtOnce = 8;

    Task? _details;
    IReadOnlyList<string> _detailNotes = [];

    /// <summary>Raised (on a worker thread) when the background read has stored new values.</summary>
    public event EventHandler? BackgroundUpdated;

    /// <summary>The background read now running, if any (tests await it).</summary>
    public Task BackgroundWork => _details ?? Task.CompletedTask;

    /// <summary>"Mailbox sizes: 120 of 604", while the background read runs; null otherwise.</summary>
    public string? BackgroundStatus
    {
        get => _backgroundStatus;
        private set
        {
            _backgroundStatus = value;
            BackgroundStatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    string? _backgroundStatus;

    /// <summary>Raised (on a worker thread) as the background read moves on, and when it ends.</summary>
    public event EventHandler? BackgroundStatusChanged;

    void StartMailboxDetails()
    {
        if (_details is { IsCompleted: false })
            return;
        _details = Task.Run(async () =>
        {
            try
            {
                var notes = new List<string>();
                if (await SyncMailboxDetailsAsync(notes, CancellationToken.None) > 0)
                    BackgroundUpdated?.Invoke(this, EventArgs.Empty);
                _detailNotes = notes;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _detailNotes = [$"Mailbox sizes and automatic replies weren't read: {e.Message}"];
            }
            finally
            {
                BackgroundStatus = null;
            }
        });
    }

    /// <summary>
    /// Every mailbox's size (Get-MailboxStatistics) and automatic-reply state
    /// (Get-MailboxAutoReplyConfiguration). Exchange has no tenant-wide form of either, so it's
    /// two reads per mailbox, sent ten to a $batch request, a few requests at a time (measured
    /// live: one by one took about 11 minutes for 600 mailboxes). A mailbox that can't be read
    /// keeps what was stored.
    /// </summary>
    async Task<int> SyncMailboxDetailsAsync(List<string> notes, CancellationToken cancellationToken)
    {
        var mailboxes = new List<(string Id, string Address)>();
        foreach (var json in store.All(Mailboxes))
        {
            using var m = JsonDocument.Parse(json);
            if (m.RootElement.TryGetProperty("ExternalDirectoryObjectId", out var o) && o.GetString() is { Length: > 0 } id
                && m.RootElement.TryGetProperty("PrimarySmtpAddress", out var a) && a.GetString() is { Length: > 0 } address)
                mailboxes.Add((id, address));
        }
        if (mailboxes.Count == 0)
        {
            store.SetMark(MailboxDetails, new SyncMark(null, _now()));
            return 0;
        }

        var details = new System.Collections.Concurrent.ConcurrentBag<EntityChange>();
        var failed = 0;
        var done = 0;
        BackgroundStatus = $"Mailbox sizes: 0 of {mailboxes.Count:N0}";
        await Parallel.ForEachAsync(mailboxes.Chunk(MailboxesPerBatch), new ParallelOptions { MaxDegreeOfParallelism = BatchesAtOnce, CancellationToken = cancellationToken },
            async (chunk, ct) =>
            {
                var calls = chunk.SelectMany(m => new[]
                {
                    new CmdletCall("Get-MailboxStatistics", new Dictionary<string, object?> { ["Identity"] = m.Address }, ["TotalItemSize", "ItemCount"]),
                    new CmdletCall("Get-MailboxAutoReplyConfiguration", new Dictionary<string, object?> { ["Identity"] = m.Address }, ["AutoReplyState"]),
                }).ToArray();
                IReadOnlyList<CmdletResult> results;
                try
                {
                    results = await exchange!.InvokeManyAsync(CmdletEndpoint.Exchange, calls, ct);
                }
                catch (CmdletException)
                {
                    Interlocked.Add(ref failed, chunk.Length);
                    return;
                }
                for (var i = 0; i < chunk.Length; i++)
                {
                    var (stats, reply) = (results[2 * i], results[2 * i + 1]);
                    if (!stats.Succeeded && !reply.Succeeded)
                    {
                        Interlocked.Increment(ref failed);
                        continue;
                    }
                    var row = new JsonObject { ["id"] = chunk[i].Id };
                    if (stats.Rows.Count > 0 && stats.Rows[0].TryGetProperty("TotalItemSize", out var size)
                        && MailDetails.ParseBytes(size.ValueKind == JsonValueKind.String ? size.GetString() : size.ToString()) is { } used)
                        row["usedBytes"] = used;
                    if (reply.Rows.Count > 0 && reply.Rows[0].TryGetProperty("AutoReplyState", out var state))
                        row["autoReply"] = state.GetString() switch { "Enabled" => "On", "Scheduled" => "Scheduled", _ => "Off" };
                    details.Add(new EntityChange(chunk[i].Id, row));
                }
                var now = Interlocked.Add(ref done, chunk.Length);
                BackgroundStatus = $"Mailbox sizes: {now:N0} of {mailboxes.Count:N0}";
            });
        if (failed > 0)
            notes.Add($"The size or automatic replies of {failed:N0} mailboxes couldn't be read; their previous values are shown.");
        var changed = store.Apply(MailboxDetails, details.ToArray())
                      + store.RemoveExcept(MailboxDetails, mailboxes.Select(m => m.Id).ToHashSet(StringComparer.Ordinal));
        store.SetMark(MailboxDetails, new SyncMark(null, _now()));
        return changed;
    }

    /// <summary>One mailbox's Full access and Send as trustees, as stored under <see cref="MailboxAccess"/>.</summary>
    internal static async Task<JsonObject> ReadMailboxAccessAsync(ICmdletRunner exchange, string id, string address, CancellationToken cancellationToken)
    {
        var identity = new Dictionary<string, object?> { ["Identity"] = address };
        var full = await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-MailboxPermission", identity, cancellationToken,
            ["User", "AccessRights", "IsInherited", "Deny"]);
        var sendAs = await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-RecipientPermission", identity, cancellationToken,
            ["Trustee", "AccessRights", "IsInherited", "AccessControlType"]);
        return new JsonObject
        {
            ["id"] = id,
            ["fullAccess"] = Trustees(full, "User", "FullAccess"),
            ["sendAs"] = Trustees(sendAs, "Trustee", "SendAs"),
        };
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
