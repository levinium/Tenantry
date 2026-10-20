using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;

namespace Tenantry.Tests;

public class GraphClientTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Follows_next_links_to_the_end()
    {
        var http = new FakeHttp()
            .Once(HttpMethod.Get, "/users", """{"value":[{"id":"1"},{"id":"2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=abc"}""")
            .Once(HttpMethod.Get, "skiptoken=abc", """{"value":[{"id":"3"}]}""");
        var graph = new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance);

        var ids = new List<string>();
        await foreach (var u in graph.GetAllAsync("users", Ct))
            ids.Add(u.GetProperty("id").GetString()!);

        Assert.Equal(["1", "2", "3"], ids);
        Assert.Equal("Bearer token", http.Requests[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task Retries_throttled_requests_then_succeeds()
    {
        var delays = new List<TimeSpan>();
        var http = new FakeHttp()
            .Once(HttpMethod.Get, "/me", _ =>
            {
                var r = FakeHttp.Json("{}", HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return r;
            })
            .Once(HttpMethod.Get, "/me", """{"id":"me"}""");
        var graph = new GraphClient(http.Client(), new FakeTokens(), delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var me = await graph.GetAsync("me", Ct);

        Assert.Equal("me", me.GetProperty("id").GetString());
        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
    }

    [Fact]
    public async Task Gives_up_after_the_retry_limit()
    {
        var http = new FakeHttp().On(HttpMethod.Get, "/me", """{"error":{"code":"TooMany","message":"slow down"}}""", HttpStatusCode.TooManyRequests);
        var graph = new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance);

        var e = await Assert.ThrowsAsync<GraphException>(() => graph.GetAsync("me", Ct));

        Assert.Equal(HttpStatusCode.TooManyRequests, e.Status);
        Assert.Equal("TooMany", e.Code);
        Assert.Equal(5, http.Requests.Count);
    }

    // Graph demands MFA within the last 10 minutes before authentication-method changes, by
    // answering 401 with a claims challenge. The client must step up once and retry.
    [Fact]
    public async Task Answers_a_claims_challenge_with_a_stepped_up_token()
    {
        const string claims = """{"access_token":{"acrs":{"essential":true,"value":"c1"}}}""";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(claims));
        var tokens = new FakeTokens();
        var http = new FakeHttp()
            .Once(HttpMethod.Post, "/phoneMethods", _ =>
            {
                var r = FakeHttp.Json("""{"error":{"code":"InvalidAuthenticationToken","message":"claims"}}""", HttpStatusCode.Unauthorized);
                r.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer",
                    $"authorization_uri=\"https://login.microsoftonline.com/common/oauth2/authorize\", error=\"insufficient_claims\", claims=\"{encoded}\""));
                return r;
            })
            .Once(HttpMethod.Post, "/phoneMethods", """{"id":"m1"}""");
        var graph = new GraphClient(http.Client(), tokens, delay: NoDelay.Instance);

        var result = await graph.PostAsync("users/u/authentication/phoneMethods", new System.Text.Json.Nodes.JsonObject(), Ct);

        Assert.Equal("m1", result.GetProperty("id").GetString());
        Assert.Equal(claims, tokens.Calls[1].Claims);
        Assert.Equal("Bearer stepped-up-token", http.Requests[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task A_second_challenge_is_not_retried_forever()
    {
        var http = new FakeHttp().On(HttpMethod.Get, "/me", _ =>
        {
            var r = FakeHttp.Json("""{"error":{"code":"x","message":"no"}}""", HttpStatusCode.Unauthorized);
            r.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer", "claims=\"e30=\""));
            return r;
        });
        var graph = new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance);

        await Assert.ThrowsAsync<GraphException>(() => graph.GetAsync("me", Ct));
        Assert.Equal(2, http.Requests.Count);
    }
}
