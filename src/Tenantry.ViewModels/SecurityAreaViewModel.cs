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
        new("ca", "Conditional Access", "ShieldLock"),
        new("whatif", "What If", "QuestionCircle"),
        new("roles", "Admin Roles", "PersonKey"),
        new("myroles", "My Roles", "PersonStar"),
        new("approvals", "Approvals", "ApprovalsApp"),
    ];

    /// <param name="openRole">Opens a role's page.</param>
    /// <param name="newRole">Opens the custom-role editor for a new role; null when the admin can't edit.</param>
    public SecurityAreaViewModel(IRoleRecords? roles, ChangePipeline? pipeline, IListStateStore? listState, Func<DateTimeOffset> now,
        Action<RoleSummary> openRole, Action? newRole, string? cantEditReason, string? me = null, IDirectoryIndex? index = null,
        IConditionalAccessRecords? ca = null, Action<CaPolicyRow>? openPolicy = null, Action? newPolicy = null, string? cantEditPolicies = null)
    {
        _roles = roles;
        _ca = ca;
        Policies = DataListViewModel.Create(CaList.Definition(p => new Records.CaPolicyPreviewViewModel(p, openPolicy)),
            LoadPoliciesAsync, row => openPolicy?.Invoke((CaPolicyRow)row), listState, now);
        Policies.EmptyText = "No policies match.";
        Policies.PrimaryAction = newPolicy is not null
            ? new ListPrimaryAction("New Policy", "Add", true, "Make a policy, blank or from one of Microsoft's templates; it starts in report-only", newPolicy)
            : new ListPrimaryAction("New Policy", "Add", false, cantEditPolicies ?? "This tenant can't be edited");
        WhatIf = new Records.WhatIfViewModel(ca, index ?? EmptyIndex.Instance);
        if (pipeline is not null)
            pipeline.Applied += (_, applied) =>
            {
                if (_policiesRead && applied.Any(c => c is CaPolicyChange))
                    _ = Policies.ReloadAsync(busyText: "Reading policies again…");
            };
        MyRoles = new Records.MyRolesViewModel(roles, pipeline, me, index ?? EmptyIndex.Instance);
        Approvals = new Records.ApprovalsViewModel(roles, pipeline);
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

    readonly IConditionalAccessRecords? _ca;
    bool _policiesRead;

    /// <summary>Conditional Access policies (8b).</summary>
    public DataListViewModel Policies { get; }

    /// <summary>Which policies a sign-in would meet.</summary>
    public Records.WhatIfViewModel WhatIf { get; }

    /// <summary>The policies as last read, with the names their IDs stand for.</summary>
    public CaDirectory? CaDirectory { get; private set; }

    /// <summary>Raised after each read of the policies, so open policy pages can show it.</summary>
    public event EventHandler? PoliciesRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCaNote))]
    public partial string? CaNote { get; private set; }

    public bool HasCaNote => CaNote is not null;

    async Task<IReadOnlyList<CaPolicyRow>> LoadPoliciesAsync(CancellationToken cancellationToken)
    {
        if (_ca is null)
        {
            CaNote = "Conditional Access isn't available for this tenant.";
            return [];
        }
        try
        {
            CaDirectory = await _ca.GetPoliciesAsync(cancellationToken);
            CaNote = null;
            PoliciesRead?.Invoke(this, EventArgs.Empty);
            return [.. CaDirectory.Policies.Select(p => CaPolicyRow.Of(p, CaDirectory.Names))];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            CaNote = $"Couldn't read Conditional Access: {Friendly.Message(e)}";
            return [];
        }
    }

    [RelayCommand]
    Task RefreshPolicies() => Policies.ReloadAsync(busyText: "Reading Conditional Access from Entra…");

    /// <summary>The signed-in admin's own eligible and switched-on roles (PIM).</summary>
    public Records.MyRolesViewModel MyRoles { get; }

    /// <summary>Activation requests waiting for the signed-in admin (PIM).</summary>
    public Records.ApprovalsViewModel Approvals { get; }

    /// <summary>The roles as last read, with the administrative units and PIM's availability.</summary>
    public RoleDirectory? Directory { get; private set; }

    /// <summary>Raised after each read, so open role pages can show it.</summary>
    public event EventHandler? RolesRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRoles), nameof(ShowMyRoles), nameof(ShowApprovals), nameof(ShowPolicies), nameof(ShowWhatIf))]
    public partial MailSecurityPage SelectedPage { get; set; }

    public bool ShowRoles => SelectedPage.Key == "roles";
    public bool ShowPolicies => SelectedPage.Key == "ca";
    public bool ShowWhatIf => SelectedPage.Key == "whatif";
    public bool ShowMyRoles => SelectedPage.Key == "myroles";
    public bool ShowApprovals => SelectedPage.Key == "approvals";

    /// <summary>Reads the roles the first time the area is shown.</summary>
    public void Shown()
    {
        if (ShowRoles && !_rolesRead)
        {
            _rolesRead = true;
            _ = Roles.LoadAsync();
        }
        if (ShowPolicies && !_policiesRead)
        {
            _policiesRead = true;
            _ = Policies.LoadAsync();
        }
        if (ShowMyRoles)
            MyRoles.Shown();
        if (ShowApprovals)
            Approvals.Shown();
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
