using System.Net;
using Tenantry.Core.Diagnostics;
using Tenantry.Core.Http;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Xunit;

namespace Tenantry.Tests;

public class ErrorReportTests
{
    static GraphClient Client(FakeHttp http) => new(http.Client(), new FakeTokens(), delay: NoDelay.Instance);

    [Fact]
    public async Task A_bad_gateway_is_tried_again_and_the_download_goes_on()
    {
        var http = new FakeHttp()
            .Once(HttpMethod.Get, "users/delta", """{"error":{"code":"badGateway","message":"The request could not be completed."}}""", HttpStatusCode.BadGateway)
            .On(HttpMethod.Get, "users/delta", """{"value":[{"id":"1"}]}""");

        var page = await Client(http).GetAsync("users/delta", TestContext.Current.CancellationToken);

        Assert.Equal(1, page.GetProperty("value").GetArrayLength());
        Assert.Equal(2, http.Requests.Count);
        // Both tries carry the same ID, so Microsoft support finds them together.
        Assert.Equal(http.Requests[0].Headers["client-request-id"], http.Requests[1].Headers["client-request-id"]);
    }

    [Fact]
    public async Task A_server_error_on_a_read_is_tried_three_times_then_reported_with_its_request()
    {
        var http = new FakeHttp().On(HttpMethod.Get, "organization", _ =>
        {
            var answer = FakeHttp.Json("""{"error":{"code":"generalException","message":"Boom."}}""", HttpStatusCode.InternalServerError);
            answer.Headers.Add("request-id", "11111111-2222-3333-4444-555555555555");
            return answer;
        });

        var e = await Assert.ThrowsAsync<GraphException>(() => Client(http).GetAsync("organization?$skiptoken=SECRETPAGE&$select=id", TestContext.Current.CancellationToken));

        Assert.Equal(HttpRetry.MaxServerErrorAttempts, http.Requests.Count);
        var request = RequestDetails.Of(e);
        Assert.NotNull(request);
        Assert.Equal(500, request.Status);
        Assert.Equal(3, request.Attempts);
        Assert.Contains(request.Ids, i => i.Key == "request-id" && i.Value == "11111111-2222-3333-4444-555555555555");
        Assert.DoesNotContain("SECRETPAGE", request.Url);
        Assert.Contains("$select=id", request.Url);
    }

    [Fact]
    public async Task A_server_error_on_a_change_is_not_repeated()
    {
        var http = new FakeHttp().On(HttpMethod.Post, "groups", """{"error":{"code":"generalException","message":"Boom."}}""", HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<GraphException>(() => Client(http).PostAsync("groups", new System.Text.Json.Nodes.JsonObject(), TestContext.Current.CancellationToken));

        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_dropped_connection_on_a_read_is_tried_again()
    {
        var calls = 0;
        var http = new FakeHttp().On(HttpMethod.Get, "domains", _ =>
            ++calls == 1 ? throw new HttpRequestException("The connection was reset.") : FakeHttp.Json("""{"value":[]}"""));

        await Client(http).GetAsync("domains", TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task The_shown_message_and_the_copied_report_say_what_happened()
    {
        var http = new FakeHttp().On(HttpMethod.Get, "users/delta", """{"error":{"code":"badGateway","message":"The request could not be completed."}}""", HttpStatusCode.BadGateway);
        var e = await Assert.ThrowsAsync<GraphException>(() => Client(http).GetAsync("users/delta?$deltatoken=abc", TestContext.Current.CancellationToken));

        var shown = Friendly.Message(e);
        Assert.StartsWith("Microsoft's service had a problem", shown);
        Assert.Contains($"after {HttpRetry.MaxAttempts} tries", shown);

        var copied = ErrorLog.DetailsFor($"Couldn't download the directory: {shown} Use Refresh (bottom left) to try again.");
        Assert.Contains("Tenantry error report", copied);
        Assert.Contains("Shown: Couldn't download the directory:", copied);
        Assert.Contains("Request: GET https://graph.microsoft.com/v1.0/users/delta?$deltatoken=…", copied);
        Assert.Contains("Answer: 502", copied);
        Assert.Contains("client-request-id:", copied);
        Assert.Contains("Tenantry.Graph.GraphException", copied);
        Assert.DoesNotContain("token\"", copied);
        Assert.DoesNotContain("Bearer", copied);
        Assert.DoesNotContain(":line ", copied);
    }

    [Fact]
    public void The_same_failure_shown_twice_is_kept_once()
    {
        var e = new InvalidOperationException("Once only " + Guid.NewGuid());
        Friendly.Message(e);
        Friendly.Message(e);

        Assert.Single(ErrorLog.Recent, r => r.Error == e);
    }

    [Fact]
    public void A_message_without_a_report_still_copies_the_app_and_time()
    {
        var copied = ErrorLog.DetailsFor("Something nobody recorded " + Guid.NewGuid());

        Assert.Contains("App: Tenantry", copied);
        Assert.Contains("Shown: Something nobody recorded", copied);
    }
}
