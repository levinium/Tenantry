using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.Tests;

/// <summary>Mail &amp; security (5a): quarantine and message trace reads, the changes they send, and the guard.</summary>
public sealed class MailSecurityTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static GraphUserRecords Records(FakeCmdlets exchange) =>
        new(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), exchange);

    static GraphChangeExecutor Executor(FakeCmdlets exchange) =>
        new(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);

    const string Held = """
        [{"Identity":"a\\1","ReceivedTime":"2026-10-25T10:00:00Z","SenderAddress":"x@bad.example","RecipientAddress":["zz-tenantry-test-bo@contoso.example","pat@contoso.example"],
          "Subject":"Verify now","Size":2048,"Type":"HighConfPhish","PolicyName":"Default","Expires":"2026-11-24T10:00:00Z","ReleaseStatus":"NOTRELEASED"}]
        """;

    [Fact]
    public async Task Quarantine_rows_read_their_reason_recipients_and_status()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-QuarantineMessage" ? Held : "[]");

        var message = Assert.Single(await Records(exchange).GetQuarantineAsync(30, Ct));

        Assert.Equal(("High-Confidence Phishing", true, 2, MailSecurityLists.Q.Held), (message.ReasonText, message.IsDangerous, message.Recipients.Count, MailSecurityLists.StatusOf(message)));
        Assert.Contains("\"PageSize\":1000", exchange.Calls[0].Parameters);
    }

    [Fact]
    public async Task A_trace_search_sends_only_what_was_filled_in()
    {
        var exchange = new FakeCmdlets((_, _) => """[{"MessageTraceId":"t1","Received":"2026-10-25T10:00:00Z","SenderAddress":"a@x.example","RecipientAddress":"b@y.example","Subject":"Hi","Status":"Failed"}]""");
        var start = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

        var found = await Records(exchange).TraceAsync(new TraceSearch(start, start.AddDays(2), Recipient: "b@y.example", Subject: "Hi"), Ct);

        Assert.Equal("Failed", Assert.Single(found).Status);
        var sent = exchange.Calls[0].Parameters;
        Assert.Contains("\"RecipientAddress\":\"b@y.example\"", sent);
        Assert.Contains("\"SubjectFilterType\":\"Contains\"", sent);
        Assert.DoesNotContain("SenderAddress", sent);
        Assert.DoesNotContain("\"Status\"", sent);
    }

    [Fact]
    public async Task Releasing_can_allow_the_sender_and_is_read_back()
    {
        var reads = 0;
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-QuarantineMessage"
            ? (++reads == 1 ? Held : Held.Replace("NOTRELEASED", "RELEASED"))
            : "[]");
        var change = new ReleaseQuarantined(new ChangeTarget("quarantine", "a\\1", "Verify now", null), "x@bad.example", ["zz-tenantry-test-bo@contoso.example"], AllowSender: true);

        var result = await Executor(exchange).ApplyAsync(change, Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var release = exchange.Calls.First(c => c.Cmdlet == "Release-QuarantineMessage").Parameters;
        Assert.Contains("\"ReleaseToAll\":true", release);
        Assert.Contains("\"AllowSender\":true", release);
    }

    [Fact]
    public async Task Blocking_a_sender_never_expires_and_allowing_one_lasts_45_days()
    {
        var exchange = new FakeCmdlets((cmdlet, p) => cmdlet == "Get-TenantAllowBlockListItems" ? """[{"Action":"Block"},{"Action":"Allow"}]""" : "[]");
        var executor = Executor(exchange);
        var sender = new ChangeTarget("sender", "zz-tenantry-test-x@bad.example", "zz-tenantry-test-x@bad.example", "zz-tenantry-test-x@bad.example");

        await executor.ApplyAsync(new SetSenderListed(sender, true, "spam"), Ct);
        await executor.ApplyAsync(new SetSenderListed(sender, false, null), Ct);

        var calls = exchange.Calls.Where(c => c.Cmdlet == "New-TenantAllowBlockListItems").Select(c => c.Parameters).ToArray();
        Assert.Contains("\"NoExpiration\":true", calls[0]);
        Assert.Contains("\"RemoveAfter\":45", calls[1]);
    }

    [Fact]
    public async Task Test_mode_refuses_mail_to_a_real_person_without_sending_anything()
    {
        var exchange = new FakeCmdlets();
        var pipeline = new ChangePipeline(Executor(exchange), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        pipeline.Stage(new DeleteQuarantined(new ChangeTarget("quarantine", "a\\1", "Verify now", null), "x@bad.example",
            ["zz-tenantry-test-bo@contoso.example", "pat@contoso.example"]));

        var result = Assert.Single(await pipeline.ApplyAsync(cancellationToken: Ct));

        Assert.Equal(ChangeOutcome.Refused, result.Result.Outcome);
        Assert.Empty(exchange.Calls);
    }

    [Fact]
    public void The_message_is_shown_as_text_with_links_spelled_out_and_nothing_hidden()
    {
        var text = PlainText.FromHtml("<html><head><style>p{}</style></head><body><script>alert(1)</script><p>Hello&nbsp;there</p><a href=\"https://evil.example/x\">Click</a><img src=\"https://track.example/p.gif\"></body></html>");

        Assert.Equal("Hello there\nClick [https://evil.example/x]", text);
    }

    [Fact]
    public void The_preview_stages_release_with_the_sender_choice_and_undoes_it()
    {
        var pipeline = new ChangePipeline(new DemoFree(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var message = new QuarantinedMessage("a\\1", DateTimeOffset.UnixEpoch, "x@bad.example", ["zz-tenantry-test-bo@contoso.example"], "Verify now", 1, "Phish", null, null, "NOTRELEASED", null);
        var preview = new QuarantinePreviewViewModel(message, null, pipeline, Availability.Yes);

        preview.ReleaseCommand.Execute(null);
        preview.AllowSender = true;
        Assert.True(Assert.IsType<ReleaseQuarantined>(Assert.Single(pipeline.Pending)).AllowSender);
        Assert.Equal("Undo Release", preview.ReleaseText);
        preview.ReleaseCommand.Execute(null);
        Assert.Empty(pipeline.Pending);

        preview.DeleteCommand.Execute(null);
        preview.BlockSenderCommand.Execute(null);
        Assert.Equal(2, pipeline.Pending.Count);
    }

    [Fact]
    public void Trace_searches_stay_within_what_Exchange_allows()
    {
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var area = new MailSecurityViewModel(null, null, Availability.Yes, null, () => now)
        {
            TraceStart = now.Date.AddDays(-200),
            TraceLength = MailSecurityViewModel.TraceRanges[^1],
            TraceStatus = "Failed",
        };

        var search = area.CurrentSearch();

        Assert.Equal(now.Date.AddDays(-TraceSearch.HistoryDays), search.Start.Date);
        Assert.Equal(TraceSearch.MaxDays, (search.End - search.Start).TotalDays);
        Assert.Equal("Failed", search.Status);
    }

    sealed class DemoFree : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }
}
