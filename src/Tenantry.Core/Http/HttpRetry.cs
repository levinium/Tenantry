using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Tenantry.Core.Http;

/// <summary>Retry and challenge handling shared by the Graph and Exchange clients.</summary>
public static class HttpRetry
{
    public const int MaxAttempts = 5;
    static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

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
