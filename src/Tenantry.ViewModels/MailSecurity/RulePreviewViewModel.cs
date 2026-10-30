using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>
/// A mail flow rule in the preview pane: what it checks, its exceptions and what it does, in
/// Exchange's plain words; and the everyday changes: on or off, Enforce or Test, its place in the
/// order, delete. Each goes to Pending changes; pressing again takes it back out.
/// </summary>
public sealed partial class RulePreviewViewModel : ObservableObject, IDisposable
{
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly int _ruleCount;

    readonly Action<MailFlowRule>? _edit;

    public RulePreviewViewModel(MailFlowRule rule, int ruleCount, ChangePipeline? pipeline, Availability canEdit, Action<MailFlowRule>? edit = null)
    {
        Rule = rule;
        _ruleCount = ruleCount;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _edit = edit;
        _mode = Modes.First(m => m.Value == rule.Mode);
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public MailFlowRule Rule { get; }

    public string Name => Rule.Name;
    public string OrderText => $"Runs {Ordinal(Rule.Priority + 1)} of {_ruleCount}";
    public string StateText => Rule.Enabled ? "On" : "Off";
    public BadgeKind StateKind => Rule.Enabled ? BadgeKind.Success : BadgeKind.Neutral;
    public string ModeText => MailFlowRule.ModeName(Rule.Mode);
    public BadgeKind ModeKind => Rule.Mode == RuleMode.Enforce ? BadgeKind.Info : BadgeKind.Warning;

    public IReadOnlyList<string> Conditions => Rule.Conditions.Count == 0 ? ["Every message"] : Rule.Conditions;
    public IReadOnlyList<string> Exceptions => Rule.Exceptions;
    public bool HasExceptions => Rule.Exceptions.Count > 0;
    public IReadOnlyList<string> Actions => Rule.Actions;
    public string? Comments => Rule.Comments;
    public bool HasComments => !string.IsNullOrEmpty(Rule.Comments);

    public string DatesText => string.Join(" · ", new[]
    {
        Rule.ActivationDate is { } from ? $"Starts {from.ToLocalTime():d MMM yyyy}" : null,
        Rule.ExpiryDate is { } to ? $"Stops {to.ToLocalTime():d MMM yyyy}" : null,
        Rule.Changed is { } at ? $"Changed {at.ToLocalTime():d MMM yyyy}{(Rule.ChangedBy is { } by ? $" by {by}" : "")}" : null,
    }.OfType<string>());

    static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    // ---- Changes ------------------------------------------------------------------------------

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;
    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    ChangeTarget Target => new("rule", Rule.Identity, Rule.Name, null);

    ChangeOp? Pending(string key) => _pipeline?.PendingFor(Rule.Identity, key);

    /// <summary>What's waiting in Pending changes for this rule.</summary>
    public string? PendingText
    {
        get
        {
            var parts = new[] { "enabled", "mode", "priority", "remove" }.Select(Pending).OfType<ChangeOp>()
                .Select(c => c is RemoveRule ? "delete" : c.After.ToLowerInvariant()).ToArray();
            return parts.Length == 0 ? null : "Pending: " + string.Join(", ", parts);
        }
    }

    public bool HasPending => PendingText is not null;

    public bool CanOpenEditor => CanEdit && _edit is not null;

    /// <summary>Opens the full editor in its own tab.</summary>
    [RelayCommand]
    void Edit() => _edit?.Invoke(Rule);

    public string EnableText => Pending("enabled") is not null ? "Undo" : Rule.Enabled ? "Turn Off" : "Turn On";

    [RelayCommand]
    void ToggleEnabled()
    {
        if (!CanEdit)
            return;
        if (Pending("enabled") is { } pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new SetRuleEnabled(Target, Rule.Enabled, !Rule.Enabled));
    }

    public static IReadOnlyList<Choice<RuleMode>> Modes { get; } =
        Enum.GetValues<RuleMode>().Select(m => new Choice<RuleMode>(m, MailFlowRule.ModeName(m) + m switch
        {
            RuleMode.Enforce => " (Acts on Mail)",
            RuleMode.Test => " (Only Records Matches)",
            _ => " (Records Matches, Shows Senders a Tip)",
        })).ToArray();

    Choice<RuleMode> _mode;

    /// <summary>Choosing a mode stages it; choosing the rule's own mode again takes the change out.</summary>
    public Choice<RuleMode> Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value) || !CanEdit)
                return;
            _pipeline!.Stage(new SetRuleMode(Target, Rule.Mode, value.Value));
        }
    }

    int PlannedPriority => Pending("priority") is SetRulePriority p ? p.New : Rule.Priority;

    /// <summary>
    /// Another rule's move waiting in Pending changes. Exchange renumbers the other rules on each
    /// move, so two moves planned from the same order wouldn't end where either showed: one at a time.
    /// </summary>
    bool OtherMovePending => _pipeline?.Pending.Any(c => c is SetRulePriority p && p.Rule.Id != Rule.Identity) == true;

    public string? OrderNote => CanEdit && OtherMovePending
        ? "Another rule's move is waiting in Pending Changes. Apply it first: rules move one at a time, because each move renumbers the rest."
        : null;

    public bool HasOrderNote => OrderNote is not null;

    public bool CanMoveUp => CanEdit && !OtherMovePending && PlannedPriority > 0;
    public bool CanMoveDown => CanEdit && !OtherMovePending && PlannedPriority < _ruleCount - 1;

    void MoveTo(int priority)
    {
        if (!CanEdit || OtherMovePending)
            return;
        _pipeline!.Stage(new SetRulePriority(Target, Rule.Priority, Math.Clamp(priority, 0, _ruleCount - 1)));
    }

    [RelayCommand]
    void MoveUp() => MoveTo(PlannedPriority - 1);

    [RelayCommand]
    void MoveDown() => MoveTo(PlannedPriority + 1);

    [RelayCommand]
    void MoveToTop() => MoveTo(0);

    [RelayCommand]
    void MoveToBottom() => MoveTo(_ruleCount - 1);

    public string DeleteText => Pending("remove") is not null ? "Undo Delete" : "Delete Rule";

    [RelayCommand]
    void Delete()
    {
        if (!CanEdit)
            return;
        if (Pending("remove") is { } pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new RemoveRule(Target, $"{StateText}, {ModeText}"));
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        foreach (var name in new[] { nameof(PendingText), nameof(HasPending), nameof(EnableText), nameof(CanMoveUp), nameof(CanMoveDown), nameof(DeleteText),
                     nameof(OrderNote), nameof(HasOrderNote) })
            OnPropertyChanged(name);
        var planned = Pending("mode") is SetRuleMode m ? m.New : Rule.Mode;
        if (_mode.Value != planned)
        {
            _mode = Modes.First(x => x.Value == planned);
            OnPropertyChanged(nameof(Mode));
        }
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
