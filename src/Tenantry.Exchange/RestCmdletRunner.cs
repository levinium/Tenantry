using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tenantry.Core.Auth;
using Tenantry.Core.Exchange;
using Tenantry.Core.Http;

namespace Tenantry.Exchange;

/// <summary>
/// Runs Exchange Online and Security &amp; Compliance cmdlets by calling the admin API the
/// ExchangeOnlineManagement module itself uses: <c>POST {host}/adminapi/beta/{tenant}/InvokeCommand</c>
/// with a <c>CmdletInput</c> body. Works the same on every OS.
///
/// This endpoint is undocumented and Microsoft does not support it (see
/// docs/research/phase0-feasibility.md, section 7). Everything about its contract lives in
/// this one class, and the route table can move any cmdlet to the PowerShell worker if it
/// breaks.
/// </summary>
public sealed class RestCmdletRunner : ICmdletRunner
{
    public const string ExchangeHost = "https://outlook.office365.com";
    public const string ComplianceHost = "https://ps.compliance.protection.outlook.com";

    // The Security & Compliance service lives on a regional host found by asking the global one;
    // learned once per tenant and process.
    static readonly ConcurrentDictionary<string, string> ComplianceHosts = new(StringComparer.OrdinalIgnoreCase);

    readonly HttpClient _http;
    readonly HttpClient _noRedirect;
    readonly ITokenSource _tokens;
    readonly string _tenantId;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="http">Client for cmdlet calls.</param>
    /// <param name="noRedirectHttp">Client whose handler does NOT follow redirects, for region discovery.</param>
    public RestCmdletRunner(HttpClient http, HttpClient noRedirectHttp, ITokenSource tokens, string tenantId,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _noRedirect = noRedirectHttp;
        _tokens = tokens;
        _tenantId = tenantId;
        _delay = delay ?? Task.Delay;
    }

