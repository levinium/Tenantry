using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's sites: one per Microsoft 365 group, a couple of communication sites and a hub,
/// SharePoint's own system sites, and a OneDrive for each member (archived for some unlicensed
/// people, as Microsoft does after 93 days).
/// </summary>
public sealed partial class DemoTenantDirectory
{
    const string SharePointHost = "https://northgate.sharepoint.example";

    List<SiteSummary>? _sites;

    public async Task<IReadOnlyList<SiteSummary>> GetSitesAsync(CancellationToken cancellationToken = default)
    {
        if (_sites is null)
        {
            var users = await GetUsersAsync(cancellationToken);
            var groups = await GetGroupsAsync(cancellationToken);
            _sites = GenerateSites(users, groups);
        }
        return _sites.ToArray();
    }

    /// <summary>Changes one site, found by ID; false when there's none (the demo's change executor writes through this).</summary>
    public bool UpdateSite(string id, Func<SiteSummary, SiteSummary?> change)
    {
        if (_sites is null)
            return false;
        var index = _sites.FindIndex(s => s.Id == id);
        if (index < 0)
            return false;
        if (change(_sites[index]) is { } changed)
            _sites[index] = changed;
        else
            _sites.RemoveAt(index);
        return true;
    }

    static List<SiteSummary> GenerateSites(IReadOnlyList<UserSummary> users, IReadOnlyList<GroupSummary> groups)
    {
        var now = DateTimeOffset.UtcNow;
        var random = new Random(20261029);
        var sites = new List<SiteSummary>();
        string Id() => new Guid(Enumerable.Range(0, 16).Select(_ => (byte)random.Next(256)).ToArray()).ToString();
        const long TeamQuota = 25L * 1024 * 1024;

        var hubId = Id();
        sites.Add(new SiteSummary(hubId, $"{SharePointHost}/sites/intranet", "Northgate Intranet", "SITEPAGEPUBLISHING#0", SiteKind.CommunicationSite,
            "Northgate IT", null, null, null, false, true, null, 3200, TeamQuota, 0, "Unlock", "NotArchived", now.AddDays(-900), now.AddHours(-5)));
        sites.Add(new SiteSummary(Id(), $"{SharePointHost}/sites/news", "Northgate News", "SITEPAGEPUBLISHING#0", SiteKind.CommunicationSite,
            "Northgate IT", null, null, null, false, false, hubId, 1800, TeamQuota, 0, "Unlock", "NotArchived", now.AddDays(-700), now.AddDays(-2)));

        foreach (var g in groups.Where(g => g.Kind == GroupKind.Microsoft365))
        {
            var slug = g.Mail is { } mail ? mail[..mail.IndexOf('@')] : g.DisplayName.Replace(' ', '-').ToLowerInvariant();
            var quiet = random.NextDouble() < 0.2;
            sites.Add(new SiteSummary(Id(), $"{SharePointHost}/sites/{slug}", g.DisplayName, "GROUP#0", SiteKind.TeamSite, g.DisplayName, g.Mail, null, g.Id,
                g.IsTeam, false, random.NextDouble() < 0.5 ? hubId : null, random.Next(200, 60000), TeamQuota, random.NextDouble() < 0.25 ? 1 : 0,
                "Unlock", "NotArchived", g.CreatedAt, quiet ? now.AddDays(-random.Next(200, 600)) : now.AddHours(-random.Next(1, 300))));
            if (g.IsTeam && random.NextDouble() < 0.3)
                sites.Add(new SiteSummary(Id(), $"{SharePointHost}/sites/{slug}-leads", $"{g.DisplayName} - Leads", "TEAMCHANNEL#1", SiteKind.ChannelSite, g.DisplayName,
                    null, null, g.Id, false, false, null, random.Next(50, 3000), TeamQuota, 0, "Unlock", "NotArchived", g.CreatedAt, now.AddDays(-random.Next(1, 90))));
        }
        sites.Add(new SiteSummary(Id(), $"{SharePointHost}/sites/old-projects", "Old Projects", "STS#3", SiteKind.TeamSite, null, null, null, null, false, false, null,
            42000, TeamQuota, 0, "ReadOnly", "NotArchived", now.AddDays(-2400), now.AddDays(-1100)));
        sites.Add(new SiteSummary(Id(), $"{SharePointHost}/sites/2019-archive", "2019 Archive", "STS#3", SiteKind.TeamSite, null, null, null, null, false, false, null,
            15000, TeamQuota, 0, "Unlock", "FullyArchived", now.AddDays(-2600), now.AddDays(-1500)));
        foreach (var (path, title, template) in new[]
                 {
                     ("search", "Search Center", "SRCHCEN#0"), ("appcatalog", "Apps", "APPCATALOG#0"), ("contenttypehub", "Content Type Hub", "POINTPUBLISHINGHUB#0"),
                 })
            sites.Add(new SiteSummary(Id(), $"{SharePointHost}/sites/{path}", title, template, SiteKind.System, null, null, null, null, false, false, null,
                random.Next(1, 50), TeamQuota, 0, "Unlock", "NotArchived", now.AddDays(-2000), now.AddDays(-random.Next(30, 400))));

        foreach (var u in users.Where(u => u.Kind == UserKind.Member))
        {
            var unlicensed = u.Licenses.Count == 0;
            var archived = unlicensed && random.NextDouble() < 0.8;
            var slug = u.UserPrincipalName.Replace('.', '_').Replace('@', '_');
            sites.Add(new SiteSummary(Id(), $"https://northgate-my.sharepoint.example/personal/{slug}", u.DisplayName, "SPSPERS#10", SiteKind.OneDrive,
                u.DisplayName, u.Mail, $"i:0#.f|membership|{u.UserPrincipalName}", null, false, false, null,
                unlicensed ? random.Next(0, 300) : random.Next(100, 90000), 1024L * 1024, 0, "Unlock", archived ? "FullyArchived" : "NotArchived",
                u.CreatedAt, unlicensed ? now.AddDays(-random.Next(100, 900)) : now.AddHours(-random.Next(1, 400))));
        }
        return sites;
    }
}
