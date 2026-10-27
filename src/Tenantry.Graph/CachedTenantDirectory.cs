using System.Globalization;
using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Core.Licensing;

namespace Tenantry.Graph;

/// <summary>
/// The directory as the lists see it: read from the tenant's local copy, which
/// <see cref="DirectorySync"/> keeps current. Objects are stored as Graph returned them, so this
/// is where Graph's shape becomes Tenantry's.
/// </summary>
public sealed class CachedTenantDirectory(IEntityStore store, string tenantName) : ITenantDirectory
{
    public string TenantName => tenantName;

    public Task<IReadOnlyList<UserSummary>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadUsers(cancellationToken), cancellationToken);

    IReadOnlyList<UserSummary> ReadUsers(CancellationToken cancellationToken)
    {
        // Product names ("Microsoft 365 Business Premium"), not Graph's codes ("SPB").
        var skuNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in store.All(DirectorySync.Skus))
        {
            using var sku = JsonDocument.Parse(json);
            var id = sku.RootElement.GetProperty("skuId").GetString()!;
            skuNames[id] = LicenseNames.For(id, Str(sku.RootElement, "skuPartNumber"));
        }

        var domains = Ids(DirectorySync.Domains);
        // Size and automatic replies, from the hourly per-mailbox read (absent until it has run).
        var details = new Dictionary<string, (long? Used, AutoReplyState? Reply)>(StringComparer.Ordinal);
        foreach (var json in store.All(DirectorySync.MailboxDetails))
        {
            using var d = JsonDocument.Parse(json);
            if (Str(d.RootElement, "id") is not { } id)
                continue;
            long? used = d.RootElement.TryGetProperty("usedBytes", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : null;
            AutoReplyState? reply = Str(d.RootElement, "autoReply") is { } r && Enum.TryParse<AutoReplyState>(r, out var state) ? state : null;
            details[id] = (used, reply);
        }
        // Email apps (Get-CASMailbox), with the organization's SMTP-sending default.
        var apps = new Dictionary<string, EmailApps>(StringComparer.Ordinal);
        var cas = store.All(DirectorySync.CasMailboxes).Select(j => JsonDocument.Parse(j)).ToArray();
        var orgSmtpOn = !cas.Any(d => Str(d.RootElement, "id") == DirectorySync.OrgSettingsId && Bool(d.RootElement, "smtpClientAuthenticationDisabled"));
        foreach (var d in cas)
        {
            if (Str(d.RootElement, "ExternalDirectoryObjectId") is { } owner)
                apps[owner] = ToEmailApps(d.RootElement, orgSmtpOn);
            d.Dispose();
        }
        var mailboxes = new Dictionary<string, MailboxInfo>(StringComparer.Ordinal);
        foreach (var json in store.All(DirectorySync.Mailboxes))
        {
            using var mailbox = JsonDocument.Parse(json);
            if (Str(mailbox.RootElement, "ExternalDirectoryObjectId") is not { } owner)
                continue;
            var info = ToMailbox(mailbox.RootElement, domains);
            if (details.TryGetValue(owner, out var extra))
                info = info with { UsedBytes = extra.Used, AutoReply = extra.Reply };
            mailboxes[owner] = apps.TryGetValue(owner, out var a) ? info with { Apps = a } : info;
        }

        var security = new Dictionary<string, UserSecurity>(StringComparer.Ordinal);
        foreach (var json in store.All(DirectorySync.Registration))
        {
            using var r = JsonDocument.Parse(json);
            if (Str(r.RootElement, "id") is { } id)
                security[id] = ToSecurity(r.RootElement);
        }

        // Microsoft 365 Apps activations, by sign-in name. Once the report has been read, someone
        // missing from it has activated nothing (0), not "not read".
        var activations = new Dictionary<string, IReadOnlyList<OfficeActivation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in store.All(DirectorySync.Activations))
        {
            using var a = JsonDocument.Parse(json);
            if (ActivationReport.UserPrincipalName(a.RootElement) is { } upn)
                activations[upn] = ActivationReport.Parse(a.RootElement);
        }
        IReadOnlyList<OfficeActivation>? none = store.Get(DirectorySync.Activations, DirectorySync.ActivationsReadId) is not null ? [] : null;

        var stored = store.All(DirectorySync.Users);
        var users = new List<UserSummary>(stored.Count);
        foreach (var json in stored)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var user = JsonDocument.Parse(json);
            var summary = ToSummary(user.RootElement, skuNames);
            if (mailboxes.TryGetValue(summary.Id, out var mailboxInfo))
                summary = summary with { Mailbox = mailboxInfo };
            if (security.TryGetValue(summary.Id, out var registered))
                summary = summary with { Security = registered };
            summary = summary with { Activations = activations.GetValueOrDefault(summary.UserPrincipalName) ?? none };
            users.Add(summary);
        }
        return users;
    }

    /// <summary>One user, read by key (a record page refreshing itself): the same joins as the list, for one row.</summary>
    public Task<UserSummary?> GetUserAsync(string id, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (store.Get(DirectorySync.Users, id) is not { } json)
                return null;
            var skuNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var skuJson in store.All(DirectorySync.Skus))
            {
                using var sku = JsonDocument.Parse(skuJson);
                var skuId = sku.RootElement.GetProperty("skuId").GetString()!;
                skuNames[skuId] = LicenseNames.For(skuId, Str(sku.RootElement, "skuPartNumber"));
            }
            using var user = JsonDocument.Parse(json);
            var summary = ToSummary(user.RootElement, skuNames);
            if (store.Get(DirectorySync.Registration, id) is { } registrationJson)
            {
                using var registration = JsonDocument.Parse(registrationJson);
                summary = summary with { Security = ToSecurity(registration.RootElement) };
            }
            if (store.Get(DirectorySync.Activations, summary.UserPrincipalName.ToLowerInvariant()) is { } activationJson)
            {
                using var activation = JsonDocument.Parse(activationJson);
                summary = summary with { Activations = ActivationReport.Parse(activation.RootElement) };
            }
            else if (store.Get(DirectorySync.Activations, DirectorySync.ActivationsReadId) is not null)
                summary = summary with { Activations = [] };
            if (store.Get(DirectorySync.Mailboxes, id) is not { } mailboxJson)
                return summary;
            using var mailbox = JsonDocument.Parse(mailboxJson);
            var info = ToMailbox(mailbox.RootElement, Ids(DirectorySync.Domains));
            if (store.Get(DirectorySync.MailboxDetails, id) is { } detailsJson)
            {
                using var d = JsonDocument.Parse(detailsJson);
                long? used = d.RootElement.TryGetProperty("usedBytes", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : null;
                AutoReplyState? reply = Str(d.RootElement, "autoReply") is { } r && Enum.TryParse<AutoReplyState>(r, out var state) ? state : null;
                info = info with { UsedBytes = used, AutoReply = reply };
            }
            if (store.Get(DirectorySync.CasMailboxes, id) is { } casJson)
            {
                var orgSmtpOn = true;
                if (store.Get(DirectorySync.CasMailboxes, DirectorySync.OrgSettingsId) is { } orgJson)
                {
                    using var org = JsonDocument.Parse(orgJson);
                    orgSmtpOn = !Bool(org.RootElement, "smtpClientAuthenticationDisabled");
                }
                using var cas = JsonDocument.Parse(casJson);
                info = info with { Apps = ToEmailApps(cas.RootElement, orgSmtpOn) };
            }
            return summary with { Mailbox = info };
        }, cancellationToken);

    /// <summary>One group by key; a shared mailbox's row needs every user's addresses, so it comes from the full read.</summary>
    public async Task<GroupSummary?> GetGroupAsync(string id, CancellationToken cancellationToken = default)
    {
        if (store.Get(DirectorySync.Groups, id) is { } json)
            return await Task.Run(() =>
            {
                using var group = JsonDocument.Parse(json);
                return ToGroup(group.RootElement, Ids(DirectorySync.Teams));
            }, cancellationToken);
        return (await GetGroupsAsync(cancellationToken)).FirstOrDefault(g => g.Id == id);
    }

    public Task<IReadOnlyList<GroupSummary>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<GroupSummary>>(() =>
        {
            var teams = Ids(DirectorySync.Teams);
            var stored = store.All(DirectorySync.Groups);
            var groups = new List<GroupSummary>(stored.Count);
            foreach (var json in stored)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var group = JsonDocument.Parse(json);
                groups.Add(ToGroup(group.RootElement, teams));
            }
            groups.AddRange(SharedMailboxes(groups, cancellationToken));
            return groups;
        }, cancellationToken);

    /// <summary>
    /// Shared, room and equipment mailboxes as rows beside the groups: named after their
    /// account, with the people who can use them as members. Trustees are addresses, matched to
    /// users (sign-in name, email, mailbox addresses) or mail-enabled groups; Send on behalf is
    /// matched by address or display name. Anything unmatched is shown as it was given.
    /// </summary>
    List<GroupSummary> SharedMailboxes(IReadOnlyList<GroupSummary> groups, CancellationToken cancellationToken)
    {
        var mailboxes = new Dictionary<string, (GroupKind Kind, string? Primary, string[] Aliases, string[] OnBehalf)>(StringComparer.Ordinal);
        foreach (var json in store.All(DirectorySync.Mailboxes))
        {
            using var m = JsonDocument.Parse(json);
            var info = ToMailbox(m.RootElement, new HashSet<string>());
            if (Str(m.RootElement, "ExternalDirectoryObjectId") is { } owner && GroupSummary.MailboxKind(info.Type) is { } kind)
                mailboxes[owner] = (kind, info.PrimaryAddress, info.Aliases.ToArray(), Strings(m.RootElement, "GrantSendOnBehalfTo"));
        }
        if (mailboxes.Count == 0)
            return [];

        // Who an address or name belongs to: users first, then mail-enabled groups.
        var byAddress = new Dictionary<string, (string Id, string Type)>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, (string Id, string Type)>(StringComparer.CurrentCultureIgnoreCase);
        var accounts = new Dictionary<string, (string Name, DateTimeOffset? Created)>(StringComparer.Ordinal);
        foreach (var json in store.All(DirectorySync.Users))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var u = JsonDocument.Parse(json);
            var id = u.RootElement.GetProperty("id").GetString()!;
            foreach (var address in new[] { Str(u.RootElement, "userPrincipalName"), Str(u.RootElement, "mail") })
                if (address is { Length: > 0 })
                    byAddress.TryAdd(address, (id, "user"));
            if (Str(u.RootElement, "displayName") is { Length: > 0 } name)
            {
                byName.TryAdd(name, (id, "user"));
                if (mailboxes.ContainsKey(id))
                    accounts[id] = (name, Date(u.RootElement, "createdDateTime"));
            }
        }
        foreach (var (owner, mailbox) in mailboxes)
            foreach (var address in mailbox.Aliases.Prepend(mailbox.Primary ?? ""))
                if (address.Length > 0)
                    byAddress.TryAdd(address, (owner, "user"));
        foreach (var g in groups)
        {
            if (g.Mail is { Length: > 0 } mail)
                byAddress.TryAdd(mail, (g.Id, "group"));
            byName.TryAdd(g.DisplayName, (g.Id, "group"));
        }

        var access = new Dictionary<string, (string[] Full, string[] SendAs)>(StringComparer.Ordinal);
        foreach (var json in store.All(DirectorySync.MailboxAccess))
        {
            using var a = JsonDocument.Parse(json);
            if (Str(a.RootElement, "id") is { } id)
                access[id] = (Strings(a.RootElement, "fullAccess"), Strings(a.RootElement, "sendAs"));
        }

        var rows = new List<GroupSummary>(mailboxes.Count);
        foreach (var (owner, mailbox) in mailboxes)
        {
            // Each person once, with everything they can do.
            var rights = new Dictionary<string, (GroupMember Who, List<string> Can)>(StringComparer.OrdinalIgnoreCase);
            void Grant(string trustee, string right, bool byDisplayName)
            {
                var found = byAddress.TryGetValue(trustee, out var hit) || (byDisplayName && byName.TryGetValue(trustee, out hit));
                var member = found ? new GroupMember(hit.Id, hit.Type) : new GroupMember(trustee, "unresolved", Name: trustee);
                if (member.Id == owner)
                    return;
                if (!rights.TryGetValue(member.Id, out var entry))
                    rights[member.Id] = entry = (member, []);
                if (!entry.Can.Contains(right))
                    entry.Can.Add(right);
            }
            if (access.TryGetValue(owner, out var a))
            {
                foreach (var t in a.Full)
                    Grant(t, "Full access", byDisplayName: false);
                foreach (var t in a.SendAs)
                    Grant(t, "Send as", byDisplayName: false);
            }
            foreach (var t in mailbox.OnBehalf)
                Grant(t, "Send on behalf", byDisplayName: true);

            var account = accounts.GetValueOrDefault(owner);
            rows.Add(new GroupSummary(
                Id: owner,
                DisplayName: account.Name ?? mailbox.Primary ?? owner,
                Mail: mailbox.Primary,
                Description: null,
                Kind: mailbox.Kind,
                IsDynamic: false,
                IsTeam: false,
                Visibility: null,
                CreatedAt: account.Created,
                Members: rights.Values.Select(r => r.Who with { Access = string.Join(", ", r.Can) }).ToArray()));
        }
        return rows;
    }

    /// <summary>
    /// Entra's devices joined with Intune's on the Entra device ID. A device Intune manages without
    /// an Entra record (or with one not yet synced) is still listed, as "Intune only".
    /// </summary>
    public Task<IReadOnlyList<DeviceSummary>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<DeviceSummary>>(() =>
        {
            var managed = new Dictionary<string, JsonDocument>(StringComparer.OrdinalIgnoreCase);
            var unjoined = new List<JsonDocument>();
            foreach (var json in store.All(DirectorySync.ManagedDevices))
            {
                var m = JsonDocument.Parse(json);
                if (DirectorySync.AzureDeviceId(m.RootElement) is { } id && managed.TryAdd(id, m))
                    continue;
                unjoined.Add(m);
            }
            // A BitLocker key backed up means it's encrypted, whatever Intune says (see DirectorySync.RecoveryKeys).
            var keyed = Ids(DirectorySync.RecoveryKeys);
            DeviceSummary WithKey(DeviceSummary d) =>
                d.DeviceId is { } id && keyed.Contains(id) ? d with { KeyBackedUp = true, Encrypted = true } : d;
            try
            {
                var stored = store.All(DirectorySync.Devices);
                var devices = new List<DeviceSummary>(stored.Count + unjoined.Count);
                foreach (var json in stored)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var d = JsonDocument.Parse(json);
                    JsonDocument? hit = null;
                    if (Str(d.RootElement, "deviceId") is { } deviceId)
                        managed.Remove(deviceId, out hit);
                    using (hit)
                        devices.Add(WithKey(ToDevice(d.RootElement, hit?.RootElement)));
                }
                // Intune devices whose Entra record isn't here.
                foreach (var m in managed.Values.Concat(unjoined))
                    devices.Add(WithKey(ToDevice(null, m.RootElement)));
                return devices;
            }
            finally
            {
                foreach (var m in managed.Values.Concat(unjoined))
                    m.Dispose();
            }
        }, cancellationToken);

    public Task<IReadOnlyList<SiteSummary>> GetSitesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<SiteSummary>>(() =>
        {
            // SharePoint lists no owner names: a OneDrive's owner is a sign-in name (matched to the
            // person), a group's site belongs to the group (named after it).
            var people = new Dictionary<string, (string Name, string? Mail)>(StringComparer.OrdinalIgnoreCase);
            foreach (var json in store.All(DirectorySync.Users))
            {
                using var u = JsonDocument.Parse(json);
                if (Str(u.RootElement, "userPrincipalName") is { } upn)
                    people[upn] = (Str(u.RootElement, "displayName") ?? upn, Str(u.RootElement, "mail") ?? upn);
            }
            var groups = new Dictionary<string, (string Name, string? Mail)>(StringComparer.OrdinalIgnoreCase);
            foreach (var json in store.All(DirectorySync.Groups))
            {
                using var g = JsonDocument.Parse(json);
                if (Str(g.RootElement, "id") is { } id)
                    groups[id] = (Str(g.RootElement, "displayName") ?? id, Str(g.RootElement, "mail"));
            }
            var stored = store.All(DirectorySync.Sites);
            var sites = new List<SiteSummary>(stored.Count);
            foreach (var json in stored)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var s = JsonDocument.Parse(json);
                var site = ToSite(s.RootElement);
                if (site.OwnerName is null)
                {
                    if (site.GroupId is { } groupId && groups.TryGetValue(groupId, out var group))
                        site = site with { OwnerName = group.Name, OwnerEmail = site.OwnerEmail ?? group.Mail };
                    else if (site.OwnerLogin is { } login)
                    {
                        // Someone no longer in the directory still has a sign-in name to show.
                        var upn = login[(login.LastIndexOf('|') + 1)..];
                        site = people.TryGetValue(upn, out var person)
                            ? site with { OwnerName = person.Name, OwnerEmail = site.OwnerEmail ?? person.Mail }
                            : upn.Contains('@') ? site with { OwnerName = upn } : site;
                    }
                }
                sites.Add(site);
            }
            return sites;
        }, cancellationToken);

    /// <summary>A stored site row (see <c>DirectorySync.SiteRow</c>) as the lists use it.</summary>
    internal static SiteSummary ToSite(JsonElement s)
    {
        static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        var template = Str(s, "template") ?? "";
        return new SiteSummary(
            Id: Str(s, "id") ?? "",
            Url: Str(s, "url") ?? "",
            Title: Str(s, "title") ?? Str(s, "url") ?? "",
            Template: template,
            Kind: SiteSummary.KindOf(template),
            OwnerName: Str(s, "ownerName"),
            OwnerEmail: Str(s, "ownerEmail"),
            OwnerLogin: Str(s, "ownerLogin"),
            GroupId: Str(s, "groupId"),
            IsTeam: Bool(s, "isTeam"),
            IsHubSite: Bool(s, "isHub"),
            HubSiteId: Str(s, "hubSiteId"),
            StorageUsedMb: Num(s, "storageMb"),
            StorageQuotaMb: Num(s, "quotaMb"),
            Sharing: (int)Num(s, "sharing"),
            Lock: Str(s, "lock") ?? "Unlock",
            Archive: Str(s, "archive") ?? "NotArchived",
            Created: Date(s, "created"),
            LastActivity: Date(s, "lastActivity"),
            SensitivityLabel: Str(s, "label"),
            Subsites: (int)Num(s, "webs"));
    }

    /// <summary>One device from its Entra record, its Intune record, or both; Intune's word wins where both say.</summary>
    internal static DeviceSummary ToDevice(JsonElement? entra, JsonElement? intune)
    {
        static bool? Flag(JsonElement? e, string name) =>
            e is { } x && x.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.ValueKind == JsonValueKind.True : null;
        static string? Text(JsonElement? e, string name) => e is { } x ? Str(x, name) is { Length: > 0 } s ? s : null : null;
        static DateTimeOffset? When(JsonElement? e, string name) => e is { } x ? Date(x, name) : null;
        static long? Bytes(JsonElement? e, string name) =>
            e is { } x && x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt64() > 0 ? v.GetInt64() : null;

        var managedId = Text(intune, "id");
        var objectId = Text(entra, "id");
        return new DeviceSummary(
            Key: objectId ?? "intune:" + managedId,
            ObjectId: objectId,
            DeviceId: Text(entra, "deviceId") ?? (intune is { } i ? DirectorySync.AzureDeviceId(i) : null),
            ManagedId: managedId,
            Name: Text(intune, "deviceName") ?? Text(entra, "displayName") ?? "A device",
            Os: Text(intune, "operatingSystem") ?? Text(entra, "operatingSystem"),
            OsVersion: Text(intune, "osVersion") ?? Text(entra, "operatingSystemVersion"),
            Manufacturer: Text(intune, "manufacturer") ?? Text(entra, "manufacturer"),
            Model: Text(intune, "model") ?? Text(entra, "model"),
            Serial: Text(intune, "serialNumber"),
            JoinType: entra is not null ? GraphUserRecords.Join(Text(entra, "trustType")) : "Intune only",
            Enabled: Flag(entra, "accountEnabled"),
            Compliant: intune is not null
                ? Text(intune, "complianceState") switch { "compliant" => true, "noncompliant" => false, _ => null }
                : Flag(entra, "isCompliant"),
            Encrypted: Flag(intune, "isEncrypted"),
            LastCheckIn: When(intune, "lastSyncDateTime"),
            LastSignIn: When(entra, "approximateLastSignInDateTime"),
            Registered: When(entra, "registrationDateTime") ?? When(intune, "enrolledDateTime"),
            Ownership: Text(intune, "managedDeviceOwnerType") switch { "company" => DeviceFields.Company, "personal" => DeviceFields.Personal, _ => null },
            UserId: Text(intune, "userId"),
            UserName: Text(intune, "userDisplayName") ?? Text(intune, "userPrincipalName"),
            UserPrincipalName: Text(intune, "userPrincipalName"),
            StorageTotal: Bytes(intune, "totalStorageSpaceInBytes"),
            StorageFree: Bytes(intune, "freeStorageSpaceInBytes"));
    }

    static string[] Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString()).OfType<string>().Where(s => s.Length > 0).ToArray()
            : [];

    HashSet<string> Ids(string kind)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in store.All(kind))
        {
            using var item = JsonDocument.Parse(json);
            if (Str(item.RootElement, "id") is { } id)
                ids.Add(id);
        }
        return ids;
    }

    internal static GroupSummary ToGroup(JsonElement g, IReadOnlySet<string> teams)
    {
        var types = g.TryGetProperty("groupTypes", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
            : [];
        var mailEnabled = Bool(g, "mailEnabled");
        var kind = types.Contains("Unified", StringComparer.OrdinalIgnoreCase) ? GroupKind.Microsoft365
            : mailEnabled && Bool(g, "securityEnabled") ? GroupKind.MailEnabledSecurity
            : mailEnabled ? GroupKind.Distribution
            : GroupKind.Security;
        var id = g.GetProperty("id").GetString()!;
        return new GroupSummary(
            Id: id,
            DisplayName: Str(g, "displayName") ?? "",
            Mail: Str(g, "mail"),
            Description: Str(g, "description"),
            Kind: kind,
            IsDynamic: types.Contains("DynamicMembership", StringComparer.OrdinalIgnoreCase),
            IsTeam: teams.Contains(id),
            Visibility: Str(g, "visibility"),
            CreatedAt: Date(g, "createdDateTime"),
            Members: Relation(g, "members"),
            Owners: Relation(g, "owners"));
    }

    /// <summary>A stored relation ({id: type}) as a list; empty when the group has none (or was synced before owners were).</summary>
    static GroupMember[] Relation(JsonElement g, string name) =>
        g.TryGetProperty(name, out var m) && m.ValueKind == JsonValueKind.Object
            ? m.EnumerateObject().Select(p => new GroupMember(p.Name, p.Value.GetString() ?? "unknown")).ToArray()
            : [];

    /// <summary>
    /// A Get-Mailbox row as the list needs it. Forwarding to an address in one of the tenant's
    /// domains (or a subdomain of one) is internal; anything else is external.
    /// </summary>
    internal static MailboxInfo ToMailbox(JsonElement m, IReadOnlySet<string> domains)
    {
        var primary = Str(m, "PrimarySmtpAddress");
        var aliases = m.TryGetProperty("EmailAddresses", out var addresses) && addresses.ValueKind == JsonValueKind.Array
            ? addresses.EnumerateArray()
                .Select(a => a.GetString() ?? "")
                .Where(a => a.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase))
                .Select(a => a[5..])
                .Where(a => !string.Equals(a, primary, StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];

        var smtpForward = Str(m, "ForwardingSmtpAddress") is { Length: > 0 } s
            ? s.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) ? s[5..] : s
            : null;
        var recipientForward = Str(m, "ForwardingAddress") is { Length: > 0 } r ? r : null;
        var forwarding = smtpForward is not null ? (IsInternal(smtpForward, domains) ? ForwardingKind.Internal : ForwardingKind.External)
            : recipientForward is not null ? ForwardingKind.Internal
            : ForwardingKind.None;

        var type = Str(m, "RecipientTypeDetails") ?? "";
        return new MailboxInfo(
            Type: type.EndsWith("Mailbox", StringComparison.Ordinal) && type.Length > "Mailbox".Length ? type[..^"Mailbox".Length] : type,
            PrimaryAddress: primary,
            Aliases: aliases,
            Forwarding: forwarding,
            ForwardingTo: smtpForward ?? recipientForward,
            KeepsCopyWhenForwarding: Bool(m, "DeliverToMailboxAndForward"),
            LitigationHold: Bool(m, "LitigationHoldEnabled"),
            ArchiveEnabled: string.Equals(Str(m, "ArchiveStatus"), "Active", StringComparison.OrdinalIgnoreCase),
            HiddenFromAddressList: Bool(m, "HiddenFromAddressListsEnabled"),
            QuotaBytes: MailDetails.ParseBytes(Str(m, "ProhibitSendReceiveQuota")));
    }

    /// <summary>A Get-CASMailbox row as <see cref="EmailApps"/>; SMTP sending left unset follows the organization.</summary>
    /// <summary>A row of Entra's MFA registration report.</summary>
    internal static UserSecurity ToSecurity(JsonElement r) => new(
        Bool(r, "isMfaRegistered"),
        Bool(r, "isAdmin"),
        r.TryGetProperty("methodsRegistered", out var m) && m.ValueKind == JsonValueKind.Array ? m.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray() : [],
        Str(r, "userPreferredMethodForSecondaryAuthentication") is { } preferred && preferred != "none" ? preferred : null);

    internal static EmailApps ToEmailApps(JsonElement c, bool orgSmtpOn)
    {
        bool On(string name) => !c.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.False;
        bool? smtp = c.TryGetProperty("SmtpClientAuthenticationDisabled", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? s.ValueKind == JsonValueKind.False
            : null;
        return new EmailApps(On("OWAEnabled"), On("MAPIEnabled"), On("ActiveSyncEnabled"), On("PopEnabled"), On("ImapEnabled"), On("EwsEnabled"), smtp, orgSmtpOn);
    }

    static bool IsInternal(string address, IReadOnlySet<string> domains)
    {
        var at = address.LastIndexOf('@');
        if (at < 0)
            return true;
        var domain = address[(at + 1)..];
        return domains.Contains(domain) || domains.Any(d => domain.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    internal static UserSummary ToSummary(JsonElement u, IReadOnlyDictionary<string, string> skuNames) => new(
        Id: u.GetProperty("id").GetString()!,
        DisplayName: Str(u, "displayName") ?? Str(u, "userPrincipalName") ?? "",
        UserPrincipalName: Str(u, "userPrincipalName") ?? "",
        Mail: Str(u, "mail"),
        Department: Str(u, "department"),
        JobTitle: Str(u, "jobTitle"),
        AccountEnabled: u.TryGetProperty("accountEnabled", out var e) && e.ValueKind == JsonValueKind.True,
        Kind: string.Equals(Str(u, "userType"), "Guest", StringComparison.OrdinalIgnoreCase) ? UserKind.Guest : UserKind.Member,
        CreatedAt: Date(u, "createdDateTime") ?? DateTimeOffset.MinValue,
        LastSignIn: u.TryGetProperty("signInActivity", out var activity) && activity.ValueKind == JsonValueKind.Object
            ? Latest(Date(activity, "lastSignInDateTime"), Date(activity, "lastNonInteractiveSignInDateTime"))
            : null,
        Licenses: u.TryGetProperty("assignedLicenses", out var licenses) && licenses.ValueKind == JsonValueKind.Array
            ? licenses.EnumerateArray()
                .Select(l => l.GetProperty("skuId").GetString()!)
                .Select(id => skuNames.GetValueOrDefault(id, id))
                .ToArray()
            : []);

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);
}
