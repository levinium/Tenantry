using System.Net;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// The site page's reads (7b), from the site's own REST API (through <see cref="SharePointAdmin"/>)
/// and Graph's usage reports. Facts checked live:
/// <list type="bullet">
/// <item>A sharing link is a SharePoint group named <c>SharingLinks.{item}.{kind}.{link}</c>; its
/// kind says who the link lets in (Anonymous…, Organization…, Flexible) and whether it edits.</item>
/// <item>A group-connected site's Owners and Members groups hold the Microsoft 365 group as a claim
/// (<c>c:0o.c|federateddirectoryclaimprovider|{id}</c>, owners with <c>_o</c>).</item>
/// <item>Sorting a big library's items by date fails (SharePoint's list threshold): that library
/// says so instead of listing.</item>
/// </list>
/// </summary>
public sealed partial class GraphUserRecords : ISiteRecords
{
    SharePointAdmin SharePoint => sharePoint ?? throw new InvalidOperationException("SharePoint isn't connected for this tenant.");

    /// <summary>
    /// A read of a site's contents. SharePoint shows them only to the site's members and admins,
    /// not to a SharePoint or Global Administrator as such (seen live): say that, not "forbidden".
    /// </summary>
    static async Task<T> InsideAsync<T>(SiteSummary site, Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (SharePointException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException(site.IsArchived
                ? "It's archived, so nothing inside it can be read until it's reactivated."
                : "SharePoint shows what's inside a site only to its members and admins, and you aren't one here. Add yourself as a site admin to see it.");
        }
    }

    /// <summary>A SharePoint claim, sorted into who it means.</summary>
    internal static SitePrincipal Principal(string name, string login)
    {
        var lower = login.ToLowerInvariant();
        var last = login[(login.LastIndexOf('|') + 1)..];
        if (lower.StartsWith("c:0(.s|true", StringComparison.Ordinal))
            return new(name, login, PrincipalKind.Everyone);
        if (lower.Contains("spo-grid-all-users", StringComparison.Ordinal))
            return new(name, login, PrincipalKind.EveryoneExceptExternal);
        if (lower.StartsWith("c:0o.c|federateddirectoryclaimprovider|", StringComparison.Ordinal))
            return last.EndsWith("_o", StringComparison.OrdinalIgnoreCase)
                ? new(name, login, PrincipalKind.GroupOwners, last[..^2])
                : new(name, login, PrincipalKind.GroupMembers, last);
        if (lower.StartsWith("c:0t.c|tenant|", StringComparison.Ordinal))
            return new(name, login, PrincipalKind.SecurityGroup, last);
        if (lower.StartsWith("i:0#.f|membership|", StringComparison.Ordinal))
            return new(name, login, last.Contains("#ext#", StringComparison.OrdinalIgnoreCase) ? PrincipalKind.Guest : PrincipalKind.Person, Upn: last);
        if (lower.Contains("urn:spo:guest", StringComparison.Ordinal) || lower.Contains("urn%3aspo%3aguest", StringComparison.Ordinal))
            return new(name, login, PrincipalKind.Guest, Upn: last.Contains('@') ? last : null);
        return new(name, login, PrincipalKind.System);
    }

    static SitePrincipal[] Principals(JsonElement users) =>
        users.ValueKind != JsonValueKind.Array ? [] :
        [
            .. users.EnumerateArray()
                .Select(u => Principal(Text(u, "Title") ?? Text(u, "LoginName") ?? "Someone", Text(u, "LoginName") ?? ""))
                .Where(p => p.Kind != PrincipalKind.System),
        ];

    static IEnumerable<JsonElement> Rows(JsonElement answer) =>
        answer.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    async Task<IReadOnlyList<JsonElement>> SiteGroupsAsync(SiteSummary site, CancellationToken cancellationToken) =>
        Rows(await SharePoint.GetSiteAsync($"{site.Url}/_api/web/SiteGroups?$select=Id,Title&$expand=Users&$top=5000", cancellationToken)).ToArray();

    public Task<SitePermissions> GetPermissionsAsync(SiteSummary site, CancellationToken cancellationToken = default) =>
        InsideAsync(site, () => ReadPermissionsAsync(site, cancellationToken));

