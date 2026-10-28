using System.Net;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>Identity Protection (8d-2): reading, the P2 note, previews and bulk actions.</summary>
public sealed class RiskTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("anonymizedIPAddress", "Anonymous IP address")]
    [InlineData("leakedCredentials", "Leaked credentials")]
    [InlineData("someNewThing", "Some new thing")]
    public void Detections_read_in_words(string type, string words) => Assert.Equal(words, Risk.EventName(type));

    [Fact]
    public async Task Each_list_says_whether_it_needs_P2_or_a_permission()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "riskyUsers", """{"error":{"code":"Forbidden","message":"Your tenant is not licensed for this feature."}}""", HttpStatusCode.Forbidden)
            .On(HttpMethod.Get, "riskDetections", """{"error":{"code":"Forbidden","message":"You cannot perform the requested operation, required scopes are missing in the token."}}""",
                HttpStatusCode.Forbidden)
            .On(HttpMethod.Get, "riskyServicePrincipals", """{"value":[{"id":"s1","appId":"a1","displayName":"Survey","riskLevel":"medium","riskState":"atRisk"}]}""");
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

        var risk = await records.GetRiskAsync(Ct);

        Assert.Equal(Risk.NeedsP2, risk.UsersUnavailable);
        Assert.Contains("Grant Permissions", risk.DetectionsUnavailable, StringComparison.Ordinal);
        Assert.Null(risk.AppsUnavailable);
        Assert.Equal("Survey", Assert.Single(risk.Apps).Name);
    }

    [Fact]
    public async Task Detections_read_with_place_and_address()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "riskyUsers", """{"value":[]}""")
            .On(HttpMethod.Get, "riskyServicePrincipals", """{"value":[]}""")
            .On(HttpMethod.Get, "riskDetections", """
                {"value":[{"id":"d1","userId":"u1","userDisplayName":"Ada","riskEventType":"unfamiliarFeatures","riskLevel":"medium","riskState":"atRisk",
                  "activity":"signin","ipAddress":"198.51.100.1","location":{"city":"Lisbon","state":"","countryOrRegion":"PT"},"detectedDateTime":"2026-10-01T10:00:00Z"}]}
                """);
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

        var detection = Assert.Single((await records.GetRiskAsync(Ct)).Detections);

        Assert.Equal("Lisbon, PT", detection.Location);
        Assert.Equal("198.51.100.1", detection.Ip);
    }

    [Fact]
    public async Task Without_P2_the_demo_answers_as_Graph_does()
    {
        var risk = await new DemoUserRecords(new DemoTenantDirectory(userCount: 60)).GetRiskAsync(Ct);
        Assert.Equal(Risk.NeedsP2, risk.UsersUnavailable);
        Assert.Empty(risk.Users);
    }

    static async Task<(SecurityAreaViewModel Area, ChangePipeline Pipeline)> Premium()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory, pim: true);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var area = new SecurityAreaViewModel(records, pipeline, null, () => DateTimeOffset.UtcNow, _ => { }, null, null, risk: records, canManageRisk: Availability.Yes);
        area.SelectedPage = SecurityAreaViewModel.Pages.Single(p => p.Key == "riskyusers");
        for (var i = 0; i < 100 && area.RiskyUsers.Total == 0; i++)
            await Task.Delay(5, Ct);
        return (area, pipeline);
    }

    [Fact]
    public async Task The_preview_confirms_or_dismisses_and_then_offers_Undo()
    {
        var (area, pipeline) = await Premium();
        Assert.Equal(3, area.RiskyUsers.Rows.Count);
        var user = area.RiskyUsers.Rows.Cast<RiskyUser>().First(u => u.Level == "high");
        area.RiskyUsers.IsPreviewOpen = true;
        area.RiskyUsers.SelectedRow = user;
        var preview = Assert.IsType<RiskPreviewViewModel>(area.RiskyUsers.PreviewModel);
        Assert.Equal(["Confirm Compromised", "Dismiss Risk"], preview.Buttons.Select(b => b.Text));
        Assert.True(preview.HasDetections);

        preview.Buttons[1].ActCommand.Execute(null);

        Assert.False(Assert.IsType<SetUserRisk>(Assert.Single(pipeline.Pending)).Compromised);
        var after = Assert.IsType<RiskPreviewViewModel>(area.RiskyUsers.PreviewModel);
        Assert.Equal("Undo", Assert.Single(after.Buttons).Text);
    }

    [Fact]
    public async Task Bulk_dismiss_skips_people_not_at_risk()
    {
        var (_, pipeline) = await Premium();
        var all = (await new DemoUserRecords(new DemoTenantDirectory(userCount: 60), pim: true).GetRiskAsync(Ct)).Users;
        var panel = RiskLists.UsersPanel([.. all.Cast<object>()], _ => { }, pipeline, Availability.Yes);

        var plan = await panel.Bulk!.Actions.Single(a => a.Title == "Dismiss Risk").PlanAsync(Ct);

        Assert.Equal(3, plan.Changes.Count);
        Assert.Equal(2, plan.Skipped.Count);
    }

    [Fact]
    public async Task An_apps_risk_is_sent_by_its_service_principal_ID()
    {
        var http = new FakeHttp().On(HttpMethod.Post, "riskyServicePrincipals/dismiss", "{}");
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

        var result = await executor.ApplyAsync(new SetAppRisk(new ChangeTarget(AppChange.Enterprise, "s1", "Survey", null), false), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("s1", http.Requests.Single().Json!["servicePrincipalIds"]![0]!.GetValue<string>());
    }
}
