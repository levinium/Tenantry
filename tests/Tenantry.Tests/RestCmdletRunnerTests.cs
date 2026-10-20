using System.Net;
using Tenantry.Core.Exchange;
using Tenantry.Exchange;
using Tenantry.Tests.Fakes;

namespace Tenantry.Tests;

public class RestCmdletRunnerTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static RestCmdletRunner Runner(FakeHttp http, FakeTokens? tokens = null, string tenant = "tid-1") =>
        new(http.Client(), http.Client(), tokens ?? new FakeTokens(), tenant, NoDelay.Instance);

    [Fact]
    public async Task Sends_the_cmdlet_envelope_with_routing_headers()
    {
        var http = new FakeHttp().On(HttpMethod.Post, "outlook.office365.com/adminapi/beta/tid-1/InvokeCommand", """{"value":[{"Name":"m1"}]}""");
        var tokens = new FakeTokens();

        var result = await Runner(http, tokens).InvokeAsync(CmdletEndpoint.Exchange, "Get-Mailbox",
            new Dictionary<string, object?> { ["ResultSize"] = "Unlimited" }, Ct);

        Assert.Equal("m1", result.Single().GetProperty("Name").GetString());
        var request = http.Requests.Single();
        Assert.Equal("Get-Mailbox", (string?)request.Json!["CmdletInput"]!["CmdletName"]);
        Assert.Equal("Unlimited", (string?)request.Json!["CmdletInput"]!["Parameters"]!["ResultSize"]);
        Assert.Equal("UPN:admin@contoso.example", request.Headers["X-AnchorMailbox"]);
        Assert.Equal("odata.maxpagesize=1000", request.Headers["Prefer"]);
        Assert.Equal(["https://outlook.office365.com/.default"], tokens.Calls.Single().Scopes);
    }

    [Fact]
    public async Task Pages_by_resending_the_same_body_to_the_next_link()
    {
        var http = new FakeHttp()
            .Once(HttpMethod.Post, "/InvokeCommand", """{"value":[{"n":1}],"@odata.nextLink":"https://outlook.office365.com/adminapi/beta/tid-1/InvokeCommand?$skiptoken=p2"}""")
            .Once(HttpMethod.Post, "/InvokeCommand", """{"value":[{"n":2}]}""");

        var result = await Runner(http).InvokeAsync(CmdletEndpoint.Exchange, "Get-Recipient", cancellationToken: Ct);

        Assert.Equal(2, result.Count);
        Assert.Contains("skiptoken=p2", http.Requests[1].Url.Query);
        Assert.Equal(http.Requests[0].Body, http.Requests[1].Body);
    }

    [Fact]
    public async Task Reports_the_most_specific_error_message()
    {
        var http = new FakeHttp().On(HttpMethod.Post, "/InvokeCommand",
            """{"error":{"code":"x","message":"generic","details":[{"message":"The mailbox 'nobody' couldn't be found."}]}}""",
            HttpStatusCode.BadRequest);

        var e = await Assert.ThrowsAsync<CmdletException>(() =>
            Runner(http).InvokeAsync(CmdletEndpoint.Exchange, "Get-Mailbox", cancellationToken: Ct));

        Assert.Equal("Get-Mailbox", e.Cmdlet);
        Assert.Equal(400, e.Status);
        Assert.Equal("The mailbox 'nobody' couldn't be found.", e.Detail);
    }

    [Theory]
    [InlineData("""{"error":{"message":"top","innererror":{"internalException":{"message":"deep"}}}}""", "deep")]
    [InlineData("""{"error":{"message":"top","innererror":{"message":"inner"}}}""", "inner")]
    [InlineData("""{"error":{"message":"top"}}""", "top")]
    [InlineData("not json", "not json")]
    public void Error_message_precedence(string body, string expected) =>
        Assert.Equal(expected, RestCmdletRunner.ErrorMessage(body));

    [Fact]
    public async Task Compliance_calls_discover_the_regional_host_once_and_use_the_compliance_audience()
    {
        var tenant = $"tid-{Guid.NewGuid():N}"; // discovery is cached per tenant for the process
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/EXOModuleFile", _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://nam12b.ps.compliance.protection.outlook.com/AdminApi/v1.0/x/EXOModuleFile");
                return r;
            })
            .On(HttpMethod.Post, "nam12b.ps.compliance.protection.outlook.com/adminapi/beta/", """{"value":[]}""");
        var tokens = new FakeTokens();
        var runner = Runner(http, tokens, tenant);

        await runner.InvokeAsync(CmdletEndpoint.Compliance, "Get-DlpCompliancePolicy", cancellationToken: Ct);
        await runner.InvokeAsync(CmdletEndpoint.Compliance, "Get-RetentionCompliancePolicy", cancellationToken: Ct);

        Assert.Single(http.Requests, r => r.Method == HttpMethod.Get);
        Assert.Equal(2, http.Requests.Count(r => r.Url.Host == "nam12b.ps.compliance.protection.outlook.com"));
        Assert.All(tokens.Calls, c => Assert.Equal(["https://ps.compliance.protection.outlook.com/.default"], c.Scopes));
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, "https://eur03b.ps.compliance.protection.outlook.com/x", "https://eur03b.ps.compliance.protection.outlook.com")]
    [InlineData(HttpStatusCode.OK, null, null)]
    [InlineData(HttpStatusCode.Found, null, null)]
    public void Regional_host_comes_from_the_redirect_only(HttpStatusCode status, string? location, string? expected) =>
        Assert.Equal(expected, RestCmdletRunner.RegionalHost(status, location is null ? null : new Uri(location)));
}
