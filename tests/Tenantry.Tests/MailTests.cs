using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>The Mail tab: reading Exchange's formats, what each change sends, and the tab's editing.</summary>
public sealed class MailTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ChangeTarget Bo = new("user", "u2", "zz-tenantry-test-bo", "zz-tenantry-test-bo@contoso.example");

    // ---- Formats ----------------------------------------------------------------------------

    [Theory]
    [InlineData("1.234 GB (1,325,000,000 bytes)", 1_325_000_000L)]
    [InlineData("100 GB (107,374,182,400 bytes)", 107_374_182_400L)]
    [InlineData("Unlimited", null)]
    [InlineData("", null)]
    public void Sizes_are_read_from_exchanges_text(string text, long? bytes) => Assert.Equal(bytes, MailDetails.ParseBytes(text));

    [Fact]
    public void Reply_text_drops_exchanges_html_and_its_source_line_breaks()
    {
        // As Get-MailboxAutoReplyConfiguration returns a two-line message (seen live).
        const string stored = "<html>\r\n<body>\r\nOut of the office (Tenantry test).<br>\r\nBack soon &amp; well.\r\n</body>\r\n</html>\r\n";

        Assert.Equal("Out of the office (Tenantry test).\nBack soon & well.", AutoReply.PlainText(stored));
    }

    [Fact]
    public void Auto_reply_configuration_is_read_with_its_schedule()
    {
        var row = JsonDocument.Parse("""
            {"AutoReplyState":"Scheduled","StartTime":"2026-10-22T13:00:00Z","EndTime":"2026-10-24T21:00:00Z",
             "InternalMessage":"<html><body>Away</body></html>","ExternalMessage":"","ExternalAudience":"Known"}
            """).RootElement;

        var reply = GraphUserRecords.ToAutoReply(row);

        Assert.Equal(AutoReplyState.Scheduled, reply.State);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 13, 0, 0, TimeSpan.Zero), reply.Start);
        Assert.Equal(AutoReplyAudience.Known, reply.Audience);
        Assert.Equal("Away", reply.Internal);
    }

    // ---- What's sent ------------------------------------------------------------------------

    static GraphChangeExecutor Executor(FakeCmdlets exchange) =>
        new(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);

    [Fact]
    public async Task Aliases_and_forwarding_go_through_set_mailbox_and_are_read_back()
    {
        var mailbox = """{"ExternalDirectoryObjectId":"u2","PrimarySmtpAddress":"zz-tenantry-test-bo@contoso.example","EmailAddresses":["SMTP:zz-tenantry-test-bo@contoso.example","smtp:bo2@contoso.example"],"ForwardingSmtpAddress":"smtp:ada@contoso.example","ForwardingAddress":null}""";
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-Mailbox" ? $"[{mailbox}]" : "[]");
        var executor = Executor(exchange);

        var alias = await executor.ApplyAsync(new SetMailAlias(Bo, "bo2@contoso.example", false, true), Ct);
        var forward = await executor.ApplyAsync(new SetForwarding(Bo, null, false, "ada@contoso.example", true, false), Ct);

        Assert.Equal(ChangeOutcome.Applied, alias.Outcome);
        Assert.Equal(ChangeOutcome.Applied, forward.Outcome);
        Assert.Contains("\"EmailAddresses\":{\"@odata.type\":\"#Exchange.GenericHashTable\",\"Add\":[\"smtp:bo2@contoso.example\"]}", exchange.Calls[0].Parameters);
        Assert.Contains("\"ForwardingSmtpAddress\":\"smtp:ada@contoso.example\"", exchange.Calls[2].Parameters);
        Assert.Contains("\"ForwardingAddress\":null", exchange.Calls[2].Parameters);
        Assert.Contains("\"DeliverToMailboxAndForward\":true", exchange.Calls[2].Parameters);
    }

    [Fact]
    public async Task Scheduled_replies_send_utc_times_and_html()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-MailboxAutoReplyConfiguration"
            ? """[{"AutoReplyState":"Scheduled","InternalMessage":"<html><body>Away<br>Back Monday</body></html>"}]""" : "[]");
        var start = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.FromHours(-4));
        var reply = new AutoReply(AutoReplyState.Scheduled, start, start.AddDays(2), "Away\nBack Monday", "Away", AutoReplyAudience.All);

        var result = await Executor(exchange).ApplyAsync(new SetAutoReply(Bo, AutoReply.Off, reply), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Contains("\"AutoReplyState\":\"Scheduled\"", exchange.Calls[0].Parameters);
        Assert.Contains("\"StartTime\":\"2026-10-22T13:00:00Z\"", exchange.Calls[0].Parameters);
        Assert.Contains("\"InternalMessage\":\"Away\\u003Cbr\\u003EBack Monday\"", exchange.Calls[0].Parameters);
    }

    [Fact]
    public void Forwarding_outside_is_a_caution_change()
    {
        Assert.True(new SetForwarding(Bo, null, false, "someone@gmail.example", false, true).IsCaution);
        Assert.False(new SetForwarding(Bo, null, false, "ada@contoso.example", false, false).IsCaution);
        Assert.True(new SetForwarding(Bo, "a@contoso.example", true, "A@contoso.example", true, false).IsNoChange);
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

    static readonly MailDetails Mail = new("zz-tenantry-test-bo@contoso.example", ["bo.alias@contoso.example"], null, false, false, false, AutoReply.Off,
        47L << 30, 50L << 30, 49L << 30, 1234, ["contoso.example", "contoso.onmicrosoft.example"]);

    static async Task<(MailSection Tab, ChangePipeline Pipeline)> Tab()
    {
        var pipeline = new ChangePipeline(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var bo = new UserSummary("u2", "zz-tenantry-test-bo", "zz-tenantry-test-bo@contoso.example", "zz-tenantry-test-bo@contoso.example", null, null, true,
            UserKind.Member, DateTimeOffset.UnixEpoch, null, ["Business Basic"],
            Mailbox: new MailboxInfo("User", "zz-tenantry-test-bo@contoso.example", ["bo.alias@contoso.example"], ForwardingKind.None, null, false, false, false, false));
        var page = new UserDetailViewModel(bo, pipeline: pipeline, canEdit: Availability.Yes, records: new Records(Mail));
        var tab = page.Sections.OfType<MailSection>().Single();
        await tab.LoadAsync(Ct);
        return (tab, pipeline);
    }

    [Fact]
    public async Task Storage_shows_against_the_quota_and_a_nearly_full_mailbox_is_flagged()
    {
        var (tab, _) = await Tab();

        Assert.Equal("47 GB of 50 GB", tab.UsedText);
        Assert.True(tab.IsNearlyFull);
        Assert.Contains("94% used", tab.Summary);
    }

    [Fact]
    public async Task Addresses_add_remove_and_change_primary_through_pending_changes()
    {
        var (tab, pipeline) = await Tab();
        Assert.Equal(["zz-tenantry-test-bo@contoso.example", "bo.alias@contoso.example"], tab.Addresses.Select(a => a.Address));
        Assert.False(tab.Addresses[0].CanRemove);

        tab.NewAliasName = "bo.sales";
        tab.AddAliasCommand.Execute(null);
        tab.Addresses.Single(a => a.Address == "bo.alias@contoso.example").MakePrimaryCommand.Execute(null);

        Assert.Equal(2, pipeline.Pending.Count);
        Assert.Equal("Adding", tab.Addresses.Single(a => a.Address == "bo.sales@contoso.example").PendingText);
        Assert.Equal("bo.alias@contoso.example", tab.Addresses[0].Address);
        Assert.Equal("Becomes primary", tab.Addresses[0].PendingText);

        tab.NewAliasName = "bad name";
        tab.AddAliasCommand.Execute(null);
        Assert.NotNull(tab.AliasError);
    }

    [Fact]
    public async Task Forwarding_outside_warns_and_stages_as_caution()
    {
        var (tab, pipeline) = await Tab();

        tab.ForwardOn = true;
        Assert.Empty(pipeline.Pending);
        Assert.Null(tab.ForwardError);
        tab.ForwardTo = "someone@gmail.example";
        Assert.True(tab.ForwardDraftOutside);

        var change = Assert.IsType<SetForwarding>(pipeline.Pending.Single());
        Assert.True(change.IsCaution);
        Assert.StartsWith("Pending: To someone@gmail.example", tab.ForwardingPendingText);
    }

    [Fact]
    public async Task Replies_stage_as_they_are_edited_once_they_have_a_message_and_a_sensible_schedule()
    {
        var (tab, pipeline) = await Tab();
        tab.ReplyScheduled = true;
        Assert.True(tab.NeedsReplyText);
        Assert.Null(tab.ReplyError);
        Assert.Empty(pipeline.Pending);

        tab.InternalMessage = "Away";
        Assert.Single(pipeline.Pending);

        tab.StartDate = new DateTime(2026, 11, 2);
        tab.EndDate = new DateTime(2026, 11, 1);
        Assert.Contains("stop after", tab.ReplyError);

        tab.EndDate = new DateTime(2026, 11, 6);
        tab.InternalMessage = "Away until the 6th";
        Assert.Null(tab.ReplyError);
        var reply = Assert.IsType<SetAutoReply>(pipeline.Pending.Single()).New;
        Assert.Equal((AutoReplyState.Scheduled, "Away until the 6th", "Away until the 6th"), (reply.State, reply.Internal, reply.External));
    }

    // ---- Bulk ---------------------------------------------------------------------------------

    static UserSummary Person(string id, MailboxInfo? mailbox) =>
        new(id, $"zz-tenantry-test-{id}", $"zz-tenantry-test-{id}@contoso.example", null, null, null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, [], Mailbox: mailbox);

    static readonly MailboxInfo Plain = new("User", "p@contoso.example", [], ForwardingKind.None, null, false, false, false, false);

    static Tenantry.ViewModels.Editing.BulkAction Bulk(string title, params UserSummary[] users) =>
        BulkActions.ForUsers(() => users, EmptyIndex.Instance, new Records(Mail)).Single(a => a.Title == title);

    [Fact]
    public async Task Bulk_forwarding_off_and_address_list_skip_who_they_dont_apply_to()
    {
        var forwarding = Person("a", Plain with { Forwarding = ForwardingKind.External, ForwardingTo = "x@gmail.example", KeepsCopyWhenForwarding = true });
        var hidden = Person("b", Plain with { HiddenFromAddressList = true });
        var noMailbox = Person("c", null);

        var off = await Bulk("Turn forwarding off", forwarding, hidden, noMailbox).PlanAsync(Ct);
        var show = Bulk("Address list", forwarding, hidden, noMailbox);
        show.Choice = show.Choices.Single(c => c.Label == "Show in the address list");
        var shown = await show.PlanAsync(Ct);

        Assert.Equal(("a", "Off"), (off.Changes.Single().Target.Id, off.Changes.Single().After));
        Assert.Equal(["zz-tenantry-test-b: not forwarding", "zz-tenantry-test-c: no mailbox"], off.Skipped);
        Assert.Equal("b", shown.Changes.Single().Target.Id);
    }

    [Fact]
    public async Task Bulk_automatic_replies_need_a_message_to_turn_on()
    {
        var replies = Bulk("Automatic replies", Person("a", Plain), Person("c", null));
        replies.Choice = replies.Choices.Single(c => c.Label == "Turn on");

        Assert.Contains("Write the reply", (await replies.PlanAsync(Ct)).Error);
        replies.Text = "Closed for the holiday";
        var plan = await replies.PlanAsync(Ct);

        var change = Assert.IsType<SetAutoReply>(plan.Changes.Single());
        Assert.Equal((AutoReplyState.On, "Closed for the holiday"), (change.New.State, change.New.External));
        Assert.Equal(["zz-tenantry-test-c: no mailbox"], plan.Skipped);
    }

    [Fact]
    public async Task Discarding_in_the_tray_resets_the_forms()
    {
        var (tab, pipeline) = await Tab();
        tab.ForwardOn = true;
        tab.ForwardTo = "ada@contoso.example";
        Assert.Single(pipeline.Pending);

        pipeline.Clear();

        Assert.False(tab.ForwardOn);
        Assert.Equal("", tab.ForwardTo);
    }

    [Fact]
    public async Task Hiding_from_the_address_list_stages_at_once_and_switching_back_undoes_it()
    {
        var (tab, pipeline) = await Tab();

        tab.ShowInAddressList = false;
        Assert.True(Assert.IsType<SetHiddenFromAddressList>(pipeline.Pending.Single()).New);
        Assert.False(tab.ShowInAddressList);

        tab.ShowInAddressList = true;
        Assert.Empty(pipeline.Pending);
    }
}
