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
        Assert.Equal(("notSpam", "Reviewed: no threat found"), (review.Verdict, review.After));
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

    sealed class Fixed(ReportRouting routing) : ISubmissionRecords
    {
        public Task<ReportRouting> GetReportRoutingAsync(CancellationToken cancellationToken = default) => Task.FromResult(routing);
        public Task<IReadOnlyList<ThreatSubmission>> GetSubmissionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ThreatSubmission>>([]);
        public Task<ThreatSubmission> SubmitQuarantinedAsync(string identity, string recipient, string category, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreatSubmission> SubmitUrlAsync(string url, string category, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreatSubmission> SubmitFileAsync(string fileName, byte[] content, string category, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }
}
