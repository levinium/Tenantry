using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's admin roles: a catalog of familiar built-in roles, the demo's admins holding Global or
/// User Administrator (the same ones their Security tab shows), a few apps and one group with roles,
/// and no PIM (the demo has no Entra ID P2). One list of assignments serves the Admin Roles area and
/// each person's page.
/// </summary>
public sealed partial class DemoUserRecords : IRoleRecords
{
    static readonly (string Id, string Name, string Description, bool Privileged, string[] Actions)[] Catalog =
    [
        ("62e90394-69f5-4237-9190-012177145e10", "Global Administrator", "Can manage all aspects of Microsoft Entra ID and Microsoft services that use Microsoft Entra identities.", true,
            ["microsoft.directory/users/allProperties/allTasks", "microsoft.directory/groups/allProperties/allTasks", "microsoft.directory/roleAssignments/allProperties/allTasks"]),
        ("fe930be7-5e62-47db-91af-98c3a49a38b1", "User Administrator", "Can manage all aspects of users and groups, including resetting passwords for limited admins.", true,
            ["microsoft.directory/users/create", "microsoft.directory/users/delete", "microsoft.directory/users/password/update", "microsoft.directory/groups/create"]),
        ("29232cdf-9323-42fd-ade2-1d097af3e4de", "Exchange Administrator", "Can manage all aspects of the Exchange product.", false,
            ["microsoft.office365.exchange/allEntities/basic/allTasks", "microsoft.directory/groups.unified/create"]),
        ("f28a1f50-f6e7-4571-818b-6a12f2af6b6c", "SharePoint Administrator", "Can manage all aspects of the SharePoint service.", false,
            ["microsoft.office365.sharePoint/allEntities/allTasks", "microsoft.directory/groups.unified/create"]),
        ("729827e3-9c14-49f7-bb1b-9608f156bbb8", "Helpdesk Administrator", "Can reset passwords for non-administrators and Helpdesk Administrators.", true,
            ["microsoft.directory/users/password/update", "microsoft.directory/users/invalidateAllRefreshTokens"]),
        ("3a2c62db-5318-420d-8d74-23affee5d9d5", "Intune Administrator", "Can manage all aspects of the Intune product.", false,
            ["microsoft.intune/allEntities/allTasks", "microsoft.directory/devices/delete"]),
        ("e8611ab8-c189-46e8-94e1-60213ab1f814", "Privileged Role Administrator", "Can manage role assignments in Microsoft Entra ID, and all aspects of Privileged Identity Management.", true,
            ["microsoft.directory/roleAssignments/allProperties/allTasks", "microsoft.directory/roleDefinitions/allProperties/allTasks"]),
        ("194ae4cb-b126-40b2-bd5b-6091b380977d", "Security Administrator", "Can read security information and reports, and manage configuration in Microsoft Entra ID and Office 365.", true,
            ["microsoft.directory/identityProtection/allProperties/update", "microsoft.directory/conditionalAccessPolicies/create"]),
        ("5d6b6bb7-de71-4623-b4af-96380a352509", "Security Reader", "Can read security information and reports in Microsoft Entra ID and Office 365.", true,
            ["microsoft.directory/identityProtection/allProperties/read", "microsoft.directory/auditLogs/allProperties/read"]),
        ("f2ef992c-3afb-46b9-b7cf-a126ee74c451", "Global Reader", "Can read everything that a Global Administrator can, but not update anything.", true,
            ["microsoft.directory/users/allProperties/read", "microsoft.directory/groups/allProperties/read"]),
        ("69091246-20e8-4a56-aa4d-066075b2a7a8", "Teams Administrator", "Can manage the Microsoft Teams service.", false,
            ["microsoft.teams/allEntities/allProperties/allTasks", "microsoft.directory/groups.unified/create"]),
        ("b0f54661-2d74-4c50-afa3-1ec803f12efe", "Billing Administrator", "Can perform common billing related tasks like updating payment information.", false,
            ["microsoft.commerce.billing/allEntities/allProperties/allTasks"]),
        ("9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3", "Application Administrator", "Can create and manage all aspects of app registrations and enterprise apps.", true,
            ["microsoft.directory/applications/create", "microsoft.directory/servicePrincipals/managePermissionGrantsForAll.microsoft-application-default"]),
        ("b1be1c3e-b65d-4f19-8427-f6fa0d97feb9", "Conditional Access Administrator", "Can manage Conditional Access capabilities.", true,
            ["microsoft.directory/conditionalAccessPolicies/create", "microsoft.directory/conditionalAccessPolicies/delete"]),
        ("4a5d8f65-41da-4de4-8968-e035b65339cf", "Reports Reader", "Can read sign-in and audit reports.", false,
            ["microsoft.directory/auditLogs/allProperties/read", "microsoft.directory/signInReports/allProperties/read"]),
        ("95e79109-95c0-4d8e-aee3-d01accf2d47b", "Guest Inviter", "Can invite guest users independent of the 'members can invite guests' setting.", false,
            ["microsoft.directory/users/inviteGuest"]),
        ("d29b2b05-8046-44ba-8758-1e26182fcf32", "Directory Synchronization Accounts", "Only used by Microsoft Entra Connect service.", true,
            ["microsoft.directory/organization/dirSync/update"]),
    ];

