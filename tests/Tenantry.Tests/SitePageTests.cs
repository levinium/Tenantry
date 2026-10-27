using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>The site page (7b): who SharePoint's claims are, what sharing links say, activity matching, and the tabs.</summary>
public sealed class SitePageTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("i:0#.f|membership|ada@contoso.example", PrincipalKind.Person)]
    [InlineData("i:0#.f|membership|sam_partner.example#ext#@contoso.example", PrincipalKind.Guest)]
    [InlineData("c:0o.c|federateddirectoryclaimprovider|aaaaaaaa-0000-0000-0000-000000000001", PrincipalKind.GroupMembers)]
    [InlineData("c:0o.c|federateddirectoryclaimprovider|aaaaaaaa-0000-0000-0000-000000000001_o", PrincipalKind.GroupOwners)]
    [InlineData("c:0t.c|tenant|bbbbbbbb-0000-0000-0000-000000000002", PrincipalKind.SecurityGroup)]
    [InlineData("c:0(.s|true", PrincipalKind.Everyone)]
    [InlineData("c:0-.f|rolemanager|spo-grid-all-users/cccccccc-0000-0000-0000-000000000003", PrincipalKind.EveryoneExceptExternal)]
    [InlineData("i:0#.w|nt service\\spsearch", PrincipalKind.System)]
    [InlineData("i:0i.t|00000003-0000-0ff1-ce00-000000000000|app@sharepoint", PrincipalKind.System)]
    public void Claims_say_who_they_are(string login, PrincipalKind kind) => Assert.Equal(kind, GraphUserRecords.Principal("x", login).Kind);

    [Fact]
    public void A_group_claim_carries_the_group_id_without_the_owners_suffix()
    {
        var owners = GraphUserRecords.Principal("x", "c:0o.c|federateddirectoryclaimprovider|aaaaaaaa-0000-0000-0000-000000000001_o");
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", owners.ObjectId);
    }

    [Theory]
    [InlineData("SharingLinks.1111.AnonymousEdit.2222", LinkKind.Anyone, true)]
    [InlineData("SharingLinks.1111.AnonymousView.2222", LinkKind.Anyone, false)]
    [InlineData("SharingLinks.1111.OrganizationView.2222", LinkKind.Organization, false)]
    [InlineData("SharingLinks.1111.Flexible.2222", LinkKind.SpecificPeople, true)]
    public void Sharing_link_groups_say_who_the_link_lets_in(string title, LinkKind kind, bool edit) =>
        Assert.Equal((kind, edit), GraphUserRecords.LinkOf(title));

    [Fact]
    public void Other_groups_are_not_links() => Assert.Null(GraphUserRecords.LinkOf("Finance Members"));

    static SiteSummary Drive(string id, string upn) =>
        new(id, $"https://x-my/personal/{id}", "Ada", "SPSPERS#10", SiteKind.OneDrive, "Ada", null, $"i:0#.f|membership|{upn}", null, false, false, null,
            100, 1000, 0, "Unlock", "NotArchived", null, null);

    [Fact]
    public async Task OneDrive_activity_is_matched_by_owner_when_the_report_has_no_address()
    {
        var http = new FakeHttp().On(HttpMethod.Get, "getOneDriveUsageAccountDetail", """
            {"value":[{"siteId":"","siteUrl":"","ownerPrincipalName":"ada@contoso.example","fileCount":120,"activeFileCount":7,"lastActivityDate":"2026-10-20"}]}
            """);
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

        var usage = await records.GetUsageAsync(Drive("d1", "ada@contoso.example"), Ct);
        var none = await records.GetUsageAsync(Drive("d2", "bo@contoso.example"), Ct);

        Assert.Equal((120L, 7L, new DateOnly(2026, 10, 20)), (usage.Value!.Files, usage.Value.ActiveFiles, usage.Value.LastActivity));
        Assert.Null(none.Value);
        Assert.Single(http.Requests);
    }

    static async Task<(SiteDetailViewModel Page, SiteSummary Site)> DemoPage(Func<SiteSummary, bool> pick)
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var site = (await directory.GetSitesAsync(Ct)).First(pick);
        return (new SiteDetailViewModel(site, records), site);
    }

    [Fact]
    public async Task A_site_the_admin_cant_see_inside_says_why_on_each_tab()
    {
        var (page, _) = await DemoPage(s => s.IsArchived && s.Kind == SiteKind.OneDrive);
        foreach (var section in page.Sections.OfType<SiteSection>().Skip(1))
            await section.LoadAsync(Ct);

        Assert.All(page.Sections.OfType<SiteSection>().Skip(1), s => Assert.Contains("archived", s.Note, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sharing_counts_links_by_who_they_let_in_and_lists_guests()
    {
        var (page, site) = await DemoPage(s => s.Kind == SiteKind.TeamSite && s.Sharing > 0 && s.OwnerName is not null);
        var sharing = page.Sections.OfType<SiteSharingSection>().Single();

        await sharing.LoadAsync(Ct);

        Assert.Contains(sharing.Links, l => l.Title == "Anyone With the Link Can View");
        Assert.Equal("Guest", Assert.Single(sharing.Guests).KindText);
        Assert.Contains("anyone link", sharing.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_library_too_big_to_sort_explains_instead_of_listing()
    {
        var (page, _) = await DemoPage(s => s.Kind == SiteKind.TeamSite && s.OwnerName is not null && !s.IsArchived);
        var libraries = page.Sections.OfType<SiteLibrariesSection>().Single();
        await libraries.LoadAsync(Ct);
        var archive = libraries.Libraries.Single(l => l.Name == "Archive");

        archive.IsExpanded = true;
        for (var i = 0; i < 20 && archive.IsLoading; i++)
            await Task.Delay(5, Ct);

        Assert.Contains("too many files", archive.Note, StringComparison.Ordinal);
        Assert.Contains(libraries.Lists, l => l.Name == "Tasks" && !l.CanExpand);
    }
}
