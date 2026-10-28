using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>A directory role (built-in or custom) and what it allows.</summary>
/// <param name="Id">The role definition's ID (a built-in role's is its template ID).</param>
/// <param name="IsPrivileged">Microsoft marks roles that can change security or reach every user's data as privileged.</param>
/// <param name="AllowedActions">What it lets holders do ("microsoft.directory/users/password/update").</param>
public sealed record AdminRole(string Id, string Name, string Description, bool IsBuiltIn, bool IsPrivileged, bool IsEnabled, IReadOnlyList<string> AllowedActions);

/// <summary>Who holds a role: a person, a role-assignable group, or an app (service principal).</summary>
public enum RolePrincipalType
{
    User,
    Group,
    App,
}

/// <summary>Active (holds it now) or eligible (can activate it through PIM).</summary>
public enum RoleGrantKind
{
    Active,
    Eligible,
}

/// <summary>
/// One assignment of a role: who, where (the organization or an administrative unit), and for how
/// long. Without Entra ID P2 every assignment is active and permanent.
/// </summary>
/// <param name="Id">The assignment's ID (roleAssignments), or its PIM schedule's ID.</param>
/// <param name="PrincipalDetail">A person's sign-in name, an app's app ID.</param>
/// <param name="ScopeId">"/" for the whole organization, "/administrativeUnits/{id}" for one unit.</param>
/// <param name="End">When it ends; null when it's permanent.</param>
/// <param name="ViaPim">Made through Privileged Identity Management (its own schedule, removed through PIM).</param>
/// <param name="Activated">An eligible role someone has switched on for a while (PIM).</param>
public sealed record RoleGrant(
    string Id,
    string RoleId,
    string PrincipalId,
    string PrincipalName,
    string? PrincipalDetail,
    RolePrincipalType PrincipalType,
    string ScopeId,
    string ScopeName,
    RoleGrantKind Kind,
    DateTimeOffset? Start = null,
    DateTimeOffset? End = null,
    bool ViaPim = false,
    bool Activated = false)
{
    public bool IsPermanent => End is null;

    public static string TypeName(RolePrincipalType type) => type switch
    {
        RolePrincipalType.Group => "Group",
        RolePrincipalType.App => "App",
        _ => "Person",
    };
}

/// <summary>A role with everyone who holds it, as the Admin Roles list shows it.</summary>
public sealed record RoleSummary(AdminRole Role, IReadOnlyList<RoleGrant> Grants)
{
    public int Active => Grants.Count(g => g.Kind == RoleGrantKind.Active);
    public int Eligible => Grants.Count(g => g.Kind == RoleGrantKind.Eligible);

    /// <summary>People who hold it actively with no end: what a privileged-access review looks for.</summary>
    public int PermanentPeople => Grants.Count(g => g.Kind == RoleGrantKind.Active && g.IsPermanent && g.PrincipalType == RolePrincipalType.User);
}

/// <summary>An administrative unit: a slice of the directory a role can be limited to.</summary>
public sealed record AdminUnit(string Id, string Name);

/// <summary>One permission a custom role can include.</summary>
public sealed record ResourceAction(string Name, string? Description, bool IsPrivileged);

/// <summary>Every role and its holders, the administrative units, and why PIM can't be read here (or null).</summary>
public sealed record RoleDirectory(IReadOnlyList<RoleSummary> Roles, IReadOnlyList<AdminUnit> Units, string? PimUnavailable)
{
    /// <summary>What a PIM read says without Entra ID P2.</summary>
    public const string PimNeedsP2 = "Privileged Identity Management (eligible and time-limited roles, switching roles on, approvals and role settings) needs Microsoft Entra ID P2.";
}

/// <summary>Admin roles, read live from Entra (they aren't kept in the local copy).</summary>
public interface IRoleRecords
{
    Task<RoleDirectory> GetRolesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every permission a custom role can be given (Entra's microsoft.directory actions).</summary>
    Task<IReadOnlyList<ResourceAction>> GetResourceActionsAsync(CancellationToken cancellationToken = default);

    /// <summary>The role's PIM settings; unavailable without Entra ID P2.</summary>
    Task<Available<RoleSettings>> GetRoleSettingsAsync(string roleId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Available<RoleSettings>.Because(RoleDirectory.PimNeedsP2));

    /// <summary>The signed-in admin's own eligible and activated roles (PIM).</summary>
    Task<Available<IReadOnlyList<RoleGrant>>> GetMyRolesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Available<IReadOnlyList<RoleGrant>>.Because(RoleDirectory.PimNeedsP2));

    /// <summary>Activation requests waiting for the signed-in admin's approval (PIM).</summary>
    Task<Available<IReadOnlyList<RoleRequest>>> GetApprovalsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Available<IReadOnlyList<RoleRequest>>.Because(RoleDirectory.PimNeedsP2));
}

/// <summary>The Admin Roles list's fields.</summary>
public static class RoleFields
{
    public const string Name = "name";
    public const string Type = "type";
    public const string Privileged = "privileged";
    public const string Active = "active";
    public const string Eligible = "eligible";
    public const string Permanent = "permanent";
    public const string Holders = "holders";

    public const string BuiltIn = "Built-In";
    public const string Custom = "Custom";
    public const string Yes = "Privileged";

    public static IReadOnlyList<Field<RoleSummary>> All { get; } =
    [
        new() { Key = Name, Label = "Role", Kind = FieldKind.Text, Searchable = true, Aliases = ["role"], Text = r => r.Role.Name },
        new() { Key = Type, Label = "Type", Kind = FieldKind.Choice, ChoiceOrder = [BuiltIn, Custom], Text = r => r.Role.IsBuiltIn ? BuiltIn : Custom },
        new() { Key = Privileged, Label = "Privileged", Kind = FieldKind.Choice, EmptyLabel = "No", Text = r => r.Role.IsPrivileged ? Yes : null },
        new() { Key = Active, Label = "Active Assignments", Kind = FieldKind.Number, Aliases = ["assigned"], Number = r => r.Active },
        new() { Key = Eligible, Label = "Eligible Assignments", Kind = FieldKind.Number, Number = r => r.Eligible },
        new() { Key = Permanent, Label = "Permanent People", Kind = FieldKind.Number, Number = r => r.PermanentPeople },
        new()
        {
            Key = Holders, Label = "Held By", Kind = FieldKind.Text, Searchable = true, Aliases = ["who", "person", "member"],
            Text = r => string.Join(", ", r.Grants.Select(g => g.PrincipalName).Distinct()),
        },
    ];
}
