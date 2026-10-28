using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;

namespace Tenantry.Tests;

/// <summary>Conditional Access (8b-1): policies in words, the state switch, What If, and what Graph is sent.</summary>
public sealed class CaTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static GraphClient Graph(FakeHttp http) => new(http.Client(), new FakeTokens(), delay: NoDelay.Instance);

    static readonly CaNames Names = new(
        new Dictionary<string, string> { ["11111111-1111-1111-1111-111111111111"] = "Break Glass" },
        new Dictionary<string, string> { ["22222222-2222-2222-2222-222222222222"] = "Finance" },
        new Dictionary<string, string> { ["62e90394-69f5-4237-9190-012177145e10"] = "Global Administrator" },
        new Dictionary<string, string>(),
        new Dictionary<string, string> { ["loc1"] = "Head Office" },
        new Dictionary<string, string>());

    [Fact]
    public void A_policy_reads_as_lines_of_words()
    {
        var policy = JsonNode.Parse("""
            {"conditions":{"users":{"includeUsers":["All"],"excludeUsers":["11111111-1111-1111-1111-111111111111"],"includeGroups":[],"excludeGroups":["22222222-2222-2222-2222-222222222222"],
               "excludeRoles":["62e90394-69f5-4237-9190-012177145e10"]},
              "applications":{"includeApplications":["Office365"],"excludeApplications":["MicrosoftAdminPortals"]},
              "locations":{"includeLocations":["All"],"excludeLocations":["loc1"]},
              "clientAppTypes":["browser","mobileAppsAndDesktopClients"]},
             "grantControls":{"operator":"OR","builtInControls":["mfa","compliantDevice"]},
             "sessionControls":{"signInFrequency":{"isEnabled":true,"value":12,"type":"hours","frequencyInterval":"timeBased"}}}
            """)!.AsObject();

        var lines = CaText.Lines(policy, Names).ToDictionary(l => l.Label, l => l.Text);

        Assert.Equal("Everyone", lines["Who"]);
        Assert.Equal("Break Glass, members of Finance and holders of Global Administrator", lines["Except"]);
        Assert.Equal(("Office 365", "Microsoft admin portals"), (lines["Apps"], lines["Except Apps"]));
        Assert.Equal("Any location, except Head Office", lines["Locations"]);
        Assert.Equal("Browsers and mobile and desktop apps", lines["Client Apps"]);
        Assert.Equal("Require MFA or Require a compliant device · Sign in again every 12 hours", lines["Then"]);
    }

    [Fact]
    public void A_policy_naming_someone_no_longer_there_says_so()
    {
        var policy = JsonNode.Parse("""{"conditions":{"users":{"includeUsers":["99999999-9999-9999-9999-999999999999"]}},"grantControls":{"builtInControls":["block"]}}""")!.AsObject();

        Assert.Equal("Someone no longer in the directory", CaText.Lines(policy, CaNames.Empty)[0].Text);
    }

    static async Task<(DemoUserRecords Records, ChangePipeline Pipeline, CaDirectory Directory)> Demo()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), DemoUserRecords.Account);
        return (records, pipeline, await records.GetPoliciesAsync(Ct));
    }

    [Fact]
    public async Task Choosing_a_state_stages_it_switching_on_is_a_caution_and_undoing_puts_the_switch_back()
    {
        var (_, pipeline, directory) = await Demo();
        var report = directory.Policies.Single(p => p.State == "enabledForReportingButNotEnforced");
        var page = new CaPolicyDetailViewModel(CaPolicyRow.Of(report, directory.Names), pipeline, Availability.Yes);

        page.State = CaStateChoice.All.Single(c => c.State == "enabled");
        var on = Assert.IsType<SetCaPolicyState>(Assert.Single(pipeline.Pending));
        Assert.True(on.IsCaution);
        Assert.Contains("On", page.PendingText, StringComparison.Ordinal);

        pipeline.Unstage(on);
        Assert.Equal("enabledForReportingButNotEnforced", page.State!.State);
    }

    [Fact]
    public async Task Policies_are_guarded_by_name_and_deleting_asks_for_it()
    {
        var (_, _, directory) = await Demo();
        var policy = directory.Policies[0];
        var delete = new DeleteCaPolicy(new ChangeTarget("capolicy", policy.Id, policy.Name, null), "On");
        var test = new SetCaPolicyState(new ChangeTarget("capolicy", "x", WriteGuard.Marker + "mfa", null), "disabled", "enabledForReportingButNotEnforced");

        Assert.NotNull(WriteGuard.Refusal(delete, testMode: true));
        Assert.Equal(policy.Name, delete.ConfirmPhrase);
        Assert.Null(WriteGuard.Refusal(test, testMode: true));
    }

    [Fact]
    public async Task The_state_is_patched_and_read_back()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Patch, "conditionalAccess/policies/p1", "{}")
            .On(HttpMethod.Get, "conditionalAccess/policies/p1", """{"state":"disabled"}""");
        var executor = new GraphChangeExecutor(Graph(http), new MemoryEntityStore(), NoDelay.Instance);

        var result = await executor.ApplyAsync(new SetCaPolicyState(new ChangeTarget("capolicy", "p1", "A", null), "enabled", "disabled"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("disabled", http.Requests[0].Json!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task What_if_sends_the_sign_in_and_reads_reasons_in_words()
    {
        var http = new FakeHttp().On(HttpMethod.Post, "conditionalAccess/evaluate", """
            {"value":[
              {"id":"p1","displayName":"MFA","state":"enabled","policyApplies":true,"analysisReasons":"notSet","grantControls":{"operator":"OR","builtInControls":["mfa"]}},
              {"id":"p2","displayName":"Legacy","state":"enabled","policyApplies":false,"analysisReasons":"clientApps, users"}]}
            """);
        var records = new GraphUserRecords(Graph(http), new MemoryEntityStore());

        var results = await records.WhatIfAsync(new WhatIfInput("u1", "app-1", "any", "browser", "US", null, "none", "none"), Ct);

        var body = http.Requests.Single().Json!;
        Assert.Equal("u1", body["signInIdentity"]!["userId"]!.GetValue<string>());
        Assert.Null(body["signInConditions"]!["devicePlatform"]);
        Assert.Equal("US", body["signInConditions"]!["country"]!.GetValue<string>());
        Assert.Equal(("MFA", "Require MFA"), (results[0].Name, results[0].Requires));
        Assert.Equal(["it doesn't cover this kind of app", "it doesn't target this person"], results[1].Reasons);
    }

    [Fact]
    public async Task The_demo_applies_state_changes_and_deletes()
    {
        var (records, pipeline, directory) = await Demo();
        var off = directory.Policies.Single(p => p.State == "disabled");

        pipeline.Stage(new SetCaPolicyState(new ChangeTarget("capolicy", off.Id, off.Name, null), "disabled", "enabledForReportingButNotEnforced"));
        pipeline.Stage(new DeleteCaPolicy(new ChangeTarget("capolicy", directory.Policies[0].Id, directory.Policies[0].Name, null), "On"));
        await pipeline.ApplyAsync(cancellationToken: Ct);

        var after = await records.GetPoliciesAsync(Ct);
        Assert.Equal("enabledForReportingButNotEnforced", after.Policies.Single(p => p.Id == off.Id).State);
        Assert.Equal(directory.Policies.Count - 1, after.Policies.Count);
    }
}
