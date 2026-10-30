using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.Records;

/// <summary>A permission a custom role can include, as a tickable row.</summary>
public sealed partial class PermissionChoice(ResourceAction action, string group, string item) : ObservableObject
{
    public ResourceAction Action => action;
    public string Group => group;
    public string Item => item;
    /// <summary>Microsoft's description, or one made from the action's name when that's missing or machine-like.</summary>
    public string? Description => string.IsNullOrWhiteSpace(action.Description) || action.Description.Contains('/') || action.Description.StartsWith("microsoft.", StringComparison.OrdinalIgnoreCase)
        ? Words(action.Name)
        : action.Description;

    /// <summary>"microsoft.directory/users/password/update" → "Update users: password".</summary>
    static string Words(string name)
    {
        var parts = name.Split('/');
        if (parts.Length < 3)
            return name;
        var verb = parts[^1] switch
        {
            "allTasks" => "Do everything with",
            "allProperties" => "Change everything about",
            "standard" => "Read the basics of",
            var v => char.ToUpperInvariant(v[0]) + string.Concat(v[1..].Select(c => char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString())),
        };
        var what = parts[1..^1].Select(s => string.Concat(s.Select(c => char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString())));
        var list = what.ToArray();
        return list.Length == 1 ? $"{verb} {list[0]}" : $"{verb} {list[0]}: {string.Join(", ", list[1..])}";
    }
    public bool IsPrivileged => action.IsPrivileged;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>The heading shown above this row: its group, when it starts one in the filtered list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeading))]
    public partial string? Heading { get; set; }

    public bool HasHeading => Heading is not null;
}

/// <summary>
/// New Custom Role or Edit: a name and description, optionally a role to start from, and the
/// permissions, searchable and grouped by what they act on, privileged ones marked. Adding stages one
/// change. Custom roles need Entra ID P1.
/// </summary>
public sealed partial class CustomRoleEditorViewModel : ObservableObject
{
    readonly ChangePipeline _pipeline;
    readonly IRoleRecords _records;
    readonly IReadOnlyList<AdminRole> _roles;
    readonly AdminRole? _editing;
    readonly Action _close;
    List<PermissionChoice> _all = [];

    /// <param name="roles">Every role, to start from one and to catch a name already used.</param>
    /// <param name="editing">The custom role being edited, or null for a new one.</param>
    public CustomRoleEditorViewModel(ChangePipeline pipeline, IRoleRecords records, IReadOnlyList<AdminRole> roles, AdminRole? editing, Action close)
    {
        _pipeline = pipeline;
        _records = records;
        _roles = roles;
        _editing = editing;
        _close = close;
        Name = editing?.Name ?? "";
        Description = editing?.Description ?? "";
        _ = LoadAsync();
    }

    public bool IsNew => _editing is null;
    public string SaveText => IsNew ? "Add to Pending Changes" : "Add Changes";

    /// <summary>Roles to copy permissions from (built-in ones first).</summary>
    public IReadOnlyList<AdminRole> StartFrom => [.. _roles.Where(r => r.AllowedActions.Count > 0 && r.Id != _editing?.Id).OrderBy(r => r.IsBuiltIn ? 0 : 1).ThenBy(r => r.Name)];

    [ObservableProperty]
    public partial AdminRole? Template { get; set; }

    partial void OnTemplateChanged(AdminRole? value)
    {
        if (value is null)
            return;
        var copy = value.AllowedActions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _all)
            p.IsChecked = copy.Contains(p.Action.Name);
        Changed();
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    partial void OnNameChanged(string value) => Changed();

    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string? LoadError { get; private set; }

    public bool HasLoadError => LoadError is not null;

    async Task LoadAsync()
    {
        try
        {
            var actions = await _records.GetResourceActionsAsync();
            var have = (_editing?.AllowedActions ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _all =
            [
                .. actions.Select(a =>
                {
                    var (group, item) = RolePermissionsSection.Split(a.Name);
                    var choice = new PermissionChoice(a, group, item) { IsChecked = have.Contains(a.Name) };
                    choice.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(PermissionChoice.IsChecked))
                            Changed();
                    };
                    return choice;
                }).OrderBy(p => p.Group, StringComparer.CurrentCulture).ThenBy(p => p.Item, StringComparer.Ordinal),
            ];
            Filter();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read the permissions a role can have: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
            Changed();
        }
    }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool OnlyChosen { get; set; }

    partial void OnSearchChanged(string value) => Filter();
    partial void OnOnlyChosenChanged(bool value) => Filter();

    public ObservableCollection<PermissionChoice> Shown { get; } = [];

    void Filter()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Shown.Clear();
        string? group = null;
        foreach (var p in _all.Where(p => (!OnlyChosen || p.IsChecked)
                                          && words.All(w => p.Action.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                            || (p.Description?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false))))
        {
            p.Heading = p.Group != group ? p.Group : null;
            group = p.Group;
            Shown.Add(p);
        }
        OnPropertyChanged(nameof(HasNoMatch));
    }

    public bool HasNoMatch => !IsLoading && Shown.Count == 0;

    PermissionChoice? _anchor;

    /// <summary>
    /// A row's box was clicked (its new state already set). With Shift, every shown row between the
    /// last one clicked and this one takes the same state, as in the lists.
    /// </summary>
    public void Clicked(PermissionChoice choice, bool shift)
    {
        if (shift && _anchor is not null && Shown.IndexOf(_anchor) is >= 0 and var from && Shown.IndexOf(choice) is >= 0 and var to)
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                Shown[i].IsChecked = choice.IsChecked;
        _anchor = choice;
    }

    public string ChosenText
    {
        get
        {
            var chosen = _all.Count(p => p.IsChecked);
            var privileged = _all.Count(p => p.IsChecked && p.IsPrivileged);
            return $"{chosen:N0} chosen" + (privileged > 0 ? $", {privileged:N0} privileged" : "");
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    void Changed()
    {
        var problems = new List<string>();
        var name = Name.Trim();
        if (name.Length == 0)
            problems.Add("Give it a name.");
        else if (_roles.Any(r => r.Id != _editing?.Id && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"There's already a role called \"{name}\".");
        if (!IsLoading && !_all.Any(p => p.IsChecked))
            problems.Add("Choose at least one permission.");
        Problems = problems;
        OnPropertyChanged(nameof(ChosenText));
    }

    bool CanSave => Problems.Count == 0 && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanSave))]
    void Save()
    {
        Changed();
        if (!CanSave)
            return;
        var actions = _all.Where(p => p.IsChecked).Select(p => p.Action.Name).ToArray();
        if (_editing is null)
            _pipeline.Stage(new CreateCustomRole(new ChangeTarget("role", Name.Trim(), Name.Trim(), null), Description.Trim(), actions));
        else
            _pipeline.Stage(new UpdateCustomRole(new ChangeTarget("role", _editing.Id, _editing.Name, null), Name.Trim(), _editing.Description, Description.Trim(),
                _editing.AllowedActions, actions));
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}
