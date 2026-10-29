using System.Text.Json;
using Tenantry.Core.Exchange;

namespace Tenantry.Tests.Fakes;

/// <summary>
/// An Exchange cmdlet runner that records every call and answers from a function (no rows by
/// default). Parameters are kept as JSON, as the REST runner would send them.
/// </summary>
sealed class FakeCmdlets(Func<string, IReadOnlyDictionary<string, object?>?, string>? answer = null) : ICmdletRunner
{
    public List<(string Cmdlet, string Parameters)> Calls { get; } = [];

    /// <summary>Where each call in <see cref="Calls"/> went (Exchange or Security &amp; Compliance).</summary>
    public List<CmdletEndpoint> Endpoints { get; } = [];

    public Task<IReadOnlyList<JsonElement>> InvokeAsync(CmdletEndpoint endpoint, string cmdlet,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? select = null)
    {
        Calls.Add((cmdlet, JsonSerializer.Serialize(parameters)));
        Endpoints.Add(endpoint);
        var json = answer?.Invoke(cmdlet, parameters) ?? "[]";
        return Task.FromResult<IReadOnlyList<JsonElement>>(JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToArray());
    }
}
