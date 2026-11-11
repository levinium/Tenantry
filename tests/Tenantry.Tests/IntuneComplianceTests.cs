using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Intune;

namespace Tenantry.Tests;

/// <summary>Intune compliance policies (13a): assignments, settings, actions, and the page.</summary>
public sealed class IntuneComplianceTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ChangeTarget Test = IntuneChange.TargetFor("deviceManagement/deviceCompliancePolicies/p1", "zz-tenantry-test-compliance");

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Assignments_read_from_Graph_and_go_back_the_same_with_their_filter()
    {
        var read = IntuneAssignment.From(Json("""
            {"target":{"@odata.type":"#microsoft.graph.allDevicesAssignmentTarget","deviceAndAppManagementAssignmentFilterId":"f1","deviceAndAppManagementAssignmentFilterType":"exclude"}}
            """))!;
        Assert.Equal((AssignmentKind.AllDevices, "f1", "exclude"), (read.Kind, read.FilterId, read.FilterMode));
        Assert.Equal("exclude", read.Target()["deviceAndAppManagementAssignmentFilterType"]!.GetValue<string>());
        var none = IntuneAssignment.From(Json("""{"target":{"@odata.type":"#microsoft.graph.groupAssignmentTarget","groupId":"g1","deviceAndAppManagementAssignmentFilterId":"00000000-0000-0000-0000-000000000000"}}"""))!;
        Assert.Null(none.FilterId);
        Assert.Equal("Not Staff", new IntuneAssignment(AssignmentKind.ExcludeGroup, "g2", "Staff").Text);
        Assert.Equal("Required: All Users (except devices matching Kiosks)", new IntuneAssignment(AssignmentKind.AllUsers, FilterName: "Kiosks", FilterId: "f", FilterMode: "exclude", Intent: "required").Text);
    }

    [Fact]
    public void Test_mode_lets_a_test_policy_reach_only_test_groups_and_never_everyone()
    {
        var testGroup = new IntuneAssignment(AssignmentKind.Group, "g-t", "zz-tenantry-test-group");
        var realGroup = new IntuneAssignment(AssignmentKind.Group, "g-r", "Staff");
        Assert.Null(WriteGuard.Refusal(new SetIntuneAssignments(Test, "Compliance Policy", [], [testGroup]), testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new SetIntuneAssignments(Test, "Compliance Policy", [], [realGroup]), testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new SetIntuneAssignments(Test, "Compliance Policy", [], [IntuneAssignment.AllDevices()]), testMode: true));
        Assert.True(new SetIntuneAssignments(Test, "x", [testGroup], []).IsCaution);
        Assert.True(new SetIntuneAssignments(Test, "x", [], [IntuneAssignment.AllUsers()]).IsCaution);
        Assert.False(new SetIntuneAssignments(Test, "x", [], [testGroup]).IsCaution);
        Assert.NotNull(WriteGuard.Refusal(new SetIntuneSetting(Test, CompliancePlatforms.Windows, "displayName", "Name", System.Text.Json.Nodes.JsonValue.Create("Real"), "a", "b"), testMode: true));
    }

    [Fact]
    public void An_apps_assignments_carry_their_intent_and_a_dotted_setting_keeps_its_neighbours()
    {
        var body = GraphChangeExecutor.AssignBody(new SetIntuneAssignments(Test, "App", [], [IntuneAssignment.AllUsers() with { Intent = "available" }], "mobileAppAssignments"));
        Assert.Equal("available", body["mobileAppAssignments"]![0]!["intent"]!.GetValue<string>());

        var setting = new SetIntuneSetting(IntuneChange.Organization("x"), "", "settings.secureByDefault", "x", System.Text.Json.Nodes.JsonValue.Create(true), "Off", "On");
        var merged = GraphChangeExecutor.SettingBody(setting, Json("""{"settings":{"secureByDefault":false,"deviceComplianceCheckinThresholdDays":30}}"""));
        Assert.Equal(30, merged["settings"]!["deviceComplianceCheckinThresholdDays"]!.GetValue<int>());
        Assert.True(merged["settings"]!["secureByDefault"]!.GetValue<bool>());
        var fromNothing = GraphChangeExecutor.SettingBody(setting, Json("""{"settings":null}"""));
        Assert.Equal(30, fromNothing["settings"]!["deviceComplianceCheckinThresholdDays"]!.GetValue<int>());
        var plain = GraphChangeExecutor.SettingBody(new SetIntuneSetting(Test, CompliancePlatforms.Windows, "bitLockerEnabled", "x", System.Text.Json.Nodes.JsonValue.Create(true), "Off", "On"), null);
        Assert.Equal(CompliancePlatforms.Windows, plain["@odata.type"]!.GetValue<string>());
    }

    [Fact]
    public void A_policy_reads_its_actions_in_order_and_its_device_counts()
    {
        var policy = GraphUserRecords.ToCompliancePolicy(Json("""
            {"id":"p1","displayName":"Windows","@odata.type":"#microsoft.graph.windows10CompliancePolicy","assignments":[],
             "scheduledActionsForRule":[{"scheduledActionConfigurations":[{"actionType":"retire","gracePeriodHours":720},{"actionType":"block","gracePeriodHours":24,"notificationTemplateId":"00000000-0000-0000-0000-000000000000"}]}]}
            """), Json("""{"successCount":9,"failedCount":2,"errorCount":0,"conflictCount":0,"notApplicableCount":1,"pendingCount":1}"""));

        Assert.Equal(["block", "retire"], policy.Actions.Select(a => a.Action));
        Assert.Null(policy.Actions[0].TemplateId);
        Assert.Equal("Mark the device noncompliant after 1 day", policy.Actions[0].Text);
        Assert.Equal((9, 2, 12), (policy.Counts!.Compliant, policy.Counts.NonCompliant, policy.Counts.Total));
    }

    [Fact]
    public async Task A_new_policy_is_made_then_assigned()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "deviceManagement/deviceCompliancePolicies", """{"id":"new1"}""")
            .On(HttpMethod.Post, "new1/assign", "{}");
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

        var result = await executor.ApplyAsync(new CreateCompliancePolicy(IntuneChange.TargetFor("new:zz-tenantry-test-c", "zz-tenantry-test-c"), CompliancePlatforms.IOS, null, 2,
            [new IntuneAssignment(AssignmentKind.Group, "g-t", "zz-tenantry-test-group")]), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal(48, http.Requests[0].Json!["scheduledActionsForRule"]![0]!["scheduledActionConfigurations"]![0]!["gracePeriodHours"]!.GetValue<int>());
        Assert.Equal("g-t", http.Requests[1].Json!["assignments"]![0]!["target"]!["groupId"]!.GetValue<string>());
    }

    static async Task<(DemoUserRecords Records, ChangePipeline Pipeline, CompliancePoliciesViewModel Page)> Page()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var page = new CompliancePoliciesViewModel(records, pipeline, Availability.Yes, new Groups(await directory.GetGroupsAsync(Ct)));
        await page.Refresh();
        return (records, pipeline, page);
    }

    [Fact]
    public async Task On_the_page_a_setting_an_action_and_an_assignment_are_staged_and_apply()
    {
        var (records, pipeline, page) = await Page();
        var card = page.Cards.Single(c => c.Name == "zz-tenantry-test-compliance");

        var bitLocker = card.Groups.SelectMany(g => g.Rows).Single(r => r.Def.Parameter == "bitLockerEnabled");
        bitLocker.IsOn = true;
        bitLocker.IsOn = false;
        Assert.Empty(pipeline.Pending);
        bitLocker.IsOn = true;
        var minimum = card.Groups.SelectMany(g => g.Rows).Single(r => r.Def.Parameter == "osMinimumVersion");
        minimum.TextValue = "10.0.22631";
        minimum.CommitTextCommand.Execute(null);

        card.AddActionCommand.Execute(card.ActionChoices.Single(a => a.Value == "retire"));
        Assert.Equal(3, pipeline.Pending.Count);
        Assert.Contains(pipeline.Pending, p => p is SetComplianceActions { New.Count: 2 });

        await pipeline.ApplyAsync(cancellationToken: Ct);
        var after = (await records.GetCompliancePoliciesAsync(Ct)).Single(p => p.Name == "zz-tenantry-test-compliance");
        Assert.True(after.Raw.GetProperty("bitLockerEnabled").GetBoolean());
        Assert.Equal("10.0.22631", after.Raw.GetProperty("osMinimumVersion").GetString());
        Assert.Equal(["block", "retire"], after.Actions.Select(a => a.Action));
    }

    sealed class Groups(IReadOnlyList<GroupSummary> groups) : IDirectoryIndex
    {
        public UserSummary? User(string id) => null;
        public GroupSummary? Group(string id) => groups.FirstOrDefault(g => g.Id == id);
        public IEnumerable<UserSummary> AllUsers => [];
        public IEnumerable<GroupSummary> AllGroups => groups;
    }

    [Fact]
    public async Task All_Devices_is_added_and_undone_and_a_new_policy_is_checked_first()
    {
        var (_, pipeline, page) = await Page();
        var card = page.Cards.Single(c => c.Name == "Macs");
        Assert.True(card.Assignments.IsEmpty);

        card.Assignments.AddAllDevicesCommand.Execute(null);
        Assert.True(((SetIntuneAssignments)Assert.Single(pipeline.Pending)).IsCaution);
        Assert.False(card.Assignments.CanAddAllDevices);
        Assert.Equal("Adding", card.Assignments.Rows.Single().StateText);
        card.Assignments.Rows.Single().DoCommand.Execute(null);
        Assert.Empty(pipeline.Pending);

        page.ToggleAddCommand.Execute(null);
        page.NewName = "Macs";
        page.AddCommand.Execute(null);
        Assert.NotNull(page.AddProblem);
        page.NewName = "zz-tenantry-test-new";
        page.NewPlatform = CompliancePoliciesViewModel.Platforms.Single(p => p.Value == CompliancePlatforms.AndroidWorkProfile);
        page.AddCommand.Execute(null);
        Assert.Equal(CompliancePlatforms.AndroidWorkProfile, ((CreateCompliancePolicy)Assert.Single(pipeline.Pending)).ODataType);
        Assert.False(page.IsAddOpen);
    }
}
