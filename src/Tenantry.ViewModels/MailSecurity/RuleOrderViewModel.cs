using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>A rule in the reorder list: where it runs now, where it ran, and whether it's ticked to move.</summary>
public sealed partial class RuleOrderRow(MailFlowRule rule, int originalIndex) : ObservableObject
{
    public MailFlowRule Rule => rule;
    public string Identity => rule.Identity;
    public string Name => rule.Name;
    public string StateText => rule.Enabled ? "On" : "Off";
    public BadgeKind StateKind => rule.Enabled ? BadgeKind.Success : BadgeKind.Neutral;
    public string ModeText => MailFlowRule.ModeName(rule.Mode);
    public BadgeKind ModeKind => rule.Mode == RuleMode.Enforce ? BadgeKind.Info : BadgeKind.Warning;
    public int OriginalIndex => originalIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    public partial int Index { get; set; }

    public string PositionText => (Index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>One of the rules Exchange is asked to move (the others only shift to make room).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WasText))]
    public partial bool IsMoved { get; set; }

    public string WasText => IsMoved ? $"was {originalIndex + 1}" : "";

    [ObservableProperty]
    public partial bool IsTicked { get; set; }
}

/// <summary>
/// Reorder Rules: every mail flow rule in the order it runs, to arrange by dragging or with
/// Top, Up, Down and Bottom (on one rule or several ticked together; Shift-click ticks a range).
/// The whole new order is one change in Pending Changes, kept up to date as rules move; applying
/// it sends only the moves needed, one after another, with no wait between them.
/// </summary>
public sealed partial class RuleOrderViewModel : ObservableObject, IDisposable
{
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly List<MailFlowRule> _original;
    RuleOrderRow? _anchor;
    bool _restaging;

