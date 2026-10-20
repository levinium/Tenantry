using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Auth;
using Tenantry.Core.Http;

namespace Tenantry.Graph;

public sealed class GraphException(HttpStatusCode status, string? code, string message)
    : Exception($"Graph {(int)status} {code}: {message}")
{
    public HttpStatusCode Status { get; } = status;
    public string? Code { get; } = code;
}

/// <summary>
/// Microsoft Graph over plain HTTP. Handles the token, throttling (429/503 with Retry-After),
/// paging through <c>@odata.nextLink</c>, and claims challenges such as Graph demanding recent
/// MFA, which it answers by asking the token source for a stepped-up token and retrying once.
/// </summary>
public sealed class GraphClient
{
    public const string V1 = "https://graph.microsoft.com/v1.0/";
    static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    readonly HttpClient _http;
    readonly ITokenSource _tokens;
    readonly IReadOnlyList<string> _scopes;
    readonly Uri _base;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="scopes">Scopes to request; defaults to <c>.default</c> (everything consented).</param>
    /// <param name="delay">Waits between retries; replaced in tests so they don't sleep.</param>
    public GraphClient(HttpClient http, ITokenSource tokens, IReadOnlyList<string>? scopes = null,
        string baseUrl = V1, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _tokens = tokens;
        _scopes = scopes ?? Scopes;
        _base = new Uri(baseUrl);
        _delay = delay ?? Task.Delay;
    }

    public Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, path, null, cancellationToken);

    public Task<JsonElement> PostAsync(string path, JsonNode body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, path, body, cancellationToken);

    public Task<JsonElement> PatchAsync(string path, JsonNode body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, path, body, cancellationToken);

    /// <summary>Every item of a collection, following <c>@odata.nextLink</c> to the end.</summary>
    public async IAsyncEnumerable<JsonElement> GetAllAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? next = path;
        while (next is not null)
        {
            var page = await GetAsync(next, cancellationToken);
            if (page.TryGetProperty("value", out var items))
                foreach (var item in items.EnumerateArray())
                    yield return item;
            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
    }

    /// <summary>The first item of a collection, or null.</summary>
    public async Task<JsonElement?> GetFirstAsync(string path, CancellationToken cancellationToken = default)
    {
        var page = await GetAsync(path, cancellationToken);
        return page.TryGetProperty("value", out var items) && items.GetArrayLength() > 0 ? items[0] : null;
    }

    async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        var url = Uri.TryCreate(path, UriKind.Absolute, out var absolute) ? absolute : new Uri(_base, path);
        var payload = body?.ToJsonString();
        string? claims = null;
        var challenged = false;

        for (var attempt = 1; ; attempt++)
        {
            var token = await _tokens.GetTokenAsync(_scopes, claims, cancellationToken);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (payload is not null)
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
            }

            if (!challenged && HttpRetry.ClaimsChallenge(response) is { } demanded)
            {
                claims = demanded;
                challenged = true;
                continue;
            }

            if (HttpRetry.IsTransient(response.StatusCode) && attempt < HttpRetry.MaxAttempts)
            {
                await _delay(HttpRetry.DelayFor(response, attempt), cancellationToken);
                continue;
            }

            throw await ToExceptionAsync(response, cancellationToken);
        }
    }

    static async Task<GraphException> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var error = JsonDocument.Parse(text).RootElement.GetProperty("error");
            return new GraphException(response.StatusCode,
                error.TryGetProperty("code", out var c) ? c.GetString() : null,
                error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : text);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new GraphException(response.StatusCode, null, string.IsNullOrWhiteSpace(text) ? response.ReasonPhrase ?? "" : text);
        }
    }
}
