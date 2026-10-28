using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.ViewModels;

/// <summary>
/// The Security &amp; Compliance area: one tab with a page per list. Admin Roles (8a) is read live
/// from Entra when first shown, on Refresh, and after role changes are applied.
/// </summary>
public sealed partial class SecurityAreaViewModel : TabViewModel
{
    readonly IRoleRecords? _roles;
    bool _rolesRead;

    public static IReadOnlyList<MailSecurityPage> Pages { get; } =
    [
        new("roles", "Admin Roles", "PersonKey"),
    ];

    /// <param name="openRole">Opens a role's page.</param>
    /// <param name="newRole">Opens the custom-role editor for a new role; null when the admin can't edit.</param>
    public SecurityAreaViewModel(IRoleRecords? roles, ChangePipeline? pipeline, IListStateStore? listState, Func<DateTimeOffset> now,
        Action<RoleSummary> openRole, Action? newRole, string? cantEditReason)
    {
        _roles = roles;
        Roles = DataListViewModel.Create(RoleList.Definition(r => new Records.RolePreviewViewModel(r, openRole)),
            LoadRolesAsync, row => openRole((RoleSummary)row), listState, now);
        Roles.EmptyText = "No roles match.";
        Roles.PrimaryAction = newRole is not null
            ? new ListPrimaryAction("New Custom Role", "Add", true, "Make a role with just the permissions you choose (needs Microsoft Entra ID P1)", newRole)
            : new ListPrimaryAction("New Custom Role", "Add", false, cantEditReason ?? "This tenant can't be edited");
        if (pipeline is not null)
            pipeline.Applied += (_, applied) =>
            {
                if (_rolesRead && applied.Any(c => c is AddRoleAssignment or RemoveRoleAssignment or CustomRoleChange))
                    _ = Roles.ReloadAsync(busyText: "Reading admin roles again…");
            };
        SelectedPage = Pages[0];
    }

    public override string Title => "Security & Compliance";

    public override bool CanClose => false;

    public DataListViewModel Roles { get; }

    /// <summary>The roles as last read, with the administrative units and PIM's availability.</summary>
    public RoleDirectory? Directory { get; private set; }

    /// <summary>Raised after each read, so open role pages can show it.</summary>
    public event EventHandler? RolesRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRoles))]
    public partial MailSecurityPage SelectedPage { get; set; }

    public bool ShowRoles => SelectedPage.Key == "roles";

    /// <summary>Reads the roles the first time the area is shown.</summary>
    public void Shown()
    {
        if (ShowRoles && !_rolesRead)
        {
            _rolesRead = true;
            _ = Roles.LoadAsync();
        }
    }

    partial void OnSelectedPageChanged(MailSecurityPage value) => Shown();

    /// <summary>Why roles can't be listed, or what PIM can't show here, above the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRolesNote))]
    public partial string? RolesNote { get; private set; }

    public bool HasRolesNote => RolesNote is not null;

    async Task<IReadOnlyList<RoleSummary>> LoadRolesAsync(CancellationToken cancellationToken)
    {
        if (_roles is null)
        {
            RolesNote = "Admin roles aren't available for this tenant.";
            return [];
        }
        try
        {
            Directory = await _roles.GetRolesAsync(cancellationToken);
            RolesNote = Directory.PimUnavailable;
            RolesRead?.Invoke(this, EventArgs.Empty);
            return Directory.Roles;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            RolesNote = $"Couldn't read admin roles: {Friendly.Message(e)}";
            return [];
        }
    }

    [RelayCommand]
    Task RefreshRoles() => Roles.ReloadAsync(busyText: "Reading admin roles from Entra…");
}
