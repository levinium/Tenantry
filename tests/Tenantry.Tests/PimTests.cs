using System.Text.Json.Nodes;
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

/// <summary>PIM (8a-2): role settings read and rewritten, activating, approving and extending, against the P2 demo and fake Graph.</summary>
public sealed class PimTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static GraphClient Graph(FakeHttp http) => new(http.Client(), new FakeTokens(), delay: NoDelay.Instance);

    static async Task<(DemoUserRecords Records, ChangePipeline Pipeline, RoleDirectory Directory)> Demo(bool pim = true)
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory, pim);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), DemoUserRecords.Account);
        return (records, pipeline, await records.GetRolesAsync(Ct));
    }

    static async Task<RoleSettingsSection> SettingsFor(string roleName, bool pim = true)
    {
        var (records, pipeline, directory) = await Demo(pim);
        var page = new RoleDetailViewModel(directory.Roles.Single(r => r.Role.Name == roleName), directory, pipeline, Availability.Yes, records: records);
        await page.Settings.LoadAsync(Ct);
        return page.Settings;
    }

    // ---- Rules -------------------------------------------------------------------------------------

    [Fact]
    public async Task Settings_are_read_from_the_rules_and_rewriting_one_keeps_the_rest_of_it()
    {
        var settings = (await (await Demo()).Records.GetRoleSettingsAsync(DirectoryRoles.SecurityAdministrator, Ct)).Value!;

        Assert.Equal((TimeSpan.FromHours(8), true, true, false, false), (settings.MaxActivation, settings.ActivationMfa, settings.ActivationJustification, settings.ActivationTicket,
            settings.ApprovalRequired));
        Assert.Equal((TimeSpan?)TimeSpan.FromDays(365), settings.EligibleMax);

        var approval = JsonNode.Parse(RolePolicyRules.WithApproval(settings.Rules[RolePolicyRules.Approval], true, [new RoleApprover("u1", "Ada")]))!;
        Assert.True(approval["setting"]!["isApprovalRequired"]!.GetValue<bool>());
        Assert.Equal("u1", approval["setting"]!["approvalStages"]![0]!["primaryApprovers"]![0]!["userId"]!.GetValue<string>());
        Assert.Equal(1, approval["setting"]!["approvalStages"]![0]!["approvalStageTimeOutInDays"]!.GetValue<int>());

        var permanent = JsonNode.Parse(RolePolicyRules.WithExpiration(settings.Rules[RolePolicyRules.EligibleExpiry], null))!;
        Assert.False(permanent["isExpirationRequired"]!.GetValue<bool>());
        Assert.Equal(RolePolicyRules.EligibleExpiry, permanent["id"]!.GetValue<string>());
    }

    // ---- The Settings tab ---------------------------------------------------------------------------

    [Fact]
    public async Task Without_P2_the_settings_tab_says_why()
    {
        var settings = await SettingsFor("Security Administrator", pim: false);

        Assert.Equal(RoleDirectory.PimNeedsP2, settings.Note);
        Assert.False(settings.CanEdit);
    }

    [Fact]
    public async Task Settings_edits_reach_pending_changes_as_rule_changes()
    {
        var (records, pipeline, directory) = await Demo();
        var page = new RoleDetailViewModel(directory.Roles.Single(r => r.Role.Name == "Security Administrator"), directory, pipeline, Availability.Yes, records: records);
        await page.Settings.LoadAsync(Ct);

        page.Settings.MaxActivation = RoleSettingsSection.ActivationChoices.Single(c => c.Value == TimeSpan.FromHours(4));
        var shorter = Assert.IsType<SetRolePolicyRule>(Assert.Single(pipeline.Pending));
        Assert.Equal(("8 hours", "4 hours", false), (shorter.Before, shorter.After, shorter.IsCaution));
        Assert.Equal("PT4H", JsonNode.Parse(shorter.Rule)!["maximumDuration"]!.GetValue<string>());

        page.Settings.ActivationMfa = false;
        Assert.True(pipeline.Pending.OfType<SetRolePolicyRule>().Single(r => r.RuleId == RolePolicyRules.ActivationRequirements).IsCaution);

        page.Settings.ActivationMfa = true;
        page.Settings.MaxActivation = RoleSettingsSection.ActivationChoices.Single(c => c.Value == TimeSpan.FromHours(8));
        Assert.Empty(pipeline.Pending);
        Assert.Contains("Security Administrator", page.Title, StringComparison.Ordinal);

        await page.Settings.LoadAsync(Ct);
        page.Settings.ApprovalRequired = true;
        await pipeline.ApplyAsync(cancellationToken: Ct);
        Assert.True((await records.GetRoleSettingsAsync(page.Role.Role.Id, Ct)).Value!.ApprovalRequired);
    }

    // ---- Activating, approving, extending -------------------------------------------------------------

    [Fact]
    public async Task Switching_on_an_eligible_role_needs_a_reason_and_shows_it_switched_on_once_applied()
    {
        var (records, pipeline, _) = await Demo();
        var mine = new MyRolesViewModel(records, pipeline, DemoUserRecords.Account, EmptyIndex.Instance);
        await mine.ReadAgainAsync();
        Assert.Equal(2, mine.Eligible.Count);
        var row = mine.Eligible[0];

        await row.OpenCommand.ExecuteAsync(null);
        Assert.Equal(8, row.HourChoices.Count);
        row.ActivateCommand.Execute(null);
        Assert.Equal("Say why you need it.", row.Error);

        row.Justification = "Checking an alert";
        row.Hours = 2;
        row.ActivateCommand.Execute(null);
        var on = Assert.IsType<ActivateRole>(Assert.Single(pipeline.Pending));
        Assert.Equal((true, 2), (on.On, on.Hours));
        Assert.Equal("Switching On", row.StateText);

        await pipeline.ApplyAsync(cancellationToken: Ct);
        for (var i = 0; i < 50 && mine.Active.Count == 0; i++)
            await Task.Delay(5, Ct);
        Assert.Contains(mine.Active, r => r.IsActivated && r.Grant.RoleId == on.RoleId);
    }

    [Fact]
    public async Task Approving_a_request_takes_it_off_the_list_and_switches_the_role_on_for_them()
    {
        var (records, pipeline, _) = await Demo();
        var approvals = new ApprovalsViewModel(records, pipeline);
        await approvals.ReadAgainAsync();
        var request = Assert.Single(approvals.Requests);

        request.Reason = "Fine for today";
        request.ApproveCommand.Execute(null);
        var review = Assert.IsType<ReviewRoleRequest>(Assert.Single(pipeline.Pending));
        Assert.Equal(("Fine for today", true), (review.Justification, review.IsCaution));
        await pipeline.ApplyAsync(cancellationToken: Ct);

        for (var i = 0; i < 50 && approvals.Requests.Count > 0; i++)
            await Task.Delay(5, Ct);
        Assert.Empty(approvals.Requests);
        Assert.Contains((await records.GetRolesAsync(Ct)).Roles.Single(r => r.Role.Id == request.Request.RoleId).Grants,
            g => g.PrincipalId == request.Request.PrincipalId && g.Activated);
    }

    [Fact]
    public async Task A_time_limited_assignment_can_be_extended_a_year()
    {
        var (_, pipeline, directory) = await Demo();
        var security = directory.Roles.Single(r => r.Role.Name == "Security Administrator");
        var page = new RoleDetailViewModel(security, directory, pipeline, Availability.Yes);
        var row = page.Assignments.Rows.First(r => r.CanExtend);

        row.ExtendCommand.Execute(null);

        var extend = Assert.IsType<ExtendRoleAssignment>(Assert.Single(pipeline.Pending));
        Assert.True(extend.NewEnd > extend.OldEnd!.Value.AddDays(360));
        Assert.Contains(page.Assignments.Rows, r => r.StateText == "Extending");
    }

    // ---- What Graph is sent ----------------------------------------------------------------------------

    [Fact]
    public async Task Activation_looks_up_the_admins_id_and_asks_for_a_duration()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/me?", """{"id":"me-id"}""")
            .On(HttpMethod.Post, "roleAssignmentScheduleRequests", """{"status":"PendingApproval"}""");
        var executor = new GraphChangeExecutor(Graph(http), new MemoryEntityStore(), NoDelay.Instance);

        var result = await executor.ApplyAsync(new ActivateRole(new ChangeTarget("user", "admin@contoso.example", "You", "admin@contoso.example"), "r1", "Security Administrator",
            "/", true, 2, "Alert", "INC-1"), Ct);

        Assert.Contains("approval", result.Detail, StringComparison.Ordinal);
        var body = http.Requests.Last().Json!;
        Assert.Equal(("selfActivate", "me-id", "PT2H", "INC-1"), (body["action"]!.GetValue<string>(), body["principalId"]!.GetValue<string>(),
            body["scheduleInfo"]!["expiration"]!["duration"]!.GetValue<string>(), body["ticketInfo"]!["ticketNumber"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Approving_answers_the_step_assigned_to_the_admin()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "roleAssignmentApprovals/ap1", """{"id":"ap1","steps":[{"id":"s0","status":"Completed","assignedToMe":false},{"id":"s1","status":"InProgress","assignedToMe":true}]}""")
            .On(HttpMethod.Patch, "roleAssignmentApprovals/ap1/steps/s1", "{}");
        var executor = new GraphChangeExecutor(Graph(http), new MemoryEntityStore(), NoDelay.Instance);

        var result = await executor.ApplyAsync(new ReviewRoleRequest(new ChangeTarget("rolerequest", "q1", "Ada: Security Administrator", "ada@contoso.example"), "ap1", false, "Not today"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("Deny", http.Requests.Last().Json!["reviewResult"]!.GetValue<string>());
    }

    [Fact]
    public void Role_settings_are_guarded_by_the_roles_name()
    {
        var change = new SetRolePolicyRule(new ChangeTarget("role", DirectoryRoles.GlobalAdministrator, "Global Administrator", null), "p1", RolePolicyRules.ActivationExpiry,
            "Activation Length", "8 hours", "24 hours", "{}", Loosens: true);

        Assert.NotNull(WriteGuard.Refusal(change, testMode: true));
        Assert.True(change.IsCaution);
    }
}
