using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;

namespace Tenantry.ViewModels.Editing;

/// <summary>
/// What a bulk action would stage: one change per row it applies to, plus the rows it skipped
/// and why ("Ada: already a member"). <see cref="Error"/> means the input itself is wrong and
/// nothing is staged.
/// </summary>
public sealed record BulkPlan(IReadOnlyList<ChangeOp> Changes, IReadOnlyList<string> Skipped, string? Error = null)
{
    public static BulkPlan Invalid(string error) => new([], [], error);
}

/// <summary>An option in a bulk action's list ("Block sign-in", "Microsoft 365 E3 · 4 left").</summary>
public sealed record BulkChoice(string Label, object Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// One thing that can be done to every selected row. It asks for at most a person/group (a
/// picker), a choice and a text value, then plans the changes. Generic, so the user and group
/// lists describe their actions as data rather than as screens.
/// </summary>
public sealed partial class BulkAction : ObservableObject
{
    readonly Func<BulkAction, CancellationToken, Task<BulkPlan>> _plan;
    readonly Func<CancellationToken, Task<IReadOnlyList<BulkChoice>>>? _loadChoices;

    public BulkAction(string title, string description, string icon, Func<BulkAction, CancellationToken, Task<BulkPlan>> plan,
        string? pickPlaceholder = null, Func<IEnumerable<PickOption>>? pickOptions = null,
        string? choiceLabel = null, IReadOnlyList<BulkChoice>? choices = null, Func<CancellationToken, Task<IReadOnlyList<BulkChoice>>>? loadChoices = null,
        string? textLabel = null, bool isCaution = false)
    {
        Title = title;
        Description = description;
        Icon = icon;
        _plan = plan;
        _loadChoices = loadChoices;
        if (pickOptions is not null)
            Picker = new PersonPicker(pickPlaceholder ?? "Type a name", pickOptions, o => Picked = o);
        ChoiceLabel = choiceLabel;
        Choices = choices ?? [];
        TextLabel = textLabel;
        IsCaution = isCaution;
    }

    public string Title { get; }
    public string Description { get; }
    public string Icon { get; }

    /// <summary>Takes something away (blocking, removing): the Stage button says so.</summary>
    public bool IsCaution { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    // ---- Inputs -----------------------------------------------------------------------------

    public PersonPicker? Picker { get; }

    public bool HasPicker => Picker is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicked), nameof(IsReady))]
    public partial PickOption? Picked { get; set; }

    public bool HasPicked => Picked is not null;

    [RelayCommand]
    void ClearPicked() => Picked = null;

    public string? ChoiceLabel { get; }

    public bool HasChoices => ChoiceLabel is not null;

    [ObservableProperty]
    public partial IReadOnlyList<BulkChoice> Choices { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial BulkChoice? Choice { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingChoices { get; private set; }

    public string? TextLabel { get; }

    public bool HasText => TextLabel is not null;

    /// <summary>Free text (a new department, say). Empty is allowed: it clears the value.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    public bool IsReady => (Picker is null || Picked is not null) && (!HasChoices || Choice is not null);

    /// <summary>Choices that depend on the tenant (its licenses) are read when the action is first chosen.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_loadChoices is null || Choices.Count > 0 || IsLoadingChoices)
            return;
        IsLoadingChoices = true;
        try
        {
            Choices = await _loadChoices(cancellationToken);
        }
        finally
        {
            IsLoadingChoices = false;
        }
    }

    public Task<BulkPlan> PlanAsync(CancellationToken cancellationToken = default) => _plan(this, cancellationToken);
}

/// <summary>
/// The bulk edit popup: choose one action, fill in what it asks, Stage. Every change goes to
/// Pending changes (one per selected row); rows it doesn't apply to are listed with the reason.
/// </summary>
public sealed partial class BulkEditorViewModel : ObservableObject
{
    const int SkippedShown = 6;

    readonly ChangePipeline _pipeline;
    readonly Action _close;

    public BulkEditorViewModel(string heading, IReadOnlyList<BulkAction> actions, ChangePipeline pipeline, Action close)
    {
        Heading = heading;
        Actions = actions;
        _pipeline = pipeline;
        _close = close;
        foreach (var action in actions)
            action.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(BulkAction.IsSelected) && s is BulkAction { IsSelected: true } chosen)
                    Selected = chosen;
                if (e.PropertyName == nameof(BulkAction.IsReady))
                    StageCommand.NotifyCanExecuteChanged();
            };
    }

    /// <summary>"Edit 12 users".</summary>
    public string Heading { get; }

    public IReadOnlyList<BulkAction> Actions { get; }

    public bool IsTestMode => _pipeline.IsTestMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected), nameof(StageText))]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    public partial BulkAction? Selected { get; set; }

    public bool HasSelected => Selected is not null;

    partial void OnSelectedChanged(BulkAction? value)
    {
        foreach (var action in Actions)
            action.IsSelected = action == value;
        Result = null;
        Error = null;
        Skipped.Clear();
        _ = value?.LoadAsync();
    }

    public string StageText => Selected?.IsCaution == true ? "Stage (takes something away)" : "Stage for review";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    public partial bool IsWorking { get; private set; }

    /// <summary>"Staged 12 changes. Review them in Pending changes."</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? Result { get; private set; }

    public bool HasResult => Result is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public ObservableCollection<string> Skipped { get; } = [];

    [ObservableProperty]
    public partial string? SkippedHeading { get; private set; }

    bool CanStage => Selected is { IsReady: true } && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanStage))]
    async Task Stage()
    {
        if (Selected is not { } action)
            return;
        IsWorking = true;
        Result = null;
        Error = null;
        Skipped.Clear();
        SkippedHeading = null;
        try
        {
            var plan = await action.PlanAsync();
            if (plan.Error is { } error)
            {
                Error = error;
                return;
            }
            _pipeline.StageAll(plan.Changes);
            Result = plan.Changes.Count switch
            {
                0 => "Nothing to stage: every selected row is already that way.",
                1 => "Staged 1 change. Review it in Pending changes at the bottom of the window.",
                var n => $"Staged {n:N0} changes. Review them in Pending changes at the bottom of the window.",
            };
            if (plan.Skipped.Count > 0)
            {
                SkippedHeading = $"Skipped {plan.Skipped.Count:N0}:";
                foreach (var line in plan.Skipped.Take(SkippedShown))
                    Skipped.Add(line);
                if (plan.Skipped.Count > SkippedShown)
                    Skipped.Add($"and {plan.Skipped.Count - SkippedShown:N0} more");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = $"Couldn't prepare the changes: {Friendly.Message(e)}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    void Done() => _close();
}
