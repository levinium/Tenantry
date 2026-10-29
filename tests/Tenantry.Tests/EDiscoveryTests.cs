using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>eDiscovery (8f-1): reading Purview, the changes and how they're sent, the guard, and the case and search pages in the demo.</summary>
public sealed class EDiscoveryTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ChangeTarget TestCase = EdPlaces.CaseTarget("zz-tenantry-test-case");
    static readonly ChangeTarget TestSearch = EdPlaces.SearchTarget("zz-tenantry-test-search");
    static readonly ChangeTarget TestHold = EdPlaces.HoldTarget("zz-tenantry-test-hold");
    static readonly ChangeTarget Bo = new("mailbox", "u2", "zz-tenantry-test-bo", "zz-tenantry-test-bo@contoso.example");
    static readonly ChangeTarget Real = new("mailbox", "u9", "Pat Example", "pat@contoso.example");
    static readonly ChangeTarget TestSite = new("site", "https://contoso.sharepoint.example/sites/zz-tenantry-test-site", "Test site", "zz-tenantry-test-site");

    // ---- Words and checks ----------------------------------------------------------------------

    [Theory]
    [InlineData("from:a@b.example AND \"budget\"", null)]
    [InlineData("\"budget", "A quotation mark isn't closed.")]
    [InlineData("(a OR b", "A bracket isn't closed.")]
    [InlineData("a) OR (b", "A closing bracket comes before its opening one.")]
    [InlineData("budget and forecast", "Write \"AND\" in capitals to combine terms; in lower case it's searched for as a word.")]
    [InlineData("\"profit and loss\"", null)]
    [InlineData("", null)]
    public void Queries_are_checked_for_what_Purview_would_misread(string query, string? problem) => Assert.Equal(problem, EDiscovery.QueryProblem(query));

    [Theory]
    [InlineData("", "Give it a name.")]
    [InlineData("a/b", "Leave out these characters: \\ / \" < > | * ? : % & #")]
    [InlineData("Taken", "That name is already used.")]
    [InlineData("Fine name", null)]
    public void Names_are_checked(string name, string? problem) => Assert.Equal(problem, EDiscovery.NameProblem(name, ["taken"]));

    [Fact]
    public void Places_and_sizes_read_in_words()
    {
        Assert.Equal("All mailboxes, 2 sites", EDiscovery.PlacesText([EDiscovery.All], ["a", "b"], false));
        Assert.Equal("1 mailbox, Public folders", EDiscovery.PlacesText(["a"], [], true));
        Assert.Equal("Nowhere", EDiscovery.PlacesText([], [], false));
        Assert.Equal("1.5 KB", EDiscovery.Size(1536));
        Assert.Equal("Done", EDiscovery.SearchStatus("Completed"));
        Assert.Equal("Running", EDiscovery.SearchStatus("InProgress"));
    }

    // ---- Reading -------------------------------------------------------------------------------

    [Fact]
    public void A_search_s_counts_per_place_come_from_its_statistics()
    {
        const string stats = """
            {"ExchangeBinding":{"Search":{"ContentItems":12},"Sources":[{"Name":"a@contoso.example","ContentItems":10,"ContentSize":"1 MB"},{"Name":"b@contoso.example","ContentItems":2,"ContentSize":"2 KB"}]},
             "SharePointBinding":{"Sources":[{"Name":"https://contoso.sharepoint.example/sites/x","ContentItems":"5","ContentSize":"9 KB"}]}}
            """;
        var hits = GraphUserRecords.Hits(stats);
        Assert.Equal([("a@contoso.example", 10L), ("b@contoso.example", 2L), ("https://contoso.sharepoint.example/sites/x", 5L)], hits.Select(h => (h.Location, h.Items)));
        Assert.Empty(GraphUserRecords.Hits("not json"));
        Assert.Empty(GraphUserRecords.Hits(null));
    }

    [Fact]
    public async Task Cases_and_their_searches_are_read_and_a_search_is_listed_once()
    {
        var exchange = new FakeCmdlets((cmdlet, p) => (cmdlet, p?.GetValueOrDefault("Case") as string) switch
        {
            ("Get-ComplianceCase", _) => """[{"Identity":"c1","Name":"Case One","Status":"Active","CreatedDateTime":"2026-01-02T10:00:00.0000000","LastModifiedBy":"Avery"}]""",
            ("Get-ComplianceSearch", "Case One") => """[{"Name":"In case","Status":"Completed","CreatedTime":"2026-01-03T10:00:00.0000000"}]""",
            ("Get-ComplianceSearch", null) => """[{"Name":"In case","Status":"Completed"},{"Name":"Loose","Status":"NotStarted"}]""",
            _ => "[]",
        });
        var records = new GraphUserRecords(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), exchange);

        var ed = await records.GetEDiscoveryAsync(Ct);

        var c = Assert.Single(ed.Cases);
        Assert.False(c.IsClosed);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero), c.Created);
        Assert.Equal([("In case", "Case One"), ("Loose", null)], ed.Searches.Select(s => (s.Name, s.CaseName)));
        Assert.All(exchange.Endpoints, e => Assert.Equal(CmdletEndpoint.Compliance, e));
    }

    sealed class Recording(string name) : Tenantry.Core.Auth.ITokenSource
    {
        public List<string> Asked { get; } = [];
        public string? Account => "admin@contoso.example";
        public Task<string> GetTokenAsync(IReadOnlyList<string> scopes, string? claims = null, CancellationToken cancellationToken = default)
        {
            Asked.AddRange(scopes);
            return Task.FromResult(name);
        }
    }

    [Fact]
    public async Task Purview_calls_use_the_search_only_sign_in_and_everything_else_Tenantry_s_own()
    {
        var main = new Recording("main");
        var purview = new Recording("purview");
        var tokens = new Tenantry.Exchange.PurviewTokens(main, purview);

        Assert.Equal("purview", await tokens.GetTokenAsync([$"{Tenantry.Exchange.RestCmdletRunner.ComplianceHost}/.default"], cancellationToken: Ct));
        Assert.Equal("main", await tokens.GetTokenAsync(["https://outlook.office365.com/.default"], cancellationToken: Ct));
        Assert.Equal(["https://dataservice.o365filtering.com/.default"], purview.Asked);
        Assert.Equal(["https://outlook.office365.com/.default"], main.Asked);
    }

    // ---- Changes -------------------------------------------------------------------------------

    [Fact]
    public void The_guard_checks_every_place_a_search_or_hold_covers()
    {
        Assert.Null(WriteGuard.Refusal(new CreateSearch(TestSearch, null, "", [Bo], [TestSite], false, null), testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new CreateSearch(TestSearch, null, "", [Bo, Real], [], false, null), testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new CreateSearch(TestSearch, null, "", [EdChange.Everywhere("mailbox")], [], false, null), testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new CreateHold(EdPlaces.HoldTarget("Real hold"), "zz-tenantry-test-case", "", [Bo], [], false), testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new SetCaseMember(TestCase, Real, true), testMode: true));
        // Taking a real place off a test hold still touches the real place.
        Assert.NotNull(WriteGuard.Refusal(new UpdateHold(TestHold, "", "", [Bo, Real], [Bo], [], [], false, false), testMode: true));
    }

    [Fact]
    public void Releasing_content_is_a_caution_and_deleting_a_case_or_hold_needs_its_name()
    {
        Assert.True(new UpdateHold(TestHold, "", "", [Bo], [], [], [], false, false).IsCaution);
        Assert.False(new UpdateHold(TestHold, "", "", [], [Bo], [], [], false, false).IsCaution);
        Assert.True(new UpdateHold(TestHold, "", "", [Bo], [Bo], [], [], false, false).IsNoChange);
        Assert.True(new SetHoldEnabled(TestHold, true, false).IsCaution);
        Assert.False(new SetHoldEnabled(TestHold, false, true).IsCaution);
        Assert.True(new SetCaseClosed(TestCase, true, 1).IsCaution);
        Assert.False(new SetCaseClosed(TestCase, true, 0).IsCaution);
        Assert.Equal("zz-tenantry-test-case", new DeleteCase(TestCase, "d").ConfirmPhrase);
        Assert.Equal("zz-tenantry-test-hold", new DeleteHold(TestHold, "d").ConfirmPhrase);
        Assert.True(new UpdateSearch(TestSearch, "a", " a ", [Bo], [Bo], [], [], false, false).IsNoChange);
    }

    [Fact]
    public void Every_change_survives_the_change_log()
    {
        ChangeOp[] changes =
        [
            new CreateCase(TestCase, "d"), new SetCaseClosed(TestCase, true, 0), new SetCaseDescription(TestCase, null, "x"), new DeleteCase(TestCase, "d"),
            new SetCaseMember(TestCase, Bo, true), new CreateSearch(TestSearch, "c", "q", [Bo], [TestSite], true, null),
            new UpdateSearch(TestSearch, "a", "b", [Bo], [], [], [TestSite], false, true), new DeleteSearch(TestSearch, "d"),
            new CreateHold(TestHold, "c", "", [Bo], [], false), new UpdateHold(TestHold, "", "q", [Bo], [Bo], [], [], false, false),
            new SetHoldEnabled(TestHold, true, false), new DeleteHold(TestHold, "d"),
        ];
        foreach (var change in changes)
        {
            var back = JsonSerializer.Deserialize<ChangeOp>(JsonSerializer.Serialize(change));
            Assert.Equal(change.Summary, back!.Summary);
        }
    }

    static GraphChangeExecutor Executor(FakeCmdlets exchange) =>
        new(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);

    [Fact]
    public async Task A_hold_is_a_policy_and_its_rule_and_the_policy_goes_if_the_rule_fails()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet switch
        {
            "Get-CaseHoldPolicy" => """[{"Name":"zz-tenantry-test-hold"}]""",
            "New-CaseHoldRule" => throw new CmdletException("New-CaseHoldRule", 400, "Bad query"),
            _ => "[]",
        });

        var result = await Executor(exchange).ApplyAsync(new CreateHold(TestHold, "zz-tenantry-test-case", "subject:x", [Bo], [TestSite], false), Ct);

        Assert.Equal(ChangeOutcome.Failed, result.Outcome);
        Assert.Equal(["New-CaseHoldPolicy", "Get-CaseHoldPolicy", "New-CaseHoldRule", "Remove-CaseHoldPolicy"], exchange.Calls.Select(c => c.Cmdlet));
        Assert.Contains("\"ExchangeLocation\":[\"zz-tenantry-test-bo@contoso.example\"]", exchange.Calls[0].Parameters, StringComparison.Ordinal);
        Assert.Contains("\"SharePointLocation\":[\"https://contoso.sharepoint.example/sites/zz-tenantry-test-site\"]", exchange.Calls[0].Parameters, StringComparison.Ordinal);
        Assert.All(exchange.Endpoints, e => Assert.Equal(CmdletEndpoint.Compliance, e));
    }

    [Fact]
    public async Task Changing_a_search_sends_only_the_places_that_changed_then_runs_it()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-ComplianceSearch" ? """[{"Name":"zz-tenantry-test-search","ContentMatchQuery":"new"}]""" : "[]");
        var cy = Bo with { Id = "u3", Name = "zz-tenantry-test-cy", Address = "zz-tenantry-test-cy@contoso.example" };

        var result = await Executor(exchange).ApplyAsync(new UpdateSearch(TestSearch, "old", "new", [Bo], [cy], [], [], false, false), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var set = exchange.Calls.First(c => c.Cmdlet == "Set-ComplianceSearch").Parameters;
        Assert.Contains("\"AddExchangeLocation\":[\"zz-tenantry-test-cy@contoso.example\"]", set, StringComparison.Ordinal);
        Assert.Contains("\"RemoveExchangeLocation\":[\"zz-tenantry-test-bo@contoso.example\"]", set, StringComparison.Ordinal);
        Assert.DoesNotContain("SharePoint", set, StringComparison.Ordinal);
        Assert.Equal("Start-ComplianceSearch", exchange.Calls[^1].Cmdlet);
    }

    [Fact]
    public async Task All_mailboxes_go_to_Purview_as_All()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-ComplianceSearch" ? """[{"Name":"s"}]""" : "[]");

        await Executor(exchange).ApplyAsync(new CreateSearch(TestSearch, null, "", [EdChange.Everywhere("mailbox")], [], false, null), Ct);

        Assert.Contains("\"ExchangeLocation\":[\"All\"]", exchange.Calls[0].Parameters, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Case\"", exchange.Calls[0].Parameters, StringComparison.Ordinal);
    }

    [Fact]
    public void A_case_that_is_gone_reads_as_not_found()
    {
        Assert.True(new CmdletException("Get-ComplianceCase", 500,
            "|Microsoft.Exchange.Management.Tasks.ComplianceCaseTaskException|Unable to execute the task. Reason: The compliance case \"x\" doesn't exist. Please create the case.").IsNotFound);
        Assert.False(new CmdletException("Remove-ComplianceCase", 500,
            "|Microsoft.Exchange.Management.Tasks.ComplianceCaseTaskException|The compliance case \"x\", cannot be removed because it contains hold objects.").IsNotFound);
    }

    [Fact]
    public async Task A_hold_Purview_is_still_lifting_shows_as_being_deleted_with_nothing_to_click()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet switch
        {
            "Get-CaseHoldPolicy" => """[{"Name":"zz-tenantry-test-hold","Enabled":false,"Mode":"PendingDeletion","DistributionStatus":"Success","ExchangeLocation":["a@contoso.example"]}]""",
            _ => "[]",
        });
        var records = new GraphUserRecords(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), exchange);
        var eCase = new EdCase("c1", "zz-tenantry-test-case", null, false, null, null, null, null, null);
        var ed = new EdDirectory([eCase], []);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(new DemoTenantDirectory(userCount: 5)), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var ctx = new EdContext(records, EmptyIndex.Instance, pipeline, Availability.Yes, () => [], (_, _) => { }, () => { }, _ => { }, () => ed);
        var page = new EdCasePageViewModel(eCase, ctx);
        var holds = (AppSection)page.Sections.Single(s => s.Key == "holds");
        await holds.LoadAsync(Ct);

        var row = Assert.Single(holds.Cards[0].RowItems);
        Assert.Equal("Being Deleted", Assert.Single(row.Badges).Text);
        Assert.False(row.HasAction || row.HasAlt || row.HasEdit);
    }

    // ---- Pages, in the demo ---------------------------------------------------------------------

    static async Task<(DemoUserRecords Records, ChangePipeline Pipeline, EdContext Context, EdDirectory Directory)> Demo(List<object>? dialogs = null)
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var ed = await records.GetEDiscoveryAsync(Ct);
        var users = await directory.GetUsersAsync(Ct);
        var index = new ListIndex(users);
        var ctx = new EdContext(records, index, pipeline, Availability.Yes, () => [], (_, content) => dialogs?.Add(content), () => { }, _ => { }, () => ed);
        return (records, pipeline, ctx, ed);
    }

    sealed class ListIndex(IReadOnlyList<UserSummary> users) : IDirectoryIndex
    {
        public UserSummary? User(string id) => users.FirstOrDefault(u => u.Id == id);
        public GroupSummary? Group(string id) => null;
        public IEnumerable<UserSummary> AllUsers => users;
        public IEnumerable<GroupSummary> AllGroups => [];
    }

    [Fact]
    public async Task A_case_page_shows_its_parts_and_a_hold_is_turned_off_and_applied()
    {
        var (records, pipeline, ctx, ed) = await Demo();
        var page = new EdCasePageViewModel(ed.Cases.Single(c => c.Name == "Vendor Contract Review"), ctx);
        var holds = (AppSection)page.Sections.Single(s => s.Key == "holds");
        await holds.LoadAsync(Ct);
        var row = holds.Cards[0].RowItems.Single(r => r.Title == "Contract hold");

        Assert.Equal("Turn Off", row.AltText);
        row.AltCommand.Execute(null);

        var change = Assert.IsType<SetHoldEnabled>(Assert.Single(pipeline.Pending));
        Assert.True(change.IsCaution);
        await Task.Delay(20, Ct);
        Assert.Equal("Turning Off", holds.Cards[0].RowItems.Single(r => r.Title == "Contract hold").StateText);
        await pipeline.ApplyAsync(cancellationToken: Ct);
        var detail = await records.GetCaseDetailAsync(page.Case, Ct);
        Assert.False(detail.Holds.Single().Enabled);
    }

    [Fact]
    public async Task Closing_a_case_with_holds_says_they_stop_and_it_can_be_undone()
    {
        var (_, pipeline, ctx, ed) = await Demo();
        var page = new EdCasePageViewModel(ed.Cases.Single(c => c.Name == "Vendor Contract Review"), ctx);
        await ((AppSection)page.Sections[0]).LoadAsync(Ct);

        page.Buttons.Single(b => b.Text == "Close Case").ActCommand.Execute(null);

        var close = Assert.IsType<SetCaseClosed>(Assert.Single(pipeline.Pending));
        Assert.Contains("1 hold stops", close.After, StringComparison.Ordinal);
        page.Buttons.Single(b => b.Text == "Undo").ActCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public async Task A_closed_case_offers_no_new_searches_or_holds()
    {
        var (_, _, ctx, ed) = await Demo();
        var page = new EdCasePageViewModel(ed.Cases.Single(c => c.IsClosed), ctx);
        foreach (var key in new[] { "searches", "holds" })
        {
            var section = (AppSection)page.Sections.Single(s => s.Key == key);
            await section.LoadAsync(Ct);
            Assert.Null(section.Cards[0].Form);
        }
        Assert.Contains(page.Buttons, b => b.Text == "Reopen Case");
    }

    [Fact]
    public async Task The_editor_stages_a_new_search_and_checks_it_first()
    {
        var dialogs = new List<object>();
        var (records, pipeline, ctx, _) = await Demo(dialogs);
        ctx.NewSearch("Vendor Contract Review");
        var editor = Assert.IsType<EdItemEditorViewModel>(Assert.Single(dialogs));

        editor.SaveCommand.Execute(null);
        Assert.Contains("Give it a name.", editor.Problems);
        Assert.Contains("Choose at least one place for the search.", editor.Problems);

        editor.Name = "Invoices";
        editor.Query = "invoice and paid";
        editor.Mailboxes.Mode = editor.Mailboxes.Modes.Single(m => m.Value == PlacesEditor.All);
        editor.SaveCommand.Execute(null);
        Assert.Single(editor.Problems);
        editor.Query = "invoice AND paid";
        editor.SaveCommand.Execute(null);

        var made = Assert.IsType<CreateSearch>(Assert.Single(pipeline.Pending));
        Assert.Equal("Vendor Contract Review", made.CaseName);
        Assert.True(EdChange.IsEverywhere(made.Mailboxes));
        await pipeline.ApplyAsync(cancellationToken: Ct);
        Assert.True((await records.GetEdSearchAsync("Invoices", Ct)).HasRun);
    }

    [Fact]
    public async Task A_hold_offers_no_All_and_a_chosen_mailbox_goes_in_as_a_chip()
    {
        var dialogs = new List<object>();
        var (_, pipeline, ctx, ed) = await Demo(dialogs);
        var page = new EdCasePageViewModel(ed.Cases.Single(c => c.Name == "Former Employee Inquiry"), ctx);
        var holds = (AppSection)page.Sections.Single(s => s.Key == "holds");
        await holds.LoadAsync(Ct);
        ((AppButtonForm)holds.Cards[0].Form!).ActCommand.Execute(null);
        var editor = Assert.IsType<EdItemEditorViewModel>(Assert.Single(dialogs));

        Assert.DoesNotContain(editor.Mailboxes.Modes, m => m.Value == PlacesEditor.All);
        editor.Name = "Keep it";
        editor.Mailboxes.Mode = editor.Mailboxes.Modes.Single(m => m.Value == PlacesEditor.Chosen);
        editor.Mailboxes.Picker.Query = ctx.Index.AllUsers.First(u => u.Mailbox is not null).DisplayName;
        editor.Mailboxes.Picker.PickCommand.Execute(editor.Mailboxes.Picker.Results[0]);
        editor.SaveCommand.Execute(null);

        var hold = Assert.IsType<CreateHold>(Assert.Single(pipeline.Pending));
        Assert.Equal("Former Employee Inquiry", hold.CaseName);
        Assert.Single(hold.Mailboxes);
    }

    [Fact]
    public async Task A_search_page_shows_what_it_found_per_place_and_runs_again()
    {
        var (_, _, ctx, ed) = await Demo();
        var page = new EdSearchPageViewModel(ed.Searches.Single(s => s.Name == "Contract emails"), ctx);
        var found = (AppSection)page.Sections.Single(s => s.Key == "found");
        await found.LoadAsync(Ct);

        Assert.Equal(3, found.Cards[0].RowItems.Count);
        Assert.Contains(page.Badges, b => b.Text.EndsWith("Items", StringComparison.Ordinal));
        Assert.Contains(page.Buttons, b => b.Text == "Change Search");
        page.Dispose();
    }
}
