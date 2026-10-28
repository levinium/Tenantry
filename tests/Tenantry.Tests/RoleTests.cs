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

/// <summary>Admin roles (8a-1): reading them with and without PIM, what each change sends, and the role page and editor.</summary>
public sealed class RoleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static GraphClient Graph(FakeHttp http) => new(http.Client(), new FakeTokens(), delay: NoDelay.Instance);

    const string NoP2 = """{"error":{"code":"AadPremiumLicenseRequired","message":"The tenant needs to have Microsoft Entra ID P2 or Microsoft Entra ID Governance license."}}""";

    static readonly ChangeTarget Ada = new("user", "u1", "Ada", "zz-tenantry-test-ada@contoso.example");

    // ---- Reading ---------------------------------------------------------------------------------

    [Fact]
    public async Task Without_P2_assignments_come_from_role_assignments_and_hidden_roles_are_read_one_by_one()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "beta/roleManagement/directory/roleDefinitions", """
                {"value":[{"id":"r1","displayName":"Global Administrator","description":"All of it","isBuiltIn":true,"isEnabled":true,"isPrivileged":true,
                  "rolePermissions":[{"allowedResourceActions":["microsoft.directory/users/allProperties/allTasks"]}]}]}
                """)
            .On(HttpMethod.Get, "directory/administrativeUnits", """{"value":[{"id":"au1","displayName":"East Office"}]}""")
            .On(HttpMethod.Get, "roleAssignmentScheduleInstances", NoP2, HttpStatusCode.BadRequest)
            .On(HttpMethod.Get, "roleManagement/directory/roleAssignments", """
                {"value":[
                  {"id":"a1","roleDefinitionId":"r1","principalId":"u1","directoryScopeId":"/","principal":{"@odata.type":"#microsoft.graph.user","displayName":"Ada","userPrincipalName":"ada@contoso.example"}},
                  {"id":"a2","roleDefinitionId":"hidden","principalId":"sp1","directoryScopeId":"/administrativeUnits/au1","principal":{"@odata.type":"#microsoft.graph.servicePrincipal","displayName":"Sync","appId":"app-1"}}]}
                """)
            .On(HttpMethod.Get, "roleDefinitions/hidden", """{"id":"hidden","displayName":"Directory Writers","description":"","isBuiltIn":true}""");
        var records = new GraphUserRecords(Graph(http), new MemoryEntityStore());

        var directory = await records.GetRolesAsync(Ct);

        Assert.Equal(RoleDirectory.PimNeedsP2, directory.PimUnavailable);
        var global = directory.Roles.Single(r => r.Role.Id == "r1");
        Assert.True(global.Role.IsPrivileged);
        Assert.Equal((1, 1), (global.Active, global.PermanentPeople));
        var hidden = directory.Roles.Single(r => r.Role.Id == "hidden");
        var app = Assert.Single(hidden.Grants);
        Assert.Equal((RolePrincipalType.App, "East Office", "app-1"), (app.PrincipalType, app.ScopeName, app.PrincipalDetail));
    }

    // ---- What each change sends -------------------------------------------------------------------

    [Fact]
    public async Task A_role_is_given_with_role_assignments_without_PIM_and_through_an_eligibility_request_with_it()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "roleManagement/directory/roleAssignments", "{}")
            .On(HttpMethod.Get, "roleManagement/directory/roleAssignments?", """{"value":[{"id":"a1"}]}""")
            .On(HttpMethod.Post, "roleEligibilityScheduleRequests", "{}");
        var executor = new GraphChangeExecutor(Graph(http), new MemoryEntityStore(), NoDelay.Instance);

        var plain = await executor.ApplyAsync(new AddRoleAssignment(Ada, RolePrincipalType.User, "r1", "Helpdesk Administrator", true, "/", "Organization"), Ct);
        var end = new DateTimeOffset(2027, 1, 31, 0, 0, 0, TimeSpan.Zero);
        var eligible = await executor.ApplyAsync(new AddRoleAssignment(Ada, RolePrincipalType.User, "r1", "Helpdesk Administrator", true, "/administrativeUnits/au1", "East",
            RoleGrantKind.Eligible, end, "Cover for leave"), Ct);

        Assert.Equal((ChangeOutcome.Applied, ChangeOutcome.Applied), (plain.Outcome, eligible.Outcome));
        var direct = http.Requests.First(r => r.Method == HttpMethod.Post).Json!;
        Assert.Equal(("u1", "r1", "/"), (direct["principalId"]!.GetValue<string>(), direct["roleDefinitionId"]!.GetValue<string>(), direct["directoryScopeId"]!.GetValue<string>()));
        var request = http.Requests.Single(r => r.Url.AbsolutePath.EndsWith("roleEligibilityScheduleRequests", StringComparison.Ordinal)).Json!;
        Assert.Equal("adminAssign", request["action"]!.GetValue<string>());
        Assert.Equal("afterDateTime", request["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
        Assert.Equal("Cover for leave", request["justification"]!.GetValue<string>());
    }

    [Fact]
    public async Task One_made_through_PIM_is_removed_through_PIM_by_role_and_scope()
    {
        var http = new FakeHttp().On(HttpMethod.Post, "roleAssignmentScheduleRequests", "{}");
        var executor = new GraphChangeExecutor(Graph(http), new MemoryEntityStore(), NoDelay.Instance);

        await executor.ApplyAsync(new RemoveRoleAssignment(Ada, "sched-1", "Helpdesk Administrator", "r1", "/", RoleGrantKind.Active, ViaPim: true), Ct);

        var body = Assert.Single(http.Requests).Json!;
        Assert.Equal(("adminRemove", "u1", "r1", "/"), (body["action"]!.GetValue<string>(), body["principalId"]!.GetValue<string>(),
            body["roleDefinitionId"]!.GetValue<string>(), body["directoryScopeId"]!.GetValue<string>()));
    }

    [Fact]
    public void Custom_roles_are_guarded_by_name_and_assignments_by_who_gets_them()
    {
        var real = new CreateCustomRole(new ChangeTarget("role", "Desk", "Desk", null), "", ["microsoft.directory/users/password/update"]);
        var test = real with { Role = new ChangeTarget("role", "x", WriteGuard.Marker + "desk", null) };
        var toReal = new AddRoleAssignment(new ChangeTarget("user", "u9", "Real Person", "real@contoso.example"), RolePrincipalType.User, "r1", "Helpdesk Administrator",
            true, "/", "Organization");

        Assert.NotNull(WriteGuard.Refusal(real, testMode: true));
        Assert.Null(WriteGuard.Refusal(test, testMode: true));
        Assert.NotNull(WriteGuard.Refusal(toReal, testMode: true));
        Assert.True(toReal.IsCaution);
    }

    // ---- The role page and editor -------------------------------------------------------------------

    static async Task<(DemoUserRecords Records, ChangePipeline Pipeline, RoleDirectory Directory)> Demo()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), DemoUserRecords.Account);
        return (records, pipeline, await records.GetRolesAsync(Ct));
    }

    [Fact]
    public async Task The_only_Global_Administrator_cant_be_removed_and_without_PIM_a_role_is_given_permanently()
    {
        var (_, pipeline, directory) = await Demo();
        var global = directory.Roles.Single(r => r.Role.Id == DirectoryRoles.GlobalAdministrator);
        var lone = global with { Grants = [.. global.Grants.Where(g => g.PrincipalType == RolePrincipalType.User).Take(1)] };
        var page = new RoleDetailViewModel(lone, directory, pipeline, Availability.Yes);

        Assert.Equal("The only Global Administrator", Assert.Single(page.Assignments.Rows).Locked);
        Assert.False(page.Assignments.HasPim);

        page.Assignments.Picked = new UserSummary("u9", "Cy Jones", "cy@contoso.example", null, null, null, true, UserKind.Member, DateTimeOffset.UtcNow, null, []);
        page.Assignments.Eligible = true;
        page.Assignments.AddCommand.Execute(null);

        var add = Assert.IsType<AddRoleAssignment>(Assert.Single(pipeline.Pending));
        Assert.Equal((RoleGrantKind.Active, (DateTimeOffset?)null, "/"), (add.Kind, add.End, add.ScopeId));
        Assert.Contains(page.Assignments.Rows, r => r.StateText == "Adding");
        Assert.Contains("give it to Cy Jones", page.PendingText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_role_given_in_the_demo_shows_on_the_role_and_on_the_persons_security_tab()
    {
        var (records, pipeline, directory) = await Demo();
        var helpdesk = directory.Roles.Single(r => r.Role.Name == "Helpdesk Administrator");
        var person = new ChangeTarget("user", "demo-u-x", "Pat", "pat@northgate.example");

        pipeline.Stage(new AddRoleAssignment(person, RolePrincipalType.User, helpdesk.Role.Id, helpdesk.Role.Name, true, "/", "Organization"));
        await pipeline.ApplyAsync(cancellationToken: Ct);

        Assert.Contains((await records.GetRolesAsync(Ct)).Roles.Single(r => r.Role.Id == helpdesk.Role.Id).Grants, g => g.PrincipalId == "demo-u-x");
        Assert.Contains((await records.GetRolesAsync("demo-u-x", Ct)).Active, r => r.RoleName == "Helpdesk Administrator");
    }

    [Fact]
    public async Task The_editor_copies_a_roles_permissions_shift_click_ticks_a_range_and_saving_stages_the_role()
    {
        var (records, pipeline, directory) = await Demo();
        var closed = false;
        var editor = new CustomRoleEditorViewModel(pipeline, records, [.. directory.Roles.Select(r => r.Role)], null, () => closed = true);
        for (var i = 0; i < 50 && editor.IsLoading; i++)
            await Task.Delay(5, Ct);

        editor.Name = "Global Administrator";
        Assert.Contains(editor.Problems, p => p.Contains("already a role", StringComparison.Ordinal));
        editor.Name = WriteGuard.Marker + "desk";
        editor.Template = directory.Roles.Single(r => r.Role.Name == "Helpdesk Administrator").Role;
        Assert.Equal(2, editor.Shown.Count(p => p.IsChecked));

        editor.Template = null;
        foreach (var p in editor.Shown)
            p.IsChecked = false;
        editor.Shown[0].IsChecked = true;
        editor.Clicked(editor.Shown[0], shift: false);
        editor.Shown[3].IsChecked = true;
        editor.Clicked(editor.Shown[3], shift: true);
        Assert.Equal(4, editor.Shown.Count(p => p.IsChecked));

        editor.SaveCommand.Execute(null);
        var create = Assert.IsType<CreateCustomRole>(Assert.Single(pipeline.Pending));
        Assert.Equal(4, create.Actions.Count);
        Assert.True(closed);
    }

    [Theory]
    [InlineData("microsoft.directory/users/password/update", "Users", "password › update")]
    [InlineData("microsoft.directory/groups.unified/create", "Groups Unified", "create")]
    [InlineData("microsoft.office365.exchange/allEntities/basic/allTasks", "Office365 Exchange › All Entities", "basic › all tasks")]
    [InlineData("microsoft.directory/conditionalAccessPolicies/allProperties/read", "Conditional Access Policies", "all properties › read")]
    public void Permissions_are_grouped_by_what_they_act_on(string action, string group, string item) =>
        Assert.Equal((group, item), RolePermissionsSection.Split(action));
}
