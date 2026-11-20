using System.Net;
using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Editing;

namespace Tenantry.Tests;

/// <summary>Find Files step 3: recycle bins, Restore, Give Access To and Copy To.</summary>
public class FindFilesRecoveryTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string FinanceUrl = "https://contoso.sharepoint.com/sites/finance";

    static SiteSummary Site(string url = FinanceUrl) =>
        new("s1", url, "Finance", "GROUP#0", SiteKind.TeamSite, "Pat Lee", "pat@contoso.example", null, null, false, false, null, 0, 0, 0, "Unlock", "NotArchived", null, null);

    static FoundFile File(string name, int binStage = 0, DateTimeOffset? modified = null, DateTimeOffset? deleted = null) =>
        new($"id-{name}", binStage > 0 ? "" : "d1", name, "Documents", "", "s1", "Finance", FinanceUrl, 100, modified, null, null, null, null, null,
            binStage > 0 ? null : $"{FinanceUrl}/Shared%20Documents/{name}", BinStage: binStage, Deleted: deleted, DeletedBy: binStage > 0 ? "Pat Lee" : null);

    // ---- Reading recycle bins ------------------------------------------------------------------

    [Fact]
    public void A_recycle_bin_row_becomes_a_found_file_in_its_library_and_folder()
    {
        var row = JsonDocument.Parse("""
            {"Id":"b1","LeafName":"Budget.xlsx","DirName":"sites/finance/Shared Documents/Projects/2026","ItemState":2,"ItemType":1,"Size":512,
             "DeletedDate":"2026-11-01T10:00:00Z","DeletedByName":"Pat Lee","DeletedByEmail":"pat@contoso.example","AuthorName":"Sam Ray","AuthorEmail":"sam@contoso.example"}
            """).RootElement;

        var file = GraphUserRecords.FromBin(row, Site())!;

        Assert.Equal(("b1", "Shared Documents", "Projects/2026", 2, "Pat Lee", "Sam Ray"), (file.Id, file.Library, file.Folder, file.BinStage, file.DeletedBy, file.CreatedBy));
        Assert.True(file.IsDeleted);
        Assert.Null(file.WebUrl);
        Assert.Equal("Second-Stage Recycle Bin", FoundFileFields.StatusOf(file));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), file.Deleted);

        var folder = GraphUserRecords.FromBin(JsonDocument.Parse("""{"Id":"b2","LeafName":"Old","DirName":"sites/finance/Shared Documents","ItemType":5,"ItemState":1}""").RootElement, Site())!;
        Assert.Equal((true, "Shared Documents", "", "Recycle Bin"), (folder.IsFolder, folder.Library, folder.Folder, FoundFileFields.StatusOf(folder)));
        // Versions, list items and whole lists aren't files to find.
        Assert.Null(GraphUserRecords.FromBin(JsonDocument.Parse("""{"Id":"v","LeafName":"x","DirName":"sites/finance/Lists/Tasks","ItemType":3,"ItemState":1}""").RootElement, Site()));
    }

    static (GraphUserRecords Records, GraphChangeExecutor Executor, FakeHttp Http) WithSharePoint(FakeHttp http)
    {
        http.On(HttpMethod.Get, "sites/root?$select=siteCollection", """{"siteCollection":{"hostname":"contoso.sharepoint.com"}}""");
        var graph = new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance);
        var sharePoint = new SharePointAdmin(http.Client(), new FakeTokens(), graph, NoDelay.Instance);
        var store = new MemoryEntityStore();
        return (new GraphUserRecords(graph, store, null, sharePoint), new GraphChangeExecutor(graph, store, NoDelay.Instance, sharePoint: sharePoint), http);
    }

    [Fact]
    public async Task A_sites_recycle_bins_are_read_whole_and_a_refusal_says_only_admins_can_see_them()
    {
        var (records, _, http) = WithSharePoint(new FakeHttp()
            .On(HttpMethod.Get, "/sites/finance/_api/site/RecycleBin?", """{"value":[{"Id":"b1","LeafName":"A.docx","DirName":"sites/finance/Shared Documents","ItemType":1,"ItemState":1}]}"""));
        var bin = new List<FoundFile>();
        await foreach (var f in records.ListRecycleBinAsync(Site(), Ct))
            bin.Add(f);
        Assert.Equal(["A.docx"], bin.Select(f => f.Name));
        Assert.Contains(http.Requests, r => r.Url.Query.Contains("ItemState", StringComparison.Ordinal));

        var (locked, _, _) = WithSharePoint(new FakeHttp()
            .On(HttpMethod.Get, "_api/site/RecycleBin?", """{"odata.error":{"message":{"value":"Access denied."}}}""", HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<SiteAccessException>(async () =>
        {
            await foreach (var _ in locked.ListRecycleBinAsync(Site(), Ct))
            {
            }
        });
    }

    [Fact]
    public async Task A_restored_file_is_found_in_the_library_the_bin_named_by_its_address()
    {
        var (records, _, _) = WithSharePoint(new FakeHttp()
            .On(HttpMethod.Get, "sites/contoso.sharepoint.com:/sites/finance", """{"id":"host,site,web"}""")
            .On(HttpMethod.Get, "/drives?$select", """
                {"value":[{"id":"d2","name":"Archive","webUrl":"https://contoso.sharepoint.com/sites/finance/Archive"},
                          {"id":"d1","name":"Documents","webUrl":"https://contoso.sharepoint.com/sites/finance/Shared%20Documents"}]}
                """)
            .On(HttpMethod.Get, "drives/d1/root:/Projects/Budget.xlsx", """{"id":"i9","name":"Budget.xlsx","file":{},"size":7,"webUrl":"https://contoso.sharepoint.com/x"}"""));
        var deleted = File("Budget.xlsx", binStage: 1) with { Library = "Shared Documents", Folder = "Projects" };

        var back = await records.FindRestoredAsync(deleted, Ct);

        Assert.NotNull(back);
        Assert.Equal(("i9", "d1", "Documents", "Projects", false), (back.Id, back.DriveId, back.Library, back.Folder, back.IsDeleted));
    }

    // ---- The changes ---------------------------------------------------------------------------

    static readonly ChangeTarget FinanceTarget = new("site", "s1", "Finance", "finance");

    [Fact]
    public async Task Restore_puts_everything_back_in_one_call_and_checks_the_bin()
    {
        var (_, executor, http) = WithSharePoint(new FakeHttp()
            .On(HttpMethod.Post, "_api/site/RecycleBin/RestoreByIds", "{}")
            .On(HttpMethod.Get, "_api/site/RecycleBin?", """{"value":[]}"""));

        var result = await executor.ApplyAsync(new RestoreFiles(FinanceTarget, FinanceUrl, ["b1", "b2"], ["A.docx", "B.docx"]), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var sent = http.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal(["b1", "b2"], JsonDocument.Parse(sent.Body!).RootElement.GetProperty("ids").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Restore_tries_each_alone_when_one_cant_come_back_and_says_what_is_left()
    {
        var (_, executor, _) = WithSharePoint(new FakeHttp()
            .On(HttpMethod.Post, "_api/site/RecycleBin/RestoreByIds", r => r.Body!.Contains("\"b2\"", StringComparison.Ordinal)
                ? FakeHttp.Json("""{"odata.error":{"message":{"value":"A file with this name already exists."}}}""", HttpStatusCode.BadRequest)
                : FakeHttp.Json("{}"))
            .On(HttpMethod.Get, "_api/site/RecycleBin?", """{"value":[{"Id":"b2"}]}"""));

        var result = await executor.ApplyAsync(new RestoreFiles(FinanceTarget, FinanceUrl, ["b1", "b2"], ["A.docx", "B.docx"]), Ct);

        Assert.Equal(ChangeOutcome.Failed, result.Outcome);
        Assert.Contains("1 of 2", result.Detail);
        Assert.Contains("already exists", result.Detail);
        Assert.Equal(["b2"], Assert.IsType<RestoreFiles>(result.Remaining).BinIds);
    }

    [Fact]
    public async Task Giving_access_invites_by_object_ID_and_names_whoever_SharePoint_doesnt_know()
    {
        var (_, executor, http) = WithSharePoint(new FakeHttp()
            .On(HttpMethod.Post, "items/a/invite", r => r.Body!.Contains("\"u2\"", StringComparison.Ordinal)
                ? FakeHttp.Json("""{"error":{"code":"invalidRequest","message":"One or more users could not be resolved"}}""", HttpStatusCode.BadRequest)
                : FakeHttp.Json("""{"value":[{"id":"p1","roles":["write"]}]}""")));
        ChangeTarget pat = new("user", "u1", "Pat Lee", "pat@contoso.example"), sam = new("user", "u2", "Sam Ray", "sam@contoso.example");

        var one = await executor.ApplyAsync(new ShareFiles(FinanceTarget, [new FileRef("d1", "a", "A.docx")], [pat], CanEdit: true, SendEmail: false), Ct);
        Assert.Equal(ChangeOutcome.Applied, one.Outcome);
        var body = JsonDocument.Parse(http.Requests[^1].Body!).RootElement;
        Assert.Equal("u1", body.GetProperty("recipients")[0].GetProperty("objectId").GetString());
        Assert.Equal("write", body.GetProperty("roles")[0].GetString());
        Assert.True(body.GetProperty("requireSignIn").GetBoolean());
        Assert.False(body.GetProperty("sendInvitation").GetBoolean());

        var both = await executor.ApplyAsync(new ShareFiles(FinanceTarget, [new FileRef("d1", "a", "A.docx")], [pat, sam], CanEdit: false, SendEmail: true, "Here it is"), Ct);
        Assert.Equal(ChangeOutcome.Failed, both.Outcome);
        Assert.Contains("doesn't know Sam Ray", both.Detail);
    }

    [Fact]
    public async Task Copying_makes_the_folder_when_it_isnt_there_and_waits_for_each_copy()
    {
        var (_, executor, http) = WithSharePoint(new FakeHttp()
            .On(HttpMethod.Get, "sites/contoso-my.sharepoint.com:/personal/ada:/drive", """{"id":"dd"}""")
            .On(HttpMethod.Get, "drives/dd/root:/Recovered Files", """{"error":{"code":"itemNotFound","message":"Not found"}}""", HttpStatusCode.NotFound)
            .On(HttpMethod.Post, "drives/dd/root/children", """{"id":"folder1"}""")
            .On(HttpMethod.Post, "items/a/copy", _ =>
            {
                var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
                accepted.Headers.Location = new Uri("https://monitor.example/m1");
                return accepted;
            })
            .Once(HttpMethod.Get, "monitor.example/m1", """{"status":"inProgress"}""")
            .On(HttpMethod.Get, "monitor.example/m1", """{"status":"completed","resourceId":"new1"}"""));
        var change = new CopyFiles(new ChangeTarget("site", "od1", "Ada's OneDrive", "ada"), "https://contoso-my.sharepoint.com/personal/ada", "Recovered Files",
            [new FileRef("d1", "a", "A.docx")], "Finance");

        var result = await executor.ApplyAsync(change, Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var made = JsonDocument.Parse(http.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/root/children")).Body!).RootElement;
        Assert.Equal(("Recovered Files", "fail"), (made.GetProperty("name").GetString(), made.GetProperty("@microsoft.graph.conflictBehavior").GetString()));
        var copy = http.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/copy"));
        Assert.Contains("conflictBehavior=rename", Uri.UnescapeDataString(copy.Url.Query));
        Assert.Equal("folder1", JsonDocument.Parse(copy.Body!).RootElement.GetProperty("parentReference").GetProperty("id").GetString());
        // The progress link is read without the token: it isn't Graph's to have it.
        Assert.All(http.Requests.Where(r => r.Url.Host == "monitor.example"), r => Assert.False(r.Headers.ContainsKey("Authorization")));
        Assert.Equal(2, http.Requests.Count(r => r.Url.Host == "monitor.example"));
    }

    [Fact]
    public void Test_mode_guards_the_place_and_everyone_given_access()
    {
        var testSite = new ChangeTarget("site", "s9", "Test", "zz-tenantry-test-site");
        var testPerson = new ChangeTarget("user", "u9", "Test", "zz-tenantry-test-ada@contoso.example");
        var realPerson = new ChangeTarget("user", "u1", "Pat Lee", "pat@contoso.example");
        FileRef[] files = [new("d1", "a", "A.docx")];

        Assert.Null(WriteGuard.Refusal(new ShareFiles(testSite, files, [testPerson], false, false), testMode: true));
        Assert.Contains("Pat Lee", WriteGuard.Refusal(new ShareFiles(testSite, files, [testPerson, realPerson], false, false), testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new ShareFiles(FinanceTarget, files, [testPerson], false, false), testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new CopyFiles(FinanceTarget, FinanceUrl, "Recovered Files", files, "Test"), testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new RestoreFiles(FinanceTarget, FinanceUrl, ["b1"], ["A.docx"]), testMode: true));
    }

    [Fact]
    public void The_changes_read_as_what_they_do()
    {
        Assert.Equal("A.docx, B.docx and 3 more back where they were",
            new RestoreFiles(FinanceTarget, FinanceUrl, ["1", "2", "3", "4", "5"], ["A.docx", "B.docx", "C", "D", "E"]).After);
        Assert.Equal("Pat Lee and Sam Ray can edit (emailed a link)",
            new ShareFiles(FinanceTarget, [new("d", "a", "A.docx")], [new("user", "u1", "Pat Lee", null), new("user", "u2", "Sam Ray", null)], true, true).After);
        Assert.Equal("A.docx copied to \"Recovered Files\"", new CopyFiles(FinanceTarget, FinanceUrl, "Recovered Files", [new("d", "a", "A.docx")], "Finance").After);
    }

    [Fact]
    public void A_deleted_files_deletion_date_stands_in_for_when_it_was_last_changed()
    {
        var now = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        var lastMonth = new FileQuery(ModifiedAfter: now.AddDays(-30));
        Assert.True(lastMonth.Matches(File("a.docx", binStage: 1, deleted: now.AddDays(-3))));
        Assert.False(lastMonth.Matches(File("b.docx", binStage: 2, deleted: now.AddDays(-60))));
        Assert.True(new FileQuery(ModifiedBy: "pat").Matches(File("c.docx", binStage: 1)), "who deleted it counts as who changed it");
    }

    // ---- The page ------------------------------------------------------------------------------

    sealed class Index(IReadOnlyList<UserSummary> users, IReadOnlyList<GroupSummary> groups) : IDirectoryIndex
    {
        public UserSummary? User(string id) => users.FirstOrDefault(u => u.Id == id);
        public GroupSummary? Group(string id) => groups.FirstOrDefault(g => g.Id == id);
        public IEnumerable<UserSummary> AllUsers => users;
        public IEnumerable<GroupSummary> AllGroups => groups;
    }

    static async Task<(FindFilesViewModel Page, IReadOnlyList<SiteSummary> Sites, ChangeLog Log, IReadOnlyList<UserSummary> Users)> Demo()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var log = new ChangeLog(new MemoryEntityStore());
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), log, new MemoryTestModeStore(false), DemoUserRecords.Account);
        var sites = await directory.GetSitesAsync(Ct);
        var users = await directory.GetUsersAsync(Ct);
        var groups = await directory.GetGroupsAsync(Ct);
        return (new FindFilesViewModel(records, () => sites, new Index(users, groups), pipeline, Availability.Yes, DemoUserRecords.Account), sites, log, users);
    }

    static SiteSummary Visible(IReadOnlyList<SiteSummary> sites) => sites.First(s => s.OwnerName is not null && s.Kind == SiteKind.TeamSite && !s.IsArchived);

    static FoundFilePreviewViewModel Preview(FindFilesViewModel page, FoundFile row)
    {
        page.Results.PreviewRow(row);
        return Assert.IsType<FoundFilePreviewViewModel>(page.Results.PreviewModel);
    }

    static FoundFile[] Rows(FindFilesViewModel page) => [.. page.Results.Rows.Cast<FoundFile>()];

    [Fact]
    public void Recycle_bins_are_searched_too_unless_left_out() => SingleThread.Run(async () =>
    {
        var (page, sites, _, _) = await Demo();
        page.Add(Visible(sites));
        page.NameText = "o";
        await page.FindCommand.ExecuteAsync(null);
        Assert.Contains(Rows(page), f => f.IsDeleted);
        Assert.Contains(Rows(page), f => !f.IsDeleted);

        page.IncludeRecycleBins = false;
        await page.FindCommand.ExecuteAsync(null);
        Assert.DoesNotContain(Rows(page), f => f.IsDeleted);
    });

    [Fact]
    public void Restoring_puts_it_back_and_shows_it_as_it_is_now() => SingleThread.Run(async () =>
    {
        var (page, sites, log, _) = await Demo();
        page.Add(Visible(sites));
        page.NameText = "o";
        await page.FindCommand.ExecuteAsync(null);
        var deleted = Rows(page).First(f => f.IsDeleted);
        var preview = Preview(page, deleted);
        Assert.True(preview.IsDeleted);
        Assert.True(preview.Actions.OnlyDeleted && !preview.Actions.HasCurrent, "only Restore is offered for something in a bin");

        await preview.Actions.RestoreCommand.ExecuteAsync(null);

        Assert.StartsWith("Restored 1 item", preview.Actions.Note);
        var back = Rows(page).Single(f => f.Name == deleted.Name && f.Path.EndsWith(deleted.Folder, StringComparison.Ordinal) && f.Id != deleted.Id && f.PlaceId == deleted.PlaceId && f.Size == deleted.Size);
        Assert.False(back.IsDeleted);
        Assert.NotNull(back.WebUrl);
        Assert.DoesNotContain(Rows(page), f => f.Id == deleted.Id);
        Assert.Equal([deleted.Id], log.All().Select(e => e.Change).OfType<RestoreFiles>().Single().BinIds);
    });

    [Fact]
    public void Giving_access_and_copying_are_logged_and_say_what_happened() => SingleThread.Run(async () =>
    {
        var (page, sites, log, users) = await Demo();
        page.Add(Visible(sites));
        page.NameText = "e";
        await page.FindCommand.ExecuteAsync(null);
        var file = Rows(page).First(f => !f.IsDeleted && !f.IsFolder);
        var actions = Preview(page, file).Actions;
        Assert.False(actions.CanGiveAccess, "nobody chosen yet");

        var pat = users.First(u => u.AccountEnabled);
        actions.SharePicker.PickCommand.Execute(PickOption.Of(pat));
        Assert.True(actions.IsShareOpen && actions.CanGiveAccess);
        actions.ShareCanEdit = true;
        actions.ShareSendEmail = false;
        await actions.GiveAccessCommand.ExecuteAsync(null);

        Assert.Equal($"{pat.DisplayName} can now edit it.", actions.Note);
        Assert.False(actions.IsShareOpen);
        var shared = log.All().Select(e => e.Change).OfType<ShareFiles>().Single();
        Assert.Equal((pat.Id, true, false), (shared.People.Single().Id, shared.CanEdit, shared.SendEmail));

        var drive = sites.First(s => s.Kind == SiteKind.OneDrive && s.OwnerName is not null && !s.IsArchived);
        actions.CopyPicker.PickCommand.Execute(new PickOption(drive.Id, drive.Title, "", "site", drive.Url, ""));
        Assert.True(actions.IsCopyOpen);
        Assert.Equal(FoundFileActionsViewModel.DefaultCopyFolder, actions.CopyFolder);
        actions.CopyFolder = "Bad/Name";
        Assert.False(actions.CanCopyHere);
        actions.CopyFolder = "From Finance";
        await actions.CopyHereCommand.ExecuteAsync(null);

        Assert.Equal($"Copied it to \"From Finance\" in {drive.OwnerName}'s OneDrive.", actions.Note);
        var copied = log.All().Select(e => e.Change).OfType<CopyFiles>().Single();
        Assert.Equal((drive.Url, "From Finance", file.Id), (copied.SiteUrl, copied.Folder, copied.Files.Single().ItemId));
    });

    [Fact]
    public void Copying_to_a_place_the_admin_cant_open_adds_them_just_for_the_copy() => SingleThread.Run(async () =>
    {
        var (page, sites, log, _) = await Demo();
        page.Add(Visible(sites));
        page.NameText = "e";
        await page.FindCommand.ExecuteAsync(null);
        var actions = Preview(page, Rows(page).First(f => !f.IsDeleted && !f.IsFolder)).Actions;
        var locked = sites.First(s => s.OwnerName is null && s.Kind == SiteKind.TeamSite && !s.IsArchived);

        actions.CopyPicker.PickCommand.Execute(new PickOption(locked.Id, locked.Title, "", "site", locked.Url, ""));
        await actions.CopyHereCommand.ExecuteAsync(null);

        Assert.StartsWith("Copied it to", actions.Note);
        // Newest first: removed again, copied, added.
        var changes = log.All().Select(e => e.Change).ToArray();
        Assert.Equal([false, true], changes.OfType<SetSiteAdmin>().Select(a => a.New));
        Assert.IsType<CopyFiles>(changes[1]);
    });

    [Fact]
    public void Several_ticked_get_one_panel_that_restores_the_deleted_and_handles_the_rest() => SingleThread.Run(async () =>
    {
        var (page, sites, _, _) = await Demo();
        page.Add(Visible(sites));
        page.NameText = "o";
        await page.FindCommand.ExecuteAsync(null);
        var rows = Rows(page);
        object[] ticked = [.. rows.Where(f => f.IsDeleted).Take(2), .. rows.Where(f => !f.IsDeleted && !f.IsFolder).Take(3)];

        var panel = Assert.IsType<FoundFilesSelectionViewModel>(page.Results.SelectionPanel!(ticked, _ => { }));

        Assert.Equal("5 Ticked", panel.Heading);
        Assert.Contains("2 in a recycle bin", panel.Detail);
        Assert.Equal(("Restore 2", "Download 3"), (panel.Actions.RestoreText, panel.Actions.DownloadText));
        Assert.True(panel.Actions.CanRestore && panel.Actions.CanShare && panel.Actions.CanCopy);
        Assert.Contains("2 of the 5 ticked are in a recycle bin", panel.Actions.DeletedNote);

        await panel.Actions.RestoreCommand.ExecuteAsync(null);
        Assert.Equal("Restored 2 items to where they were.", panel.Actions.Note);
    });

    [Fact]
    public void Without_the_right_to_change_sites_only_download_is_offered() => SingleThread.Run(async () =>
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var sites = await directory.GetSitesAsync(Ct);
        var page = new FindFilesViewModel(records, () => sites, EmptyIndex.Instance, null, Availability.Yes, DemoUserRecords.Account);
        page.Add(Visible(sites));
        page.NameText = "o";
        await page.FindCommand.ExecuteAsync(null);

        var current = Preview(page, Rows(page).First(f => !f.IsDeleted && !f.IsFolder)).Actions;
        var deleted = Preview(page, Rows(page).First(f => f.IsDeleted)).Actions;

        Assert.True(current.CanDownload);
        Assert.False(current.CanShare || current.CanCopy || deleted.CanRestore);
        Assert.Contains("needs the right to change sites", deleted.RestoreTip);
    });
}
