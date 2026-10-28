using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>App registrations and enterprise apps (8c-1): reading, credential states, the lists' views and the pages' cards.</summary>
public sealed class AppTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    static AppRegistration App(params AppCredential[] credentials) =>
        new("a1", "client-1", "An App", "AzureADMyOrg", Now.AddYears(-1), credentials, [], [], []);

    static AppCredential Cred(int endDays, bool secret = true) => new($"k{endDays}", secret, "c", null, Now.AddDays(-400), Now.AddDays(endDays));

    [Fact]
    public void Credential_state_is_the_worst_that_still_matters()
    {
        Assert.Equal(AppFields.NoCredentials, App().CredentialState(Now));
        Assert.Equal(AppFields.Current, App(Cred(200)).CredentialState(Now));
        Assert.Equal(AppFields.Soon, App(Cred(200), Cred(10)).CredentialState(Now));
        Assert.Equal(AppFields.SomeExpired, App(Cred(200), Cred(-5)).CredentialState(Now));
        Assert.Equal(AppFields.Expired, App(Cred(-5), Cred(-300)).CredentialState(Now));
    }

    [Fact]
    public void Next_expiry_is_the_soonest_still_to_come_else_the_last_that_passed()
    {
        Assert.Equal(Now.AddDays(10), App(Cred(200), Cred(10), Cred(-5)).NextExpiry(Now));
        Assert.Equal(Now.AddDays(-5), App(Cred(-5), Cred(-300)).NextExpiry(Now));
        Assert.Null(App().NextExpiry(Now));
    }

    [Fact]
    public async Task Graph_apps_read_with_credentials_redirects_owners_and_where_each_comes_from()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "applications?", """
                {"value":[{"id":"o1","appId":"client-1","displayName":"Payroll","signInAudience":"AzureADMultipleOrgs","createdDateTime":"2025-01-01T00:00:00Z",
                  "passwordCredentials":[{"keyId":"k1","displayName":"Nightly","hint":"abc","startDateTime":"2025-01-01T00:00:00Z","endDateTime":"2025-06-01T00:00:00Z"}],
                  "keyCredentials":[{"keyId":"k2","displayName":"CN=payroll","customKeyIdentifier":"0a1b2c3d4e5f60718293a4b5c6d7e8f901234567","startDateTime":"2025-01-01T00:00:00Z","endDateTime":"2030-01-01T00:00:00Z"}],
                  "requiredResourceAccess":[{"resourceAppId":"graph","resourceAccess":[{"id":"p1","type":"Scope"},{"id":"p2","type":"Role"}]}],
                  "web":{"redirectUris":["https://app.example/auth"]},"spa":{"redirectUris":[]},"publicClient":{"redirectUris":["http://localhost"]},
                  "owners":[{"id":"u1","displayName":"Ada","userPrincipalName":"ada@example.com"}]}]}
                """)
            .On(HttpMethod.Get, "servicePrincipals?", """
                {"value":[
                  {"id":"s1","appId":"client-1","displayName":"Payroll","servicePrincipalType":"Application","accountEnabled":true,"tags":["WindowsAzureActiveDirectoryIntegratedApp"]},
                  {"id":"s2","appId":"ms-1","displayName":"Teams","appOwnerOrganizationId":"f8cdef31-a31e-4b4a-93e4-5f571e91255a","accountEnabled":true,"tags":["HideApp"]},
                  {"id":"s3","appId":"x-1","displayName":"Survey","appOwnerOrganizationId":"someone-else","accountEnabled":false,"appRoleAssignmentRequired":true,"tags":[],
                   "verifiedPublisher":{"displayName":"Tailspin"},"preferredSingleSignOnMode":"saml"}]}
                """);
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

        var apps = await records.GetAppsAsync(Ct);

        var app = Assert.Single(apps.Registrations);
        Assert.Equal("0A1B2C3D4E5F60718293A4B5C6D7E8F901234567", app.Credentials.Single(c => !c.IsSecret).Hint);
        Assert.Equal("abc", app.Credentials.Single(c => c.IsSecret).Hint);
        Assert.Equal([("Web", "https://app.example/auth"), ("Mobile and Desktop", "http://localhost")], app.RedirectUris);
        Assert.Equal([false, true], app.Requested.Select(p => p.IsApplication));
        Assert.Equal("Ada", Assert.Single(app.Owners).Name);
        Assert.Equal([AppSource.ThisOrganization, AppSource.Microsoft, AppSource.ThirdParty], apps.EnterpriseApps.Select(e => e.Source));
        var survey = apps.EnterpriseApps[2];
        Assert.True(!survey.IsEnabled && survey.AssignmentRequired && survey.IsVerifiedPublisher && survey.Publisher == "Tailspin" && EnterpriseApp.SsoText(survey.SsoMode) == "SAML");
        Assert.False(apps.EnterpriseApps[1].IsVisible);
        Assert.True(apps.EnterpriseApps[0].IsIntegrated);
    }

    [Fact]
    public async Task A_registrations_permissions_are_named_with_whether_an_admin_consented()
    {
        var app = new AppRegistration("o1", "client-1", "Payroll", "AzureADMyOrg", null, [],
            [new RequestedPermission("graph", "p1", false), new RequestedPermission("graph", "p2", true), new RequestedPermission("graph", "p3", true)], [], []);
        var http = new FakeHttp()
            .On(HttpMethod.Get, "appId eq 'client-1'", """{"value":[{"id":"s1"}]}""")
            .On(HttpMethod.Get, "appId eq 'graph'", """
                {"value":[{"id":"gsp","displayName":"Microsoft Graph","oauth2PermissionScopes":[{"id":"p1","value":"User.Read","adminConsentDisplayName":"Sign in and read user profile"}],
                  "appRoles":[{"id":"p2","value":"User.Read.All","displayName":"Read all users"},{"id":"p3","value":"Mail.Read","displayName":"Read all mail"}]}]}
                """)
            .On(HttpMethod.Get, "oauth2PermissionGrants", """{"value":[{"id":"g1","consentType":"AllPrincipals","resourceId":"gsp","scope":"User.Read openid"}]}""")
            .On(HttpMethod.Get, "s1/appRoleAssignments", """{"value":[{"id":"r1","resourceId":"gsp","appRoleId":"p2"}]}""");
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

        var detail = await records.GetRegistrationDetailAsync(app, Ct);

        Assert.Equal(["User.Read", "User.Read.All", "Mail.Read"], detail.Permissions.Select(p => p.Name));
        Assert.Equal([true, true, false], detail.Permissions.Select(p => p.Consented));
        Assert.All(detail.Permissions, p => Assert.Equal("Microsoft Graph", p.Resource));
    }

    static async Task<(SecurityAreaViewModel Area, DemoUserRecords Records, List<object> Opened)> Demo()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var opened = new List<object>();
        var area = new SecurityAreaViewModel(records, null, null, () => DateTimeOffset.UtcNow, _ => { }, null, null, apps: records,
            openRegistration: opened.Add, openEnterprise: opened.Add);
        area.SelectedPage = SecurityAreaViewModel.Pages.Single(p => p.Key == "appregs");
        for (var i = 0; i < 100 && (area.Registrations.Total == 0 || area.EnterpriseApps.Total == 0); i++)
            await Task.Delay(5, Ct);
        return (area, records, opened);
    }

    [Fact]
    public async Task One_read_fills_both_lists_and_the_views_find_whats_worth_a_look()
    {
        var (area, _, _) = await Demo();

        Assert.Equal(6, area.Registrations.Total);
        Assert.Equal(14, area.EnterpriseApps.Total);
        var registrations = new ListEngine<AppRegistration>(AppFields.Registration(() => DateTimeOffset.UtcNow));
        var expired = registrations.Run(registrations.Prepare(area.Apps!.Registrations), AppList.RegistrationViews.Single(v => v.Id == "expired").Query, DateTimeOffset.UtcNow);
        Assert.Equal(["Old Reporting Tool", "Payroll Export"], expired.Select(r => r.Name).Order());
        var enterprise = new ListEngine<EnterpriseApp>(AppFields.Enterprise);
        IReadOnlyList<EnterpriseApp> View(string id) =>
            enterprise.Run(enterprise.Prepare(area.Apps.EnterpriseApps), AppList.EnterpriseViews.Single(v => v.Id == id).Query, DateTimeOffset.UtcNow);
        Assert.Equal(4, View("thirdparty").Count);
        Assert.Equal(["Acme Scheduling"], View("sso").Select(a => a.Name));
        Assert.Equal(2, View("disabled").Count);
    }

    [Fact]
    public async Task An_enterprise_apps_permissions_split_admin_consent_app_permissions_and_people()
    {
        var (area, records, _) = await Demo();
        var fabrikam = area.Apps!.EnterpriseApps.Single(e => e.Name == "Fabrikam Whiteboard");
        var page = new EnterpriseAppPageViewModel(fabrikam, area.Apps, records, null, null, null, null, () => DateTimeOffset.UtcNow);
        var permissions = (AppSection)page.Sections.Single(s => s.Key == "permissions");

        await permissions.LoadAsync(Ct);

        Assert.Equal(["Admin Consent", "Application Permissions", "Consented by People"], permissions.Cards.Select(c => c.Title));
        var people = permissions.Cards[2].RowItems;
        Assert.Equal(2, people.Count);
        Assert.Contains("Files.ReadWrite", people[0].Chips);
        Assert.True(permissions.Cards[0].ShowEmpty);
    }

    [Fact]
    public async Task A_registration_page_links_to_its_enterprise_app_and_shows_expired_secrets()
    {
        var (area, records, opened) = await Demo();
        var payroll = area.Apps!.Registrations.Single(r => r.Name == "Payroll Export");
        var page = new AppRegistrationPageViewModel(payroll, area.Apps, records, null, null, opened.Add, () => DateTimeOffset.UtcNow);
        var credentials = (AppSection)page.Sections.Single(s => s.Key == "credentials");
        await credentials.LoadAsync(Ct);

        Assert.Equal("Expired", credentials.Cards[0].RowItems.Single().Badges.Single().Text);
        Assert.StartsWith("Thumbprint ", credentials.Cards[1].RowItems.Single().Code, StringComparison.Ordinal);
        Assert.Equal("Open Enterprise App", page.PartnerText);
        page.OpenPartnerCommand.Execute(null);
        Assert.Equal("Payroll Export", Assert.IsType<EnterpriseApp>(Assert.Single(opened)).Name);

        var permissions = (AppSection)page.Sections.Single(s => s.Key == "permissions");
        await permissions.LoadAsync(Ct);
        Assert.Equal("Admin Consent Given", permissions.Cards.Single().RowItems.Single().Badges[1].Text);
    }
}
