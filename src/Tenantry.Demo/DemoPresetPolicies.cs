using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's presets: Strict for two leaders, Standard for everyone else, built-in protection with one exception.</summary>
public sealed partial class DemoUserRecords : IPresetPolicyRecords
{
    List<PresetPolicy>? _presets;

    List<PresetPolicy> PresetList() => _presets ??=
    [
        Preset(PresetKind.Strict, true, new() { ["SentTo"] = ["amara.hermann@northgate.example", "frontdesk@northgate.example"] }),
        Preset(PresetKind.Standard, true, new() { ["ExceptIfSentToMemberOf"] = ["Leadership"] }),
        Preset(PresetKind.BuiltIn, true, new() { ["ExceptIfSentTo"] = ["scanner@northgate.example"] }),
    ];

    static PresetPolicy Preset(PresetKind kind, bool enabled, Dictionary<string, IReadOnlyList<string>> scope) =>
        new(kind, true, enabled, true, Presets.ScopeParts(kind).ToDictionary(p => p.Parameter, p => scope.GetValueOrDefault(p.Parameter) ?? []));

    public Task<IReadOnlyList<PresetPolicy>> GetPresetPoliciesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PresetPolicy>>([.. PresetList()]);

    /// <summary>Applies a preset change to the demo; null when it isn't one.</summary>
    public ApplyResult? ApplyPreset(ChangeOp change)
    {
        if (change is not PresetChange p)
            return null;
        var at = PresetList().FindIndex(x => x.Kind == p.Preset);
        _presets![at] = p switch
        {
            SetPresetEnabled e => _presets[at] with { Enabled = e.New },
            SetPresetScope s => _presets[at] with { Scope = new Dictionary<string, IReadOnlyList<string>>(_presets[at].Scope) { [s.Parameter] = s.New } },
            _ => _presets[at],
        };
        return new ApplyResult(ChangeOutcome.Applied);
    }
}
