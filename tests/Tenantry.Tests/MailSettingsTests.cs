using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>The Mail tab's settings (4b-2): what each change sends to Exchange, and the tab's editing.</summary>
public sealed class MailSettingsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ChangeTarget Bo = new("user", "u2", "zz-tenantry-test-bo", "zz-tenantry-test-bo@contoso.example");

    static GraphChangeExecutor Executor(FakeCmdlets exchange) =>
        new(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);

    // ---- What's sent ------------------------------------------------------------------------

    [Fact]
    public async Task A_size_limit_is_read_back_though_exchange_adds_the_bytes()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-Mailbox"
            ? """[{"ExternalDirectoryObjectId":"u2","MaxSendSize":"25 MB (26,214,400 bytes)"}]""" : "[]");

        var result = await Executor(exchange).ApplyAsync(
            new SetMailboxValue(Bo, "MaxSendSize", "Largest message they can send", "35 MB", "25 MB", JsonValue.Create("25 MB")), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("Set-Mailbox", exchange.Calls[0].Cmdlet);
        Assert.Contains("\"MaxSendSize\":\"25 MB\"", exchange.Calls[0].Parameters);
    }

    [Fact]
    public async Task Smtp_sending_is_inverted_because_exchanges_parameter_says_disabled()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-CASMailbox"
            ? """[{"ExternalDirectoryObjectId":"u2","SmtpClientAuthenticationDisabled":false}]""" : "[]");

        var result = await Executor(exchange).ApplyAsync(new SetEmailApp(Bo, "SmtpClientAuthenticationDisabled", "SMTP sending", false, true), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("Set-CASMailbox", exchange.Calls[0].Cmdlet);
        Assert.Contains("\"SmtpClientAuthenticationDisabled\":false", exchange.Calls[0].Parameters);
    }

    [Fact]
    public async Task A_hold_without_days_keeps_items_indefinitely_with_its_note()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-Mailbox"
            ? """[{"ExternalDirectoryObjectId":"u2","LitigationHoldEnabled":true}]""" : "[]");

        var result = await Executor(exchange).ApplyAsync(new SetLitigationHold(Bo, false, null, null, true, null, "Matter 12"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Contains("\"LitigationHoldDuration\":\"Unlimited\"", exchange.Calls[0].Parameters);
        Assert.Contains("\"RetentionComment\":\"Matter 12\"", exchange.Calls[0].Parameters);
    }

    [Fact]
    public async Task An_allowed_sender_is_added_to_the_list_not_replacing_it()
    {
        var ada = new ChangeTarget("user", "u1", "zz-tenantry-test-ada", "zz-tenantry-test-ada@contoso.example");
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-Mailbox"
            ? """[{"ExternalDirectoryObjectId":"u2","AcceptMessagesOnlyFromSendersOrMembers":["zz-tenantry-test-ada"]}]""" : "[]");

        var result = await Executor(exchange).ApplyAsync(new SetAllowedSender(Bo, ada, false, true), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Contains("\"AcceptMessagesOnlyFromSendersOrMembers\":{\"@odata.type\":\"#Exchange.GenericHashTable\",\"Add\":[\"zz-tenantry-test-ada@contoso.example\"]}",
            exchange.Calls[0].Parameters);
    }

    [Fact]
    public void Archive_off_turning_an_app_off_and_any_hold_change_are_caution_changes()
    {
        Assert.True(new SetArchive(Bo, true, false).IsCaution);
        Assert.False(new SetArchive(Bo, false, true).IsCaution);
        Assert.True(new SetEmailApp(Bo, "PopEnabled", "POP", true, false).IsCaution);
        Assert.False(new SetEmailApp(Bo, "PopEnabled", "POP", false, true).IsCaution);
        Assert.True(new SetLitigationHold(Bo, true, null, null, false, null, null).IsCaution);
        Assert.True(new EnableAutoExpandingArchive(Bo).IsCaution);
    }

    // ---- The tab ----------------------------------------------------------------------------

    sealed class Records(MailDetails mail) : IUserRecords
    {
        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailboxDelegate>>([]);
        public Task<MailDetails> GetMailAsync(string userId, string address, CancellationToken cancellationToken = default) => Task.FromResult(mail);
    }

    sealed class Noop : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    sealed class Index(params UserSummary[] users) : IDirectoryIndex
    {
        public UserSummary? User(string id) => users.FirstOrDefault(u => u.Id == id);
        public GroupSummary? Group(string id) => null;
        public IEnumerable<UserSummary> AllUsers => users;
        public IEnumerable<GroupSummary> AllGroups => [];
    }

    static readonly MailboxSettings Settings = new("500", 35L << 20, 36L << 20, false, ["zz-tenantry-test-cy"],
        new EmailApps(true, true, true, true, false, true, null, false), true, false, 2L << 30, false, null, null, null, null,
        "Default MRM Policy", ["Default MRM Policy", "Keep 7 years"]);

    static UserSummary Person(string id, MailboxInfo? mailbox = null) =>
        new(id, $"zz-tenantry-test-{id}", $"zz-tenantry-test-{id}@contoso.example", $"zz-tenantry-test-{id}@contoso.example", null, null, true,
            UserKind.Member, DateTimeOffset.UnixEpoch, null, [], Mailbox: mailbox);

    static readonly MailboxInfo Plain = new("User", "p@contoso.example", [], ForwardingKind.None, null, false, false, false, false);

    static async Task<(MailSection Tab, ChangePipeline Pipeline)> Tab()
    {
        var pipeline = new ChangePipeline(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var bo = Person("bo", Plain);
        var mail = new MailDetails("zz-tenantry-test-bo@contoso.example", [], null, false, false, false, AutoReply.Off,
            1L << 30, 50L << 30, 49L << 30, 10, ["contoso.example"]) { Settings = Settings };
        var page = new UserDetailViewModel(bo, pipeline: pipeline, canEdit: Availability.Yes, records: new Records(mail),
            index: new Index(bo, Person("ada"), Person("cy")));
        var tab = page.Sections.OfType<MailSection>().Single();
        await tab.LoadAsync(Ct);
        return (tab, pipeline);
    }

    [Fact]
    public async Task Limits_stage_only_what_changed_and_reject_sizes_exchange_wont_take()
    {
        var (tab, pipeline) = await Tab();
        Assert.Equal(("500", "35", "36"), (tab.RecipientLimitDraft, tab.MaxSendDraft, tab.MaxReceiveDraft));

        tab.MaxSendDraft = "200";
        Assert.Contains("150", tab.LimitsError);
        Assert.Empty(pipeline.Pending);

        tab.MaxSendDraft = "25";
        Assert.Null(tab.LimitsError);

        var change = Assert.IsType<SetMailboxValue>(pipeline.Pending.Single());
        Assert.Equal(("MaxSendSize", "35 MB", "25 MB"), (change.Parameter, change.Before, change.After));
        Assert.True(tab.HasLimitsPending);
    }

    [Fact]
    public async Task Email_app_switches_stage_at_once_and_switching_back_undoes()
    {
        var (tab, pipeline) = await Tab();
        var pop = tab.AppRows.Single(r => r.Name == "POP");
        Assert.True(pop.IsOn);

        pop.IsOn = false;
        var change = Assert.IsType<SetEmailApp>(pipeline.Pending.Single());
        Assert.True(change.IsCaution);
        Assert.False(tab.AppRows.Single(r => r.Name == "POP").IsOn);

        tab.AppRows.Single(r => r.Name == "POP").IsOn = true;
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public async Task Allowed_senders_are_matched_to_people_and_removed_or_added_through_pending_changes()
    {
        var (tab, pipeline) = await Tab();
        var cy = tab.AllowedSenders.Single();
        Assert.Equal(("zz-tenantry-test-cy", "zz-tenantry-test-cy@contoso.example"), (cy.Name, cy.Detail));

        cy.RemoveCommand.Execute(null);
        Assert.Equal("Removing", tab.AllowedSenders.Single().PendingText);

        tab.SenderPicker.Query = "ada";
        tab.SenderPicker.PickCommand.Execute(tab.SenderPicker.Results.Single());
        Assert.Equal(2, pipeline.Pending.OfType<SetAllowedSender>().Count());
        Assert.Equal("Adding", tab.AllowedSenders.Single(r => r.Name == "zz-tenantry-test-ada").PendingText);
    }

    [Fact]
    public async Task Archive_offers_auto_expanding_only_while_on_and_a_hold_needs_sensible_days()
    {
        var (tab, pipeline) = await Tab();
        Assert.True(tab.CanEnableAutoExpanding);

        tab.HoldOnDraft = true;
        Assert.IsType<SetLitigationHold>(pipeline.Pending.Single());
        tab.HoldDaysDraft = "0";
        Assert.NotNull(tab.HoldError);

        tab.HoldDaysDraft = "";
        tab.HoldNoteDraft = "Matter 12";
        Assert.Null(tab.HoldError);
        var hold = Assert.IsType<SetLitigationHold>(pipeline.Pending.Single());
        Assert.Equal((true, (int?)null, "Matter 12"), (hold.NewOn, hold.NewDays, hold.NewNote));

        tab.ArchiveOn = false;
        Assert.True(Assert.IsType<SetArchive>(pipeline.Pending.Last()).IsCaution);
    }

    [Fact]
    public async Task Choosing_a_retention_policy_stages_it()
    {
        var (tab, pipeline) = await Tab();

        tab.RetentionPolicy = "Keep 7 years";

        var change = Assert.IsType<SetMailboxValue>(pipeline.Pending.Single());
        Assert.Equal(("RetentionPolicy", "Default MRM Policy", "Keep 7 years"), (change.Parameter, change.Before, change.After));
        Assert.Equal("Keep 7 years", tab.RetentionPolicy);
    }

    // ---- Bulk ---------------------------------------------------------------------------------

    [Fact]
    public async Task Bulk_email_app_off_skips_who_already_has_it_off()
    {
        var apps = new EmailApps(true, true, true, true, false, true, null, false);
        var popOn = Person("a", Plain with { Apps = apps });
        var popOff = Person("b", Plain with { Apps = apps with { Pop = false } });
        var notRead = Person("c", Plain);
        var action = BulkActions.ForUsers(() => [popOn, popOff, notRead], EmptyIndex.Instance, null).Single(a => a.Title == "Turn an email app off");
        action.Choice = action.Choices.Single(c => c.Label == "POP");

        var plan = await action.PlanAsync(Ct);

        var change = Assert.IsType<SetEmailApp>(plan.Changes.Single());
        Assert.Equal(("a", "PopEnabled", false), (change.Target.Id, change.Parameter, change.New));
        Assert.Equal(["zz-tenantry-test-b: POP already off", "zz-tenantry-test-c: email apps not read yet (refresh first)"], plan.Skipped);
    }
}
