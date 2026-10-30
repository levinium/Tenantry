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
/// <param name="sharePoint">The SharePoint admin site, for sites and OneDrives; skipped when null.</param>
public sealed partial class DirectorySync(GraphClient graph, IEntityStore store, ICmdletRunner? exchange = null, Func<DateTimeOffset>? now = null,
    Func<SyncSettings>? settings = null, SharePointAdmin? sharePoint = null, bool keepLogs = false) : ITenantSync, IDisposable
{
    /// <summary>
    /// Cancelled when the tenant closes: the background reads (mailbox details, the log archive)
    /// run for minutes on their own and must stop rather than keep calling Microsoft 365 and
    /// writing to a closed local copy.
    /// </summary>
    readonly CancellationTokenSource _stopping = new();

    public void Dispose() => _stopping.Cancel();

    /// <summary>Each mailbox's size and automatic-reply state (one Exchange read per mailbox for each).</summary>
    public const string MailboxDetails = "mailboxdetails";

    /// <summary>Each mailbox's email apps (Get-CASMailbox), and the organization's SMTP-sending default.</summary>
    public const string CasMailboxes = "casmailbox";

    public const string Users = "user";
    public const string Groups = "group";
    public const string Skus = "sku";
    public const string Domains = "domain";
    public const string Teams = "team";
    public const string Mailboxes = "mailbox";
    public const string MailboxAccess = "mailboxaccess";
    public const string SignIns = "signin";

    /// <summary>Entra's MFA registration report, one row per user (needs Entra ID P1).</summary>
    public const string Registration = "registration";

    /// <summary>Microsoft's Microsoft 365 Apps activation report, one row per person, keyed by lower-case sign-in name.</summary>
    public const string Activations = "activations";

    /// <summary>A row stored beside the report when it was read with names: someone missing from it has activated nothing.</summary>
    public const string ActivationsReadId = "#read";

    const string UserSelect = "id,displayName,userPrincipalName,mail,department,jobTitle,accountEnabled,userType,createdDateTime,assignedLicenses,"
        + "onPremisesSyncEnabled,usageLocation";

    /// <summary>The user fields last read in full: when a newer Tenantry keeps more, everyone is read again once.</summary>
    const string UserFieldsMark = "users.fields";

    /// <summary>The user properties the local copy keeps (for writing a new user straight into it).</summary>
    public const string UserSelectFields = UserSelect;
    /// <summary>Licenses with counts and their apps, for the license picker and per-app switches.</summary>
    public const string SkuSelect = "skuId,skuPartNumber,prepaidUnits,consumedUnits,servicePlans,appliesTo,capabilityStatus";

    const string GroupSelect = "id,displayName,mail,mailEnabled,securityEnabled,groupTypes,description,visibility,createdDateTime,onPremisesSyncEnabled,members,owners";

    /// <summary>The group fields last read in full (as <see cref="UserFieldsMark"/> is for users).</summary>
    const string GroupFieldsMark = "groups.fields";

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
            // The Exchange reads (mailboxes, then who can use the shared ones) need nothing from
            // Graph and are the slow part, so they start now and run alongside the Graph steps.
            var exchangeNotes = new List<string>();
            string? exchangeStep = null;
            var exchangeProgress = new Relay(step =>
            {
                exchangeStep = step;
                progress?.Report(step);
            });
            var exchangeWork = exchange is not null && Due(Mailboxes, scope)
                ? Task.Run(() => SyncMailboxesAsync(exchangeNotes, exchangeProgress, cancellationToken), cancellationToken)
                : Task.FromResult(0);
            var changed = 0;
            try
            {
                // Each step says what it's doing, so a long Refresh never looks stuck.
                progress?.Report("Reading licenses…");
                changed = await ReadWholeAsync(Skus, $"subscribedSkus?$select={SkuSelect}", "skuId", cancellationToken);
                progress?.Report("Checking users for changes…");
                // Changes only bring the fields asked for to people who changed, so new fields need one full read.
                var userScope = store.GetMark(UserFieldsMark)?.Cursor == UserSelect ? scope : SyncScope.StartOver;
                changed += await SyncDeltaAsync(Users, Users, $"users/delta?$select={UserSelect}", "users", userScope, _ => item => new EntityChange(Id(item), ToObject(item)), progress, cancellationToken);
                store.SetMark(UserFieldsMark, new SyncMark(UserSelect, _now()));
                progress?.Report("Checking groups for changes…");
                var groupScope = store.GetMark(GroupFieldsMark)?.Cursor == GroupSelect ? scope : SyncScope.StartOver;
                changed += await SyncDeltaAsync(Groups, GroupsMark, $"groups/delta?$select={GroupSelect}", "groups", groupScope, GroupMapper, progress, cancellationToken);
                store.SetMark(GroupFieldsMark, new SyncMark(GroupSelect, _now()));
                changed += await SyncDevicesAsync(notes, scope, progress, cancellationToken);
                if (Due(ManagedDevices, scope))
                    changed += await SyncManagedDevicesAsync(notes, progress, cancellationToken);
                if (Due(RecoveryKeys, scope))
                    changed += await SyncRecoveryKeysAsync(progress, cancellationToken);
                if (Due(Sites, scope))
                    changed += await SyncSitesAsync(notes, progress, cancellationToken);

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
                if (Due(Registration, scope))
                    changed += await SyncRegistrationAsync(progress, cancellationToken);
                if (Due(Activations, scope))
                    changed += await SyncActivationsAsync(notes, progress, cancellationToken);
            }
            catch
            {
                // Finish the Exchange part before giving up, so two syncs never overlap.
                await Task.WhenAny(exchangeWork);
                throw;
            }
            // The Graph steps usually finish first: show what Exchange is still doing.
            if (!exchangeWork.IsCompleted && exchangeStep is { } still)
                progress?.Report(still);
            changed += await exchangeWork;
            notes.AddRange(exchangeNotes);
            // Mailbox sizes and replies take minutes for a big tenant: read in the background, so
            // checking for changes (and Refresh) never waits for them.
            if (exchange is not null)
            {
                var background = scope == SyncScope.Refresh ? SyncScope.Changes : scope;
                StartMailboxDetails(Settings.SizesOn && Due(SizesMark, background, Settings.MailboxSizesMinutes),
                    Settings.RepliesOn && Due(RepliesMark, background, Settings.MailboxDetailsMinutes));
            }
            // Sign-in and audit history: also in the background, on its own schedule.
            StartLogArchive(scope == SyncScope.Refresh ? SyncScope.Changes : scope);

            _notes = [.. notes, .. _detailNotes, .. _logNotes];
            return changed;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Passes progress straight on (no posting to another thread), remembering it on the way.</summary>
    sealed class Relay(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
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
                ? minutes == 60 ? "Every Hour" : minutes == 1440 ? "Once a Day" : $"Every {minutes / 60} Hours"
                : $"Every {minutes} Minutes";
            var changes = Every(s.ChangesMinutes, "When the Tenant Opens, and on Refresh");
            var details = Every(s.DetailsMinutes, "On Refresh");
            List<SyncArea> areas =
            [
                new("Users", "Everyone in the directory, their licenses and status (changes only)", store.GetMark(Users)?.SyncedAt, changes),
                new("Groups", "Groups with their members and owners (changes only)", store.GetMark(GroupsMark)?.SyncedAt, changes),
                new("Licenses", "The tenant's licenses and how many are left", store.GetMark(Skus)?.SyncedAt, changes),
                new("Devices", "Devices registered or joined in Entra (changes only)", store.GetMark(Devices)?.SyncedAt, changes),
                new("Intune Devices", "Compliance, encryption, primary user and check-ins of the devices Intune manages", store.GetMark(ManagedDevices)?.SyncedAt, details),
                new("SharePoint Sites and OneDrives", "Every site and OneDrive: storage, owner, sharing, lock and archive state", store.GetMark(Sites)?.SyncedAt, details),
                new("BitLocker Keys Backed Up", "Which devices have a recovery key backed up (never the keys themselves)", store.GetMark(RecoveryKeys)?.SyncedAt, details),
                new("Last Sign-In Times", "When each person last signed in", store.GetMark(SignIns)?.SyncedAt, details),
                new("MFA Registration", "Who has set up multi-factor sign-in, and who holds an admin role", store.GetMark(Registration)?.SyncedAt, details),
                new("Microsoft 365 Apps Activations", "How many computers and mobile devices each person has activated the apps on (Microsoft updates it daily)",
                    store.GetMark(Activations)?.SyncedAt, details),
                new("Domains and Teams", "The tenant's domains, and which groups are Teams", store.GetMark(Domains)?.SyncedAt, details),
            ];
            if (exchange is not null)
            {
                areas.Add(new("Mailboxes", "Mailbox types, addresses, forwarding, address list; who can use shared mailboxes", store.GetMark(Mailboxes)?.SyncedAt, details));
                areas.Add(new("Automatic Replies", "Whether each mailbox is replying; one read per mailbox, in the background",
                    store.GetMark(RepliesMark)?.SyncedAt, Every(s.MailboxDetailsMinutes, "Off")));
                areas.Add(new("Mailbox Sizes", "Storage used against the quota; one read per mailbox, in the background",
                    store.GetMark(SizesMark)?.SyncedAt, Every(s.MailboxSizesMinutes, "Off")));
            }
            if (keepLogs && store is ILogArchive)
                areas.Add(new("Sign-In and Audit History", s.BackgroundSignIns
                        ? "Sign-ins (non-interactive ones for 30 days), directory audits and Exchange admin actions, kept on this computer"
                        : "Sign-ins, directory audits and Exchange admin actions, kept on this computer",
                    store.GetMark(LogsMark)?.SyncedAt, Every(s.LogsMinutes, "Off")));
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

    /// <summary>
    /// Entra's MFA registration report for everyone, in pages: whether each person registered
    /// MFA, with what, and whether they hold an admin role. Needs Entra ID P1; without it the
    /// MFA column stays "Not read". Stored whole each time (people who left drop out).
    /// </summary>
    async Task<int> SyncRegistrationAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report("Reading MFA registration…");
            var changes = new List<EntityChange>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var item in graph.GetAllAsync(RegistrationPath, cancellationToken))
            {
                var id = Id(item);
                seen.Add(id);
                changes.Add(new EntityChange(id, ToObject(item)));
            }
            var changed = store.Apply(Registration, changes) + store.RemoveExcept(Registration, seen);
            store.SetMark(Registration, new SyncMark(null, _now()));
            return changed;
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            // No Entra ID P1, or no reports role: MFA stays "Not read". Not an error.
            store.SetMark(Registration, new SyncMark(null, _now()));
            return 0;
        }
    }

    /// <summary>
    /// The activation report for everyone, stored whole each time. With names concealed in reports
    /// it can't be matched to anyone, so nothing is stored and a note says how to show them.
    /// </summary>
    async Task<int> SyncActivationsAsync(List<string> notes, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report("Reading Microsoft 365 Apps activations…");
            var rows = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            await foreach (var item in graph.GetAllAsync(ActivationReport.Path, cancellationToken))
                if (ActivationReport.UserPrincipalName(item) is { } upn)
                    rows[upn.ToLowerInvariant()] = item;
            var changed = 0;
            if (ActivationReport.IsConcealed(rows.Keys))
                notes.Add(ActivationReport.ConcealedReason);
            else
            {
                var changes = rows.Select(r => new EntityChange(r.Key, ToObject(r.Value))).ToList();
                using var marker = JsonDocument.Parse("{}");
                changes.Add(new EntityChange(ActivationsReadId, ToObject(marker.RootElement)));
                changed = store.Apply(Activations, changes)
                          + store.RemoveExcept(Activations, new HashSet<string>(rows.Keys.Append(ActivationsReadId), StringComparer.Ordinal));
            }
            store.SetMark(Activations, new SyncMark(null, _now()));
            return changed;
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            // No reports role: the activation columns stay "Not read". Not an error.
            store.SetMark(Activations, new SyncMark(null, _now()));
            return 0;
        }
    }

    const string RegistrationPath =
        "reports/authenticationMethods/userRegistrationDetails?$select=id,isAdmin,isMfaRegistered,methodsRegistered,userPreferredMethodForSecondaryAuthentication";

    /// <summary>The Get-CASMailbox properties the lists use: each email app on or off.</summary>
    internal static readonly string[] CasSelect =
        ["ExternalDirectoryObjectId", "OWAEnabled", "MAPIEnabled", "ActiveSyncEnabled", "EwsEnabled", "PopEnabled", "ImapEnabled", "SmtpClientAuthenticationDisabled"];

    /// <summary>
    /// Every mailbox's email apps in one call (measured live: about 4 s for 600 mailboxes), plus
    /// the organization's SMTP-sending default, which a mailbox follows unless set on its own.
    /// </summary>
    async Task<int> SyncCasMailboxesAsync(List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            var transport = exchange!.InvokeAsync(CmdletEndpoint.Exchange, "Get-TransportConfig", null, cancellationToken, ["SmtpClientAuthenticationDisabled"]);
            var rows = await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-CASMailbox",
                new Dictionary<string, object?> { ["ResultSize"] = "Unlimited" }, cancellationToken, CasSelect);
            var changes = new List<EntityChange>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!row.TryGetProperty("ExternalDirectoryObjectId", out var owner) || owner.GetString() is not { Length: > 0 } id)
                    continue;
                seen.Add(id);
                changes.Add(new EntityChange(id, ToObject(row)));
            }
            var org = (await transport).FirstOrDefault();
            var orgSmtpOff = org.ValueKind == JsonValueKind.Object && org.TryGetProperty("SmtpClientAuthenticationDisabled", out var d) && d.ValueKind == JsonValueKind.True;
            changes.Add(new EntityChange(OrgSettingsId, new JsonObject { ["id"] = OrgSettingsId, ["smtpClientAuthenticationDisabled"] = orgSmtpOff }));
            seen.Add(OrgSettingsId);
            return store.Apply(CasMailboxes, changes) + store.RemoveExcept(CasMailboxes, seen);
        }
        catch (CmdletException e)
        {
            notes.Add($"Email app settings weren't read: {e.Explanation}");
            return 0;
        }
    }

    /// <summary>The row under <see cref="CasMailboxes"/> holding the organization's defaults.</summary>
    public const string OrgSettingsId = "_organization";
    async Task<int> SyncMailboxesAsync(List<string> notes, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var changed = 0;
        try
        {
            progress?.Report("Reading mailboxes…");
            // Email apps (Get-CASMailbox) for everyone take a few seconds; read alongside.
            var casWork = SyncCasMailboxesAsync(notes, cancellationToken);
            var rows = await exchange!.InvokeAsync(CmdletEndpoint.Exchange, "Get-Mailbox",
                new Dictionary<string, object?> { ["ResultSize"] = "Unlimited" }, cancellationToken, MailboxSelect);
            changed += await casWork;
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
            changed += store.Apply(Mailboxes, changes) + store.RemoveExcept(Mailboxes, seen);
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
    /// that maps cleanly to mailboxes, so it is two calls per mailbox, run many at once (see
    /// <see cref="RunAllAsync"/>; one by one this was 80% of a Refresh). Trustees come back as
    /// addresses; the mailbox's own SELF entry and inherited or deny entries are dropped. Send on
    /// behalf comes from Get-Mailbox itself.
    /// </summary>
    async Task<int> SyncMailboxAccessAsync(List<(string Id, string Address)> mailboxes, List<string> notes,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (mailboxes.Count == 0)
            return store.RemoveExcept(MailboxAccess, new HashSet<string>());
        progress?.Report($"Shared mailbox access: 0 of {mailboxes.Count:N0}…");
        var calls = mailboxes.SelectMany(m => AccessCalls(m.Address)).ToArray();
        var results = await RunAllAsync(calls, done => progress?.Report($"Shared mailbox access: {done / 2:N0} of {mailboxes.Count:N0}…"), cancellationToken);
        var changes = new List<EntityChange>();
        var failed = 0;
        for (var i = 0; i < mailboxes.Count; i++)
        {
            var (full, sendAs) = (results[2 * i], results[2 * i + 1]);
            // Typically a mailbox removed between the reads. Keep what was stored.
            if (!full.Succeeded || !sendAs.Succeeded)
            {
                failed++;
                continue;
            }
            changes.Add(new EntityChange(mailboxes[i].Id, AccessRow(mailboxes[i].Id, full.Rows, sendAs.Rows)));
        }
        if (failed > 0)
            notes.Add($"Access for {failed:N0} shared mailboxes couldn't be read; their previous access is shown.");
        return store.Apply(MailboxAccess, changes)
            + store.RemoveExcept(MailboxAccess, mailboxes.Select(m => m.Id).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Exchange calls in flight at once. Measured live (605 single-mailbox calls, one admin):
    /// 32 at once took 47 s and 64 took 43 s, with no throttling at either; $batch requests
    /// (ten calls each, the most Exchange allows) were slower at every setting, because Exchange
    /// runs a batch's calls one after another. 32 leaves headroom for busier days.
    /// </summary>
    const int CallsAtOnce = 32;

    /// <summary>
    /// Runs single-object cmdlets many at once and returns each one's output or error, in order.
    /// <paramref name="onDone"/> hears the running count of finished calls.
    /// </summary>
    async Task<CmdletResult[]> RunAllAsync(IReadOnlyList<CmdletCall> calls, Action<int>? onDone, CancellationToken cancellationToken)
    {
        var results = new CmdletResult[calls.Count];
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, calls.Count), new ParallelOptions { MaxDegreeOfParallelism = CallsAtOnce, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                try
                {
                    results[i] = new CmdletResult(await exchange!.InvokeAsync(CmdletEndpoint.Exchange, calls[i].Cmdlet, calls[i].Parameters, ct, calls[i].Select), null);
                }
                catch (CmdletException e)
                {
                    results[i] = new CmdletResult([], e.Explanation);
                }
                var finished = Interlocked.Increment(ref done);
                // Not every call: a progress line per call would flood the UI.
                if (finished % 10 == 0 || finished == calls.Count)
                    onDone?.Invoke(finished);
            });
        return results;
    }
    // ---- Mailbox details, in the background ---------------------------------------------------

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

    /// <summary>When mailbox sizes and automatic replies were last read (each has its own schedule).</summary>
    public const string SizesMark = "mailboxsizes";
    public const string RepliesMark = "mailboxreplies";

    void StartMailboxDetails(bool sizes, bool replies)
    {
        if (_details is { IsCompleted: false } || (!sizes && !replies))
            return;
        _details = Task.Run(async () =>
        {
            try
            {
                var notes = new List<string>();
                if (await SyncMailboxDetailsAsync(sizes, replies, notes, _stopping.Token) > 0)
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
    /// Every mailbox's size (Get-MailboxStatistics) and/or automatic-reply state
    /// (Get-MailboxAutoReplyConfiguration), whichever is due. Exchange has no tenant-wide form of
    /// either, so it's one read per mailbox for each, run many at once (<see cref="RunAllAsync"/>;
    /// measured live: one by one took about 11 minutes for 600 mailboxes, at once under one).
    /// A mailbox that can't be read keeps what was stored; what isn't read this time is kept too.
    /// </summary>
    async Task<int> SyncMailboxDetailsAsync(bool sizes, bool replies, List<string> notes, CancellationToken cancellationToken)
    {
        var mailboxes = new List<(string Id, string Address)>();
        foreach (var json in store.All(Mailboxes))
        {
            using var m = JsonDocument.Parse(json);
            if (m.RootElement.TryGetProperty("ExternalDirectoryObjectId", out var o) && o.GetString() is { Length: > 0 } id
                && m.RootElement.TryGetProperty("PrimarySmtpAddress", out var a) && a.GetString() is { Length: > 0 } address)
                mailboxes.Add((id, address));
        }
        void Marks()
        {
            if (sizes)
                store.SetMark(SizesMark, new SyncMark(null, _now()));
            if (replies)
                store.SetMark(RepliesMark, new SyncMark(null, _now()));
        }
        if (mailboxes.Count == 0)
        {
            Marks();
            return 0;
        }

        var what = sizes && replies ? "Mailbox sizes and replies" : sizes ? "Mailbox sizes" : "Automatic replies";
        var perMailbox = (sizes ? 1 : 0) + (replies ? 1 : 0);
        BackgroundStatus = $"{what}: 0 of {mailboxes.Count:N0}";
        var calls = new List<CmdletCall>(mailboxes.Count * perMailbox);
        foreach (var m in mailboxes)
        {
            var identity = new Dictionary<string, object?> { ["Identity"] = m.Address };
            if (sizes)
                calls.Add(new CmdletCall("Get-MailboxStatistics", identity, ["TotalItemSize", "ItemCount"]));
            if (replies)
                calls.Add(new CmdletCall("Get-MailboxAutoReplyConfiguration", identity, ["AutoReplyState"]));
        }
        var results = await RunAllAsync(calls, done => BackgroundStatus = $"{what}: {done / perMailbox:N0} of {mailboxes.Count:N0}", cancellationToken);

        var details = new List<EntityChange>(mailboxes.Count);
        var failed = 0;
        for (var i = 0; i < mailboxes.Count; i++)
        {
            var mine = results.AsSpan(i * perMailbox, perMailbox).ToArray();
            if (mine.All(r => !r.Succeeded))
            {
                failed++;
                continue;
            }
            // Only what was read is written: the rest of the stored row is kept as it was.
            var row = new JsonObject { ["id"] = mailboxes[i].Id };
            var next = 0;
            if (sizes && mine[next++] is { Succeeded: true } stats && stats.Rows.Count > 0 && stats.Rows[0].TryGetProperty("TotalItemSize", out var size)
                && MailDetails.ParseBytes(size.ValueKind == JsonValueKind.String ? size.GetString() : size.ToString()) is { } used)
                row["usedBytes"] = used;
            if (replies && mine[next] is { Succeeded: true } reply && reply.Rows.Count > 0 && reply.Rows[0].TryGetProperty("AutoReplyState", out var state))
                row["autoReply"] = state.GetString() switch { "Enabled" => "On", "Scheduled" => "Scheduled", _ => "Off" };
            details.Add(new EntityChange(mailboxes[i].Id, row));
        }        if (failed > 0)
            notes.Add($"{failed:N0} mailboxes couldn't be read in the background; their previous values are shown.");
        var changed = store.Apply(MailboxDetails, details)
                      + store.RemoveExcept(MailboxDetails, mailboxes.Select(m => m.Id).ToHashSet(StringComparer.Ordinal));
        Marks();
        return changed;
    }

    /// <summary>The two reads behind a mailbox's access: Full access, then Send as.</summary>
    static CmdletCall[] AccessCalls(string address)
    {
        var identity = new Dictionary<string, object?> { ["Identity"] = address };
        return
        [
            new("Get-MailboxPermission", identity, ["User", "AccessRights", "IsInherited", "Deny"]),
            new("Get-RecipientPermission", identity, ["Trustee", "AccessRights", "IsInherited", "AccessControlType"]),
        ];
    }

    static JsonObject AccessRow(string id, IReadOnlyList<JsonElement> full, IReadOnlyList<JsonElement> sendAs) => new()
    {
        ["id"] = id,
        ["fullAccess"] = Trustees(full, "User", "FullAccess"),
        ["sendAs"] = Trustees(sendAs, "Trustee", "SendAs"),
    };

    /// <summary>One mailbox's Full access and Send as trustees, as stored under <see cref="MailboxAccess"/>.</summary>
    internal static async Task<JsonObject> ReadMailboxAccessAsync(ICmdletRunner exchange, string id, string address, CancellationToken cancellationToken)
    {
        // The two reads side by side (a $batch would run them one after the other).
        var calls = AccessCalls(address);
        var reads = calls.Select(c => exchange.InvokeAsync(CmdletEndpoint.Exchange, c.Cmdlet, c.Parameters, cancellationToken, c.Select)).ToArray();
        var rows = await Task.WhenAll(reads);
        return AccessRow(id, rows[0], rows[1]);
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
