using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Workflows;

namespace Tenantry.Tests;

/// <summary>The compromised-account response (4d-2): what it finds, what it offers to fix, and what it stages.</summary>
public sealed class CompromiseTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    static InboxRule Rule(string name, string? movesTo) => new("id", name, true, 1, "", false, [], false, movesTo);

    [Theory]
    [InlineData("Tidy", "Mailbox:\\RSS Feeds", "Moves Mail to RSS Feeds", true)]
    [InlineData("Tidy", "Conversation History", "Moves Mail to Conversation History", true)]
    [InlineData(".", null, "Has No Real Name", true)]
    [InlineData("Old mail", "Archive", "Moves Mail to Archive", false)]
    public void Rules_that_hide_mail_are_warning_signs(string name, string? movesTo, string what, bool likely) =>
        Assert.Equal<(string, bool)?>((what, likely), CompromiseViewModel.Suspicion(Rule(name, movesTo)));

    [Fact]
    public void A_rule_filing_mail_into_an_ordinary_folder_is_fine() => Assert.Null(CompromiseViewModel.Suspicion(Rule("Newsletters", "Newsletters")));

    sealed class Fake : IUserRecords, ISecurityRecords, IMailboxContents, IActivityRecords
    {
        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailDetails> GetMailAsync(string userId, string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AuthMethod>> GetAuthMethodsAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuthMethod>>(
            [
                new("pw", "passwordMethods", "Password", null, null, false),
                new("m1", "microsoftAuthenticatorMethods", "Microsoft Authenticator", "Pat's phone", Now.AddDays(-200), true),
                new("m2", "phoneMethods", "Mobile phone", "+1 555 0199", Now.AddDays(-1), true),
            ]);

        public Task<Available<IReadOnlyList<SignInEvent>>> GetSignInsAsync(string userId, SignInQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Available<IReadOnlyList<SignInEvent>>(
            [
                new(Now.AddDays(-20), "Outlook", "Boston, US", "203.0.113.4", true, null, null, null, null),
                new(Now.AddDays(-2), "Outlook", "Boston, US", "203.0.113.4", true, null, null, null, null),
                new(Now.AddDays(-1), "Office 365", "Lagos, NG", "198.51.100.7", true, null, null, null, null),
            ]));

        public Task<Available<UserRisk>> GetRiskAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(IReadOnlyList<RoleHeld> Active, Available<IReadOnlyList<RoleHeld>> Eligible)> GetRolesAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AppConsent>> GetAppConsentsAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AppConsent>>([new("g1", "Mail Helper Pro", ["Mail.Read", "Mail.Send"]), new("g2", "Old App", ["User.Read"])]);

        public Task<IReadOnlyList<PolicyMatch>> GetConditionalAccessAsync(string userId, bool isGuest, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<InboxRule>> GetInboxRulesAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboxRule>>(
            [
                new("pat\\1", "..", true, 1, "Forward everything", false, ["evil@outside.example"], false, null),
                new("pat\\2", "Newsletters", true, 2, "Move newsletters", false, [], false, "Newsletters"),
                new("pat\\3", "Old cleanup", false, 3, "Delete old", false, [], true, null),
            ]);

        public Task<FolderSharing> GetCalendarSharingAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MobileDevice>> GetMobileDevicesAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeletedItem>> GetDeletedItemsAsync(string address, string? subject, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TracedMessage>> TraceAsync(string address, TraceDirection direction, int days, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TraceEvent>> TraceDetailAsync(TracedMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Available<IReadOnlyList<AuditEvent>>> GetAuditAsync(string userId, bool byThem, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Available<IReadOnlyList<AuditEvent>>(
            [
                new(Now.AddDays(-1), "Consent to application", "ApplicationManagement", true, "Pat", "Mail Helper Pro", null),
                new(Now.AddDays(-90), "Consent to application", "ApplicationManagement", true, "Pat", "Old App", null),
            ]));

        public Task<Available<AppActivity>> GetAppActivityAsync(string userPrincipalName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    static readonly UserSummary Pat = new("u1", "Pat Lee", "pat@contoso.example", "pat@contoso.example", null, null, true, UserKind.Member,
        DateTimeOffset.UnixEpoch, null, ["Business Basic"],
        new MailboxInfo("User", "pat@contoso.example", [], ForwardingKind.External, "someone@outside.example", false, false, false, false));

    static async Task<(CompromiseViewModel Workflow, ChangePipeline Pipeline)> Checked()
    {
        var pipeline = new ChangePipeline(new NoExecutor(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var workflow = new CompromiseViewModel([Pat], new Fake(), pipeline, () => { }, () => Now, checkNow: false);
        await workflow.CheckAsync(Ct);
        return (workflow, pipeline);
    }

    sealed class NoExecutor : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task It_finds_the_usual_signs_and_ticks_a_fix_for_each()
    {
        var (workflow, _) = await Checked();
        var titles = workflow.Findings.Select(f => f.Title).ToArray();

        Assert.Contains("Forwarding Mail Outside the Organization", titles);
        Assert.Contains("Mailbox Rule (no name) Forwards Mail to evil@outside.example", titles);
        Assert.DoesNotContain(titles, t => t.Contains("Newsletters") || t.Contains("Old cleanup")); // moving is fine; a turned-off rule does nothing
        Assert.Contains("New Sign-In Method: Mobile phone", titles);
        Assert.DoesNotContain(titles, t => t.Contains("Microsoft Authenticator"));
        Assert.Contains("App Given Access Recently: Mail Helper Pro", titles);
        Assert.DoesNotContain(titles, t => t.Contains("Old App"));
        var country = Assert.Single(workflow.Findings, f => f.Title == "Signed In from a New Country: NG");
        Assert.False(country.HasFix);
        Assert.All(workflow.Findings.Where(f => f.HasFix), f => Assert.True(f.IsOn));
    }

    [Fact]
    public async Task The_response_resets_reregisters_and_signs_out_but_does_not_block_unless_ticked()
    {
        var (workflow, _) = await Checked();

        var plan = workflow.Plan();

        Assert.DoesNotContain(plan.Changes, c => c is SetSignInAllowed);
        Assert.True(Assert.Single(plan.Changes.OfType<ResetPassword>()).MustChangeAtNextSignIn);
        Assert.Equal(["m1", "m2"], plan.Changes.OfType<RemoveAuthMethod>().Select(m => m.MethodId).Order());
        Assert.Single(plan.Changes.OfType<RevokeSessions>());
        Assert.Contains(plan.Changes, c => c is SetInboxRuleEnabled { RuleIdentity: "pat\\1", New: false });
        Assert.Contains(plan.Changes, c => c is SetForwarding { NewTo: null });
        Assert.Contains(plan.Changes, c => c is RevokeAppConsent { GrantId: "g1" });

        workflow.Steps.Single(s => s.Key == "block").IsOn = true;
        workflow.Findings.Single(f => f.Fix is SetForwarding).IsOn = false;
        var blocked = workflow.Plan();
        Assert.IsType<SetSignInAllowed>(blocked.Changes[0]);
        Assert.DoesNotContain(blocked.Changes, c => c is SetForwarding);
    }

    [Fact]
    public async Task Staging_puts_the_plan_into_Pending_changes()
    {
        var (workflow, pipeline) = await Checked();

        workflow.StageCommand.Execute(null);

        Assert.Equal(workflow.Plan().Changes.Count, pipeline.Pending.Count);
    }
}
