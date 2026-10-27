using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's site pages: the group's owners and members, a few sharing links and guests, its
/// libraries and their latest files, and 30 days of activity, from each site's own seed. Sites
/// with no owner stand in for ones the admin can't see inside, as happens for real.
/// </summary>
public sealed partial class DemoUserRecords : ISiteRecords, IDeletedSiteRecords
{
    void CanSeeInside(SiteSummary site)
    {
        if (site.IsArchived)
            throw new UnauthorizedAccessException("It's archived, so nothing inside it can be read until it's reactivated.");
        if (site.OwnerName is null && site.Kind != SiteKind.System && !SelfAdmin.Contains(site.Id))
            throw new SiteAccessException();
    }

    /// <summary>The demo organization lets sites share with new and existing guests, but not with anyone.</summary>
    public Task<int> GetOrganizationSharingAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);

    /// <summary>The demo's signed-in admin.</summary>
    public static string Account => $"admin@{DemoTenantDirectory.Domain}";

    /// <summary>Sites the demo's admin made themself a site admin of: they can see inside them now.</summary>
    readonly HashSet<string> SelfAdmin = [];

    /// <summary>Deleted sites as they were, to put back on restore.</summary>
    readonly Dictionary<string, SiteSummary> _removedSites = [];

    /// <summary>Applies a site change to the demo's sites; null when it isn't one.</summary>
    public async Task<ApplyResult?> ApplySiteAsync(ChangeOp change, CancellationToken cancellationToken = default)
    {
        switch (change)
        {
            case SetSiteAdmin admin:
                if (admin.New && string.Equals(admin.Person.Address, Account, StringComparison.OrdinalIgnoreCase))
                    SelfAdmin.Add(admin.Site.Id);
                return new ApplyResult(ChangeOutcome.Applied);
            case SetSiteGroupMember:
                return new ApplyResult(ChangeOutcome.Applied);
            case SetSiteSetting setting:
                var values = System.Text.Json.Nodes.JsonNode.Parse(setting.Values)!.AsObject();
                directory.UpdateSite(setting.Site.Id, s => values.Aggregate(s, (site, v) => v.Key switch
                {
                    SiteSettings.Sharing => site with { Sharing = v.Value!.GetValue<int>() },
                    SiteSettings.LinkType => site with { LinkType = v.Value!.GetValue<int>() },
                    SiteSettings.LinkPermission => site with { LinkPermission = v.Value!.GetValue<int>() },
                    SiteSettings.GuestExpiryOverride when !v.Value!.GetValue<bool>() => site with { GuestExpiryDays = null },
                    SiteSettings.GuestExpiryDays => site with { GuestExpiryDays = v.Value!.GetValue<int>() },
                    SiteSettings.Quota => site with { StorageQuotaMb = v.Value!.GetValue<long>() },
                    SiteSettings.QuotaWarning => site with { StorageWarningMb = v.Value!.GetValue<long>() },
                    SiteSettings.Lock => site with { Lock = v.Value!.GetValue<string>() },
                    _ => site,
                }));
                return new ApplyResult(ChangeOutcome.Applied);
            case SetSiteArchived archive:
                directory.UpdateSite(archive.Site.Id, s => s with { Archive = archive.Archive ? "RecentlyArchived" : "NotArchived" });
                return new ApplyResult(ChangeOutcome.Applied);
            case DeleteSite delete:
                if ((await directory.GetSitesAsync(cancellationToken)).FirstOrDefault(s => s.Id == delete.Site.Id) is { } gone)
                {
                    _removedSites[gone.Id] = gone;
                    _deletedSites.Add(new DeletedSite(gone.Id, gone.Url, gone.Title, DateTimeOffset.UtcNow, delete.GroupId is null ? 93 : 30, gone.StorageQuotaMb, delete.GroupId));
                }
                directory.UpdateSite(delete.Site.Id, _ => null);
                return new ApplyResult(ChangeOutcome.Applied);
            case RestoreSite restore:
                _deletedSites.RemoveAll(d => d.Id == restore.Site.Id);
                if (_removedSites.Remove(restore.Site.Id, out var back))
                    directory.AddSite(back);
                return new ApplyResult(ChangeOutcome.Applied);
            case CreateSite create:
                directory.AddSite(new SiteSummary(Guid.NewGuid().ToString(), create.SiteUrl, create.Site.Name,
                    create.Kind == SiteKind.TeamSite ? "GROUP#0" : "SITEPAGEPUBLISHING#0", create.Kind, create.Owners.FirstOrDefault()?.Name, null, null, null,
                    false, false, null, 0, 25L * 1024 * 1024, 0, "Unlock", "NotArchived", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                return new ApplyResult(ChangeOutcome.Applied);
            default:
                return null;
        }
    }

    readonly List<DeletedSite> _deletedSites = [];

    public async Task<IReadOnlyList<DeletedSite>> GetDeletedSitesAsync(CancellationToken cancellationToken = default)
    {
        if (_deletedSites.Count == 0)
        {
            var now = DateTimeOffset.UtcNow;
            var host = (await directory.GetSitesAsync(cancellationToken)).First(s => s.Kind != SiteKind.OneDrive).Url;
            host = host[..host.IndexOf("/sites/", StringComparison.Ordinal)];
            _deletedSites.Add(new DeletedSite(Guid.NewGuid().ToString(), $"{host}/sites/summer-event-2025", "summer-event-2025", now.AddDays(-12), 81, 25L * 1024 * 1024, null));
            _deletedSites.Add(new DeletedSite(Guid.NewGuid().ToString(), $"{host}/sites/vendor-review", "Vendor Review", now.AddDays(-4), 26, 25L * 1024 * 1024, "demo-deleted-group"));
        }
        return _deletedSites.ToArray();
    }

    async Task<UserSummary[]> PeopleAsync(SiteSummary site, int count, CancellationToken cancellationToken)
    {
        var random = new Random(StableSeed(site.Id) + 71);
        var members = (await directory.GetUsersAsync(cancellationToken)).Where(u => u.Kind == UserKind.Member).ToArray();
        return [.. Enumerable.Range(0, count).Select(_ => members[random.Next(members.Length)]).DistinctBy(u => u.Id)];
    }

    static SitePrincipal Person(UserSummary u) => new(u.DisplayName, $"i:0#.f|membership|{u.UserPrincipalName}", PrincipalKind.Person, Upn: u.UserPrincipalName);

    public async Task<SitePermissions> GetPermissionsAsync(SiteSummary site, CancellationToken cancellationToken = default)
    {
        CanSeeInside(site);
        var admins = await PeopleAsync(site, 2, cancellationToken);
        if (site.Kind == SiteKind.OneDrive)
        {
            var owner = (await directory.GetUsersAsync(cancellationToken)).FirstOrDefault(u => site.OwnerLogin?.EndsWith(u.UserPrincipalName, StringComparison.OrdinalIgnoreCase) == true);
            return new SitePermissions([], owner is null ? [] : [Person(owner)]);
        }
        var groupClaim = site.GroupId is { } g ? $"c:0o.c|federateddirectoryclaimprovider|{g}" : null;
        SiteGroup Group(int id, string name, string role, params SitePrincipal[] members) => new(id, $"{site.Title} {name}", role, members);
        return new SitePermissions(
        [
            Group(3, "Owners", "Owners", groupClaim is null ? [.. admins.Select(Person)] : [new SitePrincipal($"{site.Title} Owners", groupClaim + "_o", PrincipalKind.GroupOwners, site.GroupId)]),
            Group(5, "Members", "Members",
                [
                    .. groupClaim is null ? [] : new[] { new SitePrincipal($"{site.Title} Members", groupClaim, PrincipalKind.GroupMembers, site.GroupId) },
                    .. site.Sharing > 0 ? new[] { new SitePrincipal("Everyone except external users", "c:0-.f|rolemanager|spo-grid-all-users/x", PrincipalKind.EveryoneExceptExternal) } : [],
                ]),
            Group(4, "Visitors", "Visitors"),
        ], groupClaim is null ? [.. admins.Select(Person)] : [new SitePrincipal($"{site.Title} Owners", groupClaim + "_o", PrincipalKind.GroupOwners, site.GroupId)]);
    }

    public async Task<SiteSharing> GetSharingAsync(SiteSummary site, CancellationToken cancellationToken = default)
    {
        CanSeeInside(site);
        var random = new Random(StableSeed(site.Id) + 72);
        var people = await PeopleAsync(site, 3, cancellationToken);
        var links = new List<SharingLink>();
        for (var i = random.Next(0, 4); i > 0; i--)
            links.Add(new SharingLink(LinkKind.Organization, random.NextDouble() < 0.4, []));
        if (random.NextDouble() < 0.5)
            links.Add(new SharingLink(LinkKind.SpecificPeople, true, [.. people.Take(2).Select(Person)]));
        var guests = new List<SitePrincipal>();
        if (site.Sharing > 0)
        {
            links.Add(new SharingLink(LinkKind.Anyone, false, []));
            guests.Add(new SitePrincipal("Sam Rivera", "i:0#.f|membership|sam_partner.example#ext#@northgate.example", PrincipalKind.Guest,
                Upn: "sam_partner.example#ext#@northgate.example"));
        }
        return new SiteSharing(links, guests);
    }

    public Task<IReadOnlyList<SiteLibrary>> GetLibrariesAsync(SiteSummary site, CancellationToken cancellationToken = default)
    {
        CanSeeInside(site);
        var random = new Random(StableSeed(site.Id) + 73);
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<SiteLibrary> libraries = site.Kind == SiteKind.OneDrive
            ? [new SiteLibrary("docs", "Documents", random.Next(10, 3000), now.AddHours(-random.Next(1, 200)), true, site.Url + "/Documents")]
            :
            [
                new SiteLibrary("docs", "Documents", random.Next(50, 9000), now.AddHours(-random.Next(1, 200)), true, site.Url + "/Shared Documents"),
                new SiteLibrary("pages", "Site Pages", random.Next(2, 40), now.AddDays(-random.Next(1, 90)), true, site.Url + "/SitePages"),
                new SiteLibrary("archive", "Archive", 7200, now.AddDays(-random.Next(100, 400)), true, site.Url + "/Archive"),
                new SiteLibrary("tasks", "Tasks", random.Next(0, 60), now.AddDays(-random.Next(1, 60)), false, site.Url + "/Lists/Tasks"),
            ];
        return Task.FromResult(libraries);
    }

    static readonly string[] FileNames =
        ["Budget 2027.xlsx", "Meeting notes.docx", "Floor plan.pdf", "Volunteer schedule.xlsx", "Annual report draft.docx", "Logo.png", "Event photos.zip", "Policy update.docx"];

    public async Task<Available<IReadOnlyList<RecentFile>>> GetRecentFilesAsync(SiteSummary site, SiteLibrary library, CancellationToken cancellationToken = default)
    {
        if (library.Items > 5000)
            return Available<IReadOnlyList<RecentFile>>.Because(
                $"{library.Name} has too many files ({library.Items:N0}) for SharePoint to list the newest here. The activity numbers above come from Microsoft's report.");
        var random = new Random(StableSeed(site.Id + library.Id) + 74);
        var people = await PeopleAsync(site, 4, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return new([.. Enumerable.Range(0, Math.Min(6, library.Items)).Select(i =>
            new RecentFile(FileNames[random.Next(FileNames.Length)], now.AddHours(-i * random.Next(2, 30)), people[random.Next(people.Length)].DisplayName, library.Name))
            .OrderByDescending(f => f.Modified)]);
    }

    public Task<Available<SiteUsage>> GetUsageAsync(SiteSummary site, CancellationToken cancellationToken = default)
    {
        var random = new Random(StableSeed(site.Id) + 75);
        if (site.IsArchived || site.LastActivity < DateTimeOffset.UtcNow.AddDays(-30))
            return Task.FromResult(Available<SiteUsage>.Because("Microsoft's usage report has no activity for it in the last 30 days."));
        var files = random.Next(20, 9000);
        return Task.FromResult(new Available<SiteUsage>(new SiteUsage(files, random.Next(0, Math.Min(files, 400)),
            site.Kind == SiteKind.OneDrive ? null : random.Next(10, 3000), site.Kind == SiteKind.OneDrive ? null : random.Next(2, 80),
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-random.Next(0, 5))))));
    }
}
