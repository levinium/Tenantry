using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.Tests;

/// <summary>Reported messages, submissions and where reports go (12d).</summary>
public sealed class SubmissionTests
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData(true, true, ReportMode.MicrosoftAndMailbox)]
    [InlineData(false, true, ReportMode.MailboxOnly)]
    [InlineData(true, false, ReportMode.MicrosoftOnly)]
    [InlineData(false, false, ReportMode.Off)]
    public void The_report_mode_is_read_from_the_policy(bool microsoft, bool mailbox, ReportMode mode)
    {
        var policy = Json($$"""
            {"EnableReportToMicrosoft":{{(microsoft ? "true" : "false")}},"ReportJunkToCustomizedAddress":{{(mailbox ? "true" : "false")}},
             "ReportPhishToCustomizedAddress":{{(mailbox ? "true" : "false")}},"ReportPhishAddresses":["reports@contoso.example"],
             "EnableUserEmailNotification":true,"PreSubmitMessageEnabled":true,"PostSubmitMessageEnabled":false}
            """);

        var routing = GraphUserRecords.ToRouting(policy, default);

        Assert.Equal((mode, "reports@contoso.example", true, true, false), (routing.Mode, routing.Mailbox, routing.NotifyReporters, routing.AskFirst, routing.ThankAfter));
        Assert.False(routing.RuleExists);
    }

    static readonly ReportRouting Both = new(ReportMode.MicrosoftAndMailbox, "reports@contoso.example", false, true, true, true, true);

    [Fact]
    public void Mailbox_only_sends_the_policy_then_points_the_rule_and_turns_on_notifications()
    {
        var change = new SetReportRouting(SetReportRouting.TargetFor(), Both, Both with { Mode = ReportMode.MailboxOnly, Mailbox = "soc@contoso.example" });

        var calls = GraphMailCalls(change, policyExists: true, ruleExists: true);

        Assert.Equal(["Set-ReportSubmissionPolicy", "Set-ReportSubmissionRule"], calls.Select(c => c.Cmdlet));
        var policy = calls[0].Parameters;
        Assert.Equal((false, true, true), ((bool)policy["EnableReportToMicrosoft"]!, (bool)policy["ReportPhishToCustomizedAddress"]!, (bool)policy["EnableUserEmailNotification"]!));
        Assert.Equal(new[] { "soc@contoso.example" }, policy["ReportPhishAddresses"]);
        Assert.Equal(new[] { "soc@contoso.example" }, calls[1].Parameters["SentTo"]);
    }

    [Fact]
    public void Microsoft_only_clears_the_mailbox_and_removes_the_rule_and_a_missing_policy_is_made_first()
    {
        var change = new SetReportRouting(SetReportRouting.TargetFor(), Both, Both with { Mode = ReportMode.MicrosoftOnly });

        var calls = GraphMailCalls(change, policyExists: false, ruleExists: true);

        Assert.Equal(["New-ReportSubmissionPolicy", "Set-ReportSubmissionPolicy", "Remove-ReportSubmissionRule"], calls.Select(c => c.Cmdlet));
        Assert.Null(calls[1].Parameters["ReportPhishAddresses"]);
        Assert.False((bool)calls[1].Parameters["ReportJunkToCustomizedAddress"]!);
    }

    [Fact]
    public void A_mailbox_with_no_rule_yet_gets_one_named_as_Microsoft_expects()
    {
        var calls = GraphMailCalls(new SetReportRouting(SetReportRouting.TargetFor(), Both with { RuleExists = false }, Both), policyExists: true, ruleExists: false);

        Assert.Equal("New-ReportSubmissionRule", calls[^1].Cmdlet);
        Assert.Equal(("DefaultReportSubmissionRule", "DefaultReportSubmissionPolicy"), ((string)calls[^1].Parameters["Name"]!, (string)calls[^1].Parameters["ReportSubmissionPolicy"]!));
    }

    static IReadOnlyList<(string Cmdlet, Dictionary<string, object?> Parameters)> GraphMailCalls(SetReportRouting change, bool policyExists, bool ruleExists) =>
        GraphChangeExecutor.RoutingCalls(change, policyExists, ruleExists);

    [Fact]
    public void A_submission_reads_who_sent_it_the_verdict_and_any_review()
    {
        var s = GraphUserRecords.ToSubmission(Json("""
            {"id":"s1","contentType":"email","category":"phishing","source":"user","status":"succeeded","createdDateTime":"2026-11-09T10:00:00Z",
             "createdBy":{"user":{"email":"pat@contoso.example"}},"emailSubject":"Verify now","sender":"x@bad.example","recipientEmailAddress":"pat@contoso.example",
             "result":{"category":"notJunk","detail":"itemFoundClean"},"adminReview":{"reviewBy":"admin@contoso.example","reviewResult":"notSpam","reviewDateTime":"2026-11-09T12:00:00Z"}}
            """));

        Assert.Equal((true, "Verify now", "pat@contoso.example", "notJunk", "itemFoundClean", "notSpam"), (s.ByUser, s.Subject, s.CreatedBy, s.ResultCategory, s.ResultDetail, s.ReviewedAs));
        Assert.Equal("Microsoft found nothing wrong with it.", ThreatSubmission.DetailText(s.ResultDetail));
        Assert.Equal("Phishing Simulation", ThreatSubmission.CategoryText("phishingSimulation"));
    }

    [Fact]
    public void Answering_a_persons_report_is_staged_and_switching_the_verdict_replaces_it()
    {
        var pipeline = new ChangePipeline(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var report = new ThreatSubmission("s1", "email", true, "phishing", "succeeded", "notJunk", "itemFoundClean", "Verify now", "x@bad.example",
            "zz-tenantry-test-bo@contoso.example", null, DateTimeOffset.UnixEpoch, "zz-tenantry-test-bo@contoso.example", null, null, null);
        var preview = new SubmissionPreviewViewModel(report, pipeline, Availability.Yes);
        Assert.True(preview.CanReview);
        Assert.True(preview.Disagrees);

        preview.ReviewCommand.Execute("phishing");
        preview.ReviewCommand.Execute("notSpam");
        var review = (ReviewSubmission)Assert.Single(pipeline.Pending);
        Assert.Equal(("notSpam", "Reviewed (no threat found)"), (review.Verdict, review.After));
        Assert.Null(WriteGuard.Refusal(review, testMode: true));

        var admins = new SubmissionPreviewViewModel(report with { ByUser = false }, pipeline, Availability.Yes);
        Assert.False(admins.CanReview);
    }

    [Fact]
    public async Task The_settings_card_stages_one_change_and_won_t_without_a_mailbox()
    {
        var pipeline = new ChangePipeline(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var editor = new ReportRoutingEditor(new Fixed(Both), pipeline, Availability.Yes);
        await editor.LoadAsync();
        Assert.Empty(pipeline.Pending);

        editor.NotifyReporters = true;
        editor.SelectedMode = ReportRoutingEditor.Modes.Single(m => m.Mode == ReportMode.MicrosoftOnly);
        var change = (SetReportRouting)Assert.Single(pipeline.Pending);
        Assert.Equal((ReportMode.MicrosoftOnly, true), (change.New.Mode, change.New.NotifyReporters));
        Assert.Equal("Microsoft and reports@contoso.example", change.Before);

        editor.SelectedMode = ReportRoutingEditor.Modes.Single(m => m.Mode == ReportMode.MailboxOnly);
        editor.MailboxDraft = "";
        editor.CommitMailboxCommand.Execute(null);
        Assert.True(editor.HasProblem);
        Assert.Empty(pipeline.Pending);

        // Test mode refuses the organization's setting.
        Assert.NotNull(WriteGuard.Refusal(change, testMode: true));
    }

    [Fact]
    public async Task A_quarantined_message_is_sent_as_its_content()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Export-QuarantineMessage" ? """[{"Eml":"SGVsbG8=","BodyEncoding":"Base64"}]""" : "[]");
        var http = new FakeHttp().On(HttpMethod.Post, "security/threatSubmission/emailThreats", """{"id":"s9","status":"running","contentType":"email","category":"notJunk"}""");
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), exchange);

        var sent = await records.SubmitQuarantinedAsync("a\\1", "pat@contoso.example", "notJunk", TestContext.Current.CancellationToken);

        Assert.Equal(("s9", "running"), (sent.Id, sent.Status));
        var posted = http.Requests.Single(r => r.Method == HttpMethod.Post).Body;
        Assert.Contains("emailContentThreatSubmission", posted);
        Assert.Contains("\"fileContent\":\"SGVsbG8=\"", posted);
        Assert.Contains("\"recipientEmailAddress\":\"pat@contoso.example\"", posted);
    }

    static TracedMessage Traced(string status) => new("t-1", DateTimeOffset.UtcNow, "x@mail.example", "pat@contoso.example", "Your invoice", status, 1000, "<m1@mail.example>");

    [Fact]
    public async Task A_delivered_traced_message_is_pointed_at_in_its_mailbox()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "users?$filter=", """{"value":[{"id":"u1"}]}""")
            .On(HttpMethod.Get, "users/u1/messages?$filter=", """{"value":[{"id":"AAMk1="}]}""")
            .On(HttpMethod.Post, "security/threatSubmission/emailThreats", """{"id":"s10","status":"running","contentType":"email","category":"phishing"}""");
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), new FakeCmdlets((_, _) => "[]"));

        var sent = await records.SubmitTracedAsync(Traced("Delivered"), "phishing", TestContext.Current.CancellationToken);

        Assert.Equal("s10", sent.Id);
        var lookup = Uri.UnescapeDataString(http.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/messages")).Url.Query);
        Assert.Contains("internetMessageId eq '<m1@mail.example>'", lookup);
        Assert.Contains("proxyAddresses/any(p:p eq 'smtp:pat@contoso.example')", Uri.UnescapeDataString(http.Requests.First().Url.Query));
        var posted = http.Requests.Single(r => r.Method == HttpMethod.Post).Body;
        Assert.Contains("emailUrlThreatSubmission", posted);
        Assert.Contains("\"messageUrl\":\"https://graph.microsoft.com/beta/users/u1/messages/AAMk1=\"", posted);
        Assert.Contains("\"category\":\"phishing\"", posted);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "ErrorAccessDenied")]
    // Seen live: another person's message list answers "not found" when their mailbox isn't open to the admin.
    [InlineData(System.Net.HttpStatusCode.NotFound, "ErrorItemNotFound")]
    public async Task A_mailbox_the_admin_cant_open_says_so_and_sends_nothing(System.Net.HttpStatusCode status, string code)
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "users?$filter=", """{"value":[{"id":"u1"}]}""")
            .On(HttpMethod.Get, "users/u1/messages?$filter=", $$$"""{"error":{"code":"{{{code}}}","message":"Not available."}}""", status);
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), new FakeCmdlets((_, _) => "[]"));

        var e = await Assert.ThrowsAsync<MailboxNotOpenException>(() => records.SubmitTracedAsync(Traced("FilteredAsSpam"), "phishing", TestContext.Current.CancellationToken));

        Assert.Equal("pat@contoso.example", e.Mailbox);
        Assert.DoesNotContain(http.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_quarantined_traced_message_goes_from_quarantine()
    {
        var asked = new List<string>();
        var exchange = new FakeCmdlets((cmdlet, parameters) =>
        {
            asked.Add(cmdlet);
            return cmdlet switch
            {
                "Get-QuarantineMessage" => """[{"Identity":"q\\7"}]""",
                "Export-QuarantineMessage" => """[{"Eml":"SGVsbG8=","BodyEncoding":"Base64"}]""",
                _ => "[]",
            };
        });
        var http = new FakeHttp().On(HttpMethod.Post, "security/threatSubmission/emailThreats", """{"id":"s11","status":"running","contentType":"email","category":"notJunk"}""");
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), exchange);

        await records.SubmitTracedAsync(Traced("Quarantined"), "notJunk", TestContext.Current.CancellationToken);

        Assert.Equal(["Get-QuarantineMessage", "Export-QuarantineMessage"], asked);
        Assert.Contains("emailContentThreatSubmission", http.Requests.Single(r => r.Method == HttpMethod.Post).Body);
    }

    [Theory]
    [InlineData("Delivered", true)]
    [InlineData("FilteredAsSpam", true)]
    [InlineData("Quarantined", true)]
    [InlineData("Failed", false)]
    [InlineData("Pending", false)]
    [InlineData("Expanded", false)]
    public void Only_mail_that_arrived_or_is_held_can_be_sent(string status, bool can) =>
        Assert.Equal(can, ThreatSubmission.CanSendTraced(Traced(status)));

    [Fact]
    public async Task The_trace_preview_sends_once_and_offers_Defender_when_the_mailbox_is_closed()
    {
        var closed = new TracePreviewViewModel(Traced("Delivered"), new Closed());
        Assert.True(closed.CanSubmit);
        Assert.Equal("phishing", closed.SubmitAs.Value);

        await closed.SubmitCommand.ExecuteAsync(null);

        Assert.True(closed.ShowDefender);
        Assert.Contains("Full Access", closed.SubmitNote);
        Assert.True(closed.CanSendNow);
        Assert.Equal("notJunk", new TracePreviewViewModel(Traced("Quarantined"), new Closed()).SubmitAs.Value);
        Assert.False(new TracePreviewViewModel(Traced("Failed"), new Closed()).CanSubmit);
    }

    /// <summary>Mail security whose submissions always meet a mailbox the admin can't open.</summary>
    sealed class Closed : IMailSecurity, ISubmissionRecords
    {
        public Task<IReadOnlyList<QuarantinedMessage>> GetQuarantineAsync(int days, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QuarantinePreview> PreviewQuarantinedAsync(string identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QuarantinedMessage?> GetQuarantinedAsync(string identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetQuarantineHeadersAsync(string identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ExportQuarantinedAsync(string identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TracedMessage>> TraceAsync(TraceSearch search, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TraceEvent>> TraceDetailAsync(TracedMessage message, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TraceEvent>>([]);
        public Task<IReadOnlyList<TraceReport>> GetTraceReportsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TraceReport> StartTraceReportAsync(TraceReportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelTraceReportAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ListEntry>> GetListEntriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailFlowRule>> GetRulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Tenantry.Core.MailFlow.RuleDraft> GetRuleDraftAsync(string identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ReportRouting> GetReportRoutingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ThreatSubmission>> GetSubmissionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ThreatSubmission>>([]);
        public Task<ThreatSubmission> SubmitQuarantinedAsync(string identity, string recipient, string category, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreatSubmission> SubmitTracedAsync(TracedMessage message, string category, CancellationToken cancellationToken = default) => throw new MailboxNotOpenException(message.Recipient);
        public Task<ThreatSubmission> SubmitUrlAsync(string url, string category, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreatSubmission> SubmitFileAsync(string fileName, byte[] content, string category, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    sealed class Fixed(ReportRouting routing) : ISubmissionRecords
    {
        public Task<ReportRouting> GetReportRoutingAsync(CancellationToken cancellationToken = default) => Task.FromResult(routing);
        public Task<IReadOnlyList<ThreatSubmission>> GetSubmissionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ThreatSubmission>>([]);
        public Task<ThreatSubmission> SubmitQuarantinedAsync(string identity, string recipient, string category, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreatSubmission> SubmitTracedAsync(TracedMessage message, string category, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreatSubmission> SubmitUrlAsync(string url, string category, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreatSubmission> SubmitFileAsync(string fileName, byte[] content, string category, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }
}
