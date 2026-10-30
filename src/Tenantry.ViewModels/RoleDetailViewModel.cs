using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>
/// An admin role's page (8a): what it is and how privileged, then its Assignments (who holds it,
/// for where and how long; add and remove) and its Permissions (what it allows; a custom role can be
/// edited or deleted). Changes go to Pending changes; once applied the page shows the role as read again.
/// </summary>
public sealed partial class RoleDetailViewModel : TabViewModel, IDisposable
{
    readonly ChangePipeline? _pipeline;

    /// <param name="editRole">Opens the custom-role editor on this role.</param>
    public RoleDetailViewModel(RoleSummary role, RoleDirectory directory, ChangePipeline? pipeline, Availability canEdit, IDirectoryIndex? index = null,
        Action<UserSummary>? openUser = null, Action<GroupSummary>? openGroup = null, Action<AdminRole>? editRole = null, IRoleRecords? records = null)
    {
        Records = records;
        Role = role;
        Directory = directory;
        _pipeline = pipeline;
        Index = index ?? EmptyIndex.Instance;
        OpenUserAction = openUser;
        OpenGroupAction = openGroup;
        EditRoleAction = editRole;
        CanEdit = pipeline is not null && canEdit.IsAvailable;
        EditReason = pipeline is null ? "This tenant can't be edited" : canEdit.Reason ?? "";
        Assignments = new RoleAssignmentsSection(this);
        Settings = new RoleSettingsSection(this);
        Sections = [Assignments, new RolePermissionsSection(this), Settings];
        SelectedSection = Sections[0];
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public RoleSummary Role { get; private set; }

    internal RoleDirectory Directory { get; private set; }
    internal IDirectoryIndex Index { get; }
    internal Action<UserSummary>? OpenUserAction { get; }
    internal Action<GroupSummary>? OpenGroupAction { get; }
    internal Action<AdminRole>? EditRoleAction { get; }
    internal ChangePipeline? Pipeline => _pipeline;
    internal IRoleRecords? Records { get; }

    /// <summary>The signed-in admin's sign-in name: taking a privileged role from yourself needs it typed.</summary>
    public string? Me { get; init; }

    public RoleSettingsSection Settings { get; }

    public bool CanEdit { get; }
    public string EditReason { get; }

    public override string Title => Role.Role.Name;

    public string Description => Role.Role.Description;
    public bool IsPrivileged => Role.Role.IsPrivileged;
    public string TypeText => Role.Role.IsBuiltIn ? RoleFields.BuiltIn : RoleFields.Custom;
    public bool IsCustom => !Role.Role.IsBuiltIn;

    public string CountsText => string.Join(" · ", new[]
    {
        Role.Active == 1 ? "1 active assignment" : $"{Role.Active:N0} active assignments",
        Role.Eligible > 0 ? $"{Role.Eligible:N0} eligible" : null,
        Role.PermanentPeople > 0 ? $"{Role.PermanentPeople:N0} permanent" : null,
    }.OfType<string>());

    public RoleAssignmentsSection Assignments { get; }

    public IReadOnlyList<RecordSection> Sections { get; }

    [ObservableProperty]
    public partial RecordSection? SelectedSection { get; set; }

    partial void OnSelectedSectionChanged(RecordSection? value) => _ = value?.LoadAsync();

    /// <summary>What's waiting in Pending changes for this role.</summary>
    public string? PendingText
    {
        get
        {
            var labels = Pending.Select(p => p switch
            {
                AddRoleAssignment a => $"give it to {a.Principal.Name}",
                ExtendRoleAssignment x => $"{x.Principal.Name} until {x.NewEnd:d MMM yyyy}",
                SetRolePolicyRule r => $"{r.SettingLabel.ToLowerInvariant()} → {r.After}",
                RemoveRoleAssignment r => $"take it from {r.User.Name}",
                _ => $"{p.Label.ToLowerInvariant()} → {p.After}",
            }).ToArray();
            return labels.Length == 0 ? null : "Pending: " + string.Join(", ", labels);
        }
    }

    public bool HasPending => PendingText is not null;

    /// <summary>Pending changes to this role: its assignments, or the role itself.</summary>
    internal IEnumerable<ChangeOp> Pending => _pipeline?.Pending.Where(p => p switch
    {
        AddRoleAssignment a => a.RoleId == Role.Role.Id,
        RemoveRoleAssignment r => r.RoleId == Role.Role.Id || Role.Grants.Any(g => g.Id == r.AssignmentId),
        ExtendRoleAssignment x => x.RoleId == Role.Role.Id,
        SetRolePolicyRule r => r.Role.Id == Role.Role.Id,
        CustomRoleChange c => c.Role.Id == Role.Role.Id,
        _ => false,
    }) ?? [];

    void OnPendingChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(HasPending));
        Assignments.Rebuild();
    }

    [RelayCommand]
    void EditRole() => EditRoleAction?.Invoke(Role.Role);

    public bool CanEditRole => CanEdit && IsCustom && EditRoleAction is not null;

    /// <summary>Stages deleting this custom role (Entra refuses while anyone holds it).</summary>
    [RelayCommand]
    void DeleteRole()
    {
        if (!CanEditRole)
            return;
        _pipeline!.Stage(new DeleteCustomRole(new ChangeTarget("role", Role.Role.Id, Role.Role.Name, null),
            Role.Grants.Count == 0 ? "Held by nobody" : $"Held by {Role.Grants.Count} (remove them first)"));
    }

    [RelayCommand]
    void OpenInAdminCenter() => Platform.OpenUrl(
        $"https://entra.microsoft.com/#view/Microsoft_AAD_IAM/RoleMenuBlade/~/RoleMembers/objectId/{Role.Role.Id}/roleName/{Uri.EscapeDataString(Role.Role.Name)}");

    /// <summary>Shows the role as read again (after a refresh or applied changes).</summary>
    public void Update(RoleSummary role, RoleDirectory directory)
    {
        Role = role;
        Directory = directory;
        foreach (var name in new[] { nameof(Role), nameof(Title), nameof(Description), nameof(IsPrivileged), nameof(TypeText), nameof(IsCustom), nameof(CountsText),
                     nameof(CanEditRole), nameof(PendingText), nameof(HasPending) })
            OnPropertyChanged(name);
        Assignments.Rebuild();
        foreach (var section in Sections.OfType<RolePermissionsSection>())
            section.Rebuild();
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