    public RuleOrderViewModel(IReadOnlyList<MailFlowRule> rules, ChangePipeline? pipeline, Availability canEdit)
    {
        _pipeline = pipeline;
        _canEdit = canEdit;
        _original = [.. rules.OrderBy(r => r.Priority)];
        Reset(fromPending: true);
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    public ObservableCollection<RuleOrderRow> Rows { get; } = [];

    IReadOnlyList<RuleSlot> OriginalSlots => [.. _original.Select(r => new RuleSlot(r.Identity, r.Name))];

    ReorderRules? Pending => _pipeline?.PendingFor(ReorderRules.OrderTarget.Id, "order") as ReorderRules;

    /// <summary>The rows as the rules stand, or as a pending order has them (rules made since go last).</summary>
    void Reset(bool fromPending)
    {
        var byId = _original.ToDictionary(r => r.Identity, StringComparer.Ordinal);
        var order = fromPending && Pending is { } p
            ? p.New.Select(s => byId.GetValueOrDefault(s.Identity)).OfType<MailFlowRule>().Concat(_original.Where(r => p.New.All(s => s.Identity != r.Identity))).ToList()
            : [.. _original];
        Rows.Clear();
        for (var i = 0; i < order.Count; i++)
            Rows.Add(new RuleOrderRow(order[i], _original.IndexOf(order[i])) { Index = i });
        Changed(stage: false);
    }

    // ---- Ticking -------------------------------------------------------------------------

    /// <summary>A click on a row's box: ticks or unticks it; with Shift, every row from the last one clicked takes the same state.</summary>
    public void Tick(RuleOrderRow row, bool shift)
    {
        var on = !row.IsTicked;
        if (shift && _anchor is not null && Rows.Contains(_anchor))
        {
            var (from, to) = (Math.Min(Rows.IndexOf(_anchor), Rows.IndexOf(row)), Math.Max(Rows.IndexOf(_anchor), Rows.IndexOf(row)));
            for (var i = from; i <= to; i++)
                Rows[i].IsTicked = on;
        }
        else
            row.IsTicked = on;
        _anchor = row;
        Changed(stage: false);
    }

    [RelayCommand]
    void ClearTicks()
    {
        foreach (var r in Rows)
            r.IsTicked = false;
        Changed(stage: false);
    }

    public int TickedCount => Rows.Count(r => r.IsTicked);

    public bool HasTicked => TickedCount > 0;

    public string TickedText => TickedCount switch
    {
        0 => "Tick rules to move them together, or drag one by its handle.",
        1 => "1 rule ticked.",
        var n => $"{n} rules ticked: they move together, keeping their order.",
    };

    // ---- Moving --------------------------------------------------------------------------

    bool CanMoveUp => CanEdit && HasTicked && Rows.Select((r, i) => (r, i)).Any(x => x.r.IsTicked && x.i > 0 && Rows.Take(x.i).Any(o => !o.IsTicked));
    bool CanMoveDown => CanEdit && HasTicked && Rows.Select((r, i) => (r, i)).Any(x => x.r.IsTicked && Rows.Skip(x.i + 1).Any(o => !o.IsTicked));

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    void MoveToTop() => Place([.. Rows.Where(r => r.IsTicked), .. Rows.Where(r => !r.IsTicked)]);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    void MoveToBottom() => Place([.. Rows.Where(r => !r.IsTicked), .. Rows.Where(r => r.IsTicked)]);

    /// <summary>Each ticked rule swaps with the unticked rule just above it (a ticked block moves as one).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    void MoveUp()
    {
        var list = Rows.ToList();
        for (var i = 1; i < list.Count; i++)
            if (list[i].IsTicked && !list[i - 1].IsTicked)
                (list[i - 1], list[i]) = (list[i], list[i - 1]);
        Place(list);
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    void MoveDown()
    {
        var list = Rows.ToList();
        for (var i = list.Count - 2; i >= 0; i--)
            if (list[i].IsTicked && !list[i + 1].IsTicked)
                (list[i + 1], list[i]) = (list[i], list[i + 1]);
        Place(list);
    }

    /// <summary>A drag: the row lands at <paramref name="index"/> (the others close up around it).</summary>
    public void Drop(RuleOrderRow row, int index)
    {
        if (!CanEdit)
            return;
        var list = Rows.Where(r => r != row).ToList();
        list.Insert(Math.Clamp(index, 0, list.Count), row);
        Place(list);
    }

    void Place(List<RuleOrderRow> order)
    {
        for (var i = 0; i < order.Count; i++)
        {
            var at = Rows.IndexOf(order[i]);
            if (at != i)
                Rows.Move(at, i);
            order[i].Index = i;
        }
        Changed(stage: true);
    }

    /// <summary>Puts the order back as Exchange has it (and takes the change out of Pending Changes).</summary>
    [RelayCommand]
    void UndoAll()
    {
        if (Pending is { } p)
            _pipeline!.Unstage(p);
        Reset(fromPending: false);
    }

    // ---- The change ----------------------------------------------------------------------

    public int MovedCount => Rows.Count(r => r.IsMoved);

    /// <summary>Marks the rules that move (the planned moves); the rest only shift.</summary>
    void MarkMoved()
    {
        var moving = new ReorderRules(ReorderRules.OrderTarget, OriginalSlots, CurrentSlots).Moves().Select(m => m.Identity).ToHashSet(StringComparer.Ordinal);
        foreach (var row in Rows)
            row.IsMoved = moving.Contains(row.Identity);
    }

    public bool HasMoved => MovedCount > 0;

    /// <summary>"2 rules move (the others shift to make room). The new order is in Pending Changes…"</summary>
    public string SummaryText => !HasMoved
        ? "The rules are in the order they run now."
        : $"{MovedCount} {(MovedCount == 1 ? "rule moves" : "rules move")}, the others shift to make room. The new order is in Pending Changes; "
          + $"applying it sends {MovedCount} {(MovedCount == 1 ? "move" : "moves")} to Exchange, one after another. "
          // Seen live 2026-11-10: every rule write takes Exchange about 30 s, whatever it changes.
          + $"Exchange takes about half a minute for each, so this takes about {Duration(MovedCount)}.";

    static string Duration(int moves) => moves <= 2 ? (moves == 1 ? "half a minute" : "a minute") : $"{(moves + 1) / 2} minutes";

    IReadOnlyList<RuleSlot> CurrentSlots => [.. Rows.Select(r => new RuleSlot(r.Identity, r.Name))];

    void Changed(bool stage)
    {
        MarkMoved();
        if (stage && CanEdit)
        {
            _restaging = true;
            if (Pending is { } p)
                _pipeline!.Unstage(p);
            var change = new ReorderRules(ReorderRules.OrderTarget, OriginalSlots, CurrentSlots);
            if (!change.IsNoChange)
                _pipeline!.Stage(change);
            _restaging = false;
        }
        foreach (var name in new[] { nameof(TickedCount), nameof(HasTicked), nameof(TickedText), nameof(MovedCount), nameof(HasMoved), nameof(SummaryText) })
            OnPropertyChanged(name);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        MoveToTopCommand.NotifyCanExecuteChanged();
        MoveToBottomCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The order taken out of Pending Changes elsewhere (the tray's undo, Discard All) puts the rows back.</summary>
    void OnPendingChanged(object? sender, EventArgs e)
    {
        if (_restaging)
            return;
        var shown = CurrentSlots.Select(s => s.Identity);
        var wanted = (Pending?.New ?? OriginalSlots).Select(s => s.Identity);
        if (!shown.SequenceEqual(wanted, StringComparer.Ordinal))
            Reset(fromPending: true);
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
