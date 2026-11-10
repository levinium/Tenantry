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
    public async Task The_reason_comes_from_its_code_not_Exchanges_wording()
    {
        // As Exchange answers live: Type is wording, QuarantineTypes the code.
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-QuarantineMessage"
            ? Held.Replace("\"Type\":\"HighConfPhish\"", "\"Type\":\"High Confidence Phish\",\"QuarantineTypes\":\"HighConfPhish\"")
            : "[]");

        var message = Assert.Single(await Records(exchange).GetQuarantineAsync(30, Ct));

        Assert.Equal(("HighConfPhish", "High-Confidence Phishing", true), (message.Reason, message.ReasonText, message.IsDangerous));
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

    static readonly ChangeTarget HeldTarget = new("quarantine", "a\\1", "Verify now", null);
    const string Bo = "zz-tenantry-test-bo@contoso.example";
    const string Cy = "zz-tenantry-test-cy@contoso.example";

    [Fact]
    public async Task Releasing_to_chosen_recipients_sends_User_not_ReleaseToAll_and_checks_each_got_it()
    {
        var reads = 0;
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-QuarantineMessage"
            ? (++reads == 1 ? Held : Held.Replace("\"ReleaseStatus\":\"NOTRELEASED\"", $"\"ReleaseStatus\":\"NOTRELEASED\",\"ReleasedUser\":[\"{Bo}\"]"))
            : "[]");
        var change = new ReleaseQuarantined(HeldTarget, "x@bad.example", [Bo, Cy], false, To: [Bo], ReportFalsePositive: true);

        var result = await Executor(exchange).ApplyAsync(change, Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var release = exchange.Calls.First(c => c.Cmdlet == "Release-QuarantineMessage").Parameters;
        Assert.Contains($"\"User\":[\"{Bo}\"]", release);
        Assert.DoesNotContain("ReleaseToAll", release);
        Assert.Contains("\"ReportFalsePositive\":true", release);
        // Test mode judges only the chosen recipient.
        Assert.Equal([Bo], change.Guarded.Select(g => g.Id));
        Assert.Equal($"Released to {Bo}; reported to Microsoft as not spam", change.After);
    }

    [Fact]
    public async Task Denying_a_release_request_uses_the_bulk_form_with_a_placeholder_identity()
    {
        var reads = 0;
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-QuarantineMessage"
            ? (++reads == 1 ? Held.Replace("NOTRELEASED", "REQUESTED") : Held.Replace("NOTRELEASED", "DENIED"))
            : "[]");

        var result = await Executor(exchange).ApplyAsync(new DenyQuarantineRelease(HeldTarget, "x@bad.example", [Bo]), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var deny = exchange.Calls.First(c => c.Cmdlet == "Release-QuarantineMessage").Parameters;
        Assert.Contains("\"Identities\":[\"a\\\\1\"]", deny);
        Assert.Contains("\"Identity\":\"000\"", deny);
        Assert.Contains("\"ActionType\":\"Deny\"", deny);
    }

    [Fact]
    public void Headers_explain_the_sender_checks_and_the_verdict_even_when_folded()
    {
        const string headers = "Received: from mx.example\r\nAuthentication-Results: spf=fail (sender IP is 203.0.113.9)\r\n smtp.mailfrom=bad.example; dkim=none (message not signed)\r\n"
                               + " header.d=none;dmarc=fail action=quarantine header.from=bank.example;compauth=fail reason=000\r\n"
                               + "X-Forefront-Antispam-Report: CIP:203.0.113.9;CTRY:XX;SCL:9;SFV:SPM;CAT:HPHSH;DIR:INB;\r\nSubject: Verify now\r\n";

        var verdicts = MessageHeaders.Verdicts(headers).ToDictionary(v => v.Check);

        Assert.Equal(("fail", false), (verdicts["Sender's server (SPF)"].Result, verdicts["Sender's server (SPF)"].Good));
        Assert.Equal(("none", (bool?)null), (verdicts["Signature (DKIM)"].Result, verdicts["Signature (DKIM)"].Good));
        Assert.Equal("fail", verdicts["Sender's domain policy (DMARC)"].Result);
        Assert.Equal("fail", verdicts["Microsoft's sender check"].Result);
        Assert.Equal("High-confidence phishing.", verdicts["Microsoft's verdict"].Meaning);
        Assert.Equal("High-confidence spam.", verdicts["Spam confidence"].Meaning);
        Assert.Equal("Verify now", MessageHeaders.Value(headers, "Subject"));
    }

    [Fact]
    public void Unticking_a_recipient_restages_the_release_for_the_rest_and_a_request_offers_deny()
    {
        var pipeline = new ChangePipeline(new DemoFree(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var message = new QuarantinedMessage("a\\1", DateTimeOffset.UnixEpoch, "x@bad.example", [Bo, Cy], "Verify now", 1, "Spam", null, null, "REQUESTED", null);
        var preview = new QuarantinePreviewViewModel(message, null, pipeline, Availability.Yes);
        Assert.Equal(("Approve Release", true, true, true), (preview.ReleaseText, preview.CanDeny, preview.CanChooseRecipients, preview.CanReportFalsePositive));

        preview.ReleaseCommand.Execute(null);
        Assert.Null(Assert.IsType<ReleaseQuarantined>(Assert.Single(pipeline.Pending)).To);
        preview.Recipients[1].IsTicked = false;
        var release = Assert.IsType<ReleaseQuarantined>(Assert.Single(pipeline.Pending));
        Assert.Equal([Bo], release.To);
        Assert.Equal("Release requested", release.Before);
        // Nobody left ticked: nothing to release.
        preview.Recipients[0].IsTicked = false;
        Assert.Empty(pipeline.Pending);
        Assert.True(preview.NoneChosen);

        preview.DenyCommand.Execute(null);
        Assert.IsType<DenyQuarantineRelease>(Assert.Single(pipeline.Pending));
    }

    [Fact]
    public async Task Bulk_release_skips_what_is_out_already_and_deny_skips_what_nobody_asked_about()
    {
        QuarantinedMessage M(string id, string status) => new(id, DateTimeOffset.UnixEpoch, "x@bad.example", [Bo], $"Message {id}", 1, "Spam", null, null, status, null);
        var picked = new[] { M("1", "NOTRELEASED"), M("2", "REQUESTED"), M("3", "RELEASED") };
        var actions = Tenantry.ViewModels.Records.BulkActions.ForQuarantine(() => picked).ToDictionary(a => a.Title);

        var release = await actions["Release"].PlanAsync(Ct);
        var deny = await actions["Deny Release Requests"].PlanAsync(Ct);

        Assert.Equal(["1", "2"], release.Changes.Select(c => c.Target.Id));
        Assert.True(((ReleaseQuarantined)release.Changes[1]).WasRequested);
        Assert.Equal("2", Assert.Single(deny.Changes).Target.Id);
        Assert.Equal(2, deny.Skipped.Count);
    }

    [Fact]
    public void A_downloaded_message_gets_a_safe_file_name()
    {
        Assert.Equal("RE- Invoice -4471- overdue", QuarantinePreviewViewModel.FileName("RE: Invoice /4471\\ overdue"));
        Assert.Equal("Quarantined message", QuarantinePreviewViewModel.FileName("::"));
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

        // 90 days back at most, and a 90-day search runs to now (searched 10 days at a time).
        Assert.Equal(now.AddDays(-TraceSearch.HistoryDays), search.Start);
        Assert.Equal(now, search.End);
        Assert.Equal(9, TraceSearch.Windows(search.Start, search.End).Count());
        Assert.Equal("Failed", search.Status);
    }

    sealed class DemoFree : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }
}
