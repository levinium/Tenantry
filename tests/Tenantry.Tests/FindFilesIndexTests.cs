using System.Net;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;

namespace Tenantry.Tests;

/// <summary>Find Files step 4: each place searched keeps its file list, and searching it again reads only what changed.</summary>
public class FindFilesIndexTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static SiteSummary Site() =>
        new("s1", "https://contoso.sharepoint.com/sites/finance", "Finance", "GROUP#0", SiteKind.TeamSite, "Pat Lee", "pat@contoso.example", null, null, false, false, null, 0, 0, 0,
            "Unlock", "NotArchived", null, null);

    const string Whole = """
        {"value":[
          {"id":"root","name":"root","root":{}},
          {"id":"f1","name":"Projects","folder":{},"parentReference":{"id":"root"}},
          {"id":"a","name":"Budget.xlsx","file":{"hashes":{"quickXorHash":"x"}},"size":10,"parentReference":{"id":"f1"},"lastModifiedBy":{"user":{"displayName":"Pat Lee"}}},
          {"id":"b","name":"Notes.docx","file":{},"parentReference":{"id":"root"}}
        ],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/d1/root/delta?token=one"}
        """;

    static (GraphUserRecords Records, FakeHttp Http, MemoryEntityStore Store) Records(FakeHttp http, MemoryEntityStore? store = null)
    {
        http.On(HttpMethod.Get, "sites/contoso.sharepoint.com:/sites/finance", """{"id":"host,site,web"}""")
            .On(HttpMethod.Get, "/drives?$select", """{"value":[{"id":"d1","name":"Documents"}]}""");
        store ??= new MemoryEntityStore();
        return (new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store), http, store);
    }

    static async Task<List<FoundFile>> List(GraphUserRecords records)
    {
        var files = new List<FoundFile>();
        await foreach (var f in records.ListFilesAsync(Site(), cancellationToken: Ct))
            files.Add(f);
        return files;
    }

    [Fact]
    public async Task Searching_a_place_again_reads_only_what_changed_since()
    {
        var store = new MemoryEntityStore();
        var (records, _, _) = Records(new FakeHttp().On(HttpMethod.Get, "root/delta?$select", Whole), store);
        Assert.Equal(["Projects", "Notes.docx", "Budget.xlsx"], (await List(records)).Select(f => f.Name));
        var first = Assert.Single(records.IndexedPlaces());
        Assert.Equal(("s1", 3, null), (first.PlaceId, first.Items, first.PreviousAt));
        Assert.Null(first.RefreshText);
        // Only what a result needs is kept: no hashes.
        Assert.DoesNotContain(store.All("fileindex.drive:d1"), json => json.Contains("quickXorHash", StringComparison.Ordinal));

        // Next time: the folder renamed, a file deleted, a new one; nothing else is read.
        var (again, http, _) = Records(new FakeHttp().On(HttpMethod.Get, "token=one", """
            {"value":[
              {"id":"f1","name":"Projects 2026","folder":{},"parentReference":{"id":"root"}},
              {"id":"b","deleted":{"state":"deleted"}},
              {"id":"c","name":"Plan.pptx","file":{},"parentReference":{"id":"f1"}}
            ],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/d1/root/delta?token=two"}
            """), store);

        var files = await List(again);

        Assert.DoesNotContain(http.Requests, r => r.Url.Query.Contains("$select", StringComparison.Ordinal) && r.Url.AbsolutePath.EndsWith("/root/delta"));
        Assert.Equal([("Projects 2026", "Documents"), ("Budget.xlsx", "Documents/Projects 2026"), ("Plan.pptx", "Documents/Projects 2026")], files.Select(f => (f.Name, f.Path)));
        Assert.Equal("Pat Lee", files[1].ModifiedBy);
        var kept = Assert.Single(again.IndexedPlaces());
        Assert.Equal((3, 3), (kept.Items, kept.Changes));
        Assert.Equal(first.IndexedAt, kept.PreviousAt);
        Assert.StartsWith("Read only the 3 changes since", kept.RefreshText);
        Assert.Equal("https://graph.microsoft.com/v1.0/drives/d1/root/delta?token=two", store.GetMark("fileindex.delta:d1")?.Cursor);
    }

    [Fact]
    public async Task When_Microsoft_no_longer_has_the_changes_the_place_is_read_whole_and_what_is_gone_is_dropped()
    {
        var store = new MemoryEntityStore();
        var (records, _, _) = Records(new FakeHttp().On(HttpMethod.Get, "root/delta?$select", Whole), store);
        await List(records);

        var (again, _, _) = Records(new FakeHttp()
            .On(HttpMethod.Get, "token=one", """{"error":{"code":"resyncRequired","message":"Resync required"}}""", HttpStatusCode.Gone)
            .On(HttpMethod.Get, "root/delta?$select", """
                {"value":[{"id":"root","name":"root","root":{}},{"id":"b","name":"Notes.docx","file":{},"parentReference":{"id":"root"}}],"@odata.deltaLink":"x"}
                """), store);

        Assert.Equal(["Notes.docx"], (await List(again)).Select(f => f.Name));
        Assert.Null(Assert.Single(again.IndexedPlaces()).PreviousAt);
    }

    [Fact]
    public async Task Whatever_a_deleted_folder_left_behind_is_dropped()
    {
        var store = new MemoryEntityStore();
        var (records, _, _) = Records(new FakeHttp().On(HttpMethod.Get, "root/delta?$select", Whole), store);
        await List(records);

        // The folder's deletion is reported; the file in it isn't (SharePoint doesn't always say).
        var (again, _, _) = Records(new FakeHttp().On(HttpMethod.Get, "token=one", """{"value":[{"id":"f1","deleted":{}}],"@odata.deltaLink":"y"}"""), store);

        Assert.Equal(["Notes.docx"], (await List(again)).Select(f => f.Name));
        Assert.DoesNotContain(store.All("fileindex.drive:d1"), json => json.Contains("Budget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_kept_list_is_never_shown_for_a_place_the_admin_cant_open_now()
    {
        var store = new MemoryEntityStore();
        var (records, _, _) = Records(new FakeHttp().On(HttpMethod.Get, "root/delta?$select", Whole), store);
        await List(records);

        var (locked, _, _) = Records(new FakeHttp().On(HttpMethod.Get, "token=one", """{"error":{"code":"accessDenied","message":"Access denied"}}""", HttpStatusCode.Forbidden), store);

        await Assert.ThrowsAsync<SiteAccessException>(() => List(locked));
    }

    [Fact]
    public async Task Forgetting_drops_every_kept_list_and_the_next_search_reads_it_whole()
    {
        var store = new MemoryEntityStore();
        var (records, _, _) = Records(new FakeHttp().On(HttpMethod.Get, "root/delta?$select", Whole), store);
        await List(records);

        records.ForgetIndexed();

        Assert.Empty(records.IndexedPlaces());
        Assert.Empty(store.All("fileindex.drive:d1"));
        Assert.Null(store.GetMark("fileindex.delta:d1")?.Cursor);
        var (again, http, _) = Records(new FakeHttp().On(HttpMethod.Get, "root/delta?$select", Whole), store);
        Assert.Equal(3, (await List(again)).Count);
        Assert.Contains(http.Requests, r => r.Url.AbsolutePath.EndsWith("/root/delta") && r.Url.Query.Contains("$select", StringComparison.Ordinal));
    }

    [Fact]
    public void The_page_offers_the_places_searched_before_and_forgets_on_a_second_click() => SingleThread.Run(async () =>
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var sites = await directory.GetSitesAsync(Ct);
        var pipeline = new Core.Changes.ChangePipeline(new DemoChangeExecutor(directory, records), new Core.Changes.ChangeLog(new MemoryEntityStore()),
            new Core.Changes.MemoryTestModeStore(false), DemoUserRecords.Account);
        var page = new FindFilesViewModel(records, () => sites, EmptyIndex.Instance, pipeline, Availability.Yes, DemoUserRecords.Account);
        Assert.False(page.HasIndex || page.CanAddSearchedBefore);
        var visible = sites.Where(s => s.OwnerName is not null && s.Kind == SiteKind.TeamSite && !s.IsArchived).Take(2).ToArray();
        foreach (var site in visible)
            page.Add(site);
        page.NameText = "e";
        await page.FindCommand.ExecuteAsync(null);
        Assert.True(page.HasIndex);
        Assert.Contains("2 places", page.IndexSummary);
        Assert.False(page.CanAddSearchedBefore, "both are already in the list");

        page.ClearPlacesCommand.Execute(null);
        Assert.Equal("Add Places Searched Before (2)", page.AddSearchedBeforeText);
        page.AddSearchedBeforeCommand.Execute(null);
        Assert.Equal(visible.Select(s => s.Id).Order(), page.Places.Select(p => p.Site.Id).Order());
        await page.FindCommand.ExecuteAsync(null);
        Assert.All(page.Places, p => Assert.Contains(" since ", p.Note ?? "", StringComparison.Ordinal));

        page.ForgetIndexCommand.Execute(null);
        Assert.True(page.HasIndex && page.ForgetText == "Click Again to Forget Them");
        page.ForgetIndexCommand.Execute(null);
        Assert.False(page.HasIndex);
    });
}
