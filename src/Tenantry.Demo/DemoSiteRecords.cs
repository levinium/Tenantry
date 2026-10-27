using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's site pages: the group's owners and members, a few sharing links and guests, its
/// libraries and their latest files, and 30 days of activity, from each site's own seed. Sites
/// with no owner stand in for ones the admin can't see inside, as happens for real.
/// </summary>
public sealed partial class DemoUserRecords : ISiteRecords
{
    static void CanSeeInside(SiteSummary site)
    {
        if (site.IsArchived)
            throw new UnauthorizedAccessException("It's archived, so nothing inside it can be read until it's reactivated.");
        if (site.OwnerName is null && site.Kind != SiteKind.System)
            throw new UnauthorizedAccessException(
                "SharePoint shows what's inside a site only to its members and admins, and you aren't one here. Add yourself as a site admin to see it.");
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
