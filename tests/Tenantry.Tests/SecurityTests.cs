using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>The Security &amp; sign-in tab (4c-1): who a Conditional Access policy covers, what each change sends, and the tab's editing.</summary>
public sealed class SecurityTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ChangeTarget Bo = new("user", "u2", "zz-tenantry-test-bo", "zz-tenantry-test-bo@contoso.example");

    // ---- Conditional Access -----------------------------------------------------------------

    static JsonElement Policy(string users, string grant = """{"operator":"OR","builtInControls":["mfa"]}""", string state = "enabled") =>
        JsonDocument.Parse(string.Concat(
            "{\"displayName\":\"P\",\"state\":\"", state, "\",\"conditions\":{\"users\":", users,
            ",\"applications\":{\"includeApplications\":[\"All\"]}},\"grantControls\":", grant, "}")).RootElement;

    static readonly Dictionary<string, string> Groups = new() { ["g-fin"] = "Finance", ["g-bg"] = "Break glass" };
    static readonly Dictionary<string, string> Roles = new() { ["r-ga"] = "Global Administrator" };

    [Fact]
    public void A_policy_covers_someone_through_everyone_a_group_or_a_role_and_an_exclusion_wins()
    {
        var everyone = GraphUserRecords.Match(Policy("""{"includeUsers":["All"]}"""), "u2", false, Groups, Roles);
        var byGroup = GraphUserRecords.Match(Policy("""{"includeUsers":[],"includeGroups":["g-fin"]}"""), "u2", false, Groups, Roles);
        var byRole = GraphUserRecords.Match(Policy("""{"includeRoles":["r-ga"]}"""), "u2", false, Groups, Roles);
        var excluded = GraphUserRecords.Match(Policy("""{"includeUsers":["All"],"excludeGroups":["g-bg"]}"""), "u2", false, Groups, Roles);
        var other = GraphUserRecords.Match(Policy("""{"includeUsers":["someone-else"]}"""), "u2", false, Groups, Roles);

        Assert.Equal((PolicyFit.Applies, "Everyone", "Require MFA", "All cloud apps", "On"), (everyone.Fit, everyone.Why, everyone.Controls, everyone.Apps, everyone.State));
        Assert.Equal("Member of Finance", byGroup.Why);
        Assert.Equal("Holds Global Administrator", byRole.Why);
        Assert.Equal((PolicyFit.Excluded, "Excluded: member of Break glass"), (excluded.Fit, excluded.Why));
        Assert.Equal(PolicyFit.NotTargeted, other.Fit);
    }

    [Fact]
    public void Guests_are_covered_by_guest_conditions_and_controls_read_as_words()
    {
        var policy = Policy("""{"includeGuestsOrExternalUsers":{"guestOrExternalUserTypes":"b2bCollaborationGuest"}}""",
            """{"operator":"AND","builtInControls":["mfa","compliantDevice"]}""", "enabledForReportingButNotEnforced");

        var guest = GraphUserRecords.Match(policy, "g1", true, Groups, Roles);
        var member = GraphUserRecords.Match(policy, "u2", false, Groups, Roles);

        Assert.Equal((PolicyFit.Applies, "Report-only", "Require MFA and Require a compliant device"), (guest.Fit, guest.State, guest.Controls));
        Assert.Equal(PolicyFit.NotTargeted, member.Fit);
    }

    // ---- Phone numbers ----------------------------------------------------------------------

    [Theory]
    [InlineData("+1 206 555 1234", "+1 2065551234")]
    [InlineData("+44 20-7946-0018", "+44 2079460018")]
    [InlineData("+1 2065551234x55", "+1 2065551234x55")]
    [InlineData("2065551234", null)]
    [InlineData("+12065551234", null)]
    [InlineData("+1 phone", null)]
    public void Phone_numbers_take_the_country_code_then_a_space(string typed, string? graph) => Assert.Equal(graph, SecuritySection.NormalizePhone(typed));

    // ---- What's sent ------------------------------------------------------------------------

    static GraphChangeExecutor Executor(FakeHttp http) =>
        new(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

    [Fact]
    public async Task Removing_a_method_deletes_it_from_its_own_collection_and_reads_back_until_its_gone()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Delete, "/users/u2/authentication/phoneMethods/p1", "")
            .On(HttpMethod.Get, "/users/u2/authentication/methods", """{"value":[{"id":"pw","@odata.type":"#microsoft.graph.passwordAuthenticationMethod"}]}""");

        var result = await Executor(http).ApplyAsync(new RemoveAuthMethod(Bo, "p1", "phoneMethods", "Phone"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Contains(http.Requests, r => r.Method == HttpMethod.Delete && r.PathAndQuery.EndsWith("/phoneMethods/p1", StringComparison.Ordinal));
    }

    const string Mobile = "3179e48a-750b-4051-897c-87b9720928f7";
    const string DefaultRefusal = """{"error":{"code":"badRequest","message":"The requested authentication method id of [x] matches the user's current default authentication method, and cannot be deleted until the default authentication method is changed"}}""";
    const string TwoPhones = $$"""{"value":[{"id":"pw","@odata.type":"#microsoft.graph.passwordAuthenticationMethod"},{"id":"{{Mobile}}","@odata.type":"#microsoft.graph.phoneAuthenticationMethod","phoneType":"mobile"},{"id":"e37fc753-ff3b-4958-9484-eaa9425c82bc","@odata.type":"#microsoft.graph.phoneAuthenticationMethod","phoneType":"office"}]}""";

    [Fact]
    public async Task Removing_the_default_method_moves_the_default_to_one_they_keep_then_removes_it()
    {
        var http = new FakeHttp()
            .Once(HttpMethod.Delete, $"/phoneMethods/{Mobile}", DefaultRefusal, System.Net.HttpStatusCode.BadRequest)
            .On(HttpMethod.Delete, $"/phoneMethods/{Mobile}", "")
            .Once(HttpMethod.Get, "/users/u2/authentication/methods", TwoPhones)
            .On(HttpMethod.Get, "/users/u2/authentication/methods", """{"value":[{"id":"pw","@odata.type":"#microsoft.graph.passwordAuthenticationMethod"}]}""")
            .On(HttpMethod.Patch, "/beta/users/u2/authentication/signInPreferences", "");

        var result = await Executor(http).ApplyAsync(new RemoveAuthMethod(Bo, Mobile, "phoneMethods", "Phone"), Ct);

        Assert.Equal((ChangeOutcome.Applied, "Their default sign-in method is now their office phone."), (result.Outcome, result.Detail));
        var patch = Assert.Single(http.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Equal("voiceOffice", patch.Json!["userPreferredMethodForSecondaryAuthentication"]!.GetValue<string>());
        Assert.Equal(2, http.Requests.Count(r => r.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task When_no_kept_method_can_be_the_default_the_removal_says_what_to_do()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Delete, $"/phoneMethods/{Mobile}", DefaultRefusal, System.Net.HttpStatusCode.BadRequest)
            .On(HttpMethod.Get, "/users/u2/authentication/methods", TwoPhones)
            .On(HttpMethod.Patch, "/beta/users/u2/authentication/signInPreferences",
                """{"error":{"code":"badRequest","message":"Authentication method policy is not enabled for this user."}}""", System.Net.HttpStatusCode.BadRequest);

        var result = await Executor(http).ApplyAsync(new RemoveAuthMethod(Bo, Mobile, "phoneMethods", "Phone"), Ct);

        Assert.Equal(ChangeOutcome.Failed, result.Outcome);
        Assert.Contains("Remove their other methods first", result.Detail);
        Assert.Single(http.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task The_default_method_is_read_from_sign_in_preferences()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/users/u2/authentication/methods", TwoPhones)
            .On(HttpMethod.Get, "/beta/users/u2/authentication/hardwareOathMethods", """{"value":[]}""")
            .On(HttpMethod.Get, "/beta/users/u2/authentication/signInPreferences", """{"userPreferredMethodForSecondaryAuthentication":"sms"}""");
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

        var methods = await records.GetAuthMethodsAsync("u2", Ct);

        Assert.Equal([Mobile], methods.Where(m => m.IsDefault).Select(m => m.Id));
    }

    [Fact]
    public void Several_removals_take_the_default_last()
    {
        AuthMethod Method(string id, bool isDefault = false) => new(id, "phoneMethods", "Phone", null, null, true, IsDefault: isDefault);

        Assert.Equal(["b", "c", "a"], AuthMethod.RemovalOrder([Method("a", true), Method("b"), Method("c")]).Select(m => m.Id));
    }

    [Fact]
    public async Task Changing_a_phone_patches_its_own_slot_and_reads_the_new_number_back()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Patch, "/users/u2/authentication/phoneMethods/3179e48a-750b-4051-897c-87b9720928f7", "")
            .On(HttpMethod.Get, "/users/u2/authentication/phoneMethods/3179e48a-750b-4051-897c-87b9720928f7",
                """{"id":"3179e48a-750b-4051-897c-87b9720928f7","phoneNumber":"+1 2065550000","phoneType":"mobile"}""");

        var result = await Executor(http).ApplyAsync(
            new UpdatePhoneMethod(Bo, "3179e48a-750b-4051-897c-87b9720928f7", "mobile", "+1 2065551234", "+1 2065550000"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var body = System.Text.Json.JsonDocument.Parse(http.Requests.First().Body!).RootElement;
        Assert.Equal(("+1 2065550000", "mobile"), (body.GetProperty("phoneNumber").GetString(), body.GetProperty("phoneType").GetString()));
    }

    [Fact]
    public async Task A_password_reset_email_is_added_when_they_have_none_and_patched_when_they_do()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "/users/u2/authentication/emailMethods", """{"id":"e1","emailAddress":"bo@example.com"}""")
            .On(HttpMethod.Patch, "/users/u2/authentication/emailMethods/e1", "")
            .On(HttpMethod.Get, "/users/u2/authentication/emailMethods", """{"value":[{"id":"e1","emailAddress":"bo@example.com"}]}""");

        Assert.Equal(ChangeOutcome.Applied, (await Executor(http).ApplyAsync(new SetEmailMethod(Bo, null, null, "bo@example.com"), Ct)).Outcome);
        Assert.Equal(ChangeOutcome.Applied, (await Executor(http).ApplyAsync(new SetEmailMethod(Bo, "e1", "old@example.com", "bo@example.com"), Ct)).Outcome);
        Assert.Equal([HttpMethod.Post, HttpMethod.Get, HttpMethod.Patch, HttpMethod.Get], http.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task A_hardware_token_is_added_and_assigned_in_one_preview_request_then_activated_with_its_code()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "/beta/directory/authenticationMethodDevices/hardwareOathDevices", """{"id":"d1","status":"assigned"}""")
            .On(HttpMethod.Get, "/beta/users/u2/authentication/hardwareOathMethods", """{"value":[{"id":"m1","device":{"serialNumber":"SN123"}}]}""")
            .On(HttpMethod.Post, "/beta/users/u2/authentication/hardwareOathMethods/m1/activate", "");

        var added = await Executor(http).ApplyAsync(new AddHardwareToken(Bo, "SN123", "Contoso", "T1", 30, "hmacsha1", "JBSWY3DPEHPK3PXPJBSWY3DP"), Ct);
        var activated = await Executor(http).ApplyAsync(new ActivateHardwareToken(Bo, "m1", "SN123", "123456"), Ct);

        Assert.Equal((ChangeOutcome.Applied, ChangeOutcome.Applied), (added.Outcome, activated.Outcome));
        var body = JsonDocument.Parse(http.Requests[0].Body!).RootElement;
        Assert.Equal(("u2", "JBSWY3DPEHPK3PXPJBSWY3DP", 30), (body.GetProperty("assignTo").GetProperty("id").GetString(), body.GetProperty("secretKey").GetString(),
            body.GetProperty("timeIntervalInSeconds").GetInt32()));
        Assert.Equal("123456", JsonDocument.Parse(http.Requests.Last().Body!).RootElement.GetProperty("verificationCode").GetString());
    }

    [Fact]
    public void A_tokens_secret_key_and_activation_code_never_reach_the_log()
    {
        var json = JsonSerializer.Serialize<ChangeOp>(new AddHardwareToken(Bo, "SN123", "Contoso", "T1", 30, "hmacsha1", "JBSWY3DPEHPK3PXPJBSWY3DP"))
                   + JsonSerializer.Serialize<ChangeOp>(new ActivateHardwareToken(Bo, "m1", "SN123", "123456"));
        Assert.DoesNotContain("JBSWY3DP", json, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", json, StringComparison.Ordinal);
        Assert.Contains("SN123", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_now_skips_the_tray_but_not_Test_mode_or_the_log()
    {
        var log = new ChangeLog(new MemoryEntityStore());
        var pipeline = new ChangePipeline(new Noop(), log, new MemoryTestModeStore(true), "admin");
        var applied = new List<ChangeOp>();
        pipeline.Applied += (_, changes) => applied.AddRange(changes);

        var real = await pipeline.ApplyNowAsync(new CreateTemporaryAccessPass(new ChangeTarget("user", "u9", "Real Person", "real@contoso.example"), 60, true), Ct);
        var test = await pipeline.ApplyNowAsync(new CreateTemporaryAccessPass(Bo, 60, true), Ct);

        Assert.Equal((ChangeOutcome.Refused, ChangeOutcome.Applied), (real.Outcome, test.Outcome));
        Assert.Empty(pipeline.Pending);
        Assert.Equal(2, log.All().Count());
        Assert.Equal("u2", Assert.Single(applied).Target.Id);
    }

    [Fact]
    public async Task The_Authenticator_guide_makes_the_pass_then_shows_it_with_the_steps()
    {
        var made = 0;
        var guide = new AuthenticatorSetupViewModel("Bo", "zz-tenantry-test-bo@contoso.example",
            (minutes, once) =>
            {
                made++;
                return Task.FromResult(minutes == 60 && once ? new ApplyResult(ChangeOutcome.Applied, Reveal: "Tap-123!") : new ApplyResult(ChangeOutcome.Failed, "wrong"));
            }, () => { });
        Assert.True(guide.IsStarting);

        await guide.CreatePassCommand.ExecuteAsync(null);

        Assert.Equal((1, "Tap-123!", true), (made, guide.Pass, guide.IsReady));
        Assert.False(guide.CreatePassCommand.CanExecute(null));
        Assert.Contains(guide.Steps, s => s.Contains("zz-tenantry-test-bo@contoso.example", StringComparison.Ordinal));
        Assert.Equal("https://aka.ms/mysecurityinfo", guide.Link);
    }

    [Fact]
    public async Task A_change_form_stays_open_under_its_row_when_something_else_is_staged()
    {
        var (tab, pipeline, _) = await Tab(SecurityPage.Methods);
        tab.Methods.Single(m => m.Kind == "Phone").ChangeCommand.Execute(null);
        var form = Assert.IsType<ContactMethodForm>(tab.Methods.Single(m => m.Kind == "Phone").Form);
        form.Value = "+1 206 555";

        // Removing the Authenticator rebuilds the list; the half-typed number stays where it was.
        tab.Methods.Single(m => m.Kind == "Microsoft Authenticator").RemoveCommand.Execute(null);

        Assert.Same(form, tab.Methods.Single(m => m.Kind == "Phone").Form);
        form.CancelCommand.Execute(null);
        Assert.Null(tab.Methods.Single(m => m.Kind == "Phone").Form);
        Assert.IsType<RemoveAuthMethod>(Assert.Single(pipeline.Pending));
    }

    [Fact]
    public async Task In_the_demo_the_guide_gets_a_pass_through_the_pipeline()
    {
        var directory = new Tenantry.Demo.DemoTenantDirectory();
        var pipeline = new ChangePipeline(new Tenantry.Demo.DemoChangeExecutor(directory), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var guide = new AuthenticatorSetupViewModel("Bo", Bo.Address!, (minutes, once) => pipeline.ApplyNowAsync(new CreateTemporaryAccessPass(Bo, minutes, once)), () => { });

        await guide.CreatePassCommand.ExecuteAsync(null);

        Assert.True(guide.IsReady, guide.Problem);
        Assert.StartsWith("Tap", guide.Pass, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hardware_token_form_waits_for_a_Base32_key_and_stages_one_change()
    {
        var (tab, pipeline, _) = await Tab(SecurityPage.Methods);
        tab.AddHardwareTokenCommand.Execute(null);
        var form = Assert.IsType<HardwareTokenForm>(tab.Form);
        (form.Serial, form.Manufacturer, form.Model, form.SecretKey) = ("SN123", "Contoso", "T1", "not base32!");
        Assert.NotNull(form.Hint);
        Assert.False(form.SaveCommand.CanExecute(null));

        form.SecretKey = "JBSW Y3DP EHPK 3PXP";
        form.SaveCommand.Execute(null);

        var token = Assert.IsType<AddHardwareToken>(pipeline.Pending.Single());
        Assert.Equal(("JBSWY3DPEHPK3PXP", 30, "hmacsha1"), (token.SecretKey, token.Seconds, token.Hash));
        Assert.Null(tab.Form);
        Assert.Contains(tab.Methods, m => m.Kind == "Hardware Token" && m.PendingText == "Adding");
    }

    [Fact]
    public async Task A_temporary_access_pass_is_handed_back_once_and_never_kept()
    {
        var http = new FakeHttp().On(HttpMethod.Post, "/temporaryAccessPassMethods", """{"id":"t1","temporaryAccessPass":"Tap-123!","lifetimeInMinutes":60}""");

        var result = await Executor(http).ApplyAsync(new CreateTemporaryAccessPass(Bo, 60, true), Ct);

        Assert.Equal("Tap-123!", result.Reveal);
        Assert.Equal("""{"lifetimeInMinutes":60,"isUsableOnce":true}""", http.Requests.Single().Body);
    }

    // ---- The tab ----------------------------------------------------------------------------

    sealed class Records : IUserRecords, ISecurityRecords
    {
        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailboxDelegate>>([]);
        public Task<MailDetails> GetMailAsync(string userId, string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AuthMethod>> GetAuthMethodsAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuthMethod>>(
            [
                new("pw", "passwordMethods", "Password", null, null, false),
                new("a1", "microsoftAuthenticatorMethods", "Microsoft Authenticator", "iPhone", null, true),
                new("p1", "phoneMethods", "Phone", "+1 2065551234 (mobile)", null, true, Value: "+1 2065551234", PhoneType: "mobile"),
            ]);
        public SignInQuery? LastQuery { get; private set; }

        public Task<Available<IReadOnlyList<SignInEvent>>> GetSignInsAsync(string userId, SignInQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(new Available<IReadOnlyList<SignInEvent>>(
            [
                new(DateTimeOffset.Now, "Outlook", "Springfield, US", "203.0.113.5", true, null, "Windows", null, null, "Browser", true),
                new(DateTimeOffset.Now, "Outlook", "Lagos, NG", "198.51.100.7", false, "Invalid password", null, null, null, "IMAP4", false),
                new(DateTimeOffset.Now, "Teams", "Springfield, US", "203.0.113.6", true, null, "iOS", null, null, "Mobile Apps and Desktop clients", true),
            ]));
        }
        public Task<Available<UserRisk>> GetRiskAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Available<UserRisk>.Because("Needs Microsoft Entra ID P2."));
        public Task<(IReadOnlyList<RoleHeld> Active, Available<IReadOnlyList<RoleHeld>> Eligible)> GetRolesAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<RoleHeld>, Available<IReadOnlyList<RoleHeld>>)>(([new RoleHeld("ra1", "User Administrator", "The whole organization")], new([])));
        public Task<IReadOnlyList<AppConsent>> GetAppConsentsAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AppConsent>>([new AppConsent("g1", "Mail Helper", ["Mail.Read"])]);
        public Task<IReadOnlyList<PolicyMatch>> GetConditionalAccessAsync(string userId, bool isGuest, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyMatch>>(
            [
                new("Require MFA", "On", PolicyFit.Applies, "Everyone", "Require MFA", "All cloud apps"),
                new("Admins only", "On", PolicyFit.NotTargeted, "Doesn't target them", "Block access", "All cloud apps"),
            ]);
    }

    sealed class Noop : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static UserSummary Person(UserSecurity? security) =>
        new("u2", "zz-tenantry-test-bo", "zz-tenantry-test-bo@contoso.example", "zz-tenantry-test-bo@contoso.example", null, null, true, UserKind.Member,
            DateTimeOffset.UnixEpoch, DateTimeOffset.Now, ["Business Basic"], Security: security);

    static async Task<(SecuritySection Tab, ChangePipeline Pipeline, UserDetailViewModel Page)> Tab(SecurityPage page, UserSecurity? security = null)
    {
        var pipeline = new ChangePipeline(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var view = new UserDetailViewModel(Person(security), pipeline: pipeline, canEdit: Availability.Yes, records: new Records());
        var tab = view.Sections.OfType<SecuritySection>().Single();
        tab.SelectedPage = page;
        await tab.LoadAsync(Ct);
        return (tab, pipeline, view);
    }

    [Fact]
    public async Task Methods_remove_one_by_one_or_all_but_the_password()
    {
        var (tab, pipeline, _) = await Tab(SecurityPage.Methods);
        Assert.False(tab.Methods.Single(m => m.Kind == "Password").CanRemove);

        tab.Methods.Single(m => m.Kind == "Phone").RemoveCommand.Execute(null);
        Assert.True(Assert.IsType<RemoveAuthMethod>(pipeline.Pending.Single()).IsCaution);

        tab.RemoveAllCommand.Execute(null);
        Assert.Equal(["a1", "p1"], pipeline.Pending.Cast<RemoveAuthMethod>().Select(m => m.MethodId).Order());
        Assert.False(tab.CanRemoveAll);
    }

    [Fact]
    public async Task Lost_phone_revokes_sessions_and_requires_re_registration_each_undone_by_pressing_again()
    {
        var (tab, pipeline, page) = await Tab(SecurityPage.Methods);

        tab.ToggleRevokeSessionsCommand.Execute(null);
        tab.ToggleReregisterCommand.Execute(null);

        Assert.IsType<RevokeSessions>(pipeline.Pending[0]);
        Assert.Equal(["a1", "p1"], pipeline.Pending.OfType<RemoveAuthMethod>().Select(m => m.MethodId).Order());
        Assert.Equal(("Undo Revoke Sessions", "Undo Re-Register"), (tab.RevokeText, tab.ReregisterText));
        // The record's own Sign out everywhere follows the same pending change.
        Assert.Equal("Undo Sign Out Everywhere", page.SignOutText);

        tab.ToggleReregisterCommand.Execute(null);
        tab.ToggleRevokeSessionsCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public async Task A_second_phone_is_added_from_its_form_the_mobile_is_changed_in_place_and_a_pass_stages_from_its_button()
    {
        var (tab, pipeline, _) = await Tab(SecurityPage.Methods);

        // They have a mobile, so the button offers another phone: alternate or office.
        Assert.Equal("Add Another Phone", tab.AddPhoneText);
        tab.AddPhoneCommand.Execute(null);
        var form = Assert.IsType<ContactMethodForm>(tab.Form);
        Assert.Equal(["Alternate Mobile", "Office"], form.PhoneTypes.Select(t => t.Name));
        form.PhoneTypes[1].IsChosen = true;
        form.Value = "2065551234";
        Assert.NotNull(form.Hint);
        Assert.False(form.SaveCommand.CanExecute(null));
        // A whole number is still only typed: nothing goes to Pending Changes until Add Phone.
        form.Value = "+1 206 555 9999";
        Assert.Null(form.Hint);
        Assert.Empty(pipeline.Pending);
        form.SaveCommand.Execute(null);
        Assert.Null(tab.Form);
        Assert.Equal(("office", "+1 2065559999"), (Assert.IsType<AddPhoneMethod>(pipeline.Pending.Single()).PhoneType, ((AddPhoneMethod)pipeline.Pending[0]).Number));
        // It shows as a row being added, with Undo.
        var adding = tab.Methods.Single(m => m.PendingText == "Adding");
        Assert.True(adding.CanUndo);

        // The mobile is changed in place: the same slot, its old number kept for the log.
        var mobile = tab.Methods.Single(m => m.Detail?.Contains("mobile", StringComparison.Ordinal) == true);
        mobile.ChangeCommand.Execute(null);
        // The change form opens under its own row, not with the add forms.
        Assert.Null(tab.Form);
        var change = Assert.IsType<ContactMethodForm>(mobile.Form);
        Assert.Equal("+1 2065551234", change.Value);
        change.Value = "+1 206 555 0000";
        change.SaveCommand.Execute(null);
        var update = Assert.IsType<UpdatePhoneMethod>(pipeline.Pending.Last());
        Assert.Equal(("p1", "+1 2065551234", "+1 2065550000"), (update.MethodId, update.OldNumber, update.NewNumber));
        Assert.StartsWith("Changing to", tab.Methods.Single(m => m.Detail?.Contains("mobile", StringComparison.Ordinal) == true).PendingText);
        adding.UndoCommand.Execute(null);
        Assert.IsType<UpdatePhoneMethod>(Assert.Single(pipeline.Pending));
        pipeline.Clear();

        tab.TapLifetime = "8 Hours";
        tab.CreateTapCommand.Execute(null);
        Assert.Equal(480, pipeline.Pending.OfType<CreateTemporaryAccessPass>().Single().Minutes);
    }

    [Fact]
    public async Task Roles_and_consents_are_taken_away_through_pending_changes()
    {
        var (tab, pipeline, _) = await Tab(SecurityPage.Access);

        tab.Roles.Single().ToggleCommand.Execute(null);
        tab.Consents.Single().ToggleCommand.Execute(null);

        Assert.Equal(2, pipeline.Pending.Count(c => c.IsCaution));
        Assert.True(tab.Roles.Single().IsRevoking);
        Assert.Equal("No roles they can activate.", tab.EligibleText);
    }

    [Fact]
    public async Task Sign_ins_narrow_by_place_client_and_search_and_type_and_result_go_to_the_log()
    {
        var pipeline = new ChangePipeline(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var records = new Records();
        var view = new UserDetailViewModel(Person(null), pipeline: pipeline, canEdit: Availability.Yes, records: records);
        var tab = view.Sections.OfType<SecuritySection>().Single();
        tab.SelectedPage = SecurityPage.SignIns;
        await tab.LoadAsync(Ct);

        Assert.Equal(["Anywhere", "Springfield, US (2)", "Lagos, NG (1)"], tab.LocationOptions.Select(o => o.Label));
        Assert.Contains(tab.ClientOptions, o => o.Value == "Legacy: IMAP4");

        tab.SelectedLocation = tab.LocationOptions.Single(o => o.Value == "Springfield, US");
        Assert.Equal(2, tab.SignIns.Count);
        Assert.StartsWith("Showing 2 of 3", tab.SignInsSummary);
        tab.SignInSearch = "198.51";
        Assert.Empty(tab.SignIns);

        tab.ClearSignInFiltersCommand.Execute(null);
        tab.SelectedClient = tab.ClientOptions.Single(o => o.Value == "Legacy: IMAP4");
        Assert.True(Assert.Single(tab.SignIns).IsLegacy);

        tab.SignInKind = "Non-Interactive";
        tab.SignInResult = "Failed";
        for (var i = 0; i < 50 && records.LastQuery?.Result != SignInResult.Failed; i++)
            await Task.Delay(10, Ct);
        Assert.Equal(new SignInQuery(7, SignInKind.NonInteractive, SignInResult.Failed), records.LastQuery);
        // The chosen client is still there after the reload, so it stays chosen.
        Assert.Equal("Legacy: IMAP4", tab.SelectedClient!.Value);
    }

    [Fact]
    public async Task Only_policies_that_cover_them_show_until_asked()
    {
        var (tab, _, _) = await Tab(SecurityPage.ConditionalAccess);
        Assert.Single(tab.Policies);

        tab.ShowAllPolicies = true;

        Assert.Equal(2, tab.Policies.Count);
        Assert.StartsWith("1 of 2 policies apply", tab.PoliciesSummary);
    }

    [Fact]
    public async Task An_admin_without_MFA_is_flagged_on_the_record()
    {
        var (tab, _, page) = await Tab(SecurityPage.Methods, new UserSecurity(false, true, ["email"], null));

        Assert.Contains(page.Health, h => h.Text == "Admin Without MFA" && h.Kind == BadgeKind.Danger);
        Assert.True(tab.HasMfaWarning);
        Assert.Equal("Admin without MFA", tab.Summary);
    }
}
