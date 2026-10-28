using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Admin role changes (8a). Without PIM a role is given with <c>roleAssignments</c> (active and
/// permanent); an eligible or time-limited one goes through PIM's schedule requests
/// (<c>adminAssign</c>), as does removing one made through PIM (<c>adminRemove</c>).
/// </summary>
public sealed partial class GraphChangeExecutor
{
    const string Roles = "roleManagement/directory/";

    static JsonObject PimRequest(string action, string principalId, string roleId, string scopeId, string? justification) => new()
    {
        ["action"] = action,
        ["principalId"] = principalId,
        ["roleDefinitionId"] = roleId,
        ["directoryScopeId"] = scopeId,
        ["justification"] = justification ?? "Changed in Tenantry",
    };

    async Task<ApplyResult> AddRoleAssignmentAsync(AddRoleAssignment change, CancellationToken cancellationToken)
    {
        if (!change.ViaPim)
            return await GraphCheckedAsync(
                ct => graph.PostAsync($"{Roles}roleAssignments", new JsonObject
                {
                    ["principalId"] = change.Principal.Id,
                    ["roleDefinitionId"] = change.RoleId,
                    ["directoryScopeId"] = change.ScopeId,
                }, ct),
                ct => graph.GetAsync($"{Roles}roleAssignments?$filter=principalId eq '{change.Principal.Id}' and roleDefinitionId eq '{change.RoleId}'", ct),
                Any,
                cancellationToken);

        var request = PimRequest("adminAssign", change.Principal.Id, change.RoleId, change.ScopeId, change.Justification);
        request["scheduleInfo"] = new JsonObject
        {
            ["startDateTime"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["expiration"] = change.End is { } end
                ? new JsonObject { ["type"] = "afterDateTime", ["endDateTime"] = end.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) }
                : new JsonObject { ["type"] = "noExpiration" },
        };
        var collection = change.Kind == RoleGrantKind.Eligible ? "roleEligibilityScheduleRequests" : "roleAssignmentScheduleRequests";
        var result = await GraphCheckedAsync(ct => graph.PostAsync($"{Roles}{collection}", request, ct), null, _ => true, cancellationToken);
        return result.Succeeded ? new ApplyResult(ChangeOutcome.Applied, "PIM accepted it; it shows within a minute.") : result;
    }

    async Task<ApplyResult> RemoveRoleAssignmentAsync(RemoveRoleAssignment change, CancellationToken cancellationToken)
    {
        if (!change.ViaPim || change.RoleId is null)
            return await GraphCheckedAsync(
                ct => graph.DeleteAsync($"{Roles}roleAssignments/{change.AssignmentId}", ct),
                ct => graph.GetAsync($"{Roles}roleAssignments/{change.AssignmentId}", ct),
                answer => answer is null,
                cancellationToken);
        var collection = change.Kind == RoleGrantKind.Eligible ? "roleEligibilityScheduleRequests" : "roleAssignmentScheduleRequests";
        var result = await GraphCheckedAsync(
            ct => graph.PostAsync($"{Roles}{collection}", PimRequest("adminRemove", change.User.Id, change.RoleId, change.ScopeId ?? "/", null), ct),
            null, _ => true, cancellationToken);
        return result.Succeeded ? new ApplyResult(ChangeOutcome.Applied, "PIM accepted it; it's gone within a minute.") : result;
    }

    /// <summary>A filtered read found something.</summary>
    static bool Any(JsonElement? answer) =>
        answer is { } a && a.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0;

    static JsonObject RoleBody(string name, string description, IReadOnlyList<string> actions) => new()
    {
        ["displayName"] = name,
        ["description"] = description,
        ["rolePermissions"] = new JsonArray(new JsonObject { ["allowedResourceActions"] = new JsonArray([.. actions.Select(a => (JsonNode)a)]) }),
    };

    Task<ApplyResult> CreateCustomRoleAsync(CreateCustomRole change, CancellationToken cancellationToken)
    {
        var body = RoleBody(change.Role.Name, change.Description, change.Actions);
        body["isEnabled"] = true;
        return GraphCheckedAsync(
            ct => graph.PostAsync($"{Roles}roleDefinitions", body, ct),
            ct => graph.GetAsync($"{Roles}roleDefinitions?$filter=displayName eq '{change.Role.Name.Replace("'", "''", StringComparison.Ordinal)}'", ct),
            Any,
            cancellationToken);
    }

    Task<ApplyResult> UpdateCustomRoleAsync(UpdateCustomRole change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.PatchAsync($"{Roles}roleDefinitions/{change.Role.Id}", RoleBody(change.NewName, change.NewDescription, change.NewActions), ct),
            ct => graph.GetAsync($"{Roles}roleDefinitions/{change.Role.Id}?$select=displayName,rolePermissions", ct),
            answer => answer is { } d && d.TryGetProperty("displayName", out var n) && n.GetString() == change.NewName
                      && d.GetProperty("rolePermissions").EnumerateArray().SelectMany(p => p.GetProperty("allowedResourceActions").EnumerateArray())
                          .Select(a => a.GetString()).ToHashSet().SetEquals(change.NewActions),
            cancellationToken);

    Task<ApplyResult> DeleteCustomRoleAsync(DeleteCustomRole change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.DeleteAsync($"{Roles}roleDefinitions/{change.Role.Id}", ct),
            ct => graph.GetAsync($"{Roles}roleDefinitions/{change.Role.Id}", ct),
            answer => answer is null,
            cancellationToken);
}