    List<AdminRole>? _roleCatalog;
    List<RoleGrant>? _roleGrants;

    async Task<List<RoleGrant>> GrantsAsync(CancellationToken cancellationToken)
    {
        if (_roleGrants is not null)
            return _roleGrants;
        _roleCatalog = [.. Catalog.Select(c => new AdminRole(c.Id, c.Name, c.Description, true, c.Privileged, true, c.Actions))];
        var grants = new List<RoleGrant>();
        foreach (var u in (await directory.GetUsersAsync(cancellationToken)).Where(u => u.Security?.IsAdmin == true))
        {
            // The same role the person's Security tab has always shown.
            var global = new Random(StableSeed(u.Id)).NextDouble() < 0.3;
            grants.Add(new RoleGrant($"ra-{u.Id}", global ? Catalog[0].Id : Catalog[1].Id, u.Id, u.DisplayName, u.UserPrincipalName, RolePrincipalType.User, "/", "Organization",
                RoleGrantKind.Active));
        }
        grants.Add(new RoleGrant("ra-app-sync", Catalog[16].Id, "sp-sync", "On-Premises Directory Sync", "11111111-2222-3333-4444-555555555555", RolePrincipalType.App,
            "/", "Organization", RoleGrantKind.Active));
        grants.Add(new RoleGrant("ra-app-backup", Catalog[3].Id, "sp-backup", "Cloud Backup Service", "22222222-3333-4444-5555-666666666666", RolePrincipalType.App,
            "/", "Organization", RoleGrantKind.Active));
        if ((await directory.GetGroupsAsync(cancellationToken)).FirstOrDefault(g => g.Kind == GroupKind.Security && !g.IsDynamic) is { } helpdesk)
            grants.Add(new RoleGrant("ra-group-helpdesk", Catalog[4].Id, helpdesk.Id, helpdesk.DisplayName, null, RolePrincipalType.Group, "/", "Organization",
                RoleGrantKind.Active));
        return _roleGrants = grants;
    }

    public async Task<RoleDirectory> GetRolesAsync(CancellationToken cancellationToken = default)
    {
        var grants = await GrantsAsync(cancellationToken);
        var byRole = grants.ToLookup(g => g.RoleId);
        return new RoleDirectory([.. _roleCatalog!.Select(r => new RoleSummary(r, [.. byRole[r.Id].OrderBy(g => g.PrincipalName, StringComparer.CurrentCulture)]))],
            [new AdminUnit("au-east", "East Office")], RoleDirectory.PimNeedsP2);
    }

    public Task<IReadOnlyList<ResourceAction>> GetResourceActionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ResourceAction>>(
        [
            .. Catalog.SelectMany(c => c.Actions.Select(a => (Action: a, c.Privileged))).DistinctBy(a => a.Action).OrderBy(a => a.Action, StringComparer.Ordinal)
                .Select(a => new ResourceAction(a.Action, $"Lets holders {a.Action[(a.Action.IndexOf('/') + 1)..].Replace('/', ' ')}", a.Privileged)),
        ]);

    /// <summary>A person's roles, for their Security tab.</summary>
    async Task<IReadOnlyList<RoleHeld>> HeldAsync(string userId, CancellationToken cancellationToken)
    {
        var grants = await GrantsAsync(cancellationToken);
        return [.. grants.Where(g => g.PrincipalId == userId && g.Kind == RoleGrantKind.Active)
            .Select(g => new RoleHeld(g.Id, _roleCatalog!.FirstOrDefault(r => r.Id == g.RoleId)?.Name ?? "A role", g.ScopeId == "/" ? "The whole organization" : g.ScopeName))];
    }

    /// <summary>Applies a role change to the demo's roles; false when it isn't one.</summary>
    public async Task<bool> ApplyRoleAsync(ChangeOp change, CancellationToken cancellationToken = default)
    {
        var grants = await GrantsAsync(cancellationToken);
        switch (change)
        {
            case AddRoleAssignment add:
                grants.Add(new RoleGrant($"ra-{Guid.NewGuid():N}", add.RoleId, add.Principal.Id, add.Principal.Name, add.Principal.Address, add.PrincipalType,
                    add.ScopeId, add.ScopeName, add.Kind, DateTimeOffset.UtcNow, add.End, add.ViaPim));
                return true;
            case RemoveRoleAssignment remove:
                grants.RemoveAll(g => g.Id == remove.AssignmentId);
                return true;
            case CreateCustomRole create:
                _roleCatalog!.Add(new AdminRole(Guid.NewGuid().ToString(), create.Role.Name, create.Description, false, false, true, create.Actions));
                return true;
            case UpdateCustomRole update:
                var index = _roleCatalog!.FindIndex(r => r.Id == update.Role.Id);
                if (index >= 0)
                    _roleCatalog[index] = _roleCatalog[index] with { Name = update.NewName, Description = update.NewDescription, AllowedActions = update.NewActions };
                return true;
            case DeleteCustomRole delete:
                _roleCatalog!.RemoveAll(r => r.Id == delete.Role.Id);
                return true;
            default:
                return false;
        }
    }
}
