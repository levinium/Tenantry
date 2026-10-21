using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;

namespace Tenantry.ViewModels.Editing;

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

    public EditableField(string label, string property, string? current, ChangeTarget target, ChangePipeline? pipeline, Availability canEdit,
        bool required = false, string emptyText = "Not set")
    {
        Label = label;
        Property = property;
        Current = current;
        EmptyText = emptyText;
        _target = target;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _required = required;
        Draft = "";
    }

    public string Label { get; }

    public string Property { get; }

    public string? Current { get; }

    public string EmptyText { get; }

    public string Value => string.IsNullOrEmpty(Current) ? EmptyText : Current;

    /// <summary>"LABEL" in the record's small-caps label style.</summary>
    public string Caption => Label.ToUpperInvariant();

    SetUserText? Pending => _pipeline?.PendingFor(_target.Id, Property) as SetUserText;

    public bool HasPending => Pending is not null;

    public string PendingText => Pending is { } p ? $"→ {(string.IsNullOrEmpty(p.New) ? "(cleared)" : p.New)}" : "";

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
        Problem = null;
        IsEditing = true;
    }

    /// <summary>Puts the edit in the Pending changes tray (or takes it out, if it's back to the original).</summary>
    [RelayCommand]
    void Stage()
    {
        var value = Draft.Trim();
        if (_required && value.Length == 0)
        {
            Problem = $"{Label} can't be empty.";
            return;
        }
        _pipeline!.Stage(new SetUserText(_target, Property, Label, Current, value.Length == 0 ? null : value));
        IsEditing = false;
        Problem = null;
    }

    [RelayCommand]
    void CancelEdit()
    {
        IsEditing = false;
        Problem = null;
    }

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
