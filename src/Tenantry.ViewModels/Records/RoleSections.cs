using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>One assignment of the role as a row: who, where, how long; Remove or Undo.</summary>
public sealed partial class RoleGrantRow
{
    public required string Name { get; init; }
    public string? Detail { get; init; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public required string TypeText { get; init; }
    public required string KindText { get; init; }
    public BadgeKind KindKind => KindText switch { "Eligible" => BadgeKind.Info, "Activated" => BadgeKind.Success, _ => BadgeKind.Neutral };
    public required string ScopeText { get; init; }
    public required string EndsText { get; init; }

    /// <summary>"Adding" or "Removing" while a change waits in Pending changes.</summary>
    public string? StateText { get; init; }
    public bool HasState => StateText is not null;

    public string? ActionText { get; init; }
    public string? ActionTip { get; init; }
    internal Action? Act { get; init; }
    public bool HasAction => ActionText is not null && Act is not null;

    /// <summary>Why it can't be removed here ("The only Global Administrator").</summary>
    public string? Locked { get; init; }
    public bool IsLocked => Locked is not null;

    internal Action? OpenAction { get; init; }
    public bool CanOpen => OpenAction is not null;

    [RelayCommand]
    void Do() => Act?.Invoke();

    [RelayCommand]
    void Open() => OpenAction?.Invoke();
}

/// <summary>Where a role applies: the whole organization or one administrative unit.</summary>
public sealed record RoleScopeOption(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Who holds the role, and a form to give it to someone: for the organization or one administrative
/// unit; with PIM, eligible or active, permanently or until a date, with a reason.
/// </summary>
public sealed partial class RoleAssignmentsSection : RecordSection
{
    readonly RoleDetailViewModel _page;

    public RoleAssignmentsSection(RoleDetailViewModel page)
    {
        _page = page;
        Picker = new PersonPicker("Give the role to: type a name", Candidates, o => Picked = _page.Index.User(o.Id));
        Rebuild();
    }

    public override string Key => "assignments";
    public override string Title => "Assignments";
    public override string Icon => "PeopleCheckmark";

    public bool CanEdit => _page.CanEdit;
    public string EditReason => _page.EditReason;

    public ObservableCollection<RoleGrantRow> Rows { get; } = [];

    public bool HasNoRows => Rows.Count == 0;

    // ---- Giving the role ------------------------------------------------------------------------

    public PersonPicker Picker { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicked), nameof(PickedName))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial UserSummary? Picked { get; set; }

    public bool HasPicked => Picked is not null;
    public string PickedName => Picked?.DisplayName ?? "";

    [RelayCommand]
    void ClearPicked() => Picked = null;

    public IReadOnlyList<RoleScopeOption> Scopes =>
        [new("/", "The Whole Organization"), .. _page.Directory.Units.Select(u => new RoleScopeOption($"/administrativeUnits/{u.Id}", u.Name))];

    public bool HasUnits => _page.Directory.Units.Count > 0;

    [ObservableProperty]
    public partial RoleScopeOption? Scope { get; set; }

    /// <summary>Why only active, permanent assignments can be made (no Entra ID P2), or null.</summary>
    public string? PimNote => _page.Directory.PimUnavailable is null ? null : "Without Entra ID P2 a role is given active and permanently.";
    public bool HasPimNote => PimNote is not null;
    public bool HasPim => _page.Directory.PimUnavailable is null;

    [ObservableProperty]
    public partial bool Eligible { get; set; }

    [ObservableProperty]
    public partial DateTime? Until { get; set; }

    [ObservableProperty]
    public partial string Justification { get; set; } = "";

    IEnumerable<PickOption> Candidates()
    {
        var holding = _page.Role.Grants.Select(g => g.PrincipalId)
            .Concat(_page.Pending.OfType<AddRoleAssignment>().Select(a => a.Principal.Id)).ToHashSet(StringComparer.Ordinal);
        return GroupAccess.People(_page.Index).Where(o => !holding.Contains(o.Id));
    }

    bool CanAdd => CanEdit && Picked is not null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    void Add()
    {
        if (Picked is not { } person)
            return;
        var scope = Scope ?? Scopes[0];
        _page.Pipeline!.Stage(new AddRoleAssignment(GroupAccess.Target(person), RolePrincipalType.User, _page.Role.Role.Id, _page.Role.Role.Name,
            _page.Role.Role.IsPrivileged, scope.Id, scope.Name, HasPim && Eligible ? RoleGrantKind.Eligible : RoleGrantKind.Active,
            HasPim && Until is { } day ? new DateTimeOffset(day.Date.AddDays(1).AddSeconds(-1)) : null, HasPim && Justification.Trim().Length > 0 ? Justification.Trim() : null));
        Picked = null;
        Until = null;
        Justification = "";
    }

    // ---- The rows -------------------------------------------------------------------------------

    internal void Rebuild()
    {
        Rows.Clear();
        var role = _page.Role;
        var pending = _page.Pending.ToArray();
        var lastGlobal = role.Role.Id == DirectoryRoles.GlobalAdministrator
                         && role.Grants.Count(g => g.Kind == RoleGrantKind.Active && g.PrincipalType == RolePrincipalType.User) <= 1;
        foreach (var g in role.Grants)
        {
            var removing = pending.OfType<RemoveRoleAssignment>().FirstOrDefault(r => r.AssignmentId == g.Id);
            var locked = g.Activated ? $"Ends on its own{(g.End is { } end ? $" {end.ToLocalTime():d MMM h:mm tt}" : "")}"
                : lastGlobal && g.PrincipalType == RolePrincipalType.User ? "The only Global Administrator"
                : null;
            var target = g.PrincipalType switch
            {
                RolePrincipalType.Group => new ChangeTarget("group", g.PrincipalId, g.PrincipalName, g.PrincipalDetail),
                RolePrincipalType.App => new ChangeTarget("app", g.PrincipalId, g.PrincipalName, null),
                _ => new ChangeTarget("user", g.PrincipalId, g.PrincipalName, g.PrincipalDetail),
            };
            Rows.Add(new RoleGrantRow
            {
                Name = g.PrincipalName,
                Detail = g.PrincipalDetail,
                TypeText = RoleGrant.TypeName(g.PrincipalType),
                KindText = g.Activated ? "Activated" : g.Kind == RoleGrantKind.Eligible ? "Eligible" : "Active",
                ScopeText = g.ScopeId == "/" ? "Whole organization" : g.ScopeName,
                EndsText = g.End is { } until ? $"Until {until.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}" : "Permanent",
                StateText = removing is null ? null : "Removing",
                ActionText = !CanEdit || (locked is not null && removing is null) ? null : removing is null ? "Remove" : "Undo",
                ActionTip = removing is null ? "Take the role away (added to Pending changes)" : "Keep it (takes the change out of Pending changes)",
                Act = removing is not null ? () => _page.Pipeline!.Unstage(removing)
                    : () => _page.Pipeline!.Stage(new RemoveRoleAssignment(target, g.Id, role.Role.Name, role.Role.Id, g.ScopeId, g.Kind, g.ViaPim)),
                Locked = CanEdit ? locked : null,
                OpenAction = g.PrincipalType switch
                {
                    RolePrincipalType.User when _page.Index.User(g.PrincipalId) is { } u && _page.OpenUserAction is { } openUser => () => openUser(u),
                    RolePrincipalType.Group when _page.Index.Group(g.PrincipalId) is { } grp && _page.OpenGroupAction is { } openGroup => () => openGroup(grp),
                    _ => null,
                },
            });
        }
        foreach (var add in pending.OfType<AddRoleAssignment>())
            Rows.Add(new RoleGrantRow
            {
                Name = add.Principal.Name,
                Detail = add.Principal.Address,
                TypeText = RoleGrant.TypeName(add.PrincipalType),
                KindText = add.Kind == RoleGrantKind.Eligible ? "Eligible" : "Active",
                ScopeText = add.ScopeId == "/" ? "Whole organization" : add.ScopeName,
                EndsText = add.End is { } until ? $"Until {until.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}" : "Permanent",
                StateText = "Adding",
                ActionText = "Undo",
                ActionTip = "Don't give it (takes the change out of Pending changes)",
                Act = () => _page.Pipeline!.Unstage(add),
            });
        Summary = role.Grants.Count switch { 0 => "Nobody holds it", 1 => "1 assignment", var n => $"{n:N0} assignments" };
        OnPropertyChanged(nameof(HasNoRows));
        OnPropertyChanged(nameof(Scopes));
        OnPropertyChanged(nameof(HasUnits));
        OnPropertyChanged(nameof(PimNote));
        OnPropertyChanged(nameof(HasPimNote));
        OnPropertyChanged(nameof(HasPim));
    }
}

/// <summary>A role's permissions about one kind of thing ("Users": create, delete, password › update).</summary>
public sealed record PermissionGroup(string Title, IReadOnlyList<string> Items)
{
    public string CountText => Items.Count == 1 ? "1" : $"{Items.Count:N0}";
}

/// <summary>What the role lets holders do, grouped by what it acts on; a custom role can be edited or deleted here.</summary>
public sealed class RolePermissionsSection : RecordSection
{
    readonly RoleDetailViewModel _page;

