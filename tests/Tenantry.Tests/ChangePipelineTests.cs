using System.Net;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Editing;

namespace Tenantry.Tests;

public class ChangePipelineTests
{
    static readonly ChangeTarget Test = new("user", "t1", "zz-tenantry-test-ada", "zz-tenantry-test-ada@contoso.example");
    static readonly ChangeTarget Real = new("user", "r1", "Ada Lovelace", "ada@contoso.example");

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records what it was asked to do; answers from a table.</summary>
    sealed class FakeExecutor(Func<ChangeOp, ApplyResult>? answer = null) : IChangeExecutor
    {
        public List<ChangeOp> Applied { get; } = [];

        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default)
        {
            Applied.Add(change);
            return Task.FromResult(answer?.Invoke(change) ?? new ApplyResult(ChangeOutcome.Applied));
        }
    }

    static (ChangePipeline Pipeline, FakeExecutor Executor, ChangeLog Log) Pipeline(bool testMode, Func<ChangeOp, ApplyResult>? answer = null)
    {
        var executor = new FakeExecutor(answer);
        var log = new ChangeLog(new MemoryEntityStore());
        return (new ChangePipeline(executor, log, new MemoryTestModeStore(testMode), "admin@contoso.example"), executor, log);
    }

    [Fact]
    public void Restaging_keeps_the_original_before_and_staging_back_drops_the_change()
    {
        var (pipeline, _, _) = Pipeline(testMode: false);

        pipeline.Stage(new SetUserText(Real, "department", "Department", "Finance", "Operations"));
        pipeline.Stage(new SetUserText(Real, "department", "Department", "Operations", "IT"));
        var change = Assert.Single(pipeline.Pending);
        Assert.Equal("Department: Finance → IT", change.Summary);

        pipeline.Stage(new SetUserText(Real, "department", "Department", "IT", "Finance"));
        Assert.Empty(pipeline.Pending);

        // Different slots and different people are separate changes.
        pipeline.Stage(new SetUserText(Real, "department", "Department", "Finance", "IT"));
        pipeline.Stage(new SetUserText(Real, "jobTitle", "Job title", null, "Analyst"));
        pipeline.Stage(new SetSignInAllowed(Test, true, false));
        Assert.Equal(3, pipeline.Pending.Count);
    }

    [Fact]
    public async Task Test_mode_refuses_real_objects_before_anything_is_sent_and_logs_it()
    {
        var (pipeline, executor, log) = Pipeline(testMode: true);
        pipeline.Stage(new SetUserText(Real, "department", "Department", "Finance", "IT"));
        pipeline.Stage(new SetUserText(Test, "department", "Department", null, "IT"));

        var results = await pipeline.ApplyAsync(cancellationToken: Ct);

        Assert.Equal([ChangeOutcome.Refused, ChangeOutcome.Applied], results.Select(r => r.Result.Outcome));
        Assert.Equal([Test.Id], executor.Applied.Select(c => c.Target.Id));
        Assert.Contains("Test mode is on", results[0].Result.Detail);
        // The refused change stays in the tray; the applied one leaves.
        Assert.Equal(Real.Id, Assert.Single(pipeline.Pending).Target.Id);
        Assert.Equal(2, log.All().Count);
    }

    [Fact]
    public void A_renamed_real_user_is_still_real_to_the_guard()
    {
        var renamed = Real with { Name = "zz-tenantry-test-sneaky" };

        Assert.False(WriteGuard.IsTestObject(renamed));
        Assert.True(WriteGuard.IsTestObject(new ChangeTarget("group", "g", "zz-tenantry-test-group", null)));
    }

    [Fact]
    public async Task Failures_stay_in_the_tray_with_their_reason_and_everything_is_logged()
    {
        var (pipeline, _, log) = Pipeline(testMode: false, change => change is SetSignInAllowed
            ? new ApplyResult(ChangeOutcome.Failed, "Insufficient privileges")
            : new ApplyResult(ChangeOutcome.Applied));
        pipeline.Stage(new SetSignInAllowed(Real, true, false));
        pipeline.Stage(new SetUserText(Real, "jobTitle", "Job title", null, "Analyst"));

        await pipeline.ApplyAsync(cancellationToken: Ct);

        Assert.IsType<SetSignInAllowed>(Assert.Single(pipeline.Pending));
        var entries = log.All();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Outcome == ChangeOutcome.Failed && e.Detail == "Insufficient privileges" && e.Change is SetSignInAllowed);
        Assert.All(entries, e => Assert.Equal("admin@contoso.example", e.Account));
    }

    [Fact]
    public void Turning_test_mode_off_is_logged_with_who_did_it()
    {
        var (pipeline, _, log) = Pipeline(testMode: true);

        pipeline.SetTestMode(false, "Contoso");

        Assert.False(pipeline.IsTestMode);
        var entry = Assert.Single(log.All());
        Assert.Equal("Test mode: On → Off", entry.Change.Summary);
    }

    // ---- Graph executor -------------------------------------------------------------------

    [Fact]
    public async Task Graph_patches_reads_back_and_updates_the_local_copy()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Patch, "/users/t1", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            // Replication lag: the first read still shows the old value.
            .Once(HttpMethod.Get, "/users/t1?$select=id,department", _ => FakeHttp.Json("""{"id":"t1","department":"Finance"}"""))
            .On(HttpMethod.Get, "/users/t1?$select=id,department", """{"id":"t1","department":"IT"}""");
        var store = new MemoryEntityStore();
        store.Apply(DirectorySync.Users, [new EntityChange("t1", JsonNode.Parse("""{"id":"t1","displayName":"Ada","department":"Finance"}""")!.AsObject())]);
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store, NoDelay.Instance);

        var result = await executor.ApplyAsync(new SetUserText(Test, "department", "Department", "Finance", "IT"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("""{"department":"IT"}""", http.Requests.Single(r => r.Method == HttpMethod.Patch).Body);
        Assert.Contains("\"department\":\"IT\"", store.Get(DirectorySync.Users, "t1"));
        Assert.Contains("\"displayName\":\"Ada\"", store.Get(DirectorySync.Users, "t1"));
    }

    [Fact]
    public async Task A_phone_number_with_a_plus_is_read_back_as_applied()
    {
        // System.Text.Json writes "+" as +; Graph returns it plain. Both are the same value.
        var http = new FakeHttp()
            .On(HttpMethod.Patch, "/users/t1", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            .On(HttpMethod.Get, "/users/t1?$select=id,businessPhones", """{"id":"t1","businessPhones":["+1 555 0100"]}""");
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

        var result = await executor.ApplyAsync(new SetUserText(Test, "businessPhones", "Work phone", null, "+1 555 0100"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Contains("\"businessPhones\":[", http.Requests.Single(r => r.Method == HttpMethod.Patch).Body);
    }

    [Fact]
    public async Task Missing_permissions_show_a_one_step_grant_that_clears_when_done()
    {
        var calls = 0;
        var tenant = FakeConnector.Tenant() with
        {
            MissingPermissions = ["User-PasswordProfile.ReadWrite.All"],
            UpdatePermissions = (_, _) => { calls++; return Task.FromResult<IReadOnlyList<string>>([]); },
        };
        var vm = new MainViewModel(tenant);
        Assert.True(vm.HasMissingPermissions);
        Assert.Contains("User-PasswordProfile.ReadWrite.All", vm.PermissionsList);

        await vm.GrantPermissionsCommand.ExecuteAsync(null);

        Assert.Equal(1, calls);
        Assert.False(vm.HasMissingPermissions);
        Assert.Null(vm.PermissionsStatus);
    }

    [Fact]
    public async Task Clearing_a_value_sends_null_and_a_refusal_comes_back_in_plain_words()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Patch, "/users/t1", """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""", HttpStatusCode.Forbidden);
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

        var result = await executor.ApplyAsync(new SetUserText(Test, "department", "Department", "Finance", ""), Ct);

        Assert.Equal("""{"department":null}""", http.Requests.Single().Body);
        Assert.Equal(ChangeOutcome.Failed, result.Outcome);
        Assert.StartsWith("Not allowed: Insufficient privileges", result.Detail);
    }

    // ---- The screens ----------------------------------------------------------------------

    static UserSummary User(string id = "t1", string upn = "zz-tenantry-test-ada@contoso.example") =>
        new(id, "zz-tenantry-test-ada", upn, null, "Finance", null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, []);

    /// <summary>A field on the Account tab, once the tab has loaded.</summary>
    static async Task<EditableField> Field(UserDetailViewModel page, string property)
    {
        await page.Account.LoadAsync(Ct);
        return page.Account.Groups.SelectMany(g => g.Fields).Single(f => f.Property == property);
    }

    [Fact]
    public async Task Editing_a_field_stages_it_and_shows_it_pending_in_place()
    {
        var (pipeline, _, _) = Pipeline(testMode: true);
        var page = new UserDetailViewModel(User(), pipeline: pipeline, canEdit: Availability.Yes);
        var department = await Field(page, "department");

        department.EditCommand.Execute(null);
        Assert.Equal("Finance", department.Draft);
        department.Draft = "  IT  ";
        department.StageCommand.Execute(null);

        Assert.False(department.IsEditing);
        Assert.Equal("→ IT", department.PendingText);
        Assert.Equal("Department: Finance → IT", Assert.Single(pipeline.Pending).Summary);

        department.UnstageCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
        Assert.False(department.HasPending);
    }

    [Fact]
    public async Task A_name_cant_be_emptied_and_without_the_role_fields_say_why_they_cant_change()
    {
        var (pipeline, _, _) = Pipeline(testMode: true);
        var page = new UserDetailViewModel(User(), pipeline: pipeline, canEdit: Availability.Yes);
        var name = await Field(page, "displayName");
        name.EditCommand.Execute(null);
        name.Draft = " ";
        name.StageCommand.Execute(null);
        Assert.Equal("Display name can't be empty.", name.Problem);
        Assert.Empty(pipeline.Pending);

        var readOnly = new UserDetailViewModel(User(), pipeline: pipeline, canEdit: Availability.No("Requires the User Administrator role"));
        var locked = await Field(readOnly, "displayName");
        Assert.False(locked.CanEdit);
        Assert.Equal("Requires the User Administrator role", locked.EditTip);
    }

    [Fact]
    public void Sign_in_names_must_be_on_one_of_the_tenants_domains()
    {
        string[] domains = ["contoso.example", "contoso.onmicrosoft.example"];

        Assert.Null(Tenantry.ViewModels.Records.AccountSection.ValidateSignInName("ada@contoso.example", domains));
        Assert.Contains("isn't one of this tenant's domains", Tenantry.ViewModels.Records.AccountSection.ValidateSignInName("ada@gmail.example", domains));
        Assert.Contains("name@domain", Tenantry.ViewModels.Records.AccountSection.ValidateSignInName("ada lovelace", domains));
        // A sign-in name change is a caution change: it changes how they sign in.
        Assert.True(new SetUserText(Test, "userPrincipalName", "Sign-in name", "a@contoso.example", "b@contoso.example").IsCaution);
    }

    /// <summary>Details as the record page reads them live.</summary>
    sealed class FakeRecords : IUserRecords
    {
        public int Reads;

        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(new UserDetails(userId, new Dictionary<string, string?>
            {
                ["givenName"] = "Ada",
                ["surname"] = "Lovelace",
                ["displayName"] = "zz-tenantry-test-ada",
                ["usageLocation"] = "GB",
                ["businessPhones"] = "+44 20 0000 0000",
            }, "m1", "Charles Babbage", "PendingAcceptance", DateTimeOffset.Now.AddDays(-3)));
        }

        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["contoso.example"]);

        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserLicensing("GB", [], [], []));
    }

    [Fact]
    public async Task The_account_tab_reads_details_live_once_and_shows_choices_by_name()
    {
        var (pipeline, _, _) = Pipeline(testMode: true);
        var records = new FakeRecords();
        var page = new UserDetailViewModel(User(), pipeline: pipeline, canEdit: Availability.Yes, records: records);

        page.SelectedSection = page.Account;
        await page.Account.LoadAsync(Ct);
        await page.Account.LoadAsync(Ct);

        Assert.Equal(1, records.Reads);
        var fields = page.Account.Groups.SelectMany(g => g.Fields).ToDictionary(f => f.Property);
        Assert.Equal("Ada", fields["givenName"].Value);
        Assert.Equal("+44 20 0000 0000", fields["businessPhones"].Value);
        Assert.StartsWith("United Kingdom", fields["usageLocation"].Value);
        Assert.Equal("Charles Babbage", page.Account.ManagerText);
        Assert.StartsWith("Invitation not accepted yet", page.Account.GuestStatus);

        fields["usageLocation"].EditCommand.Execute(null);
        fields["usageLocation"].SelectedChoice = fields["usageLocation"].Choices!.Single(c => c.Value == "US");
        fields["usageLocation"].StageCommand.Execute(null);
        Assert.Equal("Usage location: GB → US", Assert.Single(pipeline.Pending).Summary);
    }

    [Fact]
    public async Task A_reset_password_is_shown_once_and_never_logged()
    {
        var (pipeline, _, log) = Pipeline(testMode: true, change => change is ResetPassword
            ? new ApplyResult(ChangeOutcome.Applied, Reveal: "Kp7r-Xw3n-Bq9t-Hm4s")
            : new ApplyResult(ChangeOutcome.Applied));
        Tenantry.ViewModels.Records.DialogViewModel? dialog = null;
        var tray = new PendingChangesViewModel(pipeline, "Contoso", showTestMode: true, () => Task.CompletedTask);
        var page = new UserDetailViewModel(User(), pipeline: pipeline, canEdit: Availability.Yes, openDialog: d => dialog = d, closeDialog: () => dialog = null);

        page.ResetPasswordCommand.Execute(null);
        var reset = Assert.IsType<Tenantry.ViewModels.Records.ResetPasswordViewModel>(dialog!.Content);
        reset.StageCommand.Execute(null);
        Assert.Null(dialog);
        page.SignOutEverywhereCommand.Execute(null);
        Assert.Equal(2, tray.Count);

        await tray.ApplyCommand.ExecuteAsync(null); // both take something away: confirm first
        await tray.ApplyCommand.ExecuteAsync(null);

        var shown = Assert.Single(tray.Reveals);
        Assert.Equal("Kp7r-Xw3n-Bq9t-Hm4s", shown.Secret);
        Assert.DoesNotContain(log.All(), e => (e.Detail ?? "").Contains("Kp7r", StringComparison.Ordinal));
        Assert.DoesNotContain("Kp7r", System.Text.Json.JsonSerializer.Serialize(log.All()), StringComparison.Ordinal);
        shown.DismissCommand.Execute(null);
        Assert.False(tray.HasReveals);
        Assert.Empty(tray.Reveals);
    }

    [Fact]
    public void Temporary_passwords_are_readable_and_meet_complexity()
    {
        var passwords = Enumerable.Range(0, 200).Select(_ => TemporaryPassword.Create()).ToArray();

        Assert.All(passwords, p =>
        {
            Assert.Matches("^([A-Z][a-z][2-9][a-z]-){3}[A-Z][a-z][2-9][a-z]$", p);
            Assert.DoesNotContain(p, c => "0O1lI".Contains(c));
        });
        Assert.Equal(passwords.Length, passwords.Distinct().Count());
    }

    [Fact]
    public void The_preview_opens_any_tab_in_a_popup()
    {
        Tenantry.ViewModels.Records.DialogViewModel? dialog = null;
        var page = new UserDetailViewModel(User(), openDialog: d => dialog = d);

        Assert.DoesNotContain(page.ManageRows, s => s.Key == "overview");
        page.ManageCommand.Execute(page.ManageRows.Single(s => s.Key == "account"));

        Assert.Equal(("Account", "zz-tenantry-test-ada"), (dialog!.Title, dialog.Subtitle));
        Assert.Same(page.Account, dialog.Content);
    }

    [Fact]
    public async Task Blocking_sign_in_asks_for_a_second_click_then_applies_and_refreshes()
    {
        var (pipeline, executor, _) = Pipeline(testMode: true);
        var refreshed = 0;
        var tray = new PendingChangesViewModel(pipeline, "Contoso", showTestMode: true, () => { refreshed++; return Task.CompletedTask; });
        var page = new UserDetailViewModel(User(), pipeline: pipeline, canEdit: Availability.Yes);

        Assert.Equal("Block sign-in", page.SignInActionText);
        page.ToggleSignInCommand.Execute(null);
        Assert.Equal("Undo sign-in change", page.SignInActionText);
        Assert.Equal(1, tray.Count);

        await tray.ApplyCommand.ExecuteAsync(null);
        Assert.True(tray.IsConfirming);
        Assert.Empty(executor.Applied);

        await tray.ApplyCommand.ExecuteAsync(null);
        Assert.Single(executor.Applied);
        Assert.Equal(0, tray.Count);
        Assert.Equal("1 change applied.", tray.Result);
        Assert.Equal(1, refreshed);
    }

    [Fact]
    public void Test_mode_turns_off_only_when_the_tenant_name_is_typed()
    {
        var (pipeline, _, _) = Pipeline(testMode: true);
        var tray = new PendingChangesViewModel(pipeline, "Contoso Ltd", showTestMode: true, () => Task.CompletedTask);
        Assert.True(tray.ShowBanner);

        tray.StartTurnOffCommand.Execute(null);
        tray.TurnOffText = "contoso";
        Assert.False(tray.TurnOffTestModeCommand.CanExecute(null));

        tray.TurnOffText = "contoso ltd";
        tray.TurnOffTestModeCommand.Execute(null);
        Assert.False(pipeline.IsTestMode);
        Assert.False(tray.ShowBanner);

        tray.TurnOnTestModeCommand.Execute(null);
        Assert.True(pipeline.IsTestMode);
    }

    [Fact]
    public async Task In_the_demo_an_applied_edit_shows_in_the_list_and_on_open_pages()
    {
        // As the app's connector builds the demo: generated people, edited in memory, no Test mode.
        var directory = new Tenantry.Demo.DemoTenantDirectory();
        var tenant = new ConnectedTenant("Northgate (demo)", "admin@northgate.example", directory,
            new TenantContext(new HashSet<string>([DirectoryRoles.UserAdministrator]), new HashSet<string>(), true),
            IsDemo: true,
            Changes: new ChangePipeline(new Tenantry.Demo.DemoChangeExecutor(directory), new ChangeLog(new MemoryEntityStore()),
                new MemoryTestModeStore(false), "admin@northgate.example"));
        var vm = new MainViewModel(tenant);
        await vm.InitializeAsync(Ct);
        var someone = vm.Users.Rows.Cast<UserSummary>().First(u => u.Department is not null);
        vm.OpenUser(someone);
        var page = Assert.IsType<UserDetailViewModel>(vm.SelectedTab);
        Assert.False(vm.Pending!.ShowBanner);

        var department = await Field(page, "department");
        department.EditCommand.Execute(null);
        department.Draft = "Moved Department";
        department.StageCommand.Execute(null);
        await vm.Pending.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Moved Department", page.User.Department);
        Assert.Equal("Moved Department", vm.Users.Rows.Cast<UserSummary>().Single(u => u.Id == someone.Id).Department);
    }
}
