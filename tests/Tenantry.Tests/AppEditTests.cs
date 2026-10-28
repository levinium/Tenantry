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

/// <summary>Changing apps (8c-2): the changes, how Graph is asked, and the pages staging them in the demo.</summary>
public sealed class AppEditTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ChangeTarget Reg = new(AppChange.Registration, "o1", "zz-tenantry-test-app", null);
    static readonly ChangeTarget Sp = new(AppChange.Enterprise, "s1", "zz-tenantry-test-app", null);

    static (GraphChangeExecutor Executor, FakeHttp Http) Graph(FakeHttp http) =>
        (new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance), http);

    [Fact]
    public void Removing_a_credential_or_deleting_needs_the_name_typed_and_switching_sign_in_off_is_a_caution()
    {
        Assert.Equal("zz-tenantry-test-app", new RemoveAppCredential(Reg, "k1", true, "s", "d").ConfirmPhrase);
        Assert.Equal("zz-tenantry-test-app", new DeleteApp(Sp, "d").ConfirmPhrase);
        Assert.True(new SetEnterpriseAppSetting(Sp, "enabled", true, false).IsCaution);
        Assert.False(new SetEnterpriseAppSetting(Sp, "enabled", false, true).IsCaution);
        Assert.True(new SetEnterpriseAppSetting(Sp, "assignment", false, true).IsCaution);
        Assert.True(new SetEnterpriseAppSetting(Sp, "visible", true, true).IsNoChange);
        var owner = new ChangeTarget("user", "u1", "Ada", "ada@example.com");
        Assert.Equal([Reg, owner], new SetAppOwner(Reg, owner, true).Guarded);
    }

    [Fact]
    public async Task A_certificate_is_removed_by_sending_the_others_back()
    {
        var (executor, http) = Graph(new FakeHttp()
            .On(HttpMethod.Get, "keyCredentials", """{"keyCredentials":[{"keyId":"k1","key":"AAA","type":"AsymmetricX509Cert","usage":"Verify"},{"keyId":"k2","key":"BBB","type":"AsymmetricX509Cert","usage":"Verify"}]}""")
            .On(HttpMethod.Patch, "applications/o1", "{}"));

        var result = await executor.ApplyAsync(new RemoveAppCredential(Reg, "k1", false, "CN=old", "d"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var sent = http.Requests.Single(r => r.Method == HttpMethod.Patch).Json!["keyCredentials"]!.AsArray();
        Assert.Equal("k2", Assert.Single(sent)!["keyId"]!.GetValue<string>());
        Assert.Equal("BBB", sent[0]!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_new_secret_is_handed_back_once_and_not_put_in_the_detail()
    {
        var (executor, _) = Graph(new FakeHttp().On(HttpMethod.Post, "addPassword", """{"keyId":"k9","secretText":"s3cr3t~value"}"""));

        var result = await executor.ApplyAsync(new AddAppSecret(Reg, "Nightly", 12), Ct);

        Assert.Equal("s3cr3t~value", result.Reveal);
        Assert.DoesNotContain("s3cr3t", result.Detail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hiding_an_app_adds_HideApp_and_keeps_its_other_tags()
    {
        var (executor, http) = Graph(new FakeHttp()
            .On(HttpMethod.Get, "servicePrincipals/s1?$select=tags", """{"tags":["WindowsAzureActiveDirectoryIntegratedApp"]}""")
            .On(HttpMethod.Patch, "servicePrincipals/s1", "{}"));

        await executor.ApplyAsync(new SetEnterpriseAppSetting(Sp, "visible", true, false), Ct);

        var tags = http.Requests.Single(r => r.Method == HttpMethod.Patch).Json!["tags"]!.AsArray().Select(t => t!.GetValue<string>());
        Assert.Equal(["WindowsAzureActiveDirectoryIntegratedApp", "HideApp"], tags);
    }

    [Fact]
    public async Task Admin_consent_adds_scopes_to_the_existing_grant_and_only_the_missing_app_roles()
    {
        var (executor, http) = Graph(new FakeHttp()
            .On(HttpMethod.Get, "appId eq 'client-1'", """{"value":[{"id":"s1"}]}""")
            .On(HttpMethod.Get, "oauth2PermissionGrants?", """{"value":[{"id":"g1","scope":"User.Read"}]}""")
            .On(HttpMethod.Patch, "oauth2PermissionGrants/g1", "{}")
            .On(HttpMethod.Get, "s1/appRoleAssignments", """{"value":[{"resourceId":"graph","appRoleId":"r1"}]}""")
            .On(HttpMethod.Post, "s1/appRoleAssignments", "{}"));

        var result = await executor.ApplyAsync(new GrantAdminConsent(Reg, "client-1",
        [
            new ConsentItem("graph", "Microsoft Graph", "p1", "Mail.Send", false),
            new ConsentItem("graph", "Microsoft Graph", "r1", "User.Read.All", true),
            new ConsentItem("graph", "Microsoft Graph", "r2", "Sites.Read.All", true),
        ]), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("User.Read Mail.Send", http.Requests.Single(r => r.Method == HttpMethod.Patch).Json!["scope"]!.GetValue<string>());
        Assert.Equal("r2", Assert.Single(http.Requests, r => r.Method == HttpMethod.Post).Json!["appRoleId"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_new_registration_is_made_with_its_redirect_and_its_enterprise_app()
    {
        var (executor, http) = Graph(new FakeHttp()
            .On(HttpMethod.Post, "/applications", """{"id":"o2","appId":"client-2"}""")
            .On(HttpMethod.Post, "/servicePrincipals", """{"id":"s2"}"""));

        var result = await executor.ApplyAsync(new CreateAppRegistration(new ChangeTarget(AppChange.Registration, "zz-tenantry-test-new", "zz-tenantry-test-new", null),
            "AzureADMyOrg", "Single-Page App", "https://app.example/signin"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("https://app.example/signin", http.Requests[0].Json!["spa"]!["redirectUris"]![0]!.GetValue<string>());
        Assert.Equal("client-2", http.Requests[1].Json!["appId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("", "", "", 1)]
    [InlineData("App", "Web", "http://example.com/x", 1)]
    [InlineData("App", "Web", "http://localhost:5000", 0)]
    [InlineData("App", "Single-Page App", "https://app.example", 0)]
    public void A_new_app_needs_a_name_and_a_full_redirect(string name, string platform, string uri, int problems) =>
        Assert.Equal(problems, NewAppViewModel.Check(name, platform, uri).Count);

    static async Task<(DemoUserRecords Records, ChangePipeline Pipeline, AppDirectory Apps)> Demo()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        return (records, pipeline, await records.GetAppsAsync(Ct));
    }

    [Fact]
    public async Task On_a_registration_page_a_secret_is_added_and_an_expired_one_removed_then_both_apply()
    {
        var (records, pipeline, apps) = await Demo();
        var app = apps.Registrations.Single(r => r.Name == "Payroll Export");
        var page = new AppRegistrationPageViewModel(app, apps, records, null, null, null, () => DateTimeOffset.UtcNow, pipeline, Availability.Yes);
        var credentials = (AppSection)page.Sections.Single(s => s.Key == "credentials");
        await credentials.LoadAsync(Ct);

        var form = (AddSecretForm)credentials.Cards[0].Form!;
        form.Description = "Rotation";
        form.AddCommand.Execute(null);
        credentials.Cards[0].RowItems.First(r => r.Title == "Nightly export").DoCommand.Execute(null);

        Assert.Equal(2, pipeline.Pending.Count);
        await Task.Delay(20, Ct);
        Assert.Contains(credentials.Cards[0].RowItems, r => r.StateText == "Adding" && r.Title == "Rotation");
        Assert.Contains(credentials.Cards[0].RowItems, r => r.StateText == "Removing" && r.IsUndo);
        var results = await pipeline.ApplyAsync(cancellationToken: Ct);
        Assert.NotNull(results.Single(r => r.Change is AddAppSecret).Result.Reveal);
        var after = (await records.GetAppsAsync(Ct)).Registrations.Single(r => r.Id == app.Id);
        Assert.Equal(["Rotation"], after.Credentials.Where(c => c.IsSecret).Select(c => c.Name));
    }

    [Fact]
    public async Task Flipping_a_switch_stages_it_and_flipping_it_back_takes_it_out()
    {
        var (records, pipeline, apps) = await Demo();
        var page = new EnterpriseAppPageViewModel(apps.EnterpriseApps.Single(e => e.Name == "Fabrikam Whiteboard"), apps, records, null, null, null, null,
            () => DateTimeOffset.UtcNow, pipeline, Availability.Yes);
        var overview = (AppSection)page.Sections[0];
        await overview.LoadAsync(Ct);
        var toggles = ((AppToggleForm)overview.Cards[0].Form!).Toggles;

        toggles[0].IsOn = false;
        Assert.True(Assert.IsType<SetEnterpriseAppSetting>(Assert.Single(pipeline.Pending)).IsCaution);
        Assert.Equal("Changing", toggles[0].StateText);
        toggles[0].IsOn = true;
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public async Task Access_is_given_as_the_chosen_role_and_a_consent_revoked()
    {
        var (records, pipeline, apps) = await Demo();
        var directory = new DemoTenantDirectory(userCount: 60);
        var acme = apps.EnterpriseApps.Single(e => e.Name == "Acme Scheduling");
        var page = new EnterpriseAppPageViewModel(acme, apps, records, null, null, null, null, () => DateTimeOffset.UtcNow, pipeline, Availability.Yes);
        var assignments = (AppSection)page.Sections.Single(s => s.Key == "assignments");
        await assignments.LoadAsync(Ct);
        var form = (AppPickForm)assignments.Cards[0].Form!;
        Assert.True(form.HasRoles);
        form.Role = form.Roles.Single(r => r.Label == "Scheduler Admin");
        var person = (await directory.GetUsersAsync(Ct)).First(u => u.Kind == UserKind.Member);
        form.Picker.PickCommand.Execute(Tenantry.ViewModels.Editing.PickOption.Of(person));

        var add = Assert.IsType<AddAppUserAssignment>(Assert.Single(pipeline.Pending));
        Assert.Equal(("r-acme-admin", "Scheduler Admin"), (add.RoleId, add.RoleName));

        var permissions = (AppSection)page.Sections.Single(s => s.Key == "permissions");
        await permissions.LoadAsync(Ct);
        permissions.Cards[0].RowItems.Single().DoCommand.Execute(null);
        Assert.True(Assert.IsType<RevokeAppGrant>(pipeline.Pending.Last()).IsCaution);
    }

    [Fact]
    public async Task A_deleted_registration_can_be_restored_from_Deleted_Apps()
    {
        var (records, pipeline, apps) = await Demo();
        var app = apps.Registrations.Single(r => r.Name == "Old Reporting Tool");
        pipeline.Stage(new DeleteApp(new ChangeTarget(AppChange.Registration, app.Id, app.Name, null), "x"));
        await pipeline.ApplyAsync(cancellationToken: Ct);
        Assert.DoesNotContain((await records.GetAppsAsync(Ct)).Registrations, r => r.Id == app.Id);

        var section = DeletedApps.Section(records, pipeline, canEdit: true);
        await section.LoadAsync(Ct);
        var row = section.Cards[0].RowItems.Single();
        Assert.Equal(("Restore", "Delete Permanently"), (row.AltText, row.ActionText));
        row.AltCommand.Execute(null);
        await pipeline.ApplyAsync(cancellationToken: Ct);

        Assert.Contains((await records.GetAppsAsync(Ct)).Registrations, r => r.Id == app.Id);
        Assert.Empty(await records.GetDeletedAppsAsync(Ct));
    }
}
