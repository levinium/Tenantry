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
        new("locations", "Named Locations", "Location"),
        new("strengths", "Authentication Strengths", "ShieldKeyhole"),
        new("appregs", "App Registrations", "AppFolder"),
        new("enterprise", "Enterprise Apps", "Apps"),
        new("deletedapps", "Deleted Apps", "Delete"),
        new("roles", "Admin Roles", "PersonKey"),
        new("myroles", "My Roles", "PersonStar"),
        new("approvals", "Approvals", "ApprovalsApp"),
    ];

    /// <param name="openRole">Opens a role's page.</param>
    /// <param name="newRole">Opens the custom-role editor for a new role; null when the admin can't edit.</param>
    public SecurityAreaViewModel(IRoleRecords? roles, ChangePipeline? pipeline, IListStateStore? listState, Func<DateTimeOffset> now,
        Action<RoleSummary> openRole, Action? newRole, string? cantEditReason, string? me = null, IDirectoryIndex? index = null,
        IConditionalAccessRecords? ca = null, Action<CaPolicyRow>? openPolicy = null, Action? newPolicy = null, string? cantEditPolicies = null,
        IAppRecords? apps = null, Action<AppRegistration>? openRegistration = null, Action<EnterpriseApp>? openEnterprise = null, Action? newApp = null, string? cantEditApps = null)
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
        NamedLocations = new Records.LocationsViewModel(this, pipeline, newPolicy is not null, cantEditPolicies);
        AuthStrengths = new Records.StrengthsViewModel(this);
        _apps = apps;
        Registrations = DataListViewModel.Create(AppList.Registrations(a => RegistrationPreview(a, now(), openRegistration), now),
            async ct => (await ReadAppsAsync(ct))?.Registrations ?? [], row => openRegistration?.Invoke((AppRegistration)row), listState, now);
        Registrations.EmptyText = "No app registrations match.";
        EnterpriseApps = DataListViewModel.Create(AppList.Enterprise(a => EnterprisePreview(a, openEnterprise)),
            async ct => (await ReadAppsAsync(ct))?.EnterpriseApps ?? [], row => openEnterprise?.Invoke((EnterpriseApp)row), listState, now);
        EnterpriseApps.EmptyText = "No apps match.";
        Registrations.PrimaryAction = newApp is not null
            ? new ListPrimaryAction("New App Registration", "Add", true, "Register an app (with its enterprise app) so it can sign people in or call Microsoft Graph", newApp)
            : new ListPrimaryAction("New App Registration", "Add", false, cantEditApps ?? "This tenant can't be edited");
        DeletedAppsSection = Records.DeletedApps.Section(apps, pipeline, newApp is not null);
        if (pipeline is not null)
        {
            pipeline.Changed += (_, _) => DeletedAppsSection.Rebuild();
            pipeline.Applied += (_, applied) =>
            {
                if (!applied.Any(c => c is AppChange))
                    return;
                if (_appsShown)
                    _ = RefreshApps();
                DeletedAppsSection.Rebuild();
            };
        }
        if (pipeline is not null)
            pipeline.Applied += (_, applied) =>
            {
                if (_policiesRead && applied.Any(c => c is CaPolicyChange or NamedLocationChange))
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

    /// <summary>Named locations (8b-3).</summary>
    public Records.LocationsViewModel NamedLocations { get; }

    /// <summary>Authentication strengths (8b-3).</summary>
    public Records.StrengthsViewModel AuthStrengths { get; }

    /// <summary>Reads the policies (with their locations and strengths) the first time any page needs them.</summary>
    public void ReadPolicies()
    {
        if (_policiesRead)
            return;
        _policiesRead = true;
        _ = Policies.LoadAsync();
    }

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
    [NotifyPropertyChangedFor(nameof(ShowRoles), nameof(ShowMyRoles), nameof(ShowApprovals), nameof(ShowPolicies), nameof(ShowWhatIf), nameof(ShowLocations),
        nameof(ShowStrengths), nameof(ShowRegistrations), nameof(ShowEnterpriseApps), nameof(ShowDeletedApps))]
    public partial MailSecurityPage SelectedPage { get; set; }

    // ---- Apps (8c) -------------------------------------------------------------------------------

    readonly IAppRecords? _apps;
    Task<AppDirectory?>? _appsRead;
    bool _appsShown;

    /// <summary>App registrations (8c).</summary>
    public DataListViewModel Registrations { get; }

    /// <summary>Enterprise apps (service principals) (8c).</summary>
    public DataListViewModel EnterpriseApps { get; }

    /// <summary>Deleted app registrations, with Restore and Delete Permanently.</summary>
    public Records.AppSection DeletedAppsSection { get; }

    /// <summary>The apps as last read, for open app pages.</summary>
    public AppDirectory? Apps { get; private set; }

    /// <summary>Raised after each read of the apps.</summary>
    public event EventHandler? AppsRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppsNote))]
    public partial string? AppsNote { get; private set; }

    public bool HasAppsNote => AppsNote is not null;

    /// <summary>One read serves both lists; Refresh starts a new one.</summary>
    Task<AppDirectory?> ReadAppsAsync(CancellationToken cancellationToken) => _appsRead ??= ReadAppsNowAsync(cancellationToken);

    async Task<AppDirectory?> ReadAppsNowAsync(CancellationToken cancellationToken)
    {
        if (_apps is null)
        {
            AppsNote = "Apps aren't available for this tenant.";
            return null;
        }
        try
        {
            Apps = await _apps.GetAppsAsync(cancellationToken);
            AppsNote = null;
            AppsRead?.Invoke(this, EventArgs.Empty);
            return Apps;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppsNote = $"Couldn't read apps: {Friendly.Message(e)}";
            _appsRead = null;
            return null;
        }
    }

    [RelayCommand]
    async Task RefreshApps()
    {
        _appsRead = null;
        await Task.WhenAll(Registrations.ReloadAsync(busyText: "Reading apps from Entra…"), EnterpriseApps.ReloadAsync(busyText: "Reading apps from Entra…"));
    }

    static Records.AppPreviewViewModel RegistrationPreview(AppRegistration a, DateTimeOffset now, Action<AppRegistration>? open)
    {
        var state = a.CredentialState(now);
        var badge = AppList.CredentialBadge(state);
        return new Records.AppPreviewViewModel(a.Name, a.AppId,
            badge.Text.Length > 0 ? [new Records.HealthChip($"Credentials {badge.Text}", badge.Kind, "Its client secrets and certificates")] : [],
            [
                new("Who Can Sign In", AppRegistration.AudienceText(a.Audience)),
                new("Secrets", $"{a.Credentials.Count(c => c.IsSecret):N0}"),
                new("Certificates", $"{a.Credentials.Count(c => !c.IsSecret):N0}"),
                new("Next Expiry", a.NextExpiry(now) is { } end ? end.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture) : "None"),
                new("API Permissions", $"{a.Requested.Count:N0}"),
                new("Owners", a.Owners.Count == 0 ? "Nobody" : string.Join(", ", a.Owners.Select(o => o.Name))),
            ],
            "Open App", open is null ? null : () => open(a));
    }

    static Records.AppPreviewViewModel EnterprisePreview(EnterpriseApp a, Action<EnterpriseApp>? open) =>
        new(a.Name, a.Publisher is { } p ? $"{p}{(a.IsVerifiedPublisher ? " (verified)" : "")}" : a.AppId,
            [
                .. a.IsEnabled ? Array.Empty<Records.HealthChip>() : [new Records.HealthChip("Sign-In Off", BadgeKind.Warning, "Nobody can sign in to it")],
                new Records.HealthChip(EnterpriseApp.SourceText(a.Source), a.Source == AppSource.ThirdParty ? BadgeKind.Info : BadgeKind.Neutral, "Who makes it"),
            ],
            [
                new("Application ID", a.AppId),
                new("Assignment", a.AssignmentRequired ? "Required" : "Not required"),
                new("Visible to People", a.IsVisible ? "Yes" : "No"),
                new("Single Sign-On", EnterpriseApp.SsoText(a.SsoMode) ?? "None"),
                new("Type", a.Type),
            ],
            "Open App", open is null ? null : () => open(a));

    public bool ShowRoles => SelectedPage.Key == "roles";
    public bool ShowPolicies => SelectedPage.Key == "ca";
    public bool ShowWhatIf => SelectedPage.Key == "whatif";
    public bool ShowLocations => SelectedPage.Key == "locations";
    public bool ShowStrengths => SelectedPage.Key == "strengths";
    public bool ShowRegistrations => SelectedPage.Key == "appregs";
    public bool ShowEnterpriseApps => SelectedPage.Key == "enterprise";
    public bool ShowDeletedApps => SelectedPage.Key == "deletedapps";
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
        if (ShowPolicies || ShowLocations || ShowStrengths)
            ReadPolicies();
        if ((ShowRegistrations || ShowEnterpriseApps) && !_appsShown)
        {
            _appsShown = true;
            _ = Registrations.LoadAsync();
            _ = EnterpriseApps.LoadAsync();
        }
        if (ShowDeletedApps)
            _ = DeletedAppsSection.LoadAsync();
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
