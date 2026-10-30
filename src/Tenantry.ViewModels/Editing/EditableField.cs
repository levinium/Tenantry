using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;

namespace Tenantry.ViewModels.Editing;

/// <summary>One option of a choice field: the value stored, and how it reads.</summary>
public sealed record FieldChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One editable text value on a record (a user's department, say). Shows the current value and,
/// once a change is staged, the pending one beside it; Edit opens a box, Stage puts the change in
/// the Pending changes tray, and the ✕ on a pending value takes it back out.
/// </summary>
public sealed partial class EditableField : ObservableObject
{
    readonly ChangePipeline? _pipeline;
    readonly ChangeTarget _target;
    readonly Availability _canEdit;
    readonly bool _required;

    readonly Func<string, string?>? _validate;

    /// <param name="choices">When given, the value is picked from a list instead of typed.</param>
    /// <param name="validate">Returns a problem with the typed value, or null when it's fine.</param>
    public EditableField(string label, string property, string? current, ChangeTarget target, ChangePipeline? pipeline, Availability canEdit,
        bool required = false, string emptyText = "Not set", IReadOnlyList<FieldChoice>? choices = null, Func<string, string?>? validate = null,
        string? hint = null)
    {
        Label = label;
        Property = property;
        Current = current;
        EmptyText = emptyText;
        Choices = choices;
        Hint = hint;
        _target = target;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _required = required;
        _validate = validate;
        Draft = "";
    }

    public IReadOnlyList<FieldChoice>? Choices { get; }

    public bool HasChoices => Choices is { Count: > 0 };

    public bool IsTyped => !HasChoices;

    /// <summary>A line under the editor explaining what the value does ("Required before licensing").</summary>
    public string? Hint { get; }

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    /// <summary>The picked option while editing a choice field.</summary>
    public FieldChoice? SelectedChoice
    {
        get => Choices?.FirstOrDefault(c => string.Equals(c.Value, Draft, StringComparison.OrdinalIgnoreCase));
        set
        {
            Draft = value?.Value ?? "";
            OnPropertyChanged();
        }
    }

    string Show(string? value) =>
        string.IsNullOrEmpty(value) ? EmptyText
        : Choices?.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase))?.Label ?? value;

    public string Label { get; }

    public string Property { get; }

    public string? Current { get; }

    public string EmptyText { get; }

    public string Value => Show(Current);

    /// <summary>"LABEL" in the record's small-caps label style.</summary>
    public string Caption => Label.ToUpperInvariant();

    SetUserText? Pending => _pipeline?.PendingFor(_target.Id, Property) as SetUserText;

    public bool HasPending => Pending is not null;

    public string PendingText => Pending is { } p ? $"→ {(string.IsNullOrEmpty(p.New) ? "(cleared)" : Show(p.New))}" : "";

    /// <summary>Editing needs a tenant that can take changes and a role that allows them.</summary>
    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    /// <summary>Shown wherever the tenant takes changes; disabled (with the reason) when the admin's role doesn't allow it.</summary>
    public bool ShowEditButton => _pipeline is not null && !IsEditing;

    public string EditTip => _pipeline is null ? "This tenant can't be edited"
        : !_canEdit.IsAvailable ? _canEdit.Reason ?? "Not available"
        : $"Edit {Label.ToLowerInvariant()}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEditButton))]
    public partial bool IsEditing { get; private set; }

    [ObservableProperty]
    public partial string Draft { get; set; }

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [RelayCommand]
    void Edit()
    {
        if (!CanEdit)
            return;
        Draft = Pending is { } p ? p.New ?? "" : Current ?? "";
        _startDraft = Draft;
        _pendingAtStart = Pending;
        OnPropertyChanged(nameof(SelectedChoice));
        Problem = null;
        IsEditing = true;
    }

    string _startDraft = "";
    SetUserText? _pendingAtStart;

    /// <summary>
    /// Ends the edit (Enter, leaving the box, or closing the choice list): an untouched value just
    /// closes; a changed one goes to the Pending changes tray (or leaves it, if back to the original).
    /// </summary>
    [RelayCommand]
    void Stage()
    {
        if (!IsEditing)
            return;
        if (Draft.Trim() == _startDraft.Trim())
        {
            CancelEdit();
            return;
        }
        if (TryStage())
            IsEditing = false;
    }

    /// <summary>A pause in typing: what's typed goes to Pending changes and the box stays open.</summary>
    [RelayCommand]
    void StageDraft()
    {
        if (!IsEditing)
            return;
        if (Draft.Trim() == _startDraft.Trim())
        {
            RestoreStart();
            Problem = null;
            return;
        }
        TryStage();
    }

    bool TryStage()
    {
        var value = Draft.Trim();
        if (_required && value.Length == 0)
        {
            Problem = $"{Label} can't be empty.";
            return false;
        }
        if (value.Length > 0 && _validate?.Invoke(value) is { } problem)
        {
            Problem = problem;
            return false;
        }
        _pipeline!.Stage(new SetUserText(_target, Property, Label, Current, value.Length == 0 ? null : value));
        Problem = null;
        return true;
    }

    /// <summary>Esc: the field goes back to how it was before editing, even if a pause staged something.</summary>
    [RelayCommand]
    void CancelEdit()
    {
        if (IsEditing)
            RestoreStart();
        IsEditing = false;
        Problem = null;
    }

    void RestoreStart() => _pipeline?.Restore([(_target.Id, Property, _pendingAtStart)]);

    [RelayCommand]
    void Unstage()
    {
        if (Pending is { } p)
            _pipeline!.Unstage(p);
    }

    /// <summary>Re-reads the pending state (the record calls this when the tray changes).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingText));
    }
}
