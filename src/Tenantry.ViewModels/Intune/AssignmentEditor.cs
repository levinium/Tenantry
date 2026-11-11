using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Intune;

/// <summary>One assignment as a row: who, and Remove (or Undo while a change to it waits).</summary>
public sealed partial class AssignmentRow(IntuneAssignment assignment, string? state, string actionText, string actionTip, Action act)
{
    public IntuneAssignment Assignment => assignment;
    public string Text => assignment.Text;
    public bool IsExclusion => assignment.Kind == AssignmentKind.ExcludeGroup;
    public string? StateText => state;
    public bool HasState => state is not null;
    public string ActionText => actionText;
    public string ActionTip => actionTip;

    [RelayCommand]
    void Do() => act();
}

/// <summary>A filter choice for a new assignment: none, only matching devices, or all but matching devices.</summary>
public sealed record FilterChoice(string? FilterId, string? FilterName, string? Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Who an Intune policy or app reaches, edited in place: include groups (narrowed by a filter),
/// All users or All devices, exclude groups; each change restages one <see cref="SetIntuneAssignments"/>
/// with the whole list (as Intune's assign takes it). Apps choose an intent per assignment.
/// </summary>
public sealed partial class AssignmentEditor : ObservableObject
{
    readonly ChangeTarget _policy;
    readonly string _kindTitle;
    readonly ChangePipeline? _pipeline;
    readonly string _listProperty;
    IReadOnlyList<IntuneAssignment> _current;

    public AssignmentEditor(ChangeTarget policy, string kindTitle, IReadOnlyList<IntuneAssignment> current, ChangePipeline? pipeline, bool canEdit, IDirectoryIndex index,
        IReadOnlyList<AssignmentFilter> filters, bool forApps = false)
    {
        _policy = policy;
        _kindTitle = kindTitle;
        _current = current;
        _pipeline = pipeline;
        CanEdit = canEdit && pipeline is not null;
        ForApps = forApps;
        _listProperty = forApps ? "mobileAppAssignments" : "assignments";
        Filters = [new FilterChoice(null, null, null, "No Filter"), .. filters.SelectMany(f => new[]
        {
            new FilterChoice(f.Id, f.Name, "include", $"Only Devices Matching {f.Name}"),
            new FilterChoice(f.Id, f.Name, "exclude", $"All but Devices Matching {f.Name}"),
        })];
        Filter = Filters[0];
        Intent = Intents[0];
        IEnumerable<PickOption> Groups(bool exclude) => index.AllGroups
            .Where(g => g.Kind is GroupKind.Security or GroupKind.Microsoft365 or GroupKind.MailEnabledSecurity)
            .Where(g => Effective().All(a => !string.Equals(a.GroupId, g.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(PickOption.Of);
        Include = new PersonPicker("Group to include", () => Groups(false), o => Add(new IntuneAssignment(AssignmentKind.Group, o.Id, o.Name, Filter?.FilterId, Filter?.FilterName,
            Filter?.Mode, ForApps ? Intent?.Value : null)));
        Exclude = new PersonPicker("Group to leave out", () => Groups(true), o => Add(new IntuneAssignment(AssignmentKind.ExcludeGroup, o.Id, o.Name,
            Intent: ForApps ? Intent?.Value : null)));
        if (pipeline is not null)
            pipeline.Changed += (_, _) => Fill();
        Fill();
    }

    public bool CanEdit { get; }
    public bool ForApps { get; }

    public PersonPicker Include { get; }
    public PersonPicker Exclude { get; }

    public IReadOnlyList<FilterChoice> Filters { get; }
    public bool HasFilters => Filters.Count > 1;

    [ObservableProperty]
    public partial FilterChoice? Filter { get; set; }

    public static IReadOnlyList<EditorChoice> Intents { get; } =
        [new("required", "Required", "Installed on its own"), new("available", "Available", "Offered in Company Portal"), new("uninstall", "Uninstall", "Removed")];

    [ObservableProperty]
    public partial EditorChoice? Intent { get; set; }

    public ObservableCollection<AssignmentRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public string Summary => Effective() is { Count: > 0 } list ? string.Join("; ", list.Select(a => a.Text)) : "Not assigned: it reaches nobody.";

    public bool CanAddAllUsers => CanEdit && Effective().All(a => a.Kind != AssignmentKind.AllUsers || (ForApps && a.Intent != Intent?.Value));
    public bool CanAddAllDevices => CanEdit && Effective().All(a => a.Kind != AssignmentKind.AllDevices || (ForApps && a.Intent != Intent?.Value));

    partial void OnIntentChanged(EditorChoice? value)
    {
        OnPropertyChanged(nameof(CanAddAllUsers));
        OnPropertyChanged(nameof(CanAddAllDevices));
    }

    [RelayCommand]
    void AddAllUsers() => Add(IntuneAssignment.AllUsers() with { FilterId = Filter?.FilterId, FilterName = Filter?.FilterName, FilterMode = Filter?.Mode, Intent = ForApps ? Intent?.Value : null });

    [RelayCommand]
    void AddAllDevices() => Add(IntuneAssignment.AllDevices() with { FilterId = Filter?.FilterId, FilterName = Filter?.FilterName, FilterMode = Filter?.Mode, Intent = ForApps ? Intent?.Value : null });

    SetIntuneAssignments? Pending => _pipeline?.PendingFor(_policy.Id, "assignments") as SetIntuneAssignments;

    IReadOnlyList<IntuneAssignment> Effective() => Pending?.New ?? _current;

    void Add(IntuneAssignment assignment)
    {
        if (CanEdit && Effective().All(a => a.Identity != assignment.Identity))
            Stage([.. Effective(), assignment]);
    }

    void Stage(IReadOnlyList<IntuneAssignment> list)
    {
        if (!CanEdit)
            return;
        if (Pending is { } pending)
            _pipeline!.Unstage(pending);
        var change = new SetIntuneAssignments(_policy, _kindTitle, _current, list, _listProperty);
        if (!change.IsNoChange)
            _pipeline!.Stage(change);
    }

    /// <summary>After applying: what Intune now has.</summary>
    public void Reset(IReadOnlyList<IntuneAssignment> current)
    {
        _current = current;
        Fill();
    }

    void Fill()
    {
        var effective = Effective();
        Rows.Clear();
        foreach (var a in _current.Concat(effective.Where(e => _current.All(c => c.Identity != e.Identity))).OrderBy(a => a.Kind == AssignmentKind.ExcludeGroup))
        {
            var kept = effective.Any(e => e.Identity == a.Identity);
            var isNew = _current.All(c => c.Identity != a.Identity);
            Rows.Add(!CanEdit ? new AssignmentRow(a, null, "", "", () => { })
                : isNew ? new AssignmentRow(a, "Adding", "Undo", "Don't add it", () => Stage([.. effective.Where(e => e.Identity != a.Identity)]))
                : !kept ? new AssignmentRow(a, "Removing", "Undo", "Keep it", () => Stage([.. effective, a]))
                : new AssignmentRow(a, null, "Remove", a.Kind == AssignmentKind.ExcludeGroup ? "Stop leaving them out (added to Pending Changes)" : "Stop reaching them (added to Pending Changes)",
                    () => Stage([.. effective.Where(e => e.Identity != a.Identity)])));
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanAddAllUsers));
        OnPropertyChanged(nameof(CanAddAllDevices));
    }
}
