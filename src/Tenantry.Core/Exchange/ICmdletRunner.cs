using System.Text.Json;

namespace Tenantry.Core.Exchange;

public enum CmdletEndpoint
{
    /// <summary>Exchange Online.</summary>
    Exchange,

    /// <summary>Security &amp; Compliance (Purview).</summary>
    Compliance,
}

/// <summary>
/// Runs an Exchange or Purview cmdlet and returns its output objects. Implemented over direct
/// REST and, as a fallback, over a PowerShell worker process; callers never know which.
/// </summary>
public interface ICmdletRunner
{
    /// <param name="select">Properties to return, when the caller needs only a few (Get-Mailbox
    /// objects carry hundreds); null returns everything.</param>
    Task<IReadOnlyList<JsonElement>> InvokeAsync(
        CmdletEndpoint endpoint,
        string cmdlet,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? select = null);
}

public sealed class CmdletException(string cmdlet, int status, string message)
    : Exception($"{cmdlet} failed ({status}): {message}")
{
    public string Cmdlet { get; } = cmdlet;
    public int Status { get; } = status;
    public string Detail { get; } = message;

    /// <summary>
    /// Exchange's message without its "Ex94914C|Microsoft.Exchange…Exception|" prefix, which
    /// means nothing to an admin.
    /// </summary>
    public string Explanation => Plain(Detail);

    /// <summary>Exchange couldn't find a recipient it was given (no such object, or not mail-enabled).</summary>
    public bool IsNotFound => Detail.Contains("ManagementObjectNotFoundException", StringComparison.Ordinal)
        || Detail.Contains("RecipientNotFoundException", StringComparison.Ordinal);

    internal static string Plain(string text)
    {
        var parts = text.Split('|');
        return parts.Length >= 3 && parts[1].Contains("Exception", StringComparison.Ordinal) ? string.Join('|', parts[2..]).Trim() : text;
    }
}
