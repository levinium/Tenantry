using System.Net;
using Tenantry.Core.Permissions;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;

namespace Tenantry.Tests;

public class AppRegistrationProvisionerTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string Org = """{"value":[{"id":"tid-1","displayName":"Contoso","verifiedDomains":[{"name":"contoso.example","isDefault":true},{"name":"contoso.onmicrosoft.example","isDefault":false}]}]}""";

    /// <summary>
    /// A tenant whose copy of each API publishes every scope Tenantry asks for, except one
    /// Graph scope left out to prove unresolved names are reported rather than fatal.
    /// </summary>
    static FakeHttp Tenant(string missingGraphScope = "SecurityAlert.ReadWrite.All")
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/organization", Org)
            .On(HttpMethod.Get, "/me?", """{"id":"me-1"}""");
        foreach (var api in TenantryPermissions.All)
        {
            // A duplicated name, as real tenants publish (User.ReadBasic.All appears twice in Graph's list).
            var scopes = api.DelegatedScopes
                .Where(s => s != missingGraphScope)
                .Append("User.ReadBasic.All").Append("User.ReadBasic.All")
                .Select((s, i) => $$"""{"value":"{{s}}","id":"{{api.ResourceAppId[..4]}}-{{i}}"}""");
            http.On(HttpMethod.Get, $"servicePrincipals?$filter=appId eq '{api.ResourceAppId}'",
                $$"""{"value":[{"id":"sp-{{api.ResourceName}}","appId":"{{api.ResourceAppId}}","oauth2PermissionScopes":[{{string.Join(",", scopes)}}]}]}""");
        }
        return http;
    }

    [Fact]
    public async Task Creates_the_app_service_principal_and_grants_on_a_fresh_tenant()
    {
        var http = Tenant()
            .On(HttpMethod.Get, "applications?$filter=tags", """{"value":[]}""")
            .On(HttpMethod.Post, "/applications", """{"id":"obj-1","appId":"app-1"}""")
            .On(HttpMethod.Get, "servicePrincipals?$filter=appId eq 'app-1'", """{"value":[]}""")
            // The new app has not replicated yet on the first try.
            .Once(HttpMethod.Post, "/servicePrincipals", """{"error":{"code":"Request_BadRequest","message":"not a valid application"}}""", HttpStatusCode.BadRequest)
            .Once(HttpMethod.Post, "/servicePrincipals", """{"id":"sp-app-1"}""")
            .On(HttpMethod.Get, "/appRoleAssignedTo", """{"value":[]}""")
            .On(HttpMethod.Post, "/appRoleAssignedTo", """{"id":"assignment"}""")
            .On(HttpMethod.Get, "oauth2PermissionGrants?$filter=clientId eq 'sp-app-1'", """{"value":[]}""")
            .On(HttpMethod.Post, "/oauth2PermissionGrants", """{"id":"g"}""");
        var provisioner = new AppRegistrationProvisioner(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), NoDelay.Instance);

        var result = await provisioner.ProvisionAsync(cancellationToken: Ct);

        Assert.Equal(("app-1", "tid-1", "Contoso", "contoso.example", false),
            (result.ClientId, result.TenantId, result.TenantName, result.PrimaryDomain, result.Reused));
        Assert.Equal(["Microsoft Graph: SecurityAlert.ReadWrite.All"], result.Unresolved);

        var app = http.Requests.Single(r => r.Method == HttpMethod.Post && r.Url.AbsolutePath.EndsWith("/applications")).Json!;
        Assert.Equal("AzureADMyOrg", (string?)app["signInAudience"]);
        Assert.True((bool?)app["isFallbackPublicClient"]);
        Assert.Equal("http://localhost", (string?)app["publicClient"]!["redirectUris"]![0]);
        Assert.Equal(AppRegistrationProvisioner.AppTag, (string?)app["tags"]![0]);
        var access = app["requiredResourceAccess"]!.AsArray();
        Assert.Equal(TenantryPermissions.All.Select(a => a.ResourceAppId), access.Select(a => (string?)a!["resourceAppId"]));
        Assert.All(access.SelectMany(a => a!["resourceAccess"]!.AsArray()), r => Assert.Equal("Scope", (string?)r!["type"]));

        var grants = http.Requests.Where(r => r.Method == HttpMethod.Post && r.Url.AbsolutePath.EndsWith("/oauth2PermissionGrants")).Select(r => r.Json!).ToList();
        Assert.Equal(TenantryPermissions.All.Count, grants.Count);
        Assert.All(grants, g =>
        {
            Assert.Equal("sp-app-1", (string?)g["clientId"]);
            Assert.Equal("AllPrincipals", (string?)g["consentType"]);
        });
        var graphGrant = grants.Single(g => (string?)g["resourceId"] == "sp-Microsoft Graph");
        Assert.Contains("User.ReadWrite.All", ((string?)graphGrant["scope"])!.Split(' '));
        Assert.DoesNotContain("SecurityAlert.ReadWrite.All", ((string?)graphGrant["scope"])!.Split(' '));

        // Only assigned users may sign in, and the admin running setup is assigned.
        var sp = http.Requests.Last(r => r.Method == HttpMethod.Post && r.Url.AbsolutePath.EndsWith("/servicePrincipals")).Json!;
        Assert.True((bool?)sp["appRoleAssignmentRequired"]);
        var assignment = http.Requests.Single(r => r.Method == HttpMethod.Post && r.Url.AbsolutePath.EndsWith("/appRoleAssignedTo")).Json!;
        Assert.Equal(("me-1", "sp-app-1", AppRegistrationProvisioner.DefaultAccessRoleId),
            ((string?)assignment["principalId"], (string?)assignment["resourceId"], (string?)assignment["appRoleId"]));
    }

    [Fact]
    public async Task Running_again_updates_the_existing_registration_instead_of_duplicating_it()
    {
        var http = Tenant(missingGraphScope: "")
            .On(HttpMethod.Get, "applications?$filter=tags", """{"value":[{"id":"obj-1","appId":"app-1"}]}""")
            .On(HttpMethod.Patch, "/applications/obj-1", "")
            .On(HttpMethod.Get, "servicePrincipals?$filter=appId eq 'app-1'", """{"value":[{"id":"sp-app-1","appRoleAssignmentRequired":false}]}""")
            .On(HttpMethod.Patch, "/servicePrincipals/sp-app-1", "")
            .On(HttpMethod.Get, "/appRoleAssignedTo", """{"value":[{"principalId":"me-1"}]}""")
            .On(HttpMethod.Get, "oauth2PermissionGrants?$filter=clientId eq 'sp-app-1'",
                """{"value":[{"id":"grant-graph","clientId":"sp-app-1","resourceId":"sp-Microsoft Graph","consentType":"AllPrincipals","scope":"User.Read"}]}""")
            .On(HttpMethod.Patch, "/oauth2PermissionGrants/grant-graph", "")
            .On(HttpMethod.Post, "/oauth2PermissionGrants", """{"id":"g"}""");
        var provisioner = new AppRegistrationProvisioner(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), NoDelay.Instance);

        var result = await provisioner.ProvisionAsync(cancellationToken: Ct);

        Assert.True(result.Reused);
        Assert.Empty(result.Unresolved);
        Assert.DoesNotContain(http.Requests, r => r.Method == HttpMethod.Post && r.Url.AbsolutePath.EndsWith("/applications"));
        Assert.DoesNotContain(http.Requests, r => r.Method == HttpMethod.Post && r.Url.AbsolutePath.EndsWith("/servicePrincipals"));
        var patched = http.Requests.Single(r => r.Method == HttpMethod.Patch && r.Url.AbsolutePath.EndsWith("/grant-graph")).Json!;
        Assert.Equal(string.Join(' ', TenantryPermissions.Graph.DelegatedScopes), (string?)patched["scope"]);
        // The other APIs had no grant yet, so they are created.
        Assert.Equal(TenantryPermissions.All.Count - 1, http.Requests.Count(r => r.Method == HttpMethod.Post && r.Url.AbsolutePath.EndsWith("/oauth2PermissionGrants")));
        // An older registration that let anyone sign in is tightened; an existing assignment is kept.
        Assert.True((bool?)http.Requests.Single(r => r.Method == HttpMethod.Patch && r.Url.AbsolutePath.EndsWith("/servicePrincipals/sp-app-1")).Json!["appRoleAssignmentRequired"]);
        Assert.DoesNotContain(http.Requests, r => r.Method == HttpMethod.Post && r.Url.AbsolutePath.EndsWith("/appRoleAssignedTo"));
    }

    [Fact]
    public async Task A_tenant_without_sharepoint_is_reported_not_fatal()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/organization", Org)
            .On(HttpMethod.Get, "/me?", """{"id":"me-1"}""")
            .On(HttpMethod.Get, "/appRoleAssignedTo", """{"value":[{"principalId":"me-1"}]}""");
        foreach (var api in TenantryPermissions.All)
        {
            var body = api == TenantryPermissions.SharePoint
                ? """{"value":[]}"""
                : $$"""{"value":[{"id":"sp-{{api.ResourceName}}","oauth2PermissionScopes":[]}]}""";
            http.On(HttpMethod.Get, $"servicePrincipals?$filter=appId eq '{api.ResourceAppId}'", body);
        }
        http.On(HttpMethod.Get, "applications?$filter=tags", """{"value":[]}""")
            .On(HttpMethod.Post, "/applications", """{"id":"obj-1","appId":"app-1"}""")
            .On(HttpMethod.Get, "servicePrincipals?$filter=appId eq 'app-1'", """{"value":[{"id":"sp-app-1","appRoleAssignmentRequired":true}]}""")
            .On(HttpMethod.Get, "oauth2PermissionGrants", """{"value":[]}""");
        var provisioner = new AppRegistrationProvisioner(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), NoDelay.Instance);

        var result = await provisioner.ProvisionAsync(cancellationToken: Ct);

        Assert.Contains("Office 365 SharePoint Online is not available in this tenant", result.Unresolved);
    }
}
