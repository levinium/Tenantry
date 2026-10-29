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

    /// <summary>
    /// Runs several single-object cmdlets (no paging) and returns each one's output or error, in
    /// order. The REST runner sends them ten to a request; this default runs them one by one.
    /// </summary>
    async Task<IReadOnlyList<CmdletResult>> InvokeManyAsync(CmdletEndpoint endpoint, IReadOnlyList<CmdletCall> calls,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CmdletResult>(calls.Count);
        foreach (var call in calls)
        {
            try
            {
                results.Add(new CmdletResult(await InvokeAsync(endpoint, call.Cmdlet, call.Parameters, cancellationToken, call.Select), null));
            }
            catch (CmdletException e)
            {
                results.Add(new CmdletResult([], e.Explanation));
            }
        }
        return results;
    }
}

/// <summary>One cmdlet call in a batch.</summary>
public sealed record CmdletCall(string Cmdlet, IReadOnlyDictionary<string, object?>? Parameters, IReadOnlyList<string>? Select = null);

/// <summary>A batched call's output, or why it failed (then <see cref="Rows"/> is empty).</summary>
public sealed record CmdletResult(IReadOnlyList<JsonElement> Rows, string? Error)
{
    public bool Succeeded => Error is null;
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
        || Detail.Contains("RecipientNotFoundException", StringComparison.Ordinal)
        // A folder permission that's gone (seen live after Remove-MailboxFolderPermission).
        || Detail.Contains("UserNotFoundInPermissionEntryException", StringComparison.Ordinal)
        // Purview: a case that's gone ("The compliance case … doesn't exist"), seen live after deleting it.
        || Detail.Contains("ComplianceCaseTaskException", StringComparison.Ordinal) && Detail.Contains("doesn't exist", StringComparison.Ordinal);

    /// <summary>Exchange's message without its internal prefix (also used for errors inside a batch).</summary>
    public static string Plain(string text)
    {
        var parts = text.Split('|');
        return parts.Length >= 3 && parts[1].Contains("Exception", StringComparison.Ordinal) ? string.Join('|', parts[2..]).Trim() : text;
    }
}
