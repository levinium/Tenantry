using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Tenantry.Core.Http;

/// <summary>
/// The request behind a failure, kept on the exception so an error report can say what was asked,
/// what Microsoft answered and the IDs Microsoft support asks for. Never holds a token or a body.
/// </summary>
public sealed partial record RequestDetails(
    string Method,
    string Url,
    int? Status,
    string? Reason,
    int Attempts,
    IReadOnlyList<KeyValuePair<string, string>> Ids,
    DateTimeOffset At)
{
    const string Key = "tenantry.request";

    /// <summary>Headers worth keeping: the IDs Microsoft looks requests up by, and where it answered from.</summary>
    static readonly string[] IdHeaders =
    [
        "request-id", "client-request-id", "x-ms-ags-diagnostic", "x-ms-request-id", "SPRequestGuid",
        "x-ms-diagnostics", "X-CalculatedBETarget", "X-BackEndHttpStatus", "Retry-After", "Date", "X-CmdletName",
    ];

    public static RequestDetails From(HttpRequestMessage request, HttpResponseMessage? response, int attempts)
    {
        var ids = new List<KeyValuePair<string, string>>();
        void Take(HttpHeaders headers)
        {
            foreach (var name in IdHeaders)
                if (headers.TryGetValues(name, out var values) && ids.All(i => !i.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    ids.Add(new(name, string.Join(", ", values)));
        }
        if (response is not null)
            Take(response.Headers);
        // The ID Tenantry sent, when the answer didn't echo it (or there was no answer).
        Take(request.Headers);
        return new RequestDetails(request.Method.Method, Clean(request.RequestUri), response is null ? null : (int)response.StatusCode,
            response?.ReasonPhrase, attempts, ids, DateTimeOffset.Now);
    }

    /// <summary>Keeps the details on the exception (any kind: Graph, Exchange, a dropped connection).</summary>
    public static TException Attach<TException>(TException e, RequestDetails details) where TException : Exception
    {
        e.Data[Key] = details;
        return e;
    }

    /// <summary>The request behind this failure, looking through wrapping exceptions too.</summary>
    public static RequestDetails? Of(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            if (e.Data[Key] is RequestDetails details)
                return details;
        return null;
    }

    /// <summary>
    /// The address without paging and delta tokens (long, opaque and useless to a reader) and
    /// without any secret a query could carry.
    /// </summary>
    internal static string Clean(Uri? uri)
    {
        if (uri is null)
            return "";
        return TokenParameter().Replace(uri.ToString(), m => m.Groups[1].Value + "…");
    }

    [GeneratedRegex(@"([?&](?:%24|\$)?(?:skiptoken|deltatoken|token|access_token|tempauth|sig|code)=)[^&#]*", RegexOptions.IgnoreCase)]
    private static partial Regex TokenParameter();

    public string ToText()
    {
        var lines = new List<string>
        {
            $"Request: {Method} {Url}",
            $"Answer: {(Status is { } s ? $"{s} {Reason}" : "none (the connection failed)")}; tries: {Attempts}; at {At:yyyy-MM-dd HH:mm:ss zzz}",
        };
        lines.AddRange(Ids.Select(i => $"{i.Key}: {i.Value}"));
        return string.Join(Environment.NewLine, lines);
    }
}
