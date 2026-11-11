using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>One "who" setting of a preset (people, groups or domains, included or excepted), edited as one per line.</summary>
public sealed partial class PresetScopeRow : ObservableObject
{
    readonly PresetCard _card;

    public PresetScopeRow(PresetScopePart part, IReadOnlyList<string> values, PresetCard card)
    {
        Part = part;
        Values = values;
        _card = card;
        Text = string.Join(Environment.NewLine, values);
    }

    public PresetScopePart Part { get; }
    public IReadOnlyList<string> Values { get; }
    public string Label => Part.Label;
    public string Help => Part.Help;
    public bool CanEdit => _card.CanEdit;

    [ObservableProperty]
    public partial string Text { get; set; }

    public string Placeholder => Part.Parameter.EndsWith("MemberOf", StringComparison.Ordinal) ? "One group per line"
        : Part.Parameter.EndsWith("DomainIs", StringComparison.Ordinal) ? "One domain per line" : "One address per line";

    internal IReadOnlyList<string> Entered() =>
        [.. Text.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Leaving the box stages the change (or drops it when it's back as it was).</summary>
    [RelayCommand]
    void Commit() => _card.StageScope(this);
}

/// <summary>A preset as a card: on or off, who it covers and who's excepted, and what it sets.</summary>
public sealed partial class PresetCard : ObservableObject
{
    readonly PresetPoliciesViewModel _page;

    public PresetCard(PresetPolicy preset, PresetPoliciesViewModel page)
    {
        Preset = preset;
        _page = page;
        Scope = [.. Presets.ScopeParts(preset.Kind).Select(p => new PresetScopeRow(p, preset.Values(p.Parameter), this))];
    }

    public PresetPolicy Preset { get; }
    public string Title => Presets.Title(Preset.Kind);
    public string About => Presets.About(Preset.Kind);
    public IReadOnlyList<string> WhatItSets => Presets.WhatItSets(Preset.Kind);
    public IReadOnlyList<PresetScopeRow> Scope { get; }
    public string AppliesTo => Preset.AppliesTo;

    public bool IsBuiltIn => Preset.Kind == PresetKind.BuiltIn;

    /// <summary>Built-in protection without Defender for Office 365 isn't there to manage.</summary>
    public bool NotLicensed => IsBuiltIn && !Preset.HasDefender;

    /// <summary>Never turned on: the Defender portal must make it first.</summary>
    public bool NeverTurnedOn => !Preset.Exists && !NotLicensed;

    public bool Exists => Preset.Exists;

    public string NeverTurnedOnText => $"{Title} has never been turned on in this tenant. Microsoft makes its policies the first time it's turned on in the Defender portal, " +
        "and that's the only way they can be made. Turn it on there once (for anyone, even just yourself); after that Tenantry can switch it on and off and choose who it covers.";

    public string NoDefenderText => IsBuiltIn
        ? "Built-in protection needs Microsoft Defender for Office 365 (included in Microsoft 365 Business Premium and E5). This tenant doesn't have it."
        : "Without Defender for Office 365, this preset covers anti-spam, anti-phishing and anti-malware; Safe Links and Safe Attachments need Defender.";

    public bool ShowNoDefender => !Preset.HasDefender && (IsBuiltIn || Preset.Exists);

    public bool CanEdit => _page.CanEdit && Preset.Exists && (!IsBuiltIn || Preset.HasDefender);

    public string StateText => NotLicensed ? "Not Licensed" : !Preset.Exists ? "Never Turned On" : Preset.Enabled ? "On" : "Off";

    public BadgeKind StateKind => !Preset.Exists || NotLicensed ? BadgeKind.Neutral : Preset.Enabled ? BadgeKind.Success : BadgeKind.Warning;

    /// <summary>Built-in protection is always on; Standard and Strict switch.</summary>
    public bool CanSwitch => CanEdit && !IsBuiltIn;

    ChangeOp? Pending(string key) => _page.Pipeline?.PendingFor(PresetChange.TargetFor(Preset.Kind).Id, key);

    public string SwitchText => Pending("state") is not null ? "Undo" : Preset.Enabled ? "Turn Off" : "Turn On";

    public string SwitchTip => Preset.Enabled
        ? "Stop applying it. Who it covers is kept, so turning it back on restores it as it was (added to Pending Changes)."
        : "Apply it again to the people it covers (added to Pending Changes).";

    [RelayCommand]
    void Switch()
    {
        if (!CanSwitch)
            return;
        if (Pending("state") is { } pending)
            _page.Pipeline!.Unstage(pending);
        else
            _page.Pipeline!.Stage(new SetPresetEnabled(Preset.Kind, Preset.HasDefender, Preset.Enabled, !Preset.Enabled));
    }

    [RelayCommand]
    void OpenPortal() => Platform.OpenUrl(Presets.PortalUrl);

    internal void StageScope(PresetScopeRow row)
    {
        if (!CanEdit)
            return;
        var pipeline = _page.Pipeline!;
        if (pipeline.PendingFor(PresetChange.TargetFor(Preset.Kind).Id, "scope:" + row.Part.Parameter) is { } pending)
            pipeline.Unstage(pending);
        var change = new SetPresetScope(Preset.Kind, Preset.HasDefender, row.Part.Parameter, row.Part.Label, row.Part.IsException, row.Values, row.Entered());
        if (!change.IsNoChange)
            pipeline.Stage(change);
    }

    internal void Refresh() => OnPropertyChanged(nameof(SwitchText));
}

/// <summary>
/// Preset Policies: Microsoft's Strict and Standard protection and built-in protection. Each person
/// gets one set of protections, in this order: Strict, Standard, your own policies, then the
/// defaults; built-in protection gives Safe Links and Safe Attachments to anyone left.
/// </summary>
public sealed partial class PresetPoliciesViewModel : LivePage
{
    readonly IPresetPolicyRecords? _records;

    public PresetPoliciesViewModel(IPresetPolicyRecords? records, ChangePipeline? pipeline, Availability canEdit) : base(pipeline, canEdit)
    {
        _records = records;
        Pipeline = pipeline;
        if (pipeline is not null)
            pipeline.Changed += (_, _) =>
            {
                foreach (var c in Cards)
                    c.Refresh();
            };
    }

    internal ChangePipeline? Pipeline { get; }

    public ObservableCollection<PresetCard> Cards { get; } = [];

    protected override bool Concerns(ChangeOp change) => change is PresetChange;

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_records is null)
            return;
        var presets = await _records.GetPresetPoliciesAsync(cancellationToken);
        Cards.Clear();
        foreach (var p in presets)
            Cards.Add(new PresetCard(p, this));
    }
}
