using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.MailFlow;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>Where a new rule goes in the order.</summary>
public sealed record RuleOrderChoice(bool First, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The rule editor (5d), in its own tab: the rule as a sentence (When… / Except when… / Then…),
/// each line with the right input, a live plain-English summary, and the rule's settings. A new
/// rule starts from a starter template and in Test mode. While the rule has no problems it sits in
/// Pending changes, kept up to date as it's edited; nothing is sent until the changes are applied.
/// </summary>
public sealed partial class RuleEditorViewModel : TabViewModel, IDisposable
{
    /// <summary>Settings offered under More Settings (the rest are fixed or Exchange's own).</summary>
    static readonly string[] SettingNames = ["ActivationDate", "ExpiryDate", "StopRuleProcessing", "SenderAddressLocation", "RuleErrorAction", "SetAuditSeverity"];

    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly IReadOnlyList<MailFlowRule> _rules;
    readonly Action<RuleEditorViewModel> _close;
    RuleDraft? _original;
    RuleDraft _draft = new();
    ChangeOp? _staged;
    bool _building;

    public static IReadOnlyList<RuleOrderChoice> OrderChoices { get; } = [new(false, "After Every Other Rule"), new(true, "Before Every Other Rule")];

    RuleEditorViewModel(ChangePipeline? pipeline, Availability canEdit, IReadOnlyList<MailFlowRule> rules, Action<RuleEditorViewModel> close)
    {
        _pipeline = pipeline;
        _canEdit = canEdit;
        _rules = rules;
        _close = close;
        ConditionPicker = new RulePartPickerViewModel(RulePartKind.Condition, Used, Add);
        ExceptionPicker = new RulePartPickerViewModel(RulePartKind.Exception, Used, Add);
        ActionPicker = new RulePartPickerViewModel(RulePartKind.Action, Used, Add);
        _mode = RulePreviewViewModel.Modes.First(m => m.Value == RuleMode.Test);
        Order = OrderChoices[0];
        if (_pipeline is not null)
        {
            _pipeline.Applied += OnApplied;
            _pipeline.Changed += OnPendingChanged;
        }
    }

    /// <summary>Once the rule is saved the editor's work is done: it closes.</summary>
    void OnApplied(object? sender, IReadOnlyList<ChangeOp> applied)
    {
        if (applied.Any(c => ReferenceEquals(c, _staged) || c is RuleChange r && RuleIdentity is not null && r.Rule.Id == RuleIdentity && c is UpdateRule or SetRuleMode))
        {
            _staged = null;
            _close(this);
        }
    }

    /// <summary>The rule's changes can also leave Pending changes from the tray (removed one by one, or applied).</summary>
    void OnPendingChanged(object? sender, EventArgs e) => UpdateStatus();

    /// <summary>A new rule: the starters show first.</summary>
    public static RuleEditorViewModel New(ChangePipeline? pipeline, Availability canEdit, IReadOnlyList<MailFlowRule> rules, Action<RuleEditorViewModel> close)
    {
        var editor = new RuleEditorViewModel(pipeline, canEdit, rules, close) { ShowStarters = true };
        editor.Load(new RuleDraft());
        return editor;
    }

    /// <summary>An existing rule, read fresh from Exchange; an edit already in Pending changes carries on.</summary>
    public static RuleEditorViewModel Edit(MailFlowRule rule, Func<CancellationToken, Task<RuleDraft>> read, ChangePipeline? pipeline, Availability canEdit,
        IReadOnlyList<MailFlowRule> rules, Action<RuleEditorViewModel> close)
    {
        var editor = new RuleEditorViewModel(pipeline, canEdit, rules, close) { RuleIdentity = rule.Identity, _name = rule.Name, IsLoading = true };
        _ = editor.ReadAsync(read);
        return editor;
    }

    async Task ReadAsync(Func<CancellationToken, Task<RuleDraft>> read)
    {
        try
        {
            var original = await read(_lifetime.Token);
            _original = original;
            var draft = original.Clone();
            if (_pipeline?.PendingFor(original.Identity!, "edit") is UpdateRule pending && JsonNode.Parse(pending.Changes) is JsonObject changes)
                draft = RuleCodec.Apply(original, changes);
            if (_pipeline?.PendingFor(original.Identity!, "mode") is SetRuleMode mode)
                draft.Mode = mode.New;
            draft.Identity = original.Identity;
            Load(draft);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read the rule: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    readonly CancellationTokenSource _lifetime = new();

    /// <summary>Exchange's identity for the rule being edited; null for a new rule.</summary>
    public string? RuleIdentity { get; private init; }

    public bool IsNew => RuleIdentity is null;

    public override string Title => IsNew ? "New Mail Flow Rule" : $"Rule: {_original?.Name ?? Name}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError), nameof(IsReady))]
    public partial string? LoadError { get; private set; }

    public bool HasLoadError => LoadError is not null;

    public bool IsReady => !IsLoading && LoadError is null && !ShowStarters;

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    // ---- Starters -----------------------------------------------------------------------------

    public IReadOnlyList<RuleStarter> Starters => RuleStarters.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool ShowStarters { get; private set; }

    [RelayCommand]
    void Start(RuleStarter starter)
    {
        var draft = starter.Build();
        draft.Mode = RuleMode.Test;
        ShowStarters = false;
        Load(draft);
    }

    // ---- The rule -----------------------------------------------------------------------------

    public ObservableCollection<RuleLineViewModel> Conditions { get; } = [];
    public ObservableCollection<RuleLineViewModel> Exceptions { get; } = [];
    public ObservableCollection<RuleLineViewModel> Actions { get; } = [];

    public bool HasNoConditions => Conditions.Count == 0;
    public bool HasNoExceptions => Exceptions.Count == 0;
    public bool HasNoActions => Actions.Count == 0;

    public RulePartPickerViewModel ConditionPicker { get; }
    public RulePartPickerViewModel ExceptionPicker { get; }
    public RulePartPickerViewModel ActionPicker { get; }

    /// <summary>Which add-a-part list is open, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConditionPickerOpen), nameof(IsExceptionPickerOpen), nameof(IsActionPickerOpen))]
    public partial RulePartKind? OpenPicker { get; private set; }

    public bool IsConditionPickerOpen => OpenPicker == RulePartKind.Condition;
    public bool IsExceptionPickerOpen => OpenPicker == RulePartKind.Exception;
    public bool IsActionPickerOpen => OpenPicker == RulePartKind.Action;

    [RelayCommand]
    void TogglePicker(RulePartKind kind)
    {
        OpenPicker = OpenPicker == kind ? null : kind;
        if (OpenPicker is { } open)
            PickerFor(open).Search = "";
    }

    RulePartPickerViewModel PickerFor(RulePartKind kind) => kind switch
    {
        RulePartKind.Condition => ConditionPicker,
        RulePartKind.Exception => ExceptionPicker,
        _ => ActionPicker,
    };

    ObservableCollection<RuleLineViewModel> LinesFor(RulePartKind kind) => kind switch
    {
        RulePartKind.Condition => Conditions,
        RulePartKind.Exception => Exceptions,
        _ => Actions,
    };

    IReadOnlySet<string> Used(RulePartKind kind) =>
        LinesFor(kind).Select(l => RulePartPickerViewModel.Key(l.Def)).Concat(kind == RulePartKind.Action ? DisclaimerKeys() : []).ToHashSet(StringComparer.Ordinal);

    /// <summary>A rule takes one disclaimer: appending and prepending share Exchange's settings.</summary>
    IEnumerable<string> DisclaimerKeys() => Actions.Any(a => a.Def.Parameter == "ApplyHtmlDisclaimerText")
        ? RuleCatalog.Instance.Actions.Where(a => a.Parameter == "ApplyHtmlDisclaimerText").Select(RulePartPickerViewModel.Key)
        : [];

    void Add(RulePartKind kind, RulePartDef def)
    {
        var line = new RuleLine(def, kind);
        foreach (var extra in def.Extras.Where(e => e.Default is not null))
            line.Extras[extra.Parameter] = JsonValue.Create(extra.Default!.Trim('"'));
        LinesFor(kind).Add(NewLine(line));
        _draft.LinesFor(kind).Add(line);
        OpenPicker = null;
        Changed();
    }

    RuleLineViewModel NewLine(RuleLine line) => new(line, Changed, Remove);

    void Remove(RuleLineViewModel line)
    {
        LinesFor(line.Line.Kind).Remove(line);
        _draft.LinesFor(line.Line.Kind).Remove(line.Line);
        Changed();
    }

    // ---- Name, mode and settings ------------------------------------------------------------------

    string _name = "";

    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value))
                return;
            _draft.Name = value.Trim();
            Changed();
        }
    }

    Choice<RuleMode> _mode;

    public static IReadOnlyList<Choice<RuleMode>> Modes => RulePreviewViewModel.Modes;

    public Choice<RuleMode> Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value))
                return;
            _draft.Mode = value.Value;
            OnPropertyChanged(nameof(IsEnforcing));
            Changed();
        }
    }

    public bool IsEnforcing => _mode.Value == RuleMode.Enforce;

    bool _turnOn = true;

    /// <summary>A new rule can start off; an existing rule's on/off is in its preview.</summary>
    public bool TurnOn
    {
        get => _turnOn;
        set
        {
            if (!SetProperty(ref _turnOn, value))
                return;
            _draft.Enabled = value;
            Changed();
        }
    }

    RuleOrderChoice _order = null!;

    public RuleOrderChoice Order
    {
        get => _order;
        set
        {
            if (SetProperty(ref _order, value) && !_building)
                Changed();
        }
    }

    string _comments = "";

    public string Comments
    {
        get => _comments;
        set
        {
            if (!SetProperty(ref _comments, value))
                return;
            if (string.IsNullOrWhiteSpace(value))
                _draft.Settings.Remove("Comments");
            else
                _draft.Settings["Comments"] = value;
            Changed();
        }
    }

    [ObservableProperty]
    public partial IReadOnlyList<RuleValueEditor> Settings { get; private set; } = [];

    [ObservableProperty]
    public partial bool ShowMoreSettings { get; set; }

    [RelayCommand]
    void ToggleMoreSettings() => ShowMoreSettings = !ShowMoreSettings;

    /// <summary>Settings from a newer Exchange than Tenantry knows: shown, never changed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKept))]
    public partial IReadOnlyList<string> Kept { get; private set; } = [];

    public bool HasKept => Kept.Count > 0;

    void Load(RuleDraft draft)
    {
        _building = true;
        _draft = draft;
        _name = draft.Name;
        _mode = Modes.First(m => m.Value == draft.Mode);
        _turnOn = draft.Enabled;
        _comments = draft.Settings.GetValueOrDefault("Comments")?.ToString() ?? "";
        foreach (var kind in new[] { RulePartKind.Condition, RulePartKind.Exception, RulePartKind.Action })
        {
            var lines = LinesFor(kind);
            lines.Clear();
            foreach (var line in draft.LinesFor(kind))
                lines.Add(NewLine(line));
        }
        var properties = RuleCatalog.Instance.Properties.ToDictionary(p => p.Parameter, StringComparer.OrdinalIgnoreCase);
        Settings = SettingNames.Where(properties.ContainsKey).Select(name =>
        {
            var p = properties[name];
            return new RuleValueEditor(Display.TitleCase(p.Label ?? name), p.ValueType, p.Choices, false, draft.Settings.GetValueOrDefault(name), v =>
            {
                if (v is null)
                    _draft.Settings.Remove(name);
                else
                    _draft.Settings[name] = v;
                Changed();
            }, p.Default);
        }).ToArray();
        Kept = draft.Kept.Select(k => $"{k.Key}: {Shorten(k.Value.ToString())}").ToArray();
        foreach (var name in new[] { nameof(Name), nameof(Mode), nameof(IsEnforcing), nameof(TurnOn), nameof(Comments), nameof(Title) })
            OnPropertyChanged(name);
        _building = false;
        Changed();
    }

    static string Shorten(string text) => text.Length > 120 ? text[..117] + "…" : text;

    // ---- Summary, problems and Pending changes --------------------------------------------------

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    public partial BadgeKind StatusKind { get; private set; } = BadgeKind.Neutral;

    /// <summary>What's wrong with the rule as it stands, in the order the admin meets it.</summary>
    List<string> FindProblems()
    {
        var problems = new List<string>();
        if (_draft.Name.Length == 0)
            problems.Add("Give the rule a name.");
        else if (_draft.Name.Length > 64)
            problems.Add("A rule's name can be at most 64 characters.");
        else if (_rules.Any(r => r.Identity != RuleIdentity && string.Equals(r.Name, _draft.Name, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"Another rule is already called \"{_draft.Name}\".");
        foreach (var (heading, lines) in new[] { ("When", Conditions), ("Except When", Exceptions), ("Then", Actions) })
            foreach (var line in lines)
                if (line.Problem is { } p)
                    problems.Add($"{heading}, \"{line.Label}\": {p}.");
        if (Actions.Count == 0)
            problems.Add("Add at least one action under Then.");
        foreach (var setting in Settings)
            if (setting.Problem is { } p)
                problems.Add($"{setting.Label}: {p}.");
        return problems;
    }

    void Changed()
    {
        if (_building)
            return;
        Summary = RuleCodec.Summary(_draft);
        Problems = FindProblems();
        foreach (var name in new[] { nameof(HasNoConditions), nameof(HasNoExceptions), nameof(HasNoActions), nameof(Title) })
            OnPropertyChanged(name);
        ConditionPicker.Refresh();
        ExceptionPicker.Refresh();
        ActionPicker.Refresh();
        Stage();
    }

    /// <summary>Keeps Pending changes in step with the editor: the rule while it's valid, nothing while it isn't.</summary>
    void Stage()
    {
        if (!CanEdit || IsLoading || LoadError is not null || ShowStarters)
            return;
        var pipeline = _pipeline!;
        if (IsNew)
        {
            if (_staged is not null)
                pipeline.Unstage(_staged);
            _staged = null;
            if (Problems.Count == 0)
            {
                var parameters = RuleCodec.NewRuleParameters(_draft);
                parameters["Priority"] = Order.First ? 0 : _rules.Count;
                _staged = new CreateRule(new ChangeTarget("rule", _draft.Name, _draft.Name, null), parameters.ToJsonString(), Summary, _draft.Mode == RuleMode.Enforce);
                pipeline.Stage(_staged);
            }
        }
        else if (_original is { } original)
        {
            var target = new ChangeTarget("rule", original.Identity!, original.Name, null);
            pipeline.Stage(new SetRuleMode(target, original.Mode, _draft.Mode));
            var changes = RuleCodec.Changes(original, _draft);
            changes.Remove("Mode");
            if (Problems.Count > 0)
            {
                if (pipeline.PendingFor(original.Identity!, "edit") is { } pending)
                    pipeline.Unstage(pending);
            }
            else
                pipeline.Stage(new UpdateRule(target, new JsonObject(changes).ToJsonString(), RuleCodec.Summary(original), Summary, false));
        }
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var waiting = IsNew ? _staged is not null && _pipeline!.Pending.Contains(_staged) :_original is { } o && (_pipeline?.PendingFor(o.Identity!, "edit") ?? _pipeline?.PendingFor(o.Identity!, "mode")) is not null;
        (Status, StatusKind) = (waiting, Problems.Count) switch
        {
            (true, _) => ("In Pending Changes: apply them to save the rule.", BadgeKind.Success),
            (false, > 0) => ("Fix what's listed below and the rule goes to Pending Changes.", BadgeKind.Warning),
            _ => ("No changes yet.", BadgeKind.Neutral),
        };
    }

    // ---- Leaving --------------------------------------------------------------------------------

    /// <summary>Closes the tab; what's in Pending changes stays there.</summary>
    [RelayCommand]
    void Done() => _close(this);

    /// <summary>Takes this rule's changes out of Pending changes and closes the tab.</summary>
    [RelayCommand]
    void Discard()
    {
        if (_pipeline is { } pipeline)
        {
            if (_staged is not null)
                pipeline.Unstage(_staged);
            if (_original?.Identity is { } id)
                foreach (var key in new[] { "edit", "mode" })
                    if (pipeline.PendingFor(id, key) is { } pending)
                        pipeline.Unstage(pending);
        }
        _close(this);
    }

    public void Dispose()
    {
        if (_pipeline is not null)
        {
            _pipeline.Applied -= OnApplied;
            _pipeline.Changed -= OnPendingChanged;
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
