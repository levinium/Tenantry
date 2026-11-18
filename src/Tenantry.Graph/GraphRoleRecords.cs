using System.Net;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Admin roles (8a), read live from Entra. Facts checked live:
/// <list type="bullet">
/// <item>Only beta role definitions say whether a role is privileged (<c>isPrivileged</c>), and beta
/// refuses a page smaller than 20.</item>
/// <item>Every PIM read (schedules, instances, policies, requests) answers 400
/// <c>AadPremiumLicenseRequired</c> without Entra ID P2: active assignments then come from
/// <c>roleAssignments</c> alone, and are all permanent.</item>
/// <item>Custom roles pick from the <c>microsoft.directory</c> resource actions (beta; about 800).</item>
/// </list>
/// </summary>
public sealed partial class GraphUserRecords : IRoleRecords
{
    /// <summary>
    /// PIM isn't there: 400 AadPremiumLicenseRequired from most reads, and 403 "The tenant has not
    /// onboarded to PIM" from the approver and own-role reads (both seen live without Entra ID P2).
    /// </summary>
    static bool NeedsPremium(GraphException e) =>
        e.Status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
        && ((e.Code ?? "").Contains("Premium", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("onboarded to PIM", StringComparison.OrdinalIgnoreCase));

    /// <summary>Every row of a collection, for reads that run side by side.</summary>
    async Task<List<JsonElement>> AllRowsAsync(string path, CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement>();
        await foreach (var row in graph.GetAllAsync(path, cancellationToken))
            rows.Add(row.Clone());
        return rows;
    }

    /// <summary>
    /// Role assignments: with PIM, the instances hold every active assignment (made through PIM or
    /// not) with its end, and the eligible ones; without Entra ID P2, roleAssignments alone.
    /// </summary>
    async Task<(List<(JsonElement Row, RoleGrantKind Kind, bool ViaPim)> Rows, string? PimUnavailable)> AssignmentRowsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var active = AllRowsAsync("roleManagement/directory/roleAssignmentScheduleInstances?$expand=principal", cancellationToken);
            var eligible = AllRowsAsync("roleManagement/directory/roleEligibilityScheduleInstances?$expand=principal", cancellationToken);
            return ([.. (await active).Select(a => (a, RoleGrantKind.Active, true)), .. (await eligible).Select(a => (a, RoleGrantKind.Eligible, true))], null);
        }
        catch (GraphException e) when (NeedsPremium(e))
        {
            var plain = await AllRowsAsync("roleManagement/directory/roleAssignments?$expand=principal", cancellationToken);
            return ([.. plain.Select(a => (a, RoleGrantKind.Active, false))], RoleDirectory.PimNeedsP2);
        }
    }

    public async Task<RoleDirectory> GetRolesAsync(CancellationToken cancellationToken = default)
    {
        // Definitions, administrative units and assignments don't depend on each other: read them together.
        var definitionsRead = AllRowsAsync($"{Beta}roleManagement/directory/roleDefinitions?$select=id,displayName,description,isBuiltIn,isEnabled,isPrivileged,rolePermissions",
            cancellationToken);
        var unitsRead = AllRowsAsync("directory/administrativeUnits?$select=id,displayName", cancellationToken);
        var assignmentsRead = AssignmentRowsAsync(cancellationToken);

        var definitions = new List<AdminRole>();
        foreach (var d in await definitionsRead)
            definitions.Add(new AdminRole(Text(d, "id") ?? "", Text(d, "displayName") ?? "A role", Text(d, "description") ?? "",
                Flag(d, "isBuiltIn") == true, Flag(d, "isPrivileged") == true, !d.TryGetProperty("isEnabled", out var on) || on.ValueKind != JsonValueKind.False,
                d.TryGetProperty("rolePermissions", out var perms) && perms.ValueKind == JsonValueKind.Array
                    ? [.. perms.EnumerateArray().SelectMany(p => p.TryGetProperty("allowedResourceActions", out var a) ? a.EnumerateArray().Select(x => x.GetString() ?? "") : [])]
                    : []));

        var units = new List<AdminUnit>();
        foreach (var u in await unitsRead)
            units.Add(new AdminUnit(Text(u, "id") ?? "", Text(u, "displayName") ?? "An administrative unit"));
        var unitNames = units.ToDictionary(u => u.Id, u => u.Name, StringComparer.OrdinalIgnoreCase);
        string ScopeName(string scope) => scope == "/" ? "Organization"
            : scope.StartsWith("/administrativeUnits/", StringComparison.OrdinalIgnoreCase) && unitNames.TryGetValue(scope[21..], out var unit) ? unit
            : "One App";

        var grants = new List<RoleGrant>();
        RoleGrant Grant(JsonElement a, RoleGrantKind kind, bool viaPim)
        {
            var principal = a.TryGetProperty("principal", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            var type = principal.ValueKind == JsonValueKind.Object ? Text(principal, "@odata.type") : null;
            var principalType = type switch
            {
                "#microsoft.graph.group" => RolePrincipalType.Group,
                "#microsoft.graph.servicePrincipal" => RolePrincipalType.App,
                _ => RolePrincipalType.User,
            };
            var scope = Text(a, "directoryScopeId") ?? "/";
            return new RoleGrant(Text(a, "id") ?? "", Text(a, "roleDefinitionId") ?? "", Text(a, "principalId") ?? "",
                principal.ValueKind == JsonValueKind.Object ? Text(principal, "displayName") ?? "Someone" : "Someone (deleted)",
                principal.ValueKind != JsonValueKind.Object ? null : principalType switch
                {
                    RolePrincipalType.User => Text(principal, "userPrincipalName"),
                    RolePrincipalType.App => Text(principal, "appId"),
                    _ => Text(principal, "mail"),
                },
                principalType, scope, ScopeName(scope), kind, When(a, "startDateTime"), When(a, "endDateTime"), viaPim,
                string.Equals(Text(a, "assignmentType"), "Activated", StringComparison.OrdinalIgnoreCase));
        }

        var (assignmentRows, pimUnavailable) = await assignmentsRead;
        foreach (var (row, kind, viaPim) in assignmentRows)
            grants.Add(Grant(row, kind, viaPim));

        // Some assignments are to roles the list leaves out (seen live: 4 apps' roles); read those together.
        var known = definitions.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = await Task.WhenAll(grants.Select(g => g.RoleId).Where(id => !known.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase).Select(async missing =>
        {
            try
            {
                var d = await graph.GetAsync($"roleManagement/directory/roleDefinitions/{missing}?$select=id,displayName,description,isBuiltIn", cancellationToken);
                return new AdminRole(missing, Text(d, "displayName") ?? "A hidden role", Text(d, "description") ?? "", Flag(d, "isBuiltIn") != false, false, true, []);
            }
            catch (GraphException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return new AdminRole(missing, "A role Entra doesn't list", "", true, false, true, []);
            }
        }));
        definitions.AddRange(hidden);

        var byRole = grants.ToLookup(g => g.RoleId, StringComparer.OrdinalIgnoreCase);
        return new RoleDirectory(
            [.. definitions.Select(d => new RoleSummary(d, [.. byRole[d.Id].OrderBy(g => g.PrincipalName, StringComparer.CurrentCultureIgnoreCase)]))],
            [.. units.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)], pimUnavailable);
    }

    IReadOnlyList<ResourceAction>? _actions;

    public async Task<IReadOnlyList<ResourceAction>> GetResourceActionsAsync(CancellationToken cancellationToken = default)
    {
        if (_actions is not null)
            return _actions;
        var actions = new List<ResourceAction>();
        await foreach (var a in graph.GetAllAsync($"{Beta}roleManagement/directory/resourceNamespaces/microsoft.directory/resourceActions?$select=name,description,isPrivileged",
                           cancellationToken))
            if (Text(a, "name") is { } name)
                actions.Add(new ResourceAction(name, Text(a, "description"), Flag(a, "isPrivileged") == true));
        return _actions = [.. actions.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>PIM reads (8a-2): a role's settings, the admin's own roles, and approvals waiting for them.</summary>
public sealed partial class GraphUserRecords
{
    public async Task<Available<RoleSettings>> GetRoleSettingsAsync(string roleId, CancellationToken cancellationToken = default)
    {
        try
        {
            var assignment = await graph.GetAsync("policies/roleManagementPolicyAssignments?$filter=scopeId eq '/' and scopeType eq 'DirectoryRole' and " +
                                                  $"roleDefinitionId eq '{roleId}'&$expand=policy($expand=rules)", cancellationToken);
            var first = assignment.TryGetProperty("value", out var v) && v.GetArrayLength() > 0 ? v[0] : default;
            if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("policy", out var policy))
                return Available<RoleSettings>.Because("Entra has no PIM settings for this role.");
            var rules = policy.GetProperty("rules").EnumerateArray()
                .ToDictionary(r => Text(r, "id") ?? "", r => r.GetRawText(), StringComparer.Ordinal);
            return new(RolePolicyRules.Read(Text(policy, "id") ?? "", rules));
        }
        catch (GraphException e) when (NeedsPremium(e))
        {
            return Available<RoleSettings>.Because(RoleDirectory.PimNeedsP2);
        }
    }

    public async Task<Available<IReadOnlyList<RoleGrant>>> GetMyRolesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var mine = new List<RoleGrant>();
            async Task ReadAsync(string collection, RoleGrantKind kind)
            {
                await foreach (var a in graph.GetAllAsync($"roleManagement/directory/{collection}/filterByCurrentUser(on='principal')?$expand=roleDefinition($select=displayName)",
                                   cancellationToken))
                {
                    var name = a.TryGetProperty("roleDefinition", out var d) && d.ValueKind == JsonValueKind.Object ? Text(d, "displayName") : null;
                    var scope = Text(a, "directoryScopeId") ?? "/";
                    mine.Add(new RoleGrant(Text(a, "id") ?? "", Text(a, "roleDefinitionId") ?? "", Text(a, "principalId") ?? "", name ?? "A role", null,
                        RolePrincipalType.User, scope, scope == "/" ? "Organization" : "One part of the organization", kind, When(a, "startDateTime"), When(a, "endDateTime"),
                        true, string.Equals(Text(a, "assignmentType"), "Activated", StringComparison.OrdinalIgnoreCase)));
                }
            }
            await ReadAsync("roleEligibilityScheduleInstances", RoleGrantKind.Eligible);
            await ReadAsync("roleAssignmentScheduleInstances", RoleGrantKind.Active);
            return new([.. mine.OrderBy(g => g.PrincipalName, StringComparer.CurrentCulture)]);
        }
        catch (GraphException e) when (NeedsPremium(e))
        {
            return Available<IReadOnlyList<RoleGrant>>.Because(RoleDirectory.PimNeedsP2);
        }
    }

    public async Task<Available<IReadOnlyList<RoleRequest>>> GetApprovalsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var requests = new List<RoleRequest>();
            await foreach (var r in graph.GetAllAsync($"{Beta}roleManagement/directory/roleAssignmentScheduleRequests/filterByCurrentUser(on='approver')" +
                                                      "?$filter=status eq 'PendingApproval'&$expand=principal,roleDefinition", cancellationToken))
            {
                var principal = r.TryGetProperty("principal", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
                var role = r.TryGetProperty("roleDefinition", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
                var duration = r.TryGetProperty("scheduleInfo", out var s) && s.TryGetProperty("expiration", out var x) && Text(x, "duration") is { } iso
                    ? RolePolicyRules.Duration(iso) : (TimeSpan?)null;
                requests.Add(new RoleRequest(Text(r, "id") ?? "", Text(r, "approvalId") ?? "", Text(r, "roleDefinitionId") ?? "",
                    role.ValueKind == JsonValueKind.Object ? Text(role, "displayName") ?? "A role" : "A role",
                    Text(r, "principalId") ?? "", principal.ValueKind == JsonValueKind.Object ? Text(principal, "displayName") ?? "Someone" : "Someone",
                    principal.ValueKind == JsonValueKind.Object ? Text(principal, "userPrincipalName") : null,
                    Text(r, "justification"), When(r, "createdDateTime"), duration, Text(r, "directoryScopeId") is "/" or null ? "Organization" : "One part of the organization"));
            }
            return new([.. requests.OrderBy(r => r.Requested)]);
        }
        catch (GraphException e) when (NeedsPremium(e))
        {
            return Available<IReadOnlyList<RoleRequest>>.Because(RoleDirectory.PimNeedsP2);
        }
    }
}
