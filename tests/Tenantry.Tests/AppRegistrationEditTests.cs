using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

/// <summary>Editing app registrations (8c-3): name and notes, redirect URIs, API permissions and certificates.</summary>
public sealed class AppRegistrationEditTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ChangeTarget Reg = new(AppChange.Registration, "o1", "zz-tenantry-test-app", null);

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Redirect_URIs_are_replaced_and_the_platforms_other_settings_kept()
    {
        var body = GraphChangeExecutor.RedirectBody(Json("""
            {"web":{"redirectUris":["https://a.example/x"],"homePageUrl":"https://a.example","implicitGrantSettings":{"enableIdTokenIssuance":true},"redirectUriSettings":[{"uri":"https://a.example/x"}]}}
            """), new SetRedirectUris(Reg, "Web", ["https://a.example/x"], ["https://a.example/x", "https://a.example/y"]));

        var web = body["web"]!.AsObject();
        Assert.Equal(["https://a.example/x", "https://a.example/y"], web["redirectUris"]!.AsArray().Select(u => u!.GetValue<string>()));
        Assert.True(web["implicitGrantSettings"]!["enableIdTokenIssuance"]!.GetValue<bool>());
        Assert.False(web.ContainsKey("redirectUriSettings"));
        Assert.Equal("spa", SetRedirectUris.GraphProperty("Single-Page App"));
    }

    [Theory]
    [InlineData("Web", "https://app.example/signin", false)]
    [InlineData("Web", "http://app.example/signin", true)]
    [InlineData("Single-Page App", "http://localhost:5173", false)]
    [InlineData("Mobile and Desktop", "msal1234://auth", false)]
    [InlineData("Web", "not a uri", true)]
    public void Web_redirects_need_https_except_for_localhost(string platform, string uri, bool problem) =>
        Assert.Equal(problem, SetRedirectUris.Problem(platform, uri) is not null);

    [Fact]
    public void A_permission_is_added_under_its_API_and_an_API_left_with_none_goes()
    {
        var app = Json("""{"requiredResourceAccess":[{"resourceAppId":"graph","resourceAccess":[{"id":"p1","type":"Scope"}]}]}""");

        var added = GraphChangeExecutor.PermissionBody(app, new SetApiPermission(Reg, "exo", "Exchange", "r1", "full_access_as_app", true, true));
        Assert.Equal(["graph", "exo"], added["requiredResourceAccess"]!.AsArray().Select(a => a!["resourceAppId"]!.GetValue<string>()));
        Assert.Equal("Role", added["requiredResourceAccess"]![1]!["resourceAccess"]![0]!["type"]!.GetValue<string>());

        var removed = GraphChangeExecutor.PermissionBody(app, new SetApiPermission(Reg, "graph", "Microsoft Graph", "p1", "User.Read", false, false));
        Assert.Empty(removed["requiredResourceAccess"]!.AsArray());

        // The same ID as an application permission isn't the delegated one.
        var other = GraphChangeExecutor.PermissionBody(app, new SetApiPermission(Reg, "graph", "Microsoft Graph", "p1", "User.Read", true, false));
        Assert.Single(other["requiredResourceAccess"]![0]!["resourceAccess"]!.AsArray());
    }

    [Fact]
    public void A_new_certificate_is_sent_with_the_others()
    {
        var body = GraphChangeExecutor.CertificateBody(Json("""{"keyCredentials":[{"keyId":"k1","key":"AAA","type":"AsymmetricX509Cert","usage":"Verify","@odata.type":"#x"}]}"""),
            new AddAppCertificate(Reg, "CN=new", "ABC", "BBB", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1)));

        var keys = body["keyCredentials"]!.AsArray();
        Assert.Equal(["AAA", "BBB"], keys.Select(k => k!["key"]!.GetValue<string>()));
        Assert.False(keys[0]!.AsObject().ContainsKey("@odata.type"));
    }

    [Fact]
    public void Test_mode_refuses_renaming_a_test_app_to_a_real_name()
    {
        Assert.NotNull(WriteGuard.Refusal(new SetAppProperty(Reg, "displayName", Reg.Name, "Payroll"), testMode: true));
        Assert.Null(WriteGuard.Refusal(new SetAppProperty(Reg, "displayName", Reg.Name, "zz-tenantry-test-app2"), testMode: true));
        Assert.Null(WriteGuard.Refusal(new SetAppProperty(Reg, "notes", "", "Anything"), testMode: true));
    }

    static X509Certificate2 SelfSigned(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest("CN=zz-tenantry-test-cert", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(notBefore, notAfter);
    }

    [Fact]
    public void A_certificate_file_is_read_as_DER_or_PEM_and_a_private_key_or_an_expired_one_refused()
    {
        var now = DateTimeOffset.UtcNow;
        using var cert = SelfSigned(now.AddDays(-1), now.AddYears(1));

        var (der, derProblem) = CertificateFile.Read(cert.Export(X509ContentType.Cert), now);
        Assert.Null(derProblem);
        Assert.Equal(("CN=zz-tenantry-test-cert", cert.Thumbprint), (der!.Subject, der.Thumbprint));
        var (pem, _) = CertificateFile.Read(System.Text.Encoding.ASCII.GetBytes(cert.ExportCertificatePem()), now);
        Assert.Equal(der.Base64, pem!.Base64);

        var withKey = cert.ExportCertificatePem() + "\n" + cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem();
        Assert.Contains("private key", CertificateFile.Read(System.Text.Encoding.ASCII.GetBytes(withKey), now).Problem);
        using var old = SelfSigned(now.AddYears(-2), now.AddDays(-1));
        Assert.Contains("expired", CertificateFile.Read(old.Export(X509ContentType.Cert), now).Problem);
        Assert.NotNull(CertificateFile.Read([1, 2, 3], now).Problem);
    }

    static async Task<(DemoUserRecords Records, ChangePipeline Pipeline, AppRegistrationPageViewModel Page)> Page(string name)
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var apps = await records.GetAppsAsync(Ct);
        var app = apps.Registrations.Single(r => r.Name == name);
        return (records, pipeline, new AppRegistrationPageViewModel(app, apps, records, null, null, null, () => DateTimeOffset.UtcNow, pipeline, Availability.Yes));
    }

    [Fact]
    public async Task On_the_page_a_redirect_is_added_one_removed_and_the_name_changed_then_all_apply()
    {
        var (records, pipeline, page) = await Page("Website Sign-In");
        var overview = (AppSection)page.Sections.Single(s => s.Key == "overview");
        await overview.LoadAsync(Ct);

        var redirects = overview.Cards.Single(c => c.Title == "Redirect URIs");
        var add = (AddEntryForm)redirects.Form!;
        add.OpenCommand.Execute(null);
        add.Choice = add.Choices.Single(c => c.Value == "Single-Page App");
        add.Text = "http://www.northgate.example/other";
        add.AddCommand.Execute(null);
        Assert.NotNull(add.Problem);
        add.Text = "https://www.northgate.example/other";
        add.AddCommand.Execute(null);
        redirects = overview.Cards.Single(c => c.Title == "Redirect URIs");
        redirects.RowItems.Single(r => r.Code == "http://localhost:5173").DoCommand.Execute(null);
        var change = (SetRedirectUris)Assert.Single(pipeline.Pending);
        Assert.Equal(["https://www.northgate.example/signin", "https://www.northgate.example/other"], change.New);
        Assert.True(change.IsCaution);

        var name = ((AppFieldsForm)overview.Cards.Single(c => c.Title == "Name and Notes").Form!).Fields[0];
        name.Text = "";
        name.CommitCommand.Execute(null);
        Assert.NotNull(name.Problem);
        name.Text = "Website Sign-In (New)";
        name.CommitCommand.Execute(null);
        Assert.Equal(2, pipeline.Pending.Count);

        await pipeline.ApplyAsync(cancellationToken: Ct);
        var after = (await records.GetAppsAsync(Ct)).Registrations.Single(r => r.Id == page.App.Id);
        Assert.Equal("Website Sign-In (New)", after.Name);
        Assert.Equal(["https://www.northgate.example/signin", "https://www.northgate.example/other"], after.RedirectUris.Select(r => r.Uri));
    }

    [Fact]
    public async Task A_permission_is_found_in_the_picker_added_and_another_removed()
    {
        var (records, pipeline, page) = await Page("Help Desk Bot");
        var permissions = (AppSection)page.Sections.Single(s => s.Key == "permissions");
        await permissions.LoadAsync(Ct);

        var form = (AddPermissionForm)permissions.Cards.Single(c => c.Title == "Ask for a Permission").Form!;
        await form.OpenCommand.ExecuteAsync(null);
        Assert.DoesNotContain(form.Matches, m => m.Name == "Mail.Send");
        form.Search = "calendars";
        form.Kind = AddPermissionForm.Kinds.Single(k => k.Value == "delegated");
        form.Matches.Single(m => m.Name == "Calendars.Read").AddCommand.Execute(null);
        Assert.Empty(form.Matches);

        await Task.Delay(20, Ct);
        permissions.Cards.Single(c => c.Title == "Microsoft Graph").RowItems.Single(r => r.Title == "Mail.Send").DoCommand.Execute(null);
        Assert.Equal(2, pipeline.Pending.Count);

        await pipeline.ApplyAsync(cancellationToken: Ct);
        var after = (await records.GetAppsAsync(Ct)).Registrations.Single(r => r.Id == page.App.Id);
        Assert.Equal(["p-user-read", "p-cal-read"], after.Requested.Select(r => r.PermissionId));
    }

    [Fact]
    public async Task An_uploaded_certificate_waits_in_Pending_Changes_and_shows_as_adding()
    {
        var (_, pipeline, page) = await Page("Backup Service");
        var credentials = (AppSection)page.Sections.Single(s => s.Key == "credentials");
        await credentials.LoadAsync(Ct);
        using var cert = SelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        ((CertificateForm)credentials.Cards.Single(c => c.Title == "Certificates").Form!).Use(cert.Export(X509ContentType.Cert));

        var change = (AddAppCertificate)Assert.Single(pipeline.Pending);
        Assert.Equal(cert.Thumbprint, change.Thumbprint);
        await Task.Delay(20, Ct);
        Assert.Contains(credentials.Cards.Single(c => c.Title == "Certificates").RowItems, r => r.StateText == "Adding" && r.Title == "CN=zz-tenantry-test-cert");
    }
}
