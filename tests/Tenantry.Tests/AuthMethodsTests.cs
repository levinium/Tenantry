using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>The authentication methods policy and security defaults (8d-1).</summary>
public sealed class AuthMethodsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static JsonObject Tap() => JsonNode.Parse("""
        {"@odata.type":"#microsoft.graph.temporaryAccessPassAuthenticationMethodConfiguration","id":"TemporaryAccessPass","state":"enabled",
         "defaultLifetimeInMinutes":60,"defaultLength":8,"minimumLifetimeInMinutes":60,"maximumLifetimeInMinutes":480,"isUsableOnce":true,
         "excludeTargets":[],"includeTargets":[{"targetType":"group","id":"all_users","isRegistrationRequired":false}],"passkeyProfiles":[]}
        """)!.AsObject();

    [Fact]
    public void A_method_reads_in_words_and_is_sent_without_read_only_parts()
    {
        var tap = Tap();
        Assert.Equal("On · Everyone · Lasts (Usually): 60 minutes · Shortest: 60 minutes · Longest: 480 minutes · Length: 8 characters · One Use Only: Yes",
            AuthMethods.Describe("TemporaryAccessPass", tap, new Dictionary<string, string>()));
        var body = AuthMethods.MethodBody(tap);
        Assert.Null(body["passkeyProfiles"]);
        Assert.Null(body["id"]);
        Assert.Equal(60, body["defaultLifetimeInMinutes"]!.GetValue<int>());
        Assert.Equal("#microsoft.graph.temporaryAccessPassAuthenticationMethodConfiguration", body["@odata.type"]!.GetValue<string>());
    }

    [Fact]
    public void A_usual_lifetime_outside_the_shortest_and_longest_is_a_problem()
    {
        var tap = Tap();
        tap["defaultLifetimeInMinutes"] = 600;
        Assert.Contains(AuthMethods.Problems("TemporaryAccessPass", tap), p => p.Contains("between the shortest and the longest", StringComparison.Ordinal));
    }

    [Fact]
    public void The_registration_campaign_is_sent_inside_registration_enforcement()
    {
        var body = AuthMethods.SectionBody("registrationCampaign", new JsonObject { ["state"] = "enabled" });
        Assert.Equal("enabled", body["registrationEnforcement"]!["authenticationMethodsRegistrationCampaign"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void Who_names_groups_and_exclusions()
    {
        var item = JsonNode.Parse("""{"includeTargets":[{"id":"g1"}],"excludeTargets":[{"id":"g2"}]}""")!.AsObject();
        Assert.Equal("Sales, except Contractors", AuthMethods.Who(item, new Dictionary<string, string> { ["g1"] = "Sales", ["g2"] = "Contractors" }));
    }

    static ChangePipeline Pipeline(DemoTenantDirectory directory, DemoUserRecords records) =>
        new(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");

    [Fact]
    public async Task Switching_a_method_off_stages_a_caution_and_switching_it_back_takes_it_out()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = Pipeline(directory, records);
        var page = new AuthMethodsViewModel(records, pipeline, true, null, null);
        page.Shown();
        for (var i = 0; i < 50 && page.Methods.Count == 0; i++)
            await Task.Delay(5, Ct);
        page.Selected = page.Methods.Single(m => m.Id == "Fido2");
        var editor = Assert.IsType<AuthItemEditor>(page.Editor);

        editor.State = editor.States.Single(s => s.Value == "disabled");
        Assert.True(Assert.IsType<SetAuthMethodConfig>(Assert.Single(pipeline.Pending)).IsCaution);
        Assert.True(page.Methods.Single(m => m.Id == "Fido2").Pending);
        editor.State = editor.States.Single(s => s.Value == "enabled");
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public async Task Choosing_a_group_replaces_everyone_and_a_setting_change_applies_in_the_demo()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = Pipeline(directory, records);
        var policy = await records.GetAuthMethodsAsync(Ct);
        var tap = policy.Methods.Single(m => m["id"]!.GetValue<string>() == "TemporaryAccessPass");
        var group = (await directory.GetGroupsAsync(Ct)).First(g => g.Kind == GroupKind.Security);
        var editor = new AuthItemEditor("TemporaryAccessPass", tap, policy.GroupNames, pipeline, true, Tenantry.ViewModels.EmptyIndex.Instance);

        editor.IncludePicker.PickCommand.Execute(Tenantry.ViewModels.Editing.PickOption.Of(group));
        editor.Settings.Single(s => s.Label == "Lasts (Usually)").Number = 120;

        Assert.False(editor.Everyone);
        var change = Assert.IsType<SetAuthMethodConfig>(Assert.Single(pipeline.Pending));
        Assert.Contains(group.DisplayName, change.After, StringComparison.Ordinal);
        Assert.Contains("120 minutes", change.After, StringComparison.Ordinal);
        await pipeline.ApplyAsync(cancellationToken: Ct);
        var after = (await records.GetAuthMethodsAsync(Ct)).Methods.Single(m => m["id"]!.GetValue<string>() == "TemporaryAccessPass");
        Assert.Equal(120, after["defaultLifetimeInMinutes"]!.GetValue<int>());
        Assert.Equal(group.Id, Assert.Single(after["includeTargets"]!.AsArray())!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Security_defaults_are_refused_while_Conditional_Access_is_on()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = Pipeline(directory, records);
        await records.GetPoliciesAsync(Ct);
        var editor = new SecurityDefaultsEditor(false, pipeline, true) { IsOn = true };

        var result = Assert.Single(await pipeline.ApplyAsync(cancellationToken: Ct)).Result;

        Assert.Equal(ChangeOutcome.Failed, result.Outcome);
        Assert.Contains("Conditional Access", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_method_is_patched_at_its_own_path_and_a_section_at_the_policy()
    {
        var http = new FakeHttp().On(HttpMethod.Patch, "authenticationMethodsPolicy", "{}");
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

        await executor.ApplyAsync(new SetAuthMethodConfig(new ChangeTarget("authmethod", "Fido2", "Passkeys", null), """{"state":"disabled"}""", "a", "b", true), Ct);
        await executor.ApplyAsync(new SetAuthMethodConfig(new ChangeTarget("authpolicy", "systemCredentialPreferences", "System-Preferred MFA", null),
            """{"systemCredentialPreferences":{"state":"enabled"}}""", "a", "b", false), Ct);

        Assert.EndsWith("/beta/policies/authenticationMethodsPolicy/authenticationMethodConfigurations/Fido2", http.Requests[0].Url.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/beta/policies/authenticationMethodsPolicy", http.Requests[1].Url.AbsolutePath, StringComparison.Ordinal);
    }
}
