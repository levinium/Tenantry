using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.Tests;

/// <summary>Mail flow reports: daily counts, top lists and Defender's breakdown.</summary>
public sealed class MailFlowReportTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DateOnly Today = new(2026, 11, 11);

    [Fact]
    public async Task Status_rows_read_as_days_and_Microsofts_empty_dates_are_dropped()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-MailFlowStatusReport" ? """
            [{"Date":"2026-11-10T00:00:00.0000000","EventType":"GoodMail","Direction":"Inbound","MessageCount":2900},
             {"Date":"2026-11-10T00:00:00.0000000","EventType":"EmailPhish","Direction":"Inbound","MessageCount":"45"},
             {"Date":"0001-01-01T00:00:00.0000000","EventType":"GoodMail","Direction":"Inbound","MessageCount":1}]
            """ : "[]");
        var records = new GraphUserRecords(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), exchange);

        var counts = await records.GetMailFlowStatusAsync(7, Ct);

        Assert.Equal([new MailFlowCount(new(2026, 11, 10), "GoodMail", "Inbound", 2900), new MailFlowCount(new(2026, 11, 10), "EmailPhish", "Inbound", 45)], counts);
        var sent = JsonDocument.Parse(exchange.Calls[0].Parameters).RootElement;
        Assert.Equal(7, (DateTime.Parse(sent.GetProperty("EndDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            - DateTime.Parse(sent.GetProperty("StartDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).TotalDays);
    }

    [Fact]
    public async Task Rule_hits_count_from_their_third_column_and_addresses_in_different_cases_count_once()
    {
        var exchange = new FakeCmdlets((cmdlet, p) => (string)p!["Category"]! switch
        {
            "InboundTransportRuleHits" => """[{"C1":"External sender warning","C2":"SetAuditSeverityLow","C3":"120"},{"C1":"External sender warning","C2":"SetAuditSeverityLow","C3":"30"}]""",
            _ => """[{"C1":"ada@contoso.example","C2":"5"},{"C1":"Ada@Contoso.example","C2":"7"},{"C1":"bo@contoso.example","C2":"20"},{"C1":"","C2":"9"}]""",
        });
        var records = new GraphUserRecords(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), exchange);

        Assert.Equal([new MailFlowTop("External sender warning", 150)], await records.GetTopTrafficAsync("InboundTransportRuleHits", 30, Ct));
        Assert.Equal([new MailFlowTop("bo@contoso.example", 20), new MailFlowTop("ada@contoso.example", 12)], await records.GetTopTrafficAsync("TopMailSender", 30, Ct));
    }

    [Fact]
    public void Unwanted_share_counts_everything_incoming_that_wasnt_delivered_and_every_day_gets_a_bar()
    {
        MailFlowCount[] counts =
        [
            new(Today, "GoodMail", "Inbound", 60), new(Today, "SpamDetections", "Inbound", 20), new(Today, "EdgeBlockSpam", "Inbound", 20),
            new(Today, "GoodMail", "Outbound", 500),
        ];

        Assert.Equal(40, MailFlowReport.UnwantedPercent(counts));
        var days = MailFlowReport.IncomingByDay(counts, Today.AddDays(-2), Today);
        Assert.Equal(3, days.Count);
        Assert.All(days.Take(2), d => Assert.All(d.Counts, c => Assert.Equal(0, c.Count)));
        Assert.Equal(60, days[^1].Counts.Single(c => c.Kind == "GoodMail").Count);
        Assert.Equal("Blocked at the Edge", MailFlowReport.KindTitle("EdgeBlockSpam"));
        Assert.Equal("SomethingNew", MailFlowReport.KindTitle("SomethingNew"));
    }

    sealed class Reports(Exception? threats = null) : IMailFlowReports
    {
        public List<int> StatusDays { get; } = [];

        public Task<IReadOnlyList<MailFlowCount>> GetMailFlowStatusAsync(int days, CancellationToken cancellationToken = default)
        {
            StatusDays.Add(days);
            return Task.FromResult<IReadOnlyList<MailFlowCount>>([.. Enumerable.Range(0, days).Select(i => new MailFlowCount(Today.AddDays(-i), "GoodMail", "Inbound", 100 + i))]);
        }

        public Task<IReadOnlyList<MailFlowTop>> GetTopTrafficAsync(string category, int days, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFlowTop>>([.. Enumerable.Range(1, 15).Select(i => new MailFlowTop($"{category}-{i}@contoso.example", 100 - i))]);

        public Task<IReadOnlyList<ThreatCatch>> GetThreatCatchesAsync(int days, CancellationToken cancellationToken = default) =>
            threats is not null ? Task.FromException<IReadOnlyList<ThreatCatch>>(threats)
            : Task.FromResult<IReadOnlyList<ThreatCatch>>([new("NotSpam", "Message passed", "Inbound", 9000), new("Phish", "URL detonation", "Inbound", 40),
                new("Phish", "Spoof DMARC", "Inbound", 10), new("Spam", "IP reputation", "Inbound", 300), new("Spam", "IP reputation", "Outbound", 3)]);
    }

    static MailFlowReportsViewModel Page(Reports reports, List<string> traced) =>
        new(reports, a => traced.Add("from " + a), a => traced.Add("to " + a), () => new DateTimeOffset(Today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));

    [Fact]
    public async Task The_page_shows_totals_a_bar_a_day_and_traces_senders_from_and_recipients_to()
    {
        var traced = new List<string>();
        var page = Page(new Reports(), traced);

        await page.Refresh();

        Assert.Equal("Received", page.Tiles[0].Title);
        Assert.Equal(7, page.Bars.Count);
        Assert.StartsWith(Today.ToString("ddd d MMM", System.Globalization.CultureInfo.CurrentCulture), page.Bars[0].Label);
        var senders = page.TopLists.Single(t => t.Title == "Top Senders");
        Assert.Equal(MailFlowTopList.Shown, senders.Rows.Count);
        senders.ShowAll = true;
        Assert.Equal(15, senders.Rows.Count);
        senders.Rows[0].TraceCommand.Execute(null);
        page.TopLists.Single(t => t.Title == "Most Phishing Received").Rows[0].TraceCommand.Execute(null);
        Assert.Equal(["from TopMailSender-1@contoso.example", "to TopPhishRecipient-1@contoso.example"], traced);
        Assert.False(page.TopLists.Single(t => t.Title == "Malware Seen").Rows[0].CanTrace);
    }

    [Fact]
    public async Task Ninety_days_shows_a_bar_a_week()
    {
        var reports = new Reports();
        var page = Page(reports, []);
        await page.Refresh();

        page.Range = MailFlowReportsViewModel.Ranges[^1];
        for (var i = 0; i < 50 && page.IsLoading; i++)
            await Task.Delay(10, Ct);

        Assert.Equal([7, 90], reports.StatusDays);
        Assert.Equal("Incoming Mail by Week", page.BarsHeading);
        Assert.InRange(page.Bars.Count, 13, 15);
    }

    [Fact]
    public async Task Defender_breakdown_groups_incoming_threats_and_says_why_when_it_cant_be_read()
    {
        var page = Page(new Reports(), []);
        await page.Refresh();
        for (var i = 0; i < 50 && page.ThreatsLoading; i++)
            await Task.Delay(10, Ct);

        Assert.Equal(["Spam", "Phishing"], page.Threats.Select(t => t.Title));
        Assert.Equal(["URL detonation", "Spoof DMARC"], page.Threats[1].Rows.Select(r => r.Technology));

        var failing = Page(new Reports(new InvalidOperationException("needs Defender for Office 365")), []);
        await failing.Refresh();
        for (var i = 0; i < 50 && failing.ThreatsLoading; i++)
            await Task.Delay(10, Ct);
        Assert.Contains("Defender for Office 365", failing.ThreatsNote);
        Assert.Empty(failing.Threats);
    }
}
