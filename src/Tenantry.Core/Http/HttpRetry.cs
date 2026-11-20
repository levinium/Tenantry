using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Tenantry.Core.Http;

/// <summary>Retry and challenge handling shared by the Graph and Exchange clients.</summary>
public static class HttpRetry
{
    public const int MaxAttempts = 5;
    static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>Tries for a 500 on a read: often a passing fault, but some services answer 500 every time.</summary>
    public const int MaxServerErrorAttempts = 3;

    /// <summary>
    /// Whether to try again after this answer. Throttling (429) and the gateway errors (502 Bad
    /// Gateway, 503, 504) are passing by Microsoft's own guidance; a first download hit a 502 and
    /// stopped (seen 2026-11-20). A plain 500 is tried again only for reads, which are safe to repeat,
    /// and fewer times, since some services answer 500 every time.
    /// </summary>
    public static bool ShouldRetry(HttpStatusCode status, int attempt, bool isRead) =>
        IsTransient(status) ? attempt < MaxAttempts
        : isRead && status == HttpStatusCode.InternalServerError && attempt < MaxServerErrorAttempts;

    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.BadGateway;

    /// <summary>
    /// A read that failed before any answer (connection dropped or reset, DNS hiccup, the client's
    /// own timeout) is tried again; a change isn't, since it may have gone through.
    /// </summary>
    public static bool ShouldRetry(Exception e, int attempt, bool isRead, CancellationToken cancellationToken) =>
        isRead && attempt < MaxAttempts && !cancellationToken.IsCancellationRequested
        && e is HttpRequestException or TaskCanceledException { InnerException: TimeoutException } or IOException;

    /// <summary>Backoff for a retry with no answer to read Retry-After from: 1, 2, 4, 8 s.</summary>
    public static TimeSpan DelayFor(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));

    /// <summary>
    /// How long to wait before the next attempt: the server's Retry-After when it gives one,
    /// otherwise exponential backoff (1, 2, 4, 8 s...).
    /// </summary>
    public static TimeSpan DelayFor(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
            ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxDelay ? MaxDelay : delay;
    }

    /// <summary>
    /// Reads a claims challenge from a 401's WWW-Authenticate header, for example Microsoft
    /// Graph demanding recent MFA before an authentication-method change. Returns the decoded
    /// claims JSON, ready to pass to the token source.
    /// </summary>
    public static string? ClaimsChallenge(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return null;
        foreach (AuthenticationHeaderValue header in response.Headers.WwwAuthenticate)
        {
            var value = header.Parameter;
            if (value is null)
                continue;
            const string key = "claims=\"";
            var start = value.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                continue;
            start += key.Length;
            var end = value.IndexOf('"', start);
            if (end < 0)
                continue;
            var encoded = value[start..end];
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException)
            {
                return encoded; // already plain JSON
            }
        }
        return null;
    }
}
