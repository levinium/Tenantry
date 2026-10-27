namespace Tenantry.Core.Directory;

/// <summary>Who a SharePoint principal is, as the site page groups them.</summary>
public enum PrincipalKind
{
    Person,
    Guest,

    /// <summary>A Microsoft 365 group's members (a group-connected site's members).</summary>
    GroupMembers,

    /// <summary>A Microsoft 365 group's owners.</summary>
    GroupOwners,

    /// <summary>An Entra security group.</summary>
    SecurityGroup,

    /// <summary>"Everyone" (guests included).</summary>
    Everyone,

    /// <summary>"Everyone except external users": every person in the organization.</summary>
    EveryoneExceptExternal,

    /// <summary>SharePoint's own accounts (search crawler, apps).</summary>
    System,
}

/// <summary>Someone (or a group of people) with access, as SharePoint names them.</summary>
/// <param name="Login">SharePoint's claim ("i:0#.f|membership|ada@contoso.example").</param>
/// <param name="ObjectId">The Entra ID behind a group claim, or null.</param>
/// <param name="Upn">A person's sign-in name, or null.</param>
public sealed record SitePrincipal(string Name, string Login, PrincipalKind Kind, string? ObjectId = null, string? Upn = null);

/// <summary>A SharePoint group on the site (Owners, Members, Visitors, or one of the site's own).</summary>
/// <param name="Role">"Owners", "Members", "Visitors", or null for another group.</param>
public sealed record SiteGroup(int Id, string Name, string? Role, IReadOnlyList<SitePrincipal> Members);

public sealed record SitePermissions(IReadOnlyList<SiteGroup> Groups, IReadOnlyList<SitePrincipal> Admins);

/// <summary>What kind of sharing link: who it lets in.</summary>
public enum LinkKind
{
    /// <summary>Anyone with the link, no sign-in.</summary>
    Anyone,

    /// <summary>People in the organization with the link.</summary>
    Organization,

    /// <summary>Specific people.</summary>
    SpecificPeople,
}

/// <summary>One sharing link on an item, from the group SharePoint keeps for it.</summary>
/// <param name="CanEdit">The link lets people edit, not just view.</param>
/// <param name="People">For a specific-people link, who it's for.</param>
public sealed record SharingLink(LinkKind Kind, bool CanEdit, IReadOnlyList<SitePrincipal> People);

public sealed record SiteSharing(IReadOnlyList<SharingLink> Links, IReadOnlyList<SitePrincipal> Guests);

/// <summary>A document library or list on the site.</summary>
public sealed record SiteLibrary(string Id, string Name, int Items, DateTimeOffset? LastModified, bool IsLibrary, string? Url);

/// <summary>A recently changed file.</summary>
public sealed record RecentFile(string Name, DateTimeOffset? Modified, string? By, string Library);

/// <summary>Microsoft's 30-day usage report for one site or OneDrive.</summary>
public sealed record SiteUsage(long Files, long ActiveFiles, long? PageViews, long? VisitedPages, DateOnly? LastActivity);

/// <summary>
/// SharePoint shows what's inside a site only to its members and admins, not to a SharePoint or
/// Global Administrator as such: the fix is to add yourself as a site admin.
/// </summary>
public sealed class SiteAccessException() : UnauthorizedAccessException(
    "SharePoint shows what's inside a site only to its members and admins, and you aren't one here. Add yourself as a site admin to see it.");

/// <summary>The site page's reads, live from SharePoint (the site's own REST API) and Graph's usage reports.</summary>
public interface ISiteRecords
{
    Task<SitePermissions> GetPermissionsAsync(SiteSummary site, CancellationToken cancellationToken = default);

    Task<SiteSharing> GetSharingAsync(SiteSummary site, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SiteLibrary>> GetLibrariesAsync(SiteSummary site, CancellationToken cancellationToken = default);

    /// <summary>The latest changed files in a library; unavailable when it's too big for SharePoint to sort.</summary>
    Task<Available<IReadOnlyList<RecentFile>>> GetRecentFilesAsync(SiteSummary site, SiteLibrary library, CancellationToken cancellationToken = default);

    /// <summary>Microsoft's usage report; unavailable without a reports role or with names concealed.</summary>
    Task<Available<SiteUsage>> GetUsageAsync(SiteSummary site, CancellationToken cancellationToken = default);

    /// <summary>The organization's external sharing level (<see cref="SiteSummary.Sharing"/>'s values): no site can be more open.</summary>
    Task<int> GetOrganizationSharingAsync(CancellationToken cancellationToken = default) => Task.FromResult(2);
}
