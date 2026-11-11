using System.Text.Json;
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

/// <summary>SAML single sign-on on enterprise apps.</summary>
public sealed class SamlTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ChangeTarget Sp = new(AppChange.Enterprise, "s1", "zz-tenantry-test-saml", null);

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void SAML_reads_from_the_enterprise_app_and_its_registration()
    {
        var sp = Json("""
            {"appId":"a1","preferredSingleSignOnMode":"saml","loginUrl":"https://app.example/login","samlSingleSignOnSettings":{"relayState":null},
             "appOwnerOrganizationId":"t1","preferredTokenSigningKeyThumbprint":"ABD00D50DC91488B4196C37BCE475961B9FA4A89",
             "keyCredentials":[{"customKeyIdentifier":"q9ANUNyRSItBlsN7zkdZYbn6Sok=","usage":"Verify","endDateTime":"2029-01-01T00:00:00Z"},{"customKeyIdentifier":"q9ANUNyRSItBlsN7zkdZYbn6Sok=","usage":"Sign","endDateTime":"2029-01-01T00:00:00Z"},
                               {"customKeyIdentifier":"def456","usage":"Sign","endDateTime":"2027-01-01T00:00:00Z"}]}
            """);
        var reg = Json("""{"id":"o1","identifierUris":["https://app.example/saml"],"web":{"redirectUris":["https://app.example/acs"],"logoutUrl":"https://app.example/out"}}""");

        var setup = GraphUserRecords.ToSaml(sp, reg);

        Assert.True(setup.IsSaml && setup.CanConfigure);
        Assert.Equal(new SamlSettings(["https://app.example/saml"], ["https://app.example/acs"], "https://app.example/login", "https://app.example/out", null).Summary, setup.Settings.Summary);
        Assert.Equal([("ABD00D50DC91488B4196C37BCE475961B9FA4A89", true), ("DEF456", false)], setup.Certificates.Select(c => (c.Thumbprint, c.Active)));
        Assert.Equal("https://login.microsoftonline.com/t1/federationmetadata/2007-06/federationmetadata.xml?appid=a1", setup.MetadataUrl);
        Assert.False(GraphUserRecords.ToSaml(sp, null).CanConfigure);
    }

    [Fact]
    public void SAML_needs_an_identifier_and_an_https_reply_URL()
    {
        Assert.Equal(["identifiers", "reply"], Saml.Problems(SamlSettings.Empty).Keys.Order());
        Assert.Contains("https", Saml.Problems(new SamlSettings(["urn:app"], ["http://app.example/acs"], null, null, null))["reply"]);
        Assert.Empty(Saml.Problems(new SamlSettings(["urn:contoso:app"], ["https://app.example/acs", "http://localhost:8080/acs"], null, null, "home")));
        Assert.True(Saml.Problems(new SamlSettings(["not a uri"], ["https://app.example/acs"], "ftp://x", null, null)).Keys.Order().SequenceEqual(["identifiers", "signon"]));
    }

    [Fact]
    public async Task Setting_up_SAML_sets_the_registration_then_the_enterprise_app_keeping_other_web_settings()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "applications/o1?$select=web", """{"web":{"redirectUris":[],"homePageUrl":"https://app.example","implicitGrantSettings":{"enableIdTokenIssuance":false}}}""")
            .On(HttpMethod.Patch, "applications/o1", "{}")
            .On(HttpMethod.Patch, "servicePrincipals/s1", "{}");
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

        var result = await executor.ApplyAsync(new SetSaml(Sp, "o1", false, SamlSettings.Empty,
            new SamlSettings(["urn:contoso:app"], ["https://app.example/acs"], null, null, "home")), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var patches = http.Requests.Where(r => r.Method == HttpMethod.Patch).ToArray();
        Assert.Equal("urn:contoso:app", patches[0].Json!["identifierUris"]![0]!.GetValue<string>());
        Assert.Equal("https://app.example", patches[0].Json!["web"]!["homePageUrl"]!.GetValue<string>());
        Assert.Equal("saml", patches[1].Json!["preferredSingleSignOnMode"]!.GetValue<string>());
        Assert.Equal("home", patches[1].Json!["samlSingleSignOnSettings"]!["relayState"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_first_signing_certificate_is_made_active()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "addTokenSigningCertificate", """{"thumbprint":"T1","key":"x"}""")
            .On(HttpMethod.Patch, "servicePrincipals/s1", "{}");
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

        await executor.ApplyAsync(new AddSamlCertificate(Sp, 3, MakeActive: true), Ct);

        Assert.Equal("T1", http.Requests.Single(r => r.Method == HttpMethod.Patch).Json!["preferredTokenSigningKeyThumbprint"]!.GetValue<string>());
    }

    static async Task<(DemoUserRecords Records, ChangePipeline Pipeline, EnterpriseAppPageViewModel Page, AppSection Sso)> Page(string name)
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var apps = await records.GetAppsAsync(Ct);
        var page = new EnterpriseAppPageViewModel(apps.EnterpriseApps.Single(e => e.Name == name), apps, records, null, null, null, null, () => DateTimeOffset.UtcNow, pipeline, Availability.Yes);
        var sso = (AppSection)page.Sections.Single(s => s.Key == "sso");
        await sso.LoadAsync(Ct);
        return (records, pipeline, page, sso);
    }

    [Fact]
    public async Task Setting_up_SAML_stages_the_settings_and_a_first_certificate_then_applies()
    {
        var (records, pipeline, page, sso) = await Page("Help Desk Bot");
        ((AppButtonForm)Assert.Single(sso.Cards).Form!).ActCommand.Execute(null);
        await Task.Delay(20, Ct);

        var fields = ((AppFieldsForm)sso.Cards.Single(c => c.Title == "Basic SAML Configuration").Form!).Fields;
        fields[0].Text = "urn:northgate:helpdesk";
        fields[0].CommitCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
        Assert.NotNull(fields[1].Problem);
        fields[1].Text = "https://helpdesk.northgate.example/saml/acs";
        fields[1].CommitCommand.Execute(null);

        Assert.Equal([typeof(SetSaml), typeof(AddSamlCertificate)], pipeline.Pending.Select(p => p.GetType()));
        Assert.False(pipeline.Pending[0].IsCaution);
        await pipeline.ApplyAsync(cancellationToken: Ct);
        var setup = (await records.GetSamlAsync((await records.GetAppsAsync(Ct)).EnterpriseApps.Single(e => e.Id == page.App.Id), Ct))!;
        Assert.True(setup.IsSaml);
        Assert.True(Assert.Single(setup.Certificates).Active);
    }

    [Fact]
    public async Task Changing_a_working_SAML_setup_is_a_caution_and_an_app_registered_elsewhere_isnt_editable()
    {
        var (_, pipeline, _, sso) = await Page("Old Reporting Tool");
        var reply = ((AppFieldsForm)sso.Cards.Single(c => c.Title == "Basic SAML Configuration").Form!).Fields[1];
        reply.Text = "https://reports.northgate.example/saml/acs2";
        reply.CommitCommand.Execute(null);
        Assert.True(((SetSaml)Assert.Single(pipeline.Pending)).IsCaution);
        Assert.Contains(sso.Cards, c => c.Title == "Stop Using SAML");

        var (_, _, _, acme) = await Page("Acme Scheduling");
        Assert.DoesNotContain(acme.Cards, c => c.Title == "Basic SAML Configuration");
        Assert.Null(acme.Cards.Single(c => c.Title == "SAML Signing Certificates").Form);
    }
}