    async Task<SitePermissions> ReadPermissionsAsync(SiteSummary site, CancellationToken cancellationToken)
    {
        var groups = SiteGroupsAsync(site, cancellationToken);
        var admins = SharePoint.GetSiteAsync($"{site.Url}/_api/web/SiteUsers?$filter=IsSiteAdmin eq true&$select=Title,LoginName", cancellationToken);
        // Which groups are the site's Owners, Members and Visitors (a OneDrive has none).
        var roles = new Dictionary<int, string>();
        try
        {
            var web = await SharePoint.GetSiteAsync($"{site.Url}/_api/web?$select=AssociatedOwnerGroup/Id,AssociatedMemberGroup/Id,AssociatedVisitorGroup/Id" +
                                                    "&$expand=AssociatedOwnerGroup,AssociatedMemberGroup,AssociatedVisitorGroup", cancellationToken);
            foreach (var (property, role) in new[] { ("AssociatedOwnerGroup", "Owners"), ("AssociatedMemberGroup", "Members"), ("AssociatedVisitorGroup", "Visitors") })
                if (web.TryGetProperty(property, out var g) && g.ValueKind == JsonValueKind.Object && g.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.Number)
                    roles[id.GetInt32()] = role;
        }
        catch (SharePointException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
        }
        var list = (await groups)
            .Where(g => !(Text(g, "Title") ?? "").StartsWith("SharingLinks.", StringComparison.Ordinal) && !(Text(g, "Title") ?? "").StartsWith("Limited Access", StringComparison.Ordinal))
            .Select(g =>
            {
                var id = g.TryGetProperty("Id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
                return new SiteGroup(id, Text(g, "Title") ?? "A group", roles.GetValueOrDefault(id), Principals(g.TryGetProperty("Users", out var u) ? u : default));
            })
            // Owners, Members, Visitors first; the site's own groups after, by name.
            .OrderBy(g => g.Role switch { "Owners" => 0, "Members" => 1, "Visitors" => 2, _ => 3 })
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new SitePermissions(list, Principals((await admins).TryGetProperty("value", out var a) ? a : default));
    }

    /// <summary>"SharingLinks.{item}.AnonymousEdit.{link}" → who it lets in and whether it edits.</summary>
    internal static (LinkKind Kind, bool CanEdit)? LinkOf(string groupTitle)
    {
        var parts = groupTitle.Split('.');
        if (parts.Length < 3 || parts[0] != "SharingLinks")
            return null;
        var kind = parts[2];
        return (kind.StartsWith("Anonymous", StringComparison.OrdinalIgnoreCase) ? LinkKind.Anyone
                : kind.StartsWith("Organization", StringComparison.OrdinalIgnoreCase) ? LinkKind.Organization
                : LinkKind.SpecificPeople,
            kind.EndsWith("Edit", StringComparison.OrdinalIgnoreCase) || kind.Equals("Flexible", StringComparison.OrdinalIgnoreCase));
    }

    public Task<SiteSharing> GetSharingAsync(SiteSummary site, CancellationToken cancellationToken = default) =>
        InsideAsync(site, () => ReadSharingAsync(site, cancellationToken));

    async Task<SiteSharing> ReadSharingAsync(SiteSummary site, CancellationToken cancellationToken)
    {
        var groups = SiteGroupsAsync(site, cancellationToken);
        var users = SharePoint.GetSiteAsync($"{site.Url}/_api/web/SiteUsers?$select=Title,LoginName&$top=5000", cancellationToken);
        var links = (await groups)
            .Select(g => (Link: LinkOf(Text(g, "Title") ?? ""), Group: g))
            .Where(x => x.Link is not null)
            .Select(x => new SharingLink(x.Link!.Value.Kind, x.Link.Value.CanEdit,
                x.Link.Value.Kind == LinkKind.SpecificPeople ? Principals(x.Group.TryGetProperty("Users", out var u) ? u : default) : []))
            .ToArray();
        var guests = Principals((await users).TryGetProperty("value", out var v) ? v : default).Where(p => p.Kind == PrincipalKind.Guest).ToArray();
        return new SiteSharing(links, guests);
    }

    public Task<IReadOnlyList<SiteLibrary>> GetLibrariesAsync(SiteSummary site, CancellationToken cancellationToken = default) =>
        InsideAsync(site, () => ReadLibrariesAsync(site, cancellationToken));

    async Task<IReadOnlyList<SiteLibrary>> ReadLibrariesAsync(SiteSummary site, CancellationToken cancellationToken)
    {
        var answer = await SharePoint.GetSiteAsync(
            $"{site.Url}/_api/web/lists?$select=Id,Title,ItemCount,BaseType,Hidden,LastItemModifiedDate,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=Hidden eq false",
            cancellationToken);
        var origin = new Uri(site.Url).GetLeftPart(UriPartial.Authority);
        return Rows(answer)
            .Select(l => new SiteLibrary(
                Text(l, "Id") ?? "",
                Text(l, "Title") ?? "A list",
                l.TryGetProperty("ItemCount", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0,
                When(l, "LastItemModifiedDate"),
                l.TryGetProperty("BaseType", out var b) && b.ValueKind == JsonValueKind.Number && b.GetInt32() == 1,
                l.TryGetProperty("RootFolder", out var r) && r.ValueKind == JsonValueKind.Object && Text(r, "ServerRelativeUrl") is { } path ? origin + path : null))
            // Libraries first, biggest first.
            .OrderBy(l => l.IsLibrary ? 0 : 1).ThenByDescending(l => l.Items)
            .ToArray();
    }

    public async Task<Available<IReadOnlyList<RecentFile>>> GetRecentFilesAsync(SiteSummary site, SiteLibrary library, CancellationToken cancellationToken = default)
    {
        try
        {
            var answer = await SharePoint.GetSiteAsync(
                $"{site.Url}/_api/web/lists(guid'{library.Id}')/items?$select=FileLeafRef,Modified,Editor/Title&$expand=Editor&$orderby=Modified desc&$top=10&$filter=FSObjType eq 0",
                cancellationToken);
            return new(Rows(answer).Select(f => new RecentFile(Text(f, "FileLeafRef") ?? "A file", When(f, "Modified"),
                f.TryGetProperty("Editor", out var e) && e.ValueKind == JsonValueKind.Object ? Text(e, "Title") : null, library.Name)).ToArray());
        }
        catch (SharePointException e) when (e.Status is HttpStatusCode.InternalServerError or HttpStatusCode.BadRequest)
        {
            return Available<IReadOnlyList<RecentFile>>.Because(
                $"{library.Name} has too many files ({library.Items:N0}) for SharePoint to list the newest here. The activity numbers above come from Microsoft's report.");
        }
    }

    (DateTimeOffset At, Dictionary<string, SiteUsage> Rows)? _siteUsage;
    (DateTimeOffset At, Dictionary<string, SiteUsage> Rows)? _driveUsage;

    public async Task<Available<SiteUsage>> GetUsageAsync(SiteSummary site, CancellationToken cancellationToken = default)
    {
        var oneDrive = site.Kind == SiteKind.OneDrive;
        var cached = oneDrive ? _driveUsage : _siteUsage;
        Dictionary<string, SiteUsage> rows;
        if (cached is { } c && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(30))
            rows = c.Rows;
        else
        {
            rows = new Dictionary<string, SiteUsage>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = oneDrive ? "getOneDriveUsageAccountDetail" : "getSharePointSiteUsageDetail";
                await foreach (var r in graph.GetAllAsync($"https://graph.microsoft.com/beta/reports/{path}(period='D30')?$format=application/json", cancellationToken))
                {
                    var usage = new SiteUsage(Long(r, "fileCount") ?? 0, Long(r, "activeFileCount") ?? 0, Long(r, "pageViewCount"), Long(r, "visitedPageCount"),
                        Text(r, "lastActivityDate") is { } d && DateOnly.TryParse(d, System.Globalization.CultureInfo.InvariantCulture, out var day) ? day : null);
                    // Matched by site ID, and a OneDrive also by its owner's sign-in name (its report has no address; checked live).
                    if (Text(r, "siteId") is { Length: > 0 } siteId)
                        rows[siteId] = usage;
                    if (oneDrive && Text(r, "ownerPrincipalName") is { Length: > 0 } ownerUpn)
                        rows[ownerUpn] = usage;
                }
            }
            catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                return Available<SiteUsage>.Because("Activity numbers need a role that can read Microsoft 365 usage reports.");
            }
            if (oneDrive)
                _driveUsage = (DateTimeOffset.UtcNow, rows);
            else
                _siteUsage = (DateTimeOffset.UtcNow, rows);
        }
        var owner = site.OwnerLogin is { } login ? login[(login.LastIndexOf('|') + 1)..] : null;
        var found = rows.GetValueOrDefault(site.Id) ?? (oneDrive && owner is not null ? rows.GetValueOrDefault(owner) : null);
        return found is not null ? new(found)
            : rows.Count > 0 && rows.Keys.All(k => !k.Contains('@', StringComparison.Ordinal) && !Guid.TryParse(k, out _))
                ? Available<SiteUsage>.Because("Microsoft conceals names in usage reports here; turn that off in the Microsoft 365 admin center (Settings › Org settings › Reports) to see activity.")
                : Available<SiteUsage>.Because("Microsoft's usage report has no activity for it in the last 30 days.");
    }
}
