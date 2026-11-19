using System.Net;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;

namespace Tenantry.Tests;

public class FindFilesTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    static FoundFile File(string name, string folder = "", long size = 1000, int daysAgo = 10, string? by = "Pat Lee", string? byEmail = "pat@contoso.example") =>
        new("i1", "d1", name, "Documents", folder, "s1", "Finance", "https://contoso.sharepoint.com/sites/finance", size,
            Now.AddDays(-daysAgo), by, byEmail, Now.AddDays(-daysAgo - 5), "Sam Ito", "sam@contoso.example", "https://contoso.sharepoint.com/sites/finance/Shared Documents/" + name);

    // ---- What matches -------------------------------------------------------------------------

    [Theory]
    [InlineData("budget", NameMatch.Contains, "2026 Budget Final.xlsx", true)]
    [InlineData("BUDGET", NameMatch.Contains, "2026 budget.xlsx", true)]
    [InlineData("budget", NameMatch.StartsWith, "2026 Budget.xlsx", false)]
    [InlineData("budget", NameMatch.StartsWith, "Budget 2026.xlsx", true)]
    [InlineData(@"^budget\s20(25|26)", NameMatch.Regex, "Budget 2026.xlsx", true)]
    [InlineData(@"^budget\s20(25|26)", NameMatch.Regex, "Budget 2023.xlsx", false)]
    [InlineData("", NameMatch.Contains, "Anything.docx", true)]
    public void Names_match_by_contains_starts_with_or_regex_ignoring_case(string name, NameMatch match, string file, bool matches) =>
        Assert.Equal(matches, new FileQuery(name, match).Matches(File(file)));

    [Fact]
    public void Types_and_extra_extensions_narrow_by_extension()
    {
        var excel = new FileQuery(Types: new HashSet<string> { "excel" });
        Assert.True(excel.Matches(File("a.xlsx")));
        Assert.True(excel.Matches(File("a.CSV")));
        Assert.False(excel.Matches(File("a.docx")));
        var cad = new FileQuery(Types: new HashSet<string> { "pdf" }, Extensions: [".dwg", "*.psd"]);
        Assert.True(cad.Matches(File("plan.dwg")));
        Assert.True(cad.Matches(File("plan.psd")));
        Assert.True(cad.Matches(File("plan.pdf")));
        Assert.False(cad.Matches(File("plan")));
    }

    [Fact]
    public void Dates_sizes_people_and_folders_all_have_to_match()
    {
        Assert.True(new FileQuery(ModifiedAfter: Now.AddDays(-30)).Matches(File("a.docx", daysAgo: 10)));
        Assert.False(new FileQuery(ModifiedAfter: Now.AddDays(-30)).Matches(File("a.docx", daysAgo: 40)));
        Assert.True(new FileQuery(ModifiedBefore: Now.AddDays(-365)).Matches(File("a.docx", daysAgo: 400)));
        Assert.False(new FileQuery(MinBytes: 2000).Matches(File("a.docx", size: 1000)));
        Assert.True(new FileQuery(ModifiedBy: "pat@").Matches(File("a.docx")));
        // Whoever made it counts too: a leaver's own files, changed since by someone else.
        Assert.True(new FileQuery(ModifiedBy: "sam ito").Matches(File("a.docx")));
        Assert.False(new FileQuery(ModifiedBy: "dana").Matches(File("a.docx")));
        Assert.True(new FileQuery(PathContains: "projects").Matches(File("a.docx", "Projects/2026")));
        Assert.True(new FileQuery(PathContains: "documents").Matches(File("a.docx")));
        Assert.False(new FileQuery(PathContains: "archive").Matches(File("a.docx", "Projects")));
    }

    [Fact]
    public void A_broken_regex_says_why_and_an_empty_search_is_refused()
    {
        Assert.NotNull(new FileQuery("budget(", NameMatch.Regex).Problem);
        Assert.Null(new FileQuery("budget(", NameMatch.Contains).Problem);
        Assert.True(new FileQuery("  ").IsEmpty);
        Assert.False(new FileQuery(Types: new HashSet<string> { "pdf" }).IsEmpty);
    }

    [Fact]
    public void A_runaway_regex_stops_instead_of_hanging() =>
        Assert.False(new FileQuery("^(a+)+$", NameMatch.Regex).Matches(File(new string('a', 40) + "!")));

    // ---- Reading a place's files --------------------------------------------------------------

    static SiteSummary Site(string url = "https://contoso.sharepoint.com/sites/finance", SiteKind kind = SiteKind.TeamSite, string? owner = "Pat Lee") =>
        new("s1", url, "Finance", "GROUP#0", kind, owner, owner is null ? null : "pat@contoso.example", null, null, false, false, null, 0, 0, 0, "Unlock", "NotArchived", null, null);

    [Fact]
    public async Task Every_library_is_read_in_full_with_folders_put_together_and_deleted_items_left_out()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "sites/contoso.sharepoint.com:/sites/finance", """{"id":"host,site,web"}""")
            .On(HttpMethod.Get, "/drives?$select", """{"value":[{"id":"d1","name":"Documents"},{"id":"d2","name":"Archive"}]}""")
            .On(HttpMethod.Get, "drives/d1/root/delta?$select", """
                {"value":[
                  {"id":"root","name":"root","root":{}},
                  {"id":"f1","name":"Projects","folder":{},"parentReference":{"id":"root"}},
                  {"id":"f2","name":"2026","folder":{},"parentReference":{"id":"f1"}},
                  {"id":"a","name":"Budget.xlsx","file":{},"size":2048,"parentReference":{"id":"f2"},"lastModifiedDateTime":"2026-10-01T09:00:00Z",
                   "lastModifiedBy":{"user":{"displayName":"Pat Lee","email":"pat@contoso.example"}},"webUrl":"https://contoso.sharepoint.com/sites/finance/Shared%20Documents/Projects/2026/Budget.xlsx"}
                ],"@odata.nextLink":"https://graph.microsoft.com/v1.0/drives/d1/root/delta?token=p2"}
                """)
            .On(HttpMethod.Get, "token=p2", """
                {"value":[
                  {"id":"b","name":"Top.docx","file":{},"size":10,"parentReference":{"id":"root"}},
                  {"id":"c","name":"Gone.docx","deleted":{"state":"deleted"},"parentReference":{"id":"root"}}
                ],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/d1/root/delta?token=end"}
                """)
            .On(HttpMethod.Get, "drives/d2/root/delta?$select", """{"value":[{"id":"r2","name":"root","root":{}},{"id":"z","name":"Old.pdf","file":{},"parentReference":{"id":"r2"}}],"@odata.deltaLink":"x"}""");
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

        var files = new List<FoundFile>();
        await foreach (var f in records.ListFilesAsync(Site(), cancellationToken: Ct))
            files.Add(f);

        // Folders are results too (a whole folder can be downloaded); the library's top isn't.
        Assert.Equal(["Projects", "2026", "Budget.xlsx", "Top.docx", "Old.pdf"], files.Select(f => f.Name));
        Assert.Equal([true, true, false, false, false], files.Select(f => f.IsFolder));
        Assert.Equal(("Documents/Projects", "Folder"), (files[1].Path, FoundFileFields.TypeOf(files[1])));
        var budget = files[2];
        Assert.Equal(("Documents/Projects/2026", 2048L, "Pat Lee"), (budget.Path, budget.Size, budget.ModifiedBy));
        Assert.Equal("Documents", files[3].Path);
        Assert.Equal("Archive", files[4].Path);
        Assert.Equal("https://contoso.sharepoint.com/sites/finance/Shared%20Documents/Projects/2026", budget.FolderUrl);
    }

    [Fact]
    public async Task A_place_the_admin_cant_see_inside_says_so()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "sites/contoso.sharepoint.com:/sites/finance", """{"id":"host,site,web"}""")
            .On(HttpMethod.Get, "/drives?$select", """{"error":{"code":"accessDenied","message":"Access denied"}}""", HttpStatusCode.Forbidden);
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

        await Assert.ThrowsAsync<SiteAccessException>(async () =>
        {
            await foreach (var _ in records.ListFilesAsync(Site(), cancellationToken: Ct))
            {
            }
        });
    }

    // ---- The page -----------------------------------------------------------------------------

    static async Task<(FindFilesViewModel Page, ChangePipeline Pipeline, IReadOnlyList<SiteSummary> Sites, ChangeLog Log)> Demo()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var log = new ChangeLog(new MemoryEntityStore());
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), log, new MemoryTestModeStore(false), DemoUserRecords.Account);
        var sites = await directory.GetSitesAsync(Ct);
        return (new FindFilesViewModel(records, () => sites, EmptyIndex.Instance, pipeline, Availability.Yes, DemoUserRecords.Account), pipeline, sites, log);
    }

    [Fact]
    public void Find_Files_needs_somewhere_to_look_and_something_to_look_for() => SingleThread.Run(async () =>
    {
        var (page, _, sites, _) = await Demo();
        Assert.False(page.FindCommand.CanExecute(null));
        Assert.Contains("OneDrive or a site", page.Problem);

        page.Add(sites.First(s => s.Kind == SiteKind.OneDrive));
        Assert.Contains("what to look for", page.Problem);
        page.NameText = "budget(";
        page.NameMatch = FindFilesViewModel.NameMatches.Single(m => m.Value == NameMatch.Regex);
        Assert.Contains("regular expression", page.Problem);
        page.NameMatch = FindFilesViewModel.NameMatches[0];
        Assert.True(page.FindCommand.CanExecute(null));
    });

    [Fact]
    public void The_type_choice_says_which_extensions_it_looks_for_and_takes_typed_ones() => SingleThread.Run(async () =>
    {
        var (page, _, _, _) = await Demo();
        Assert.Equal(("Any Type", "Any type of file (and folders)."), (page.TypeButtonText, page.TypeSummary));
        Assert.False(page.HasTypeFilter);

        var pdf = page.Types.Single(t => t.Type.Key == "pdf");
        Assert.Equal(".pdf", pdf.ExtensionsText);
        Assert.StartsWith(".doc, .docx", page.Types.Single(t => t.Type.Key == "word").ExtensionsText);
        pdf.IsOn = true;
        Assert.Equal(("PDF", "Only .pdf files."), (page.TypeButtonText, page.TypeSummary));
        Assert.Equal([pdf], page.TickedTypes);

        // Typed ones join in, with or without the dot or a star, and repeats count once.
        page.ExtensionsText = ".DWG, *.psd pdf";
        Assert.Equal("Only .pdf, .dwg and .psd files.", page.TypeSummary);
        page.Types.Single(t => t.Type.Key == "visio").IsOn = true;
        Assert.Equal("2 Types", page.TypeButtonText);
        Assert.True(page.Query(Now).Matches(File("plan.dwg")));
        Assert.True(page.Query(Now).Matches(File("flow.vsdx")));
        Assert.False(page.Query(Now).Matches(File("notes.docx")));

        // A chip's × unticks its kind; Any Type clears everything.
        pdf.UntickCommand.Execute(null);
        Assert.DoesNotContain(pdf, page.TickedTypes);
        page.ClearTypesCommand.Execute(null);
        Assert.Equal(("Any Type", ""), (page.TypeButtonText, page.ExtensionsText));
        Assert.False(page.HasTypeFilter);
    });

    [Fact]
    public void Matching_files_from_each_place_show_with_counts() => SingleThread.Run(async () =>
    {
        var (page, _, sites, _) = await Demo();
        var places = sites.Where(s => s.OwnerName is not null && s.Kind != SiteKind.System && !s.IsArchived).Take(2).ToArray();
        foreach (var place in places)
            page.Add(place);
        page.Types.Single(t => t.Type.Key == "excel").IsOn = true;

        await page.FindCommand.ExecuteAsync(null);

        Assert.All(page.Places, p => Assert.Equal(PlaceState.Done, p.State));
        var rows = page.Results.Rows.Cast<FoundFile>().ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, f => Assert.True(f.Extension is "xlsx" or "csv"));
        Assert.Equal(rows.Length, page.Places.Sum(p => p.Found));
        Assert.StartsWith("Done · ", page.Status);
        Assert.True(page.Places.All(p => p.Scanned >= p.Found));
    });

    [Fact]
    public void Add_Me_and_Search_adds_the_admin_searches_then_takes_the_access_away() => SingleThread.Run(async () =>
    {
        var (page, pipeline, sites, log) = await Demo();
        var locked = sites.First(s => s.OwnerName is null && s.Kind != SiteKind.System && !s.IsArchived);
        page.Add(locked);
        page.NameText = "e";

        await page.FindCommand.ExecuteAsync(null);
        var row = Assert.Single(page.Places);
        Assert.Equal(PlaceState.NoAccess, row.State);
        Assert.True(row.CanAddMe);
        Assert.Contains("removed again", row.Note);

        await row.AddMeCommand.ExecuteAsync(null);

        Assert.Equal(PlaceState.Done, row.State);
        Assert.True(row.Found > 0);
        Assert.Contains("was removed", row.Note);
        // Both steps logged; nothing left waiting in Pending Changes.
        var admins = log.All().Select(e => e.Change).OfType<SetSiteAdmin>().ToArray();
        Assert.Equal([false, true], admins.Select(a => a.New)); // newest first: added, then removed
        Assert.Empty(pipeline.Pending);
        Assert.False(page.IsSearching);
    });

    [Fact]
    public void Keeping_the_access_leaves_the_admin_in_place() => SingleThread.Run(async () =>
    {
        var (page, _, sites, log) = await Demo();
        page.Add(sites.First(s => s.OwnerName is null && s.Kind != SiteKind.System && !s.IsArchived));
        page.NameText = "e";
        page.KeepAccess = true;
        await page.FindCommand.ExecuteAsync(null);

        await page.Places[0].AddMeCommand.ExecuteAsync(null);

        Assert.Equal(PlaceState.Done, page.Places[0].State);
        Assert.Equal([true], log.All().Select(e => e.Change).OfType<SetSiteAdmin>().Select(a => a.New));
        Assert.Contains("still a site admin", page.Places[0].Note);
    });

    // ---- Downloading --------------------------------------------------------------------------

    /// <summary>The row's preview, as the list shows it (with the page's Download behind it).</summary>
    static FoundFilePreviewViewModel Preview(FindFilesViewModel page, FoundFile row)
    {
        page.Results.PreviewRow(row);
        return Assert.IsType<FoundFilePreviewViewModel>(page.Results.PreviewModel);
    }

    [Fact]
    public void A_found_file_and_a_whole_folder_download_into_the_chosen_folder_and_are_logged() => SingleThread.Run(async () =>
    {
        var into = Directory.CreateTempSubdirectory("tenantry-find-").FullName;
        var pick = ViewModels.Platform.PickFolder;
        ViewModels.Platform.PickFolder = () => Task.FromResult<string?>(into);
        try
        {
            var (page, _, sites, log) = await Demo();
            page.Add(sites.First(s => s.OwnerName is not null && s.Kind != SiteKind.System && !s.IsArchived));
            page.NameText = "e";
            await page.FindCommand.ExecuteAsync(null);
            var rows = page.Results.Rows.Cast<FoundFile>().ToArray();

            var file = Preview(page, rows.First(f => !f.IsFolder));
            await file.DownloadCommand.ExecuteAsync(null);
            Assert.True(System.IO.File.Exists(Path.Combine(into, file.Name)));
            Assert.StartsWith("Saved to ", file.DownloadNote);
            // A second copy doesn't overwrite the first.
            await file.DownloadCommand.ExecuteAsync(null);
            Assert.Equal(2, Directory.GetFiles(into).Length);

            var folder = Preview(page, rows.First(f => f.IsFolder));
            Assert.Equal("Download Folder", folder.DownloadText);
            await folder.DownloadCommand.ExecuteAsync(null);
            Assert.Equal(Path.Combine(into, folder.Name), folder.SavedIn);
            Assert.NotEmpty(Directory.GetFiles(folder.SavedIn!));
            Assert.True(System.IO.File.Exists(Path.Combine(folder.SavedIn!, "Older", "Notes.txt")), "subfolders keep their place");
            Assert.Equal(3, log.All().Select(e => e.Change).OfType<FilesDownloaded>().Count());
        }
        finally
        {
            ViewModels.Platform.PickFolder = pick;
            Directory.Delete(into, recursive: true);
        }
    });

    [Fact]
    public void Downloading_from_a_place_that_needed_Add_Me_adds_the_admin_again_just_for_it() => SingleThread.Run(async () =>
    {
        var into = Directory.CreateTempSubdirectory("tenantry-find-").FullName;
        var pick = ViewModels.Platform.PickFolder;
        ViewModels.Platform.PickFolder = () => Task.FromResult<string?>(into);
        try
        {
            var (page, _, sites, log) = await Demo();
            page.Add(sites.First(s => s.OwnerName is null && s.Kind != SiteKind.System && !s.IsArchived));
            page.NameText = "e";
            await page.FindCommand.ExecuteAsync(null);
            await page.Places[0].AddMeCommand.ExecuteAsync(null);
            var found = page.Results.Rows.Cast<FoundFile>().First(f => !f.IsFolder);

            var preview = Preview(page, found);
            await preview.DownloadCommand.ExecuteAsync(null);

            Assert.StartsWith("Saved to ", preview.DownloadNote);
            // Newest first: removed, added (download); removed, added (search).
            Assert.Equal([false, true, false, true], log.All().Select(e => e.Change).OfType<SetSiteAdmin>().Select(a => a.New));
        }
        finally
        {
            ViewModels.Platform.PickFolder = pick;
            Directory.Delete(into, recursive: true);
        }
    });

    [Fact]
    public async Task Graph_downloads_a_files_content_and_lists_a_folder_with_its_subfolders()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "items/f1/children", """{"value":[{"id":"a","name":"A.docx","file":{},"size":5},{"id":"f2","name":"Sub","folder":{}}]}""")
            .On(HttpMethod.Get, "items/f2/children", """{"value":[{"id":"b","name":"B.pdf","file":{}}]}""")
            .On(HttpMethod.Get, "items/a/content", "hello");
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());
        var folder = File("Projects") with { Id = "f1", IsFolder = true };

        var inside = new List<FoundFile>();
        await foreach (var f in records.ListFolderAsync(folder, Ct))
            inside.Add(f);
        using var copy = new MemoryStream();
        await records.DownloadAsync(inside[0], copy, cancellationToken: Ct);

        Assert.Equal([("A.docx", ""), ("B.pdf", "Sub")], inside.Select(f => (f.Name, f.Folder)));
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(copy.ToArray()));
        Assert.Contains(http.Requests, r => r.Url.AbsolutePath.EndsWith("/drives/d1/items/a/content"));
    }

    [Fact]
    public void Test_mode_refuses_adding_the_admin_to_a_real_site() => SingleThread.Run(async () =>
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), DemoUserRecords.Account);
        var sites = await directory.GetSitesAsync(Ct);
        var page = new FindFilesViewModel(records, () => sites, EmptyIndex.Instance, pipeline, Availability.Yes, DemoUserRecords.Account);
        page.Add(sites.First(s => s.OwnerName is null && s.Kind != SiteKind.System && !s.IsArchived && !s.Url.Contains("zz-tenantry-test-")));
        page.NameText = "e";
        await page.FindCommand.ExecuteAsync(null);

        await page.Places[0].AddMeCommand.ExecuteAsync(null);

        Assert.Equal(PlaceState.NoAccess, page.Places[0].State);
        Assert.Contains("couldn't be added", page.Places[0].Note);
    });
}
