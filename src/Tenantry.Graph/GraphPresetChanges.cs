using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>Preset security policies: on and off, and who they cover (EOP and ATP rules kept together).</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>The cmdlets a preset change runs, in order (also what Show PowerShell prints).</summary>
    internal static IReadOnlyList<(string Cmdlet, Dictionary<string, object?> Parameters)> PresetCalls(PresetChange change)
    {
        var name = Presets.RuleName(change.Preset);
        switch (change)
        {
            case SetPresetEnabled e:
                var verb = e.New ? "Enable-" : "Disable-";
                return
                [
                    (verb + "EOPProtectionPolicyRule", new() { ["Identity"] = name, ["Confirm"] = false }),
                    .. e.HasDefender ? [(verb + "ATPProtectionPolicyRule", new Dictionary<string, object?> { ["Identity"] = name, ["Confirm"] = false })] : Array.Empty<(string, Dictionary<string, object?>)>(),
                ];
            case SetPresetScope s:
                JsonNode? value = s.New.Count == 0 ? null : new JsonArray([.. s.New.Select(v => (JsonNode?)JsonValue.Create(v))]);
                Dictionary<string, object?> P() => new() { ["Identity"] = name, [s.Parameter] = value?.DeepClone() };
                if (s.Preset == PresetKind.BuiltIn)
                    return [("Set-ATPBuiltInProtectionRule", P())];
                return [("Set-EOPProtectionPolicyRule", P()), .. s.HasDefender ? [("Set-ATPProtectionPolicyRule", P())] : Array.Empty<(string, Dictionary<string, object?>)>()];
            default:
                return [];
        }
    }

    async Task<ApplyResult> PresetAsync(PresetChange change, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        var calls = PresetCalls(change);
        for (var i = 0; i < calls.Count; i++)
        {
            try
            {
                await exchange.InvokeAsync(CmdletEndpoint.Exchange, calls[i].Cmdlet, calls[i].Parameters, cancellationToken);
            }
            catch (CmdletException e)
            {
                // The EOP rule changed but not the ATP one: say so, since they should cover the same people.
                return new ApplyResult(ChangeOutcome.Failed, i == 0 ? e.Explanation
                    : $"The anti-spam, anti-phishing and anti-malware part changed, but Safe Links and Safe Attachments didn't: {e.Explanation}");
            }
        }
        var get = change.Preset == PresetKind.BuiltIn ? "Get-ATPBuiltInProtectionRule" : "Get-EOPProtectionPolicyRule";
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            var rows = await Read(get, change.Preset == PresetKind.BuiltIn ? new() : new() { ["Identity"] = Presets.RuleName(change.Preset) }, cancellationToken);
            if (rows.Count > 0 && PresetDone(change, rows[0]))
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "Exchange accepted the change but still shows the old state; it usually appears within a minute.");
    }

    internal static bool PresetDone(PresetChange change, JsonElement rule) => change switch
    {
        SetPresetEnabled e => string.Equals(GraphUserRecords.StrOf(rule, "State"), e.New ? "Enabled" : "Disabled", StringComparison.OrdinalIgnoreCase),
        SetPresetScope s => (rule.TryGetProperty(s.Parameter, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(s.New),
        _ => false,
    };
}
