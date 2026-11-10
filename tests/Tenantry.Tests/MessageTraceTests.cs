using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.Tests;

/// <summary>Message trace (12b): errors in words, 90 days in 10-day windows, trace reports, following a message up.</summary>
public sealed class MessageTraceTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static GraphUserRecords Records(FakeCmdlets exchange) =>
        new(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), exchange);

    [Theory]
    [InlineData("550 5.1.10 RESOLVER.ADR.RecipientNotFound; Recipient not found by SMTP address lookup", "5.1.10", false, "The recipient wasn't found.")]
    [InlineData("550 5.7.509 Access denied, sending domain does not pass DMARC verification", "5.7.509", false, "DMARC")]
    [InlineData("451 4.7.650 The mail server has been temporarily rate limited", "4.7.650", true, "suspicious activity")]
    [InlineData("554 5.7.134 the recipient only accepts mail from internal senders", "5.7.134", false, "inside the organization")]
    [InlineData("550 5.9.99 something new", "5.9.99", false, "failed for good")]
    public void A_delivery_code_is_explained(string text, string code, bool temporary, string meaningContains)
    {
        var problem = DeliveryErrors.Explain(text)!;

        Assert.Equal((code, temporary), (problem.Code, problem.Temporary));
        Assert.Contains(meaningContains, problem.Meaning, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(problem.WhatToDo);
    }

    [Fact]
    public void An_automatic_reply_Exchange_dropped_on_purpose_is_named_not_left_as_a_failure()
    {
        // As live trace shows it: a "Drop" with a success code.
        var problem = DeliveryErrors.Explain("Reason: [{LED=250 2.1.5 RESOLVER.OOF.IntToExt; handled internal OOF addressed to external recipient};{MSG=};{FQDN=};{IP=};{LRT=}]")!;

        Assert.Equal("Automatic reply", problem.Code);
        Assert.Contains("out-of-office", problem.Meaning);
    }

    [Fact]
    public void Success_and_text_without_a_code_explain_nothing()
    {
        Assert.Null(DeliveryErrors.Explain("250 2.6.0 Queued mail for delivery"));
        Assert.Null(DeliveryErrors.Explain("Message received by the mail server."));
        Assert.Null(DeliveryErrors.Explain(null));
    }

    [Fact]
    public void A_long_range_is_cut_into_10_day_windows_newest_first()
    {
        var end = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        var windows = TraceSearch.Windows(end.AddDays(-25), end).ToArray();

        Assert.Equal(3, windows.Length);
        Assert.Equal((end.AddDays(-10), end), windows[0]);
        Assert.Equal((end.AddDays(-25), end.AddDays(-20)), windows[2]);
        Assert.Single(TraceSearch.Windows(end.AddDays(-1), end));
    }

    [Fact]
    public async Task A_90_day_search_asks_each_window_for_what_is_left_and_stops_at_the_limit()
    {
        var calls = 0;
        // Each window answers 2,000 rows: the third reaches 5,000, so the fourth is never asked.
        var exchange = new FakeCmdlets((cmdlet, _) =>
        {
            if (cmdlet != "Get-MessageTraceV2")
                return "[]";
            calls++;
            return "[" + string.Join(",", Enumerable.Range(0, 2000).Select(i => $"{{\"MessageTraceId\":\"{calls}-{i}\",\"Received\":\"2026-10-0{calls}T10:00:00Z\",\"Status\":\"Delivered\"}}")) + "]";
        });
        var end = DateTimeOffset.UtcNow;

        var found = await Records(exchange).TraceAsync(new TraceSearch(end.AddDays(-90), end), Ct);

        Assert.Equal(3, calls);
        Assert.Equal(6000, found.Count);
        Assert.Contains("\"ResultSize\":5000", exchange.Calls[0].Parameters);
        Assert.Contains("\"ResultSize\":3000", exchange.Calls[1].Parameters);
        Assert.Contains("\"ResultSize\":1000", exchange.Calls[2].Parameters);
    }

    [Fact]
    public void A_report_needs_a_sender_or_recipient_and_a_range_in_the_last_90_days()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var none = new TraceReportRequest("x", "MessageTrace", now.AddDays(-5), now, [], [], null);
        var old = none with { Senders = ["a@contoso.example"], Start = now.AddDays(-120) };
        var fine = none with { Recipients = ["b@contoso.example"] };

        Assert.Single(none.Problems(now));
        Assert.Contains(old.Problems(now), p => p.Contains("90 days"));
        Assert.Empty(fine.Problems(now));
    }

    [Fact]
    public void A_report_sends_only_the_addresses_given_and_who_to_email()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var sent = GraphUserRecords.TraceReportParameters(new TraceReportRequest("Invoices", "MessageTraceDetail", now.AddDays(-30), now,
            ["billing@invoices.example"], [], "admin@contoso.example"));

        Assert.Equal("MessageTraceDetail", sent["ReportType"]);
        Assert.Equal(new[] { "billing@invoices.example" }, sent["SenderAddress"]);
        Assert.False(sent.ContainsKey("RecipientAddress"));
        Assert.Equal(new[] { "admin@contoso.example" }, sent["NotifyAddress"]);
    }

    [Fact]
    public async Task Trace_reports_read_their_state_and_download_link()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-HistoricalSearch"
            ? """[{"JobId":"j1","ReportTitle":"Invoices","ReportType":"MessageTrace","Status":"Done","Rows":1284,"FileUrl":"https://admin.example/r.csv","SenderAddress":["a@x.example"],"RecipientAddress":[]}]"""
            : "[]");

        var report = Assert.Single(await Records(exchange).GetTraceReportsAsync(Ct));

        Assert.True(report.IsDone);
        var row = new TraceReportRow(report, _ => Task.CompletedTask);
        Assert.Equal(("Ready: 1,284 rows", true, false, "Message Trace Summary"), (row.StatusText, row.CanOpen, row.CanCancel, row.Kind));
    }

    [Fact]
    public async Task The_reports_page_refuses_an_incomplete_request_without_asking_Exchange()
    {
        var page = new TraceReportsViewModel(new DemoFreeMail(), "admin@contoso.example");

        await page.StartCommand.ExecuteAsync(null);

        Assert.True(page.HasProblems);
        Assert.Empty(page.Reports);
    }

    [Fact]
    public void A_traced_sender_can_be_blocked_or_allowed_and_switching_replaces_the_first()
    {
        var pipeline = new ChangePipeline(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var message = new TracedMessage("t1", DateTimeOffset.UnixEpoch, "zz-tenantry-test-x@bad.example", "pat@contoso.example", "Hi", "Delivered", 1);
        string? traced = null;
        var preview = new TracePreviewViewModel(message, null, new TraceActions(s => traced = s, _ => { }, _ => { }), pipeline, Availability.Yes);

        preview.BlockSenderCommand.Execute(null);
        Assert.True(((AddListEntry)Assert.Single(pipeline.Pending)).Block);
        preview.AllowSenderCommand.Execute(null);
        var allow = (AddListEntry)Assert.Single(pipeline.Pending);
        Assert.False(allow.Block);
        Assert.Equal(ListExpiry.AllowDefault, allow.Expiry);
        preview.AllowSenderCommand.Execute(null);
        Assert.Empty(pipeline.Pending);

        preview.TraceSenderCommand.Execute(null);
        Assert.Equal("zz-tenantry-test-x@bad.example", traced);
        Assert.False(preview.CanFindInQuarantine);
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    /// <summary>A mail-security reader that fails if asked anything (the request never gets that far).</summary>
    sealed class DemoFreeMail : IMailSecurity
    {
        public Task<IReadOnlyList<QuarantinedMessage>> GetQuarantineAsync(int days, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QuarantinePreview> PreviewQuarantinedAsync(string identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QuarantinedMessage?> GetQuarantinedAsync(string identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetQuarantineHeadersAsync(string identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ExportQuarantinedAsync(string identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TracedMessage>> TraceAsync(TraceSearch search, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TraceEvent>> TraceDetailAsync(TracedMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TraceReport>> GetTraceReportsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TraceReport> StartTraceReportAsync(TraceReportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelTraceReportAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ListEntry>> GetListEntriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailFlowRule>> GetRulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Tenantry.Core.MailFlow.RuleDraft> GetRuleDraftAsync(string identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
