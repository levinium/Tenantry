using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.ViewModels.Lists;

namespace Tenantry.Tests;

/// <summary>Sites and OneDrives (7a): reading SharePoint's answer, naming owners, and the views.</summary>
public sealed class SiteListTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_CSOM_site_becomes_a_small_row_with_real_dates_and_ids()
    {
        using var json = JsonDocument.Parse("""
            {"SiteId":"/Guid(11111111-2222-3333-4444-555555555555)/","Url":"https://contoso.sharepoint.example/sites/finance","Title":"Finance",
             "Template":"GROUP#0","GroupId":"/Guid(aaaaaaaa-0000-0000-0000-000000000001)/","HubSiteId":"/Guid(00000000-0000-0000-0000-000000000000)/",
             "IsTeamsConnected":true,"StorageUsage":2048,"StorageMaximumLevel":26214400,"SharingCapability":1,"LockState":"Unlock","ArchiveStatus":"NotArchived",
             "CreatedTime":"/Date(2023,0,15,9,30,0,0)/","LastContentModifiedDate":"/Date(1,0,1,0,0,0,0)/","OwnerName":"","WebsCount":3}
            """);

        var row = DirectorySync.SiteRow(json.RootElement)!;
        using var stored = JsonDocument.Parse(row.ToJsonString());
        var site = CachedTenantDirectory.ToSite(stored.RootElement);

        Assert.Equal(("11111111-2222-3333-4444-555555555555", SiteKind.TeamSite, "aaaaaaaa-0000-0000-0000-000000000001", null), (site.Id, site.Kind, site.GroupId, site.HubSiteId));
        Assert.Equal((true, 2048L, 26214400L, 1, null), (site.IsTeam, site.StorageUsedMb, site.StorageQuotaMb, site.Sharing, site.OwnerName));
        // Months count from 0 in CSOM; year 1 means never.
        Assert.Equal(new DateTimeOffset(2023, 1, 15, 9, 30, 0, TimeSpan.Zero), site.Created);
        Assert.Null(site.LastActivity);
    }

    [Theory]
    [InlineData("SPSPERS#10", SiteKind.OneDrive)]
    [InlineData("TEAMCHANNEL#1", SiteKind.ChannelSite)]
    [InlineData("SITEPAGEPUBLISHING#0", SiteKind.CommunicationSite)]
    [InlineData("STS#3", SiteKind.TeamSite)]
    [InlineData("SRCHCEN#0", SiteKind.System)]
    [InlineData("RedirectSite#1", SiteKind.System)]
    public void Templates_say_what_kind_of_site(string template, SiteKind kind) => Assert.Equal(kind, SiteSummary.KindOf(template));

    [Fact]
    public async Task Owners_are_named_from_the_directory_a_person_for_a_OneDrive_a_group_for_its_site()
    {
        var store = new MemoryEntityStore();
        store.Apply(DirectorySync.Users, [new EntityChange("u1", JsonNode.Parse("""{"id":"u1","displayName":"Ada","userPrincipalName":"ada@contoso.example"}""")!.AsObject())]);
        store.Apply(DirectorySync.Groups, [new EntityChange("g1", JsonNode.Parse("""{"id":"g1","displayName":"Finance","mail":"finance@contoso.example"}""")!.AsObject())]);
        store.Apply(DirectorySync.Sites,
        [
            new EntityChange("s1", JsonNode.Parse("""{"id":"s1","url":"https://x/personal/ada","title":"Ada","template":"SPSPERS#10","ownerLogin":"i:0#.f|membership|ada@contoso.example"}""")!.AsObject()),
            new EntityChange("s2", JsonNode.Parse("""{"id":"s2","url":"https://x/sites/finance","title":"Finance","template":"GROUP#0","groupId":"g1"}""")!.AsObject()),
            new EntityChange("s3", JsonNode.Parse("""{"id":"s3","url":"https://x/sites/old","title":"Old","template":"STS#3"}""")!.AsObject()),
        ]);

        var sites = (await new CachedTenantDirectory(store, "Contoso").GetSitesAsync(Ct)).ToDictionary(s => s.Id);

        Assert.Equal(("Ada", "Finance", null), (sites["s1"].OwnerName, sites["s2"].OwnerName, sites["s3"].OwnerName));
        Assert.Equal("finance@contoso.example", sites["s2"].OwnerEmail);
    }

    static IReadOnlyList<string> In(IReadOnlyList<SiteSummary> sites, IReadOnlyList<SavedView> views, string view)
    {
        var engine = new ListEngine<SiteSummary>(SiteFields.All);
        return engine.Run(engine.Prepare(sites), views.Single(v => v.Id == view).Query, Now).Select(s => s.Title).ToArray();
    }

    static SiteSummary Site(string title, SiteKind kind, int sharing = 0, string archive = "NotArchived", DateTimeOffset? activity = null, long usedMb = 100) =>
        new(title, $"https://x/{title}", title, kind == SiteKind.OneDrive ? "SPSPERS#10" : "GROUP#0", kind, null, null, null, null, false, false, null,
            usedMb, 1000, sharing, "Unlock", archive, Now.AddDays(-500), activity ?? Now.AddDays(-1));

    [Fact]
    public void Views_keep_OneDrives_and_sites_apart_and_find_what_needs_review()
    {
        var sites = new[]
        {
            Site("Finance", SiteKind.TeamSite, sharing: 1),
            Site("Quiet", SiteKind.TeamSite, activity: Now.AddDays(-400)),
            Site("Search", SiteKind.System),
            Site("Ada", SiteKind.OneDrive, archive: "FullyArchived"),
            Site("Bo", SiteKind.OneDrive, usedMb: 950),
        };

        Assert.Equal(["Finance", "Quiet"], In(sites, SiteList.SiteViews, "all"));
        Assert.Equal(["Finance"], In(sites, SiteList.SiteViews, "external"));
        Assert.Equal(["Quiet"], In(sites, SiteList.SiteViews, "inactive"));
        Assert.Equal(["Ada", "Bo"], In(sites, SiteList.OneDriveViews, "all"));
        Assert.Equal(["Ada"], In(sites, SiteList.OneDriveViews, "archived"));
        Assert.Equal(["Bo"], In(sites, SiteList.OneDriveViews, "nearly-full"));
    }

    [Fact]
    public async Task The_demo_has_every_kind_of_site_and_OneDrives_archived_for_some_unlicensed_people()
    {
        var sites = await new DemoTenantDirectory(userCount: 60).GetSitesAsync(Ct);

        Assert.Equal(Enum.GetValues<SiteKind>().Order(), sites.Select(s => s.Kind).Distinct().Order());
        Assert.Contains(sites, s => s.Kind == SiteKind.OneDrive && s.IsArchived);
        Assert.Contains(sites, s => s.IsHubSite);
        Assert.DoesNotContain(sites, s => !s.Url.Contains(".example", StringComparison.Ordinal));
    }
}
