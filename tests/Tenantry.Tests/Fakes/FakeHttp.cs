using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Tenantry.Core.Auth;

namespace Tenantry.Tests.Fakes;

/// <summary>A request as the fake server saw it.</summary>
sealed record SeenRequest(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public JsonNode? Json => Body is null ? null : JsonNode.Parse(Body);
    public string PathAndQuery => Uri.UnescapeDataString(Url.PathAndQuery);
}

/// <summary>
/// An HTTP handler that answers from routes registered by the test and records every request.
/// Routes are tried in order; a route can answer once (a queue) or every time. An unmatched
/// request fails the test loudly rather than hanging.
/// </summary>
sealed class FakeHttp : HttpMessageHandler
{
    readonly List<(HttpMethod Method, string Contains, Queue<Func<SeenRequest, HttpResponseMessage>> Once, Func<SeenRequest, HttpResponseMessage>? Always)> _routes = [];

    public List<SeenRequest> Requests { get; } = [];

    public HttpClient Client() => new(this);

    /// <summary>Answers matching requests every time.</summary>
    public FakeHttp On(HttpMethod method, string urlContains, Func<SeenRequest, HttpResponseMessage> respond)
    {
        _routes.Add((method, urlContains, new(), respond));
        return this;
    }

    public FakeHttp On(HttpMethod method, string urlContains, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, urlContains, _ => Json(json, status));

    /// <summary>Answers the next matching request only; later ones fall to later routes.</summary>
    public FakeHttp Once(HttpMethod method, string urlContains, Func<SeenRequest, HttpResponseMessage> respond)
    {
        var existing = _routes.FindIndex(r => r.Method == method && r.Contains == urlContains && r.Always is null);
        if (existing >= 0)
            _routes[existing].Once.Enqueue(respond);
        else
        {
            var queue = new Queue<Func<SeenRequest, HttpResponseMessage>>();
            queue.Enqueue(respond);
            _routes.Add((method, urlContains, queue, null));
        }
        return this;
    }

    public FakeHttp Once(HttpMethod method, string urlContains, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Once(method, urlContains, _ => Json(json, status));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var seen = new SeenRequest(request.Method, request.RequestUri!, headers, body);
        Requests.Add(seen);

        var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
        foreach (var route in _routes)
        {
            if (route.Method != request.Method || !url.Contains(route.Contains, StringComparison.Ordinal))
                continue;
            if (route.Once.Count > 0)
                return route.Once.Dequeue()(seen);
            if (route.Always is not null)
                return route.Always(seen);
        }
        throw new InvalidOperationException($"No fake route for {request.Method} {url}");
    }
}

sealed class FakeTokens(string account = "admin@contoso.example") : ITokenSource
{
    public string? Account => account;

    public List<(IReadOnlyList<string> Scopes, string? Claims)> Calls { get; } = [];

    public Task<string> GetTokenAsync(IReadOnlyList<string> scopes, string? claims = null, CancellationToken cancellationToken = default)
    {
        Calls.Add((scopes, claims));
        return Task.FromResult(claims is null ? "token" : "stepped-up-token");
    }
}

static class NoDelay
{
    public static readonly Func<TimeSpan, CancellationToken, Task> Instance = (_, _) => Task.CompletedTask;
}
