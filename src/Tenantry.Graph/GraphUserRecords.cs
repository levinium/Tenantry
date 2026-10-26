using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;
using Tenantry.Core.Licensing;

namespace Tenantry.Graph;

/// <summary>Live record-page reads from Graph (and Exchange and the SharePoint admin site, where Graph falls short).</summary>
public sealed partial class GraphUserRecords(GraphClient graph, IEntityStore store, ICmdletRunner? exchange = null, SharePointAdmin? sharePoint = null)
    : IUserRecords
{
    public async Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default)
    {
        if (exchange is null)
            throw new InvalidOperationException("Exchange Online isn't connected for this tenant.");
        var access = await DirectorySync.ReadMailboxAccessAsync(exchange, userId, address, cancellationToken);
        var mailbox = await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-Mailbox",
            new Dictionary<string, object?> { ["Identity"] = address }, cancellationToken, ["GrantSendOnBehalfTo"]);

        // Each trustee once, with everything they can do, in the order Exchange listed them.
        var rights = new Dictionary<string, List<MailboxRight>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? trustee, MailboxRight right)
        {
            if (string.IsNullOrEmpty(trustee))
                return;
            if (!rights.TryGetValue(trustee, out var list))
                rights[trustee] = list = [];
            if (!list.Contains(right))
                list.Add(right);
        }
        foreach (var t in access["fullAccess"]!.AsArray())
            Add(t?.GetValue<string>(), MailboxRight.FullAccess);
        foreach (var t in access["sendAs"]!.AsArray())
            Add(t?.GetValue<string>(), MailboxRight.SendAs);
        if (mailbox.Count > 0 && mailbox[0].TryGetProperty("GrantSendOnBehalfTo", out var onBehalf) && onBehalf.ValueKind == JsonValueKind.Array)
            foreach (var t in onBehalf.EnumerateArray())
                Add(t.GetString(), MailboxRight.SendOnBehalf);
        return rights.Select(r => new MailboxDelegate(r.Key, r.Value)).ToArray();
    }

    public async Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var select = string.Join(',', UserDetails.Properties.Append("externalUserState").Append("externalUserStateChangeDateTime"));
        var user = await graph.GetAsync($"users/{userId}?$select={select}&$expand=manager($select=id,displayName)", cancellationToken);
        var text = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in UserDetails.Properties)
        {
            if (!user.TryGetProperty(property, out var value))
                continue;
            text[property] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                // businessPhones is a list; the editor works on its first entry.
                JsonValueKind.Array => value.EnumerateArray().Select(v => v.GetString()).FirstOrDefault(s => !string.IsNullOrEmpty(s)),
                _ => null,
            };
        }
        var manager = user.TryGetProperty("manager", out var m) && m.ValueKind == JsonValueKind.Object ? m : (JsonElement?)null;
        return new UserDetails(
            userId,
            text,
            manager?.GetProperty("id").GetString(),
            manager is { } mm && mm.TryGetProperty("displayName", out var n) ? n.GetString() : null,
            user.TryGetProperty("externalUserState", out var state) ? state.GetString() : null,
            user.TryGetProperty("externalUserStateChangeDateTime", out var changed) && changed.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(changed.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                : null);
    }

    /// <summary>
    /// The user's licenses with where each comes from (licenseAssignmentStates: direct or a
    /// group, and any error), the tenant's licenses with counts from the local copy, and the
    /// enterprise apps they're assigned to.
    /// </summary>
    public async Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default)
    {
        var catalog = Catalog();
        var names = catalog.ToDictionary(s => s.SkuId, s => s.Name, StringComparer.OrdinalIgnoreCase);
        var groupNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var json in store.All(DirectorySync.Groups))
        {
            using var g = JsonDocument.Parse(json);
            if (g.RootElement.TryGetProperty("displayName", out var n) && n.GetString() is { } name)
                groupNames[g.RootElement.GetProperty("id").GetString()!] = name;
        }

        var user = await graph.GetAsync($"users/{userId}?$select=usageLocation,licenseAssignmentStates", cancellationToken);
        var assignments = new List<LicenseAssignment>();
        if (user.TryGetProperty("licenseAssignmentStates", out var states) && states.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in states.EnumerateArray())
            {
                var skuId = s.GetProperty("skuId").GetString()!;
                var group = s.TryGetProperty("assignedByGroup", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
                var error = s.TryGetProperty("error", out var e) && e.GetString() is { } code && code != "None" ? code : null;
                var disabled = s.TryGetProperty("disabledPlans", out var d) ? d.EnumerateArray().Select(p => p.GetString()!).ToArray() : [];
                assignments.Add(new LicenseAssignment(skuId, names.GetValueOrDefault(skuId, LicenseNames.For(skuId, null)),
                    group, group is null ? null : groupNames.GetValueOrDefault(group, "a group"), error, disabled));
            }
        }

        var apps = new List<AppAssignment>();
        await foreach (var a in graph.GetAllAsync($"users/{userId}/appRoleAssignments?$select=id,resourceDisplayName,resourceId,createdDateTime", cancellationToken))
        {
            apps.Add(new AppAssignment(a.GetProperty("id").GetString()!, a.TryGetProperty("resourceDisplayName", out var n) ? n.GetString() ?? "An app" : "An app",
                a.GetProperty("resourceId").GetString()!,
                a.TryGetProperty("createdDateTime", out var c) && c.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(c.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null));
        }

        return new UserLicensing(
            user.TryGetProperty("usageLocation", out var location) && location.ValueKind == JsonValueKind.String ? location.GetString() : null,
            assignments, catalog, apps);
    }

    /// <summary>The tenant's licenses from the local copy (kept current by sync and by license changes).</summary>
    IReadOnlyList<LicenseSku> Catalog() =>
        store.All(DirectorySync.Skus)
            .Select(json => JsonDocument.Parse(json).RootElement)
            .Where(s => !s.TryGetProperty("appliesTo", out var to) || to.GetString() != "Company")
            .Select(s =>
            {
                var id = s.GetProperty("skuId").GetString()!;
                var total = s.TryGetProperty("prepaidUnits", out var p) && p.TryGetProperty("enabled", out var en) ? en.GetInt32() : 0;
                var used = s.TryGetProperty("consumedUnits", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 0;
                var plans = s.TryGetProperty("servicePlans", out var sp) && sp.ValueKind == JsonValueKind.Array
                    ? sp.EnumerateArray()
                        .Where(x => !x.TryGetProperty("appliesTo", out var to) || to.GetString() != "Company")
                        .Select(x => new LicensePlan(x.GetProperty("servicePlanId").GetString()!,
                            LicenseNames.PlanName(x.GetProperty("servicePlanId").GetString()!, x.TryGetProperty("servicePlanName", out var pn) ? pn.GetString() : null)))
                        .OrderBy(x => x.Name, StringComparer.CurrentCulture)
                        .ToArray()
                    : [];
                return new LicenseSku(id, LicenseNames.For(id, s.TryGetProperty("skuPartNumber", out var code) ? code.GetString() : null), total, used, plans);
            })
            .OrderBy(s => s.Name, StringComparer.CurrentCulture)
            .ToArray();

    public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(store.All(DirectorySync.Domains)
            .Select(j => JsonDocument.Parse(j).RootElement)
            .Where(d => !d.TryGetProperty("isVerified", out var v) || v.ValueKind != JsonValueKind.False)
            .Select(d => d.GetProperty("id").GetString()!)
            .OrderBy(d => d.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    /// <summary>
    /// Three Exchange reads, side by side: the mailbox (addresses, forwarding, address list,
    /// quota), its automatic replies, and its size. A size that can't be read (a brand-new
    /// mailbox) leaves storage unknown rather than failing the tab.
    /// </summary>
    public async Task<MailDetails> GetMailAsync(string userId, string address, CancellationToken cancellationToken = default)
    {
        if (exchange is null)
            throw new InvalidOperationException("Exchange Online isn't connected for this tenant.");
        var identity = new Dictionary<string, object?> { ["Identity"] = address };
        var mailboxTask = exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-Mailbox", identity, cancellationToken,
            [.. DirectorySync.MailboxSelect, .. SettingsSelect]);
        var settingsTask = ReadSettingsPartsAsync(identity, cancellationToken);
        var replyTask = exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-MailboxAutoReplyConfiguration", identity, cancellationToken,
            ["AutoReplyState", "StartTime", "EndTime", "InternalMessage", "ExternalMessage", "ExternalAudience"]);
        var statsTask = ReadStatisticsAsync(identity, cancellationToken);
        var mailbox = (await mailboxTask).FirstOrDefault();
        if (mailbox.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Exchange didn't return this mailbox.");
        var reply = (await replyTask).FirstOrDefault();
        var stats = await statsTask;

        // The same reading of addresses and forwarding as the list uses.
        var domains = await GetDomainsAsync(cancellationToken);
        var info = CachedTenantDirectory.ToMailbox(mailbox, domains.ToHashSet(StringComparer.OrdinalIgnoreCase));
        string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
        var smtpForward = Text(mailbox, "ForwardingSmtpAddress") is { Length: > 0 };

        return new MailDetails(
            PrimaryAddress: info.PrimaryAddress ?? address,
            Aliases: info.Aliases,
            ForwardingTo: info.ForwardingTo,
            ForwardingIsRecipient: info.ForwardingTo is not null && !smtpForward,
            KeepCopy: info.KeepsCopyWhenForwarding,
            HiddenFromAddressList: info.HiddenFromAddressList,
            AutoReply: ToAutoReply(reply),
            UsedBytes: MailDetails.ParseBytes(Text(stats, "TotalItemSize")),
            QuotaBytes: MailDetails.ParseBytes(Text(mailbox, "ProhibitSendReceiveQuota")),
            WarningBytes: MailDetails.ParseBytes(Text(mailbox, "IssueWarningQuota")),
            Items: long.TryParse(Text(stats, "ItemCount"), out var items) ? items : null,
            Domains: domains)
        {
            Settings = await ToSettingsAsync(mailbox, address, await settingsTask, cancellationToken),
        };
    }

    /// <summary>The Get-Mailbox properties behind the Mail tab's settings (beyond the list's).</summary>
    static readonly string[] SettingsSelect =
    [
        "IssueWarningQuota", "RecipientLimits", "MaxSendSize", "MaxReceiveSize", "RequireSenderAuthenticationEnabled",
        "AcceptMessagesOnlyFromSendersOrMembers", "AutoExpandingArchiveEnabled", "LitigationHoldDuration", "LitigationHoldDate",
        "LitigationHoldOwner", "RetentionComment", "RetentionPolicy",
    ];

    /// <summary>Email apps, the organization's SMTP-sending default and the retention policies; each optional.</summary>
    async Task<(JsonElement Cas, bool OrgSmtpOn, IReadOnlyList<string> Policies)> ReadSettingsPartsAsync(Dictionary<string, object?> identity, CancellationToken cancellationToken)
    {
        async Task<IReadOnlyList<JsonElement>> Try(string cmdlet, Dictionary<string, object?>? parameters, string[] select)
        {
            try
            {
                return await exchange!.InvokeAsync(CmdletEndpoint.Exchange, cmdlet, parameters, cancellationToken, select);
            }
            catch (CmdletException)
            {
                return [];
            }
        }
        var cas = Try("Get-CASMailbox", identity, DirectorySync.CasSelect);
        var transport = Try("Get-TransportConfig", null, ["SmtpClientAuthenticationDisabled"]);
        var policies = Try("Get-RetentionPolicy", null, ["Name"]);
        var org = (await transport).FirstOrDefault();
        return ((await cas).FirstOrDefault(),
            !(org.ValueKind == JsonValueKind.Object && org.TryGetProperty("SmtpClientAuthenticationDisabled", out var d) && d.ValueKind == JsonValueKind.True),
            (await policies).Select(p => p.TryGetProperty("Name", out var n) ? n.GetString() : null).OfType<string>().Order(StringComparer.CurrentCulture).ToArray());
    }

    async Task<MailboxSettings> ToSettingsAsync(JsonElement m, string address, (JsonElement Cas, bool OrgSmtpOn, IReadOnlyList<string> Policies) parts,
        CancellationToken cancellationToken)
    {
        string? Text(string name) => m.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? null : v.ToString() : null;
        bool Flag(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var archiveOn = string.Equals(Text("ArchiveStatus"), "Active", StringComparison.OrdinalIgnoreCase);
        long? archiveUsed = null;
        if (archiveOn)
        {
            try
            {
                var stats = await exchange!.InvokeAsync(CmdletEndpoint.Exchange, "Get-MailboxStatistics",
                    new Dictionary<string, object?> { ["Identity"] = address, ["Archive"] = true }, cancellationToken, ["TotalItemSize"]);
                archiveUsed = stats.Count > 0 && stats[0].TryGetProperty("TotalItemSize", out var s) ? MailDetails.ParseBytes(s.GetString()) : null;
            }
            catch (CmdletException)
            {
                // A new archive can take a while to be readable; its size stays unknown.
            }
        }
        var holdDuration = Text("LitigationHoldDuration");
        int? holdDays = holdDuration is null || holdDuration.Equals("Unlimited", StringComparison.OrdinalIgnoreCase) ? null
            : TimeSpan.TryParse(holdDuration, System.Globalization.CultureInfo.InvariantCulture, out var span) ? (int)span.TotalDays
            : int.TryParse(holdDuration, out var plain) ? plain : null;
        var senders = m.TryGetProperty("AcceptMessagesOnlyFromSendersOrMembers", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(s => s.GetString()).OfType<string>().ToArray()
            : [];
        return new MailboxSettings(
            RecipientLimit: Text("RecipientLimits") ?? "Unlimited",
            MaxSendBytes: MailDetails.ParseBytes(Text("MaxSendSize")),
            MaxReceiveBytes: MailDetails.ParseBytes(Text("MaxReceiveSize")),
            OnlyInternalSenders: Flag("RequireSenderAuthenticationEnabled"),
            AllowedSenders: senders,
            Apps: parts.Cas.ValueKind == JsonValueKind.Object ? CachedTenantDirectory.ToEmailApps(parts.Cas, parts.OrgSmtpOn)
                : new EmailApps(true, true, true, true, true, true, null, parts.OrgSmtpOn),
            ArchiveOn: archiveOn,
            AutoExpandingArchive: Flag("AutoExpandingArchiveEnabled"),
            ArchiveUsedBytes: archiveUsed,
            HoldOn: Flag("LitigationHoldEnabled"),
            HoldDays: holdDays,
            HoldNote: Text("RetentionComment") is { Length: > 0 } note ? note : null,
            HoldSince: DateTimeOffset.TryParse(Text("LitigationHoldDate"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var since) ? since : null,
            HoldBy: Text("LitigationHoldOwner") is { Length: > 0 } by ? by : null,
            RetentionPolicy: Text("RetentionPolicy") is { Length: > 0 } policy ? policy : null,
            RetentionPolicies: parts.Policies);
    }

    async Task<JsonElement> ReadStatisticsAsync(Dictionary<string, object?> identity, CancellationToken cancellationToken)
    {
        try
        {
            return (await exchange!.InvokeAsync(CmdletEndpoint.Exchange, "Get-MailboxStatistics", identity, cancellationToken,
                ["TotalItemSize", "ItemCount"])).FirstOrDefault();
        }
        catch (CmdletException)
        {
            return default;
        }
    }

    /// <summary>Get-MailboxAutoReplyConfiguration as an <see cref="AutoReply"/> (messages as plain text).</summary>
    internal static AutoReply ToAutoReply(JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object)
            return AutoReply.Off;
        string? Text(string name) => reply.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        DateTimeOffset? When(string name) => DateTimeOffset.TryParse(Text(name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        var state = Text("AutoReplyState") switch
        {
            "Enabled" => AutoReplyState.On,
            "Scheduled" => AutoReplyState.Scheduled,
            _ => AutoReplyState.Off,
        };
        var audience = Text("ExternalAudience") switch
        {
            "None" => AutoReplyAudience.None,
            "Known" => AutoReplyAudience.Known,
            _ => AutoReplyAudience.All,
        };
        return new AutoReply(state, state == AutoReplyState.Scheduled ? When("StartTime") : null, state == AutoReplyState.Scheduled ? When("EndTime") : null,
            AutoReply.PlainText(Text("InternalMessage")), AutoReply.PlainText(Text("ExternalMessage")), audience);
    }
}