    public RolePermissionsSection(RoleDetailViewModel page)
    {
        _page = page;
        Rebuild();
    }

    public override string Key => "permissions";
    public override string Title => "Permissions";
    public override string Icon => "ShieldKeyhole";

    public ObservableCollection<PermissionGroup> Groups { get; } = [];

    public bool HasNone => Groups.Count == 0;

    public RoleDetailViewModel Page => _page;

    /// <summary>
    /// "microsoft.directory/conditionalAccessPolicies/allProperties/read" →
    /// ("Conditional Access Policies", "all properties › read"): Microsoft's run-together words split.
    /// </summary>
    public static (string Group, string Item) Split(string action)
    {
        var parts = action.Split('/');
        if (parts.Length < 2)
            return ("Other", action);
        static IEnumerable<string> Words(string s) => System.Text.RegularExpressions.Regex.Replace(s.Replace('.', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        static string Title(string s) => string.Join(' ', Words(s).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        static string Lower(string s) => string.Join(' ', Words(s).Select(w => w.ToLowerInvariant()));
        var service = parts[0] == "microsoft.directory" ? "" : Title(parts[0].Replace("microsoft.", "", StringComparison.Ordinal)) + " › ";
        return (service + Title(parts[1]), parts.Length > 2 ? string.Join(" › ", parts[2..].Select(Lower)) : "everything");
    }

    internal void Rebuild()
    {
        Groups.Clear();
        foreach (var g in _page.Role.Role.AllowedActions.Select(Split).GroupBy(x => x.Group).OrderBy(g => g.Key, StringComparer.CurrentCulture))
            Groups.Add(new PermissionGroup(g.Key, [.. g.Select(x => x.Item).Distinct().Order(StringComparer.Ordinal)]));
        Summary = _page.Role.Role.AllowedActions.Count == 1 ? "1 permission" : $"{_page.Role.Role.AllowedActions.Count:N0} permissions";
        OnPropertyChanged(nameof(HasNone));
    }
}
