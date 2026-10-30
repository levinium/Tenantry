using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;

namespace Tenantry.Tests;

/// <summary>The Conditional Access editor (8b-2): it keeps what it doesn't cover, starts safe, refuses lockouts, and stages one change.</summary>
public sealed class CaEditorTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Index(IEnumerable<UserSummary> users, IEnumerable<GroupSummary> groups) : IDirectoryIndex
    {
        readonly Dictionary<string, UserSummary> _users = users.ToDictionary(u => u.Id);
        readonly Dictionary<string, GroupSummary> _groups = groups.ToDictionary(g => g.Id);
        public UserSummary? User(string id) => _users.GetValueOrDefault(id);
        public GroupSummary? Group(string id) => _groups.GetValueOrDefault(id);
        public IEnumerable<UserSummary> AllUsers => _users.Values;
        public IEnumerable<GroupSummary> AllGroups => _groups.Values;
    }

    sealed record Setup(DemoUserRecords Records, ChangePipeline Pipeline, CaDirectory Directory, IDirectoryIndex Index, UserSummary Me);

    static async Task<Setup> Demo()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var users = await directory.GetUsersAsync(Ct);
        return new(records, pipeline, await records.GetPoliciesAsync(Ct), new Index(users, await directory.GetGroupsAsync(Ct)), users.First(u => u.Kind == UserKind.Member));
    }

    static CaPolicyEditorViewModel Editor(Setup s, CaPolicy? policy) =>
        new(policy, s.Directory, s.Pipeline, s.Records, s.Records, s.Index, s.Me.UserPrincipalName, _ => { });

    [Fact]
    public async Task Opening_a_policy_stages_nothing_and_an_edit_keeps_settings_the_editor_doesnt_cover()
    {
        var s = await Demo();
        var original = s.Directory.Policies.First(p => p.Name == "Require MFA for everyone");
        var node = original.Node;
        node["conditions"]!["insiderRiskLevels"] = "minor";
        node["sessionControls"] = new JsonObject { ["secureSignInSession"] = new JsonObject { ["isEnabled"] = true } };
        var policy = original with { Json = node.ToJsonString() };

        var editor = Editor(s, policy);
        Assert.Empty(s.Pipeline.Pending);

        editor.Name = "Require MFA for everyone (all apps)";
        var update = Assert.IsType<UpdateCaPolicy>(Assert.Single(s.Pipeline.Pending));
        var json = JsonNode.Parse(update.Json)!;
        Assert.Equal("minor", json["conditions"]!["insiderRiskLevels"]!.GetValue<string>());
        Assert.True(json["sessionControls"]!["secureSignInSession"]!["isEnabled"]!.GetValue<bool>());
        Assert.Equal(["mfa"], json["grantControls"]!["builtInControls"]!.AsArray().Select(x => x!.GetValue<string>()));

        editor.ClientApps[0].IsChecked = true;
        Assert.Single(s.Pipeline.Pending);
    }

    [Fact]
    public async Task A_new_policy_from_a_template_starts_report_only_and_leaves_you_out()
    {
        var s = await Demo();
        var editor = Editor(s, null);
        var template = (await s.Records.GetTemplatesAsync(Ct)).First(t => t.Name == "Block legacy authentication");

        editor.StartCommand.Execute(template);

        var create = Assert.IsType<CreateCaPolicy>(Assert.Single(s.Pipeline.Pending));
        var json = JsonNode.Parse(create.Json)!;
        Assert.Equal("enabledForReportingButNotEnforced", json["state"]!.GetValue<string>());
        Assert.Contains(s.Me.Id, json["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["exchangeActiveSync", "other"], json["conditions"]!["clientAppTypes"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.False(create.IsCaution);
        Assert.Contains(editor.Lines, l => l.Label == "Then" && l.Value == "Block access");
    }

    [Fact]
    public async Task Blocking_everyone_while_on_without_leaving_yourself_out_isnt_staged()
    {
        var s = await Demo();
        var editor = Editor(s, null);
        editor.Name = "Block everything";
        editor.Who = CaPolicyEditorViewModel.WhoChoices.Single(c => c.Value == "all");
        editor.GrantMode = CaPolicyEditorViewModel.GrantModes.Single(c => c.Value == "block");
        editor.State = CaStateChoice.All.Single(c => c.State == "enabled");
        Assert.Single(s.Pipeline.Pending);

        editor.LeaveMeOut = false;

        Assert.Contains(editor.Problems, p => p.Contains("it covers everyone", StringComparison.Ordinal));
        Assert.Empty(s.Pipeline.Pending);
    }

    [Fact]
    public void Blocking_a_role_or_group_you_hold_is_refused_unless_you_are_left_out()
    {
        var policy = JsonNode.Parse("""
            {"displayName":"Block admins","state":"enabled",
             "conditions":{"users":{"includeRoles":["role-1"],"includeGroups":["group-1"]},"applications":{"includeApplications":["MicrosoftAdminPortals"]},"clientAppTypes":["all"]},
             "grantControls":{"operator":"OR","builtInControls":["block"]}}
            """)!.AsObject();

        Assert.Contains(CaJson.Problems(policy, "me", myRoles: new HashSet<string> { "role-1" }), p => p.Contains("admin role you hold", StringComparison.Ordinal));
        Assert.Contains(CaJson.Problems(policy, "me", myGroups: new HashSet<string> { "group-1" }), p => p.Contains("group you're in", StringComparison.Ordinal));
        Assert.DoesNotContain(CaJson.Problems(policy, "me", new HashSet<string>(), new HashSet<string>()), p => p.Contains("block you", StringComparison.Ordinal));

        policy["conditions"]!["users"]!["excludeUsers"] = new JsonArray("me");
        Assert.DoesNotContain(CaJson.Problems(policy, "me", myRoles: new HashSet<string> { "role-1" }), p => p.Contains("block you", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Any_edit_to_a_policy_that_is_on_is_a_caution_change()
    {
        var s = await Demo();
        var on = s.Directory.Policies.First(p => p.State == "enabled");
        var editor = Editor(s, on);

        editor.Name = on.Name + " (edited)";

        var update = Assert.IsType<UpdateCaPolicy>(Assert.Single(s.Pipeline.Pending));
        Assert.True(update.IsCaution);
    }

    [Fact]
    public async Task Guests_named_the_newer_way_stay_one_way_and_shared_apps_and_trusted_places_are_kept()
    {
        var s = await Demo();
        var original = s.Directory.Policies.First(p => p.Name == "Require MFA for everyone");
        var node = original.Node;
        node["conditions"]!["users"]!["excludeGuestsOrExternalUsers"] = new JsonObject { ["guestOrExternalUserTypes"] = "b2bCollaborationGuest" };
        node["conditions"]!["applications"]!["includeApplications"] = new JsonArray("Office365", "00000003-0000-0ff1-ce00-000000000000");
        node["conditions"]!["locations"] = new JsonObject { ["includeLocations"] = new JsonArray("AllTrusted", "11111111-1111-1111-1111-111111111111"), ["excludeLocations"] = new JsonArray() };
        node["conditions"]!["devices"] = new JsonObject { ["includeDevices"] = new JsonArray("All") };
        var editor = Editor(s, original with { Json = node.ToJsonString() });

        editor.Name = "Renamed";

        var json = JsonNode.Parse(Assert.IsType<UpdateCaPolicy>(Assert.Single(s.Pipeline.Pending)).Json)!;
        Assert.DoesNotContain("GuestsOrExternalUsers", json["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.NotNull(json["conditions"]!["users"]!["excludeGuestsOrExternalUsers"]);
        Assert.Contains("Office365", json["conditions"]!["applications"]!["includeApplications"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("AllTrusted", json["conditions"]!["locations"]!["includeLocations"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.NotNull(json["conditions"]!["devices"]?["includeDevices"]);
    }

    [Fact]
    public async Task MFA_and_an_authentication_strength_together_is_refused()
    {
        var s = await Demo();
        var editor = Editor(s, s.Directory.Policies.First(p => p.Name == "Require MFA for admins"));
        var policy = editor.Policy();
        policy["grantControls"]!["authenticationStrength"] = new JsonObject { ["id"] = "x" };

        Assert.Contains(CaJson.Problems(policy, null), p => p.Contains("authentication strength", StringComparison.Ordinal));
    }

    [Fact]
    public void Graph_gets_no_read_only_properties_and_a_strength_by_id()
    {
        var policy = JsonNode.Parse("""
            {"id":"p1","createdDateTime":"2026-01-01T00:00:00Z","templateId":null,"displayName":"A","state":"disabled",
             "grantControls":{"operator":"OR","builtInControls":[],"authenticationStrength@odata.context":"x","authenticationStrength":{"id":"s1","displayName":"Phishing-resistant MFA","allowedCombinations":["fido2"]}}}
            """)!.AsObject();

        var body = CaJson.ForWrite(policy);

        Assert.Null(body["id"]);
        Assert.Null(body["createdDateTime"]);
        Assert.False(body["grantControls"]!.AsObject().ContainsKey("authenticationStrength@odata.context"));
        Assert.Equal("""{"id":"s1"}""", body["grantControls"]!["authenticationStrength"]!.ToJsonString());
    }

    [Fact]
    public async Task A_new_policy_is_posted_and_waited_for()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "conditionalAccess/policies", """{"id":"new-1"}""")
            .On(HttpMethod.Get, "conditionalAccess/policies/new-1", """{"id":"new-1"}""");
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

        var result = await executor.ApplyAsync(new CreateCaPolicy(new ChangeTarget("capolicy", "A", "A", null),
            """{"id":"ignored","displayName":"A","state":"enabledForReportingButNotEnforced","conditions":{}}""", "A"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Null(http.Requests[0].Json!["id"]);
    }
}