    public async Task<IReadOnlyList<JsonElement>> InvokeAsync(CmdletEndpoint endpoint, string cmdlet,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? select = null)
    {
        var anchor = _tokens.Account ?? throw new InvalidOperationException("Sign in before running cmdlets.");
        var audience = endpoint == CmdletEndpoint.Compliance ? ComplianceHost : ExchangeHost;
        var host = endpoint == CmdletEndpoint.Compliance ? await ComplianceHostAsync(anchor, cancellationToken) : ExchangeHost;
        string[] scopes = [$"{audience}/.default"];

        // The service reads ResultSize as a PAGE size, not a total: Get-Mailbox -ResultSize 2
        // came back two at a time, one page per two mailboxes (spike, 2026-10-20). So the
        // limit is applied here, as the module does, and the server is sent page sizes only.
        var limit = ResultLimit(parameters);
        var sent = parameters?
            .Where(p => !p.Key.Equals("ResultSize", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<string, object?>();
        var pageSize = limit is { } n ? Math.Min(n, MaxPageSize) : MaxPageSize;

        var body = JsonSerializer.Serialize(new
        {
            CmdletInput = new { CmdletName = cmdlet, Parameters = sent },
        });

        var results = new List<JsonElement>();
        // $select trims each object server-side, as CIPP does; next links carry it on.
        var query = select is { Count: > 0 } ? "?$select=" + string.Join(',', select) : "";
        string? next = $"{host}/adminapi/beta/{_tenantId}/InvokeCommand{query}";
        while (next is not null && (limit is null || results.Count < limit))
        {
            var page = await PostAsync(next, body, scopes, anchor, cmdlet, pageSize, cancellationToken);
            if (page.TryGetProperty("value", out var items))
                results.AddRange(items.EnumerateArray().Select(i => i.Clone()));
            // Paging re-sends the SAME body to the next link; a next link lasts minutes only.
            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
        return limit is { } max && results.Count > max ? results.GetRange(0, max) : results;
    }

    const int MaxPageSize = 1000;

    /// <summary>The caller's ResultSize as a number, or null for "Unlimited" or none.</summary>
    internal static int? ResultLimit(IReadOnlyDictionary<string, object?>? parameters)
    {
        var value = parameters?.FirstOrDefault(p => p.Key.Equals("ResultSize", StringComparison.OrdinalIgnoreCase)).Value;
        return value switch
        {
            int n when n > 0 => n,
            long n when n > 0 => (int)Math.Min(n, int.MaxValue),
            string s when int.TryParse(s, out var n) && n > 0 => n,
            _ => null,
        };
    }

    async Task<JsonElement> PostAsync(string url, string body, string[] scopes, string anchor, string cmdlet, int pageSize, CancellationToken cancellationToken,
        bool acceptJson = false)
    {
        string? claims = null;
        var challenged = false;
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetTokenAsync(scopes, claims, cancellationToken));
            request.Headers.Add("X-AnchorMailbox", $"UPN:{anchor}");
            request.Headers.Add("X-CmdletName", cmdlet);
            request.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");
            // A $batch answers in multipart unless JSON is asked for (seen live).
            if (acceptJson)
                request.Headers.Accept.ParseAdd("application/json");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
                return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();

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

            throw new CmdletException(cmdlet, (int)response.StatusCode, ErrorMessage(text) ?? response.ReasonPhrase ?? "");
        }
    }

    /// <summary>Calls per $batch request: the size CIPP uses.</summary>
    const int BatchSize = 10;

    /// <summary>
    /// Sends single-object cmdlets ten to a <c>$batch</c> request (one round trip instead of ten;
    /// measured live at about half the time even in sequence). Sub-requests need absolute URLs
    /// (relative ones are refused), and the response is JSON only when asked for. A throttled
    /// sub-request is retried after its Retry-After, up to the usual number of attempts.
    /// </summary>
    public async Task<IReadOnlyList<CmdletResult>> InvokeManyAsync(CmdletEndpoint endpoint, IReadOnlyList<CmdletCall> calls,
        CancellationToken cancellationToken = default)
    {
        var anchor = _tokens.Account ?? throw new InvalidOperationException("Sign in before running cmdlets.");
        var audience = endpoint == CmdletEndpoint.Compliance ? ComplianceHost : ExchangeHost;
        var host = endpoint == CmdletEndpoint.Compliance ? await ComplianceHostAsync(anchor, cancellationToken) : ExchangeHost;
        string[] scopes = [$"{audience}/.default"];
        var results = new CmdletResult?[calls.Count];

        for (var start = 0; start < calls.Count; start += BatchSize)
        {
            var pending = Enumerable.Range(start, Math.Min(BatchSize, calls.Count - start)).ToList();
            for (var attempt = 1; pending.Count > 0; attempt++)
            {
                var requests = pending.Select(i => new
                {
                    id = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    method = "POST",
                    url = $"{host}/adminapi/beta/{_tenantId}/InvokeCommand"
                          + (calls[i].Select is { Count: > 0 } select ? "?$select=" + string.Join(',', select) : ""),
                    body = new { CmdletInput = new { CmdletName = calls[i].Cmdlet, Parameters = calls[i].Parameters ?? new Dictionary<string, object?>() } },
                    headers = new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/json",
                        ["X-CmdletName"] = calls[i].Cmdlet,
                        ["Prefer"] = "odata.maxpagesize=1000",
                    },
                }).ToArray();
                var batch = await PostAsync($"{host}/adminapi/beta/{_tenantId}/$batch", JsonSerializer.Serialize(new { requests }),
                    scopes, anchor, "$batch", BatchSize, cancellationToken, acceptJson: true);

                var throttled = new List<int>();
                var wait = TimeSpan.Zero;
                foreach (var response in batch.TryGetProperty("responses", out var list) ? list.EnumerateArray() : default)
                {
                    if (!int.TryParse(response.GetProperty("id").GetString(), out var i))
                        continue;
                    var status = response.TryGetProperty("status", out var s) ? s.GetInt32() : 500;
                    var body = response.TryGetProperty("body", out var b) ? b : default;
                    if (status is >= 200 and < 300)
                        results[i] = new CmdletResult(body.ValueKind == JsonValueKind.Object && body.TryGetProperty("value", out var rows)
                            ? rows.EnumerateArray().Select(r => r.Clone()).ToArray() : [], null);
                    else if (status == 429 && attempt < HttpRetry.MaxAttempts)
                    {
                        throttled.Add(i);
                        var retryAfter = response.TryGetProperty("headers", out var h) && h.TryGetProperty("Retry-After", out var ra)
                            && int.TryParse(ra.GetString(), out var seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(2 * attempt);
                        wait = retryAfter > wait ? retryAfter : wait;
                    }
                    else
                        results[i] = new CmdletResult([], CmdletException.Plain(ErrorMessage(body.ValueKind == JsonValueKind.Undefined ? "" : body.GetRawText()) ?? $"HTTP {status}"));
                }
                // A sub-request the batch didn't answer at all is reported, not retried forever.
                foreach (var i in pending.Where(i => results[i] is null && !throttled.Contains(i)))
                    results[i] = new CmdletResult([], "No answer in the batch response.");
                pending = throttled;
                if (pending.Count > 0)
                    await _delay(wait, cancellationToken);
            }
        }
        return results.Select(r => r ?? new CmdletResult([], "No answer in the batch response.")).ToArray();
    }

    async Task<string> ComplianceHostAsync(string anchor, CancellationToken cancellationToken)
    {
        if (ComplianceHosts.TryGetValue(_tenantId, out var known))
            return known;

        // As the module does: ask the global host for the module file and read the regional
        // host off the 302. Any other answer means the global host serves this tenant itself.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{ComplianceHost}/AdminApi/v1.0/{_tenantId}/EXOModuleFile?Version=3.10.1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await _tokens.GetTokenAsync([$"{ComplianceHost}/.default"], null, cancellationToken));
        request.Headers.Add("X-AnchorMailbox", $"UPN:{anchor}");

        using var response = await _noRedirect.SendAsync(request, cancellationToken);
        var host = RegionalHost(response.StatusCode, response.Headers.Location) ?? ComplianceHost;
        ComplianceHosts[_tenantId] = host;
        return host;
    }

    /// <summary>The regional Security &amp; Compliance host named by a discovery redirect, if any.</summary>
    internal static string? RegionalHost(HttpStatusCode status, Uri? location)
    {
        if (status is not (HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.MovedPermanently)
            || location is null || !location.IsAbsoluteUri)
            return null;
        var prefix = location.Host.Split('.')[0];
        return string.IsNullOrEmpty(prefix) ? null : $"https://{prefix}.ps.compliance.protection.outlook.com";
    }

    /// <summary>
    /// The most specific message in an admin API error, in the order CIPP found most useful:
    /// details, then the internal exception, then the inner error, then the top-level message.
    /// </summary>
    internal static string? ErrorMessage(string body)
    {
        try
        {
            var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                foreach (var d in details.EnumerateArray())
                    if (d.TryGetProperty("message", out var m) && !string.IsNullOrWhiteSpace(m.GetString()))
                        return m.GetString();
            if (error.TryGetProperty("innererror", out var inner))
            {
                if (inner.TryGetProperty("internalException", out var ie) && ie.TryGetProperty("message", out var iem))
                    return iem.GetString();
                if (inner.TryGetProperty("message", out var im))
                    return im.GetString();
            }
            return error.TryGetProperty("message", out var top) ? top.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
    }
}
