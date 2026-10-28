using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>Audit log search (8e).</summary>
public sealed class AuditTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Now = new(2026, 10, 28, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_search_sends_only_the_filters_given()
    {
        var http = new FakeHttp().On(HttpMethod.Post, "auditLog/queries", """{"id":"q1","displayName":"Ada","status":"notStarted"}""");
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

        var search = await records.StartSearchAsync(new AuditSearchRequest("Ada", Now.AddDays(-7), Now, ["ada@example.com"], [], ["exchangeAdmin"]), Ct);

        var body = http.Requests.Single().Json!.AsObject();
        Assert.Equal("ada@example.com", body["userPrincipalNameFilters"]![0]!.GetValue<string>());
        Assert.Equal("exchangeAdmin", body["recordTypeFilters"]![0]!.GetValue<string>());
        Assert.False(body.ContainsKey("operationFilters"));
        Assert.False(body.ContainsKey("keywordFilter"));
        Assert.Equal("Queued", search.StatusText);
    }

    [Fact]
    public async Task Records_read_with_the_whole_record_kept()
    {
        var http = new FakeHttp().On(HttpMethod.Get, "q1/records", """
            {"value":[{"id":"r1","createdDateTime":"2026-10-27T10:00:00Z","userPrincipalName":"ada@example.com","operation":"FileAccessed","service":"OneDrive",
              "auditLogRecordType":"sharePointFileOperation","objectId":"/Documents/a.docx","clientIp":"198.51.100.1","auditData":{"Workload":"OneDrive"}}]}
            """);
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

        var record = Assert.Single(await records.GetRecordsAsync("q1", 100, Ct));

        Assert.Equal(("FileAccessed", "SharePoint and OneDrive: Files"), (record.Operation, AuditRecordTypes.Label(record.RecordType)));
        Assert.Contains("OneDrive", record.Json, StringComparison.Ordinal);
    }

    static AuditLogViewModel Page(IAuditRecords? records = null) =>
        new(records ?? new DemoUserRecords(new DemoTenantDirectory(userCount: 60)), null, null, () => Now, (_, _) => Task.CompletedTask);

    [Fact]
    public void Dates_must_run_forwards_and_within_180_days()
    {
        var page = Page();
        page.StartDate = Now.Date.AddDays(2);
        page.EndDate = Now.Date.AddDays(-1);
        Assert.Contains(page.Request().Problems, p => p.Contains("before the end", StringComparison.Ordinal));
        page.StartDate = Now.Date.AddDays(-200);
        page.EndDate = Now.Date;
        Assert.Contains(page.Request().Problems, p => p.Contains("180 days", StringComparison.Ordinal));
    }

    [Fact]
    public void The_end_day_is_included_up_to_now_and_activities_split_by_line()
    {
        var page = Page();
        page.StartDate = Now.Date.AddDays(-6);
        page.EndDate = Now.Date;
        page.Activities = "FileAccessed\nSet-Mailbox, FileAccessed";
        var (request, problems) = page.Request();

        Assert.Empty(problems);
        Assert.Equal(Now, request.End);
        Assert.Equal(["FileAccessed", "Set-Mailbox"], request.Operations);
        Assert.StartsWith("Everyone, ", request.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_waits_then_its_records_open_when_Purview_finishes()
    {
        var page = Page();
        page.Shown();
        page.RecordTypes.Single(r => r.Value == "exchangeAdmin").IsChecked = true;

        await page.SearchCommand.ExecuteAsync(null);
        for (var i = 0; i < 100 && page.SelectedSearch?.Search.IsDone != true; i++)
            await Task.Delay(5, Ct);
        for (var i = 0; i < 100 && page.Results.Total == 0; i++)
            await Task.Delay(5, Ct);

        var row = Assert.Single(page.Searches);
        Assert.Equal("Done", row.StatusText);
        Assert.True(page.Results.Total > 0);
        Assert.All(page.Results.Rows.Cast<AuditRecord>(), r => Assert.Equal("exchangeAdmin", r.RecordType));
        Assert.Null(page.ResultsNote);
    }
}
