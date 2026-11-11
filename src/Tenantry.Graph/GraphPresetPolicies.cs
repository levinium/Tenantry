using System.Text.Json;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>Preset security policies: the EOP and ATP protection policy rules, and the ATP built-in protection rule.</summary>
public sealed partial class GraphUserRecords : IPresetPolicyRecords
{
    public async Task<IReadOnlyList<PresetPolicy>> GetPresetPoliciesAsync(CancellationToken cancellationToken = default)
    {
        var eop = Run("Get-EOPProtectionPolicyRule", new(), cancellationToken);
        var atp = TryRun("Get-ATPProtectionPolicyRule", cancellationToken);
        var builtIn = TryRun("Get-ATPBuiltInProtectionRule", cancellationToken);
        return ToPresets(await eop, await atp, await builtIn);
    }

    /// <summary>A Defender-only read: null when the tenant hasn't Defender for Office 365 (the cmdlet is refused or unknown).</summary>
    async Task<IReadOnlyList<JsonElement>?> TryRun(string cmdlet, CancellationToken cancellationToken)
    {
        try
        {
            return await Run(cmdlet, new(), cancellationToken);
        }
        catch (CmdletException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<PresetPolicy> ToPresets(IReadOnlyList<JsonElement> eop, IReadOnlyList<JsonElement>? atp, IReadOnlyList<JsonElement>? builtIn)
    {
        PresetPolicy Read(PresetKind kind, JsonElement? rule) => new(kind, rule is not null,
            rule is { } r && !string.Equals(Str(r, "State"), "Disabled", StringComparison.OrdinalIgnoreCase),
            kind == PresetKind.BuiltIn ? builtIn is not null : atp is not null,
            Presets.ScopeParts(kind).ToDictionary(p => p.Parameter, p => (IReadOnlyList<string>)(rule is { } x ? [.. List(x, p.Parameter)] : [])));
        JsonElement? Named(IEnumerable<JsonElement> rows, string name) =>
            rows.Where(r => string.Equals(Str(r, "Name") ?? Str(r, "Identity"), name, StringComparison.OrdinalIgnoreCase)).Select(r => (JsonElement?)r).FirstOrDefault();
        return
        [
            Read(PresetKind.Strict, Named(eop, Presets.RuleName(PresetKind.Strict))),
            Read(PresetKind.Standard, Named(eop, Presets.RuleName(PresetKind.Standard))),
            Read(PresetKind.BuiltIn, builtIn?.FirstOrDefault() is { ValueKind: JsonValueKind.Object } b ? b : null),
        ];
    }
}
