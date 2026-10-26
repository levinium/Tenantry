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
                new("p1", "phoneMethods", "Phone", "+1 2065551234 (mobile)", null, true),
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
    public async Task A_phone_stages_once_its_number_is_valid_and_a_pass_stages_from_its_button()
    {
        var (tab, pipeline, _) = await Tab(SecurityPage.Methods);

        tab.NewPhoneType = "Office";
        tab.NewPhoneNumber = "2065551234";
        Assert.NotNull(tab.PhoneError);
        Assert.Empty(pipeline.Pending);

        tab.NewPhoneNumber = "+1 206 555 9999";
        Assert.Equal(("office", "+1 2065559999"), (Assert.IsType<AddPhoneMethod>(pipeline.Pending.Single()).PhoneType, ((AddPhoneMethod)pipeline.Pending[0]).Number));

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
