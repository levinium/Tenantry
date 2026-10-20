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
    Task<IReadOnlyList<JsonElement>> InvokeAsync(
        CmdletEndpoint endpoint,
        string cmdlet,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);
}

public sealed class CmdletException(string cmdlet, int status, string message)
    : Exception($"{cmdlet} failed ({status}): {message}")
{
    public string Cmdlet { get; } = cmdlet;
    public int Status { get; } = status;
    public string Detail { get; } = message;
}
