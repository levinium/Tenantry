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
    public void Found_files_filter_by_extension_with_the_extensions_found_offered()
    {
        var engine = new Tenantry.Core.Lists.ListEngine<FoundFile>(FoundFileFields.All);
        var rows = engine.Prepare([File("a.xlsx"), File("b.XLSX"), File("c.dwg"), File("README"), File("Projects") with { IsFolder = true }]);
        var all = Tenantry.Core.Lists.ListQuery.Empty;

        var offered = engine.Facet(rows, all, FoundFileFields.Extension, Now).ToDictionary(f => f.Value, f => f.Count);
        Assert.Equal(new Dictionary<string, int> { [".xlsx"] = 2, [".dwg"] = 1, ["No Extension"] = 1, ["Folder"] = 1 }, offered);

        var dwg = all with { Where = Tenantry.Core.Lists.ConditionGroup.All(new Tenantry.Core.Lists.ChoiceCondition(FoundFileFields.Extension, [".dwg"], [])) };
        Assert.Equal(["c.dwg"], engine.Run(rows, dwg, Now).Select(f => f.Name));
    }

    [Fact]
    public void A_broken_regex_says_why_and_an_empty_search_is_refused()
    {
        Assert.NotNull(new FileQuery("budget(", NameMatch.Regex).Problem);
        Assert.Null(new FileQuery("budget(", NameMatch.Contains).Problem);
        Assert.True(new FileQuery("  ").IsEmpty);
        Assert.False(new FileQuery(Types: new HashSet<string> { "pdf" }).IsEmpty);
    }

    // ---- Everywhere ---------------------------------------------------------------------------

    [Theory]
    [InlineData("budget", NameMatch.Contains, "filename:budget*")]
    [InlineData("2026 Budget-final", NameMatch.StartsWith, "filename:budget*")]
    [InlineData(@"^budget\d+", NameMatch.Regex, "")]
    [InlineData("", NameMatch.Contains, "")]
    public void Purview_gets_a_broad_name_prefix_and_never_a_regex(string name, NameMatch match, string kql) =>
        Assert.Equal(kql, new FileQuery(name, match).PurviewQuery());

    [Fact]
    public void Purview_gets_types_as_filetype_and_dates_as_last_modified()
    {
        var query = new FileQuery("plan", Types: new HashSet<string> { "pdf" }, Extensions: ["dwg"],
            ModifiedAfter: new DateTimeOffset(2025, 11, 19, 0, 0, 0, TimeSpan.Zero), ModifiedBy: "pat", PathContains: "Projects", MinBytes: 5);
        Assert.Equal("filename:plan* AND (filetype:dwg OR filetype:pdf) AND lastmodifiedtime>=2025-11-19", query.PurviewQuery());
    }

    [Fact]
    public async Task Everywhere_makes_a_counting_search_over_every_site_reads_its_counts_and_deletes_it()
    {
        GraphUserRecords.EverywhereCheckEvery = TimeSpan.FromMilliseconds(1);
        var reads = 0;
        var purview = new FakeCmdlets((cmdlet, _) => cmdlet switch
        {
            "Get-ComplianceSearch" => ++reads < 2
                ? """[{"Name":"x","Status":"InProgress"}]"""
                : """[{"Name":"x","Status":"Completed","Items":5,"SearchStatistics":"{\"SharePointBinding\":{\"Sources\":[{\"Name\":\"https://contoso.sharepoint.com/sites/finance\",\"ContentItems\":4},{\"Name\":\"https://contoso-my.sharepoint.com/personal/pat_contoso_example\",\"ContentItems\":1},{\"Name\":\"https://contoso.sharepoint.com/sites/empty\",\"ContentItems\":0}]}}"}]""",
            _ => "[]",
        });
        var records = new GraphUserRecords(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), purview);

        var hits = await records.CountEverywhereAsync("filename:budget*", cancellationToken: Ct);

        Assert.Equal([("https://contoso.sharepoint.com/sites/finance", 4L), ("https://contoso-my.sharepoint.com/personal/pat_contoso_example", 1L)], hits.Select(h => (h.Location, h.Items)));
        Assert.Equal(["New-ComplianceSearch", "Start-ComplianceSearch", "Get-ComplianceSearch", "Get-ComplianceSearch", "Remove-ComplianceSearch"], purview.Calls.Select(c => c.Cmdlet));
        var made = purview.Calls[0].Parameters;
        Assert.Contains("\"SharePointLocation\":[\"All\"]", made);
        Assert.Contains("\"ContentMatchQuery\":\"filename:budget*\"", made);
        Assert.DoesNotContain("ExchangeLocation", made);
    }

    [Fact]
    public async Task Stopping_Everywhere_still_deletes_its_search()
    {
        GraphUserRecords.EverywhereCheckEvery = TimeSpan.FromMilliseconds(1);
        using var stop = new CancellationTokenSource();
        var purview = new FakeCmdlets((cmdlet, _) =>
        {
            if (cmdlet == "Get-ComplianceSearch")
                stop.Cancel();
            return cmdlet == "Get-ComplianceSearch" ? """[{"Name":"x","Status":"InProgress"}]""" : "[]";
        });
        var records = new GraphUserRecords(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), purview);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => records.CountEverywhereAsync("", cancellationToken: stop.Token));

        Assert.Equal("Remove-ComplianceSearch", purview.Calls[^1].Cmdlet);
    }

    [Fact]
    public void Everywhere_fills_the_places_from_Purviews_counts_then_looks_through_them() => SingleThread.Run(async () =>
    {
        var (page, _, _, log) = await Demo();
        Assert.Contains("choose Everywhere", page.Problem);
        page.IsEverywhere = true;
        page.NameText = "e";
        Assert.Null(page.Problem);
        Assert.EndsWith("everywhere", page.SearchSummary);

        await page.FindCommand.ExecuteAsync(null);

        Assert.NotEmpty(page.Places);
        Assert.All(page.Places, p => Assert.NotNull(p.PurviewCount));
        Assert.Equal(page.Places.Select(p => p.PurviewCount).OrderByDescending(n => n), page.Places.Select(p => p.PurviewCount));
        Assert.Contains("Purview counted", page.Places[0].Detail);
        Assert.All(page.Places, p => Assert.True(p.State is PlaceState.Done or PlaceState.NoAccess, p.State.ToString()));
        Assert.NotEmpty(page.Results.Rows);
        Assert.Single(log.All().Select(e => e.Change).OfType<FilesCountedEverywhere>());
        // Back to choosing places: Purview's list doesn't stay as a hand-picked one.
        page.ChoosePlacesCommand.Execute(null);
        Assert.Empty(page.Places);
    });

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
        // A comma or space makes a badge, always shown with its dot; what's still being typed counts too.
        page.ExtensionDraft = ".DWG, *.psd pdf";
        page.TakeFinishedExtensions();
        Assert.Equal([".dwg", ".psd"], page.Extensions.Select(e => e.Text));
        Assert.Equal("pdf", page.ExtensionDraft);
        Assert.Equal("Only .pdf, .dwg and .psd files.", page.TypeSummary);
        page.AddExtensionDraft();
        Assert.Equal([".dwg", ".psd", ".pdf"], page.Extensions.Select(e => e.Text));
        page.ExtensionDraft = "dwg ";
        page.TakeFinishedExtensions();
        Assert.Equal(3, page.Extensions.Count);
        page.Extensions.Single(e => e.Extension == "pdf").RemoveCommand.Execute(null);
        page.RemoveLastExtension();
        Assert.Equal([".dwg"], page.Extensions.Select(e => e.Text));
        page.ExtensionDraft = "psd,";
        page.TakeFinishedExtensions();
        page.Types.Single(t => t.Type.Key == "visio").IsOn = true;
        Assert.Equal("2 Types", page.TypeButtonText);
        Assert.True(page.Query(Now).Matches(File("plan.dwg")));
        Assert.True(page.Query(Now).Matches(File("flow.vsdx")));
        Assert.False(page.Query(Now).Matches(File("notes.docx")));

        // A chip's × unticks its kind; Any Type clears everything.
        pdf.UntickCommand.Execute(null);
        Assert.DoesNotContain(pdf, page.TickedTypes);
        page.ClearTypesCommand.Execute(null);
        Assert.Equal(("Any Type", ""), (page.TypeButtonText, page.ExtensionDraft));
        Assert.Empty(page.Extensions);
        Assert.False(page.HasTypeFilter);
    });

    [Fact]
    public void The_one_line_summary_says_everything_searched_for() => SingleThread.Run(async () =>
    {
        var (page, _, sites, _) = await Demo();
        page.Add(sites.First(s => s.Kind == SiteKind.OneDrive));
        page.NameText = "budget";
        page.NameMatch = FindFilesViewModel.NameMatches.Single(m => m.Value == NameMatch.StartsWith);
        page.Types.Single(t => t.Type.Key == "word").IsOn = true;
        page.ExtensionDraft = "dwg";
        page.AddExtensionDraft();
        page.Modified = FindFilesViewModel.ModifiedChoices.Single(m => m.Value == 365);
        page.ModifiedByText = "pat";
        Assert.Equal($"Name starts with \"budget\" · Word, .dwg · Modified in the last year · By pat · in {page.Places[0].Title}", page.SearchSummary);
    });

    [Fact]
    public void The_extensions_box_keeps_only_what_can_be_in_an_extension() => SingleThread.Run(async () =>
    {
        var (page, _, _, _) = await Demo();
        Assert.Equal("dwg", page.KeepForExtensionBox("d/w?g"));
        Assert.Contains("/ ?", page.ExtensionNote);
        Assert.Contains("letters, numbers, - and _", page.ExtensionNote);
        // Dots and stars go quietly (the dot is shown for you); separators stay.
        Assert.Equal("dwg, psd my-ext", page.KeepForExtensionBox(".dwg, *.psd my-ext"));
        Assert.Null(page.ExtensionNote);
        Assert.Equal("", page.KeepForExtensionBox("<>"));
        Assert.True(page.HasExtensionNote);
    });

    [Fact]
    public void Matching_files_from_each_place_show_with_counts() => SingleThread.Run(async () =>
    {
        var (page, _, sites, _) = await Demo();
        var places = sites.Where(s => s.OwnerName is not null && s.Kind != SiteKind.System && !s.IsArchived).Take(2).ToArray();
        foreach (var place in places)
            page.Add(place);
        page.Types.Single(t => t.Type.Key == "excel").IsOn = true;

        Assert.True(page.IsSearchOpen);
        // Nothing to fold back to before a search.
        page.HideSearchCommand.Execute(null);
        Assert.True(page.IsSearchOpen);
        Assert.False(page.HasSearched);
        await page.FindCommand.ExecuteAsync(null);

        // The search folds to one line so the results get the room; Edit Search opens it again.
        Assert.False(page.IsSearchOpen);
        Assert.Equal("Excel · in 2 places", page.SearchSummary);
        page.EditSearchCommand.Execute(null);
        Assert.True(page.IsSearchOpen);
        // …and Hide Search folds it again, its line saying what's there now.
        page.Types.Single(t => t.Type.Key == "pdf").IsOn = true;
        page.HideSearchCommand.Execute(null);
        Assert.False(page.IsSearchOpen);
        Assert.Equal("Excel, PDF · in 2 places", page.SearchSummary);
        page.EditSearchCommand.Execute(null);
        page.Types.Single(t => t.Type.Key == "pdf").IsOn = false;
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

            var file = Preview(page, rows.First(f => !f.IsFolder && !f.IsDeleted));
            await file.Actions.DownloadCommand.ExecuteAsync(null);
            Assert.True(System.IO.File.Exists(Path.Combine(into, file.Name)));
            Assert.StartsWith("Saved to ", file.Actions.Note);
            // A second copy doesn't overwrite the first.
            await file.Actions.DownloadCommand.ExecuteAsync(null);
            Assert.Equal(2, Directory.GetFiles(into).Length);

            var folder = Preview(page, rows.First(f => f.IsFolder && !f.IsDeleted));
            Assert.Equal("Download Folder", folder.Actions.DownloadText);
            await folder.Actions.DownloadCommand.ExecuteAsync(null);
            Assert.Equal(Path.Combine(into, folder.Name), folder.Actions.SavedIn);
            Assert.NotEmpty(Directory.GetFiles(folder.Actions.SavedIn!));
            Assert.True(System.IO.File.Exists(Path.Combine(folder.Actions.SavedIn!, "Older", "Notes.txt")), "subfolders keep their place");
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
            var found = page.Results.Rows.Cast<FoundFile>().First(f => !f.IsFolder && !f.IsDeleted);

            var preview = Preview(page, found);
            await preview.Actions.DownloadCommand.ExecuteAsync(null);

            Assert.StartsWith("Saved to ", preview.Actions.Note);
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
