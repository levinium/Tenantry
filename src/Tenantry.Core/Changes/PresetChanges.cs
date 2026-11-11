using Tenantry.Core.Directory;

namespace Tenantry.Core.Changes;

/// <summary>
/// A preset security policy's change. Standard and Strict apply to the EOP rule and, with Defender,
/// the ATP rule too, so both always cover the same people; built-in protection is the ATP built-in rule.
/// </summary>
public abstract record PresetChange(PresetKind Preset, bool HasDefender) : ChangeOp
{
    public static ChangeTarget TargetFor(PresetKind kind) => new("preset", Presets.RuleName(kind), Presets.Title(kind), null);

    public override ChangeTarget Target => TargetFor(Preset);
    public override string Label => Presets.Title(Preset);
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>
/// Turns Standard or Strict on or off (Enable-/Disable- its rules; who it covers is kept). It
/// covers real people, so Test mode refuses it.
/// </summary>
public sealed record SetPresetEnabled(PresetKind Preset, bool HasDefender, bool Old, bool New) : PresetChange(Preset, HasDefender)
{
    public override string Key => "state";
    public override string Before => Old ? "On" : "Off";
    public override string After => New ? "On" : "Off";
    public override bool IsCaution => true;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetPresetEnabled e ? this with { Old = e.Old } : this;
}

/// <summary>
/// Who a preset covers: one scope setting (people, groups or domains, included or excepted). It's
/// guarded by the people, groups and domains added or removed, so in Test mode only test objects
/// can be put in or taken out.
/// </summary>
public sealed record SetPresetScope(PresetKind Preset, bool HasDefender, string Parameter, string PartLabel, bool IsException,
    IReadOnlyList<string> Old, IReadOnlyList<string> New) : PresetChange(Preset, HasDefender)
{
    public override string Key => "scope:" + Parameter;
    public override string Label => $"{Presets.Title(Preset)}: {PartLabel}";

    public IReadOnlyList<string> Added => [.. New.Except(Old, StringComparer.OrdinalIgnoreCase)];
    public IReadOnlyList<string> Removed => [.. Old.Except(New, StringComparer.OrdinalIgnoreCase)];

    public override string Before => Old.Count == 0 ? "None" : string.Join(", ", Old);
    public override string After => New.Count == 0 ? "None" : string.Join(", ", New);

    public override IReadOnlyList<ChangeTarget> Guarded =>
        [.. Added.Concat(Removed).Select(v => new ChangeTarget("recipient", v, v, v.Contains('@') ? v : null))];

    /// <summary>Taking protection away from people (an exception added), or switching between "everyone" and named people.</summary>
    public override bool IsCaution => IsException ? Added.Count > 0 : (Old.Count == 0) != (New.Count == 0);

    public override bool IsNoChange => Added.Count == 0 && Removed.Count == 0;

    public override ChangeOp Since(ChangeOp earlier) => earlier is SetPresetScope e ? this with { Old = e.Old } : this;
}
