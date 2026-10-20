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
        var mailboxes = new Dictionary<string, MailboxInfo>(StringComparer.Ordinal);
        foreach (var json in store.All(DirectorySync.Mailboxes))
        {
            using var mailbox = JsonDocument.Parse(json);
            if (Str(mailbox.RootElement, "ExternalDirectoryObjectId") is { } owner)
                mailboxes[owner] = ToMailbox(mailbox.RootElement, domains);
        }

        var stored = store.All(DirectorySync.Users);
        var users = new List<UserSummary>(stored.Count);
        foreach (var json in stored)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var user = JsonDocument.Parse(json);
            var summary = ToSummary(user.RootElement, skuNames);
            users.Add(mailboxes.TryGetValue(summary.Id, out var mailboxInfo) ? summary with { Mailbox = mailboxInfo } : summary);
        }
        return users;
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
            return groups;
        }, cancellationToken);

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
            Members: g.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Object
                ? m.EnumerateObject().Select(p => new GroupMember(p.Name, p.Value.GetString() ?? "unknown")).ToArray()
                : []);
    }

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
            HiddenFromAddressList: Bool(m, "HiddenFromAddressListsEnabled"));
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
