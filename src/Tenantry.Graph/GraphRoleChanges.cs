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
                ct => graph.PostAsync($"{Roles}roleAssignments", DirectAssignmentBody(change), ct),
                ct => graph.GetAsync($"{Roles}roleAssignments?$filter=principalId eq '{change.Principal.Id}' and roleDefinitionId eq '{change.RoleId}'", ct),
                Any,
                cancellationToken);

        var (path, request) = PimAssignRequest(change, DateTimeOffset.UtcNow);
        var result = await GraphCheckedAsync(ct => graph.PostAsync(path, request, ct), null, _ => true, cancellationToken);
        return result.Succeeded ? new ApplyResult(ChangeOutcome.Applied, "PIM accepted it; it shows within a minute.") : result;
    }

    static JsonObject DirectAssignmentBody(AddRoleAssignment change) => new()
    {
        ["principalId"] = change.Principal.Id,
        ["roleDefinitionId"] = change.RoleId,
        ["directoryScopeId"] = change.ScopeId,
    };

    /// <summary>PIM's schedule requests: eligible roles have their own collection.</summary>
    static string ScheduleRequests(RoleGrantKind kind) =>
        Roles + (kind == RoleGrantKind.Eligible ? "roleEligibilityScheduleRequests" : "roleAssignmentScheduleRequests");

    static string UtcText(DateTimeOffset at) => at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>An eligible or time-limited assignment through PIM (adminAssign), starting at <paramref name="now"/>.</summary>
    static (string Path, JsonObject Body) PimAssignRequest(AddRoleAssignment change, DateTimeOffset now)
    {
        var request = PimRequest("adminAssign", change.Principal.Id, change.RoleId, change.ScopeId, change.Justification);
        request["scheduleInfo"] = new JsonObject
        {
            ["startDateTime"] = UtcText(now),
            ["expiration"] = change.End is { } end
                ? new JsonObject { ["type"] = "afterDateTime", ["endDateTime"] = UtcText(end) }
                : new JsonObject { ["type"] = "noExpiration" },
        };
        return (ScheduleRequests(change.Kind), request);
    }

    /// <summary>Removing a PIM-made assignment (adminRemove); only for changes that carry a role ID.</summary>
    static (string Path, JsonObject Body) PimRemoveRequest(RemoveRoleAssignment change) =>
        (ScheduleRequests(change.Kind), PimRequest("adminRemove", change.User.Id, change.RoleId!, change.ScopeId ?? "/", null));

    async Task<ApplyResult> RemoveRoleAssignmentAsync(RemoveRoleAssignment change, CancellationToken cancellationToken)
    {
        if (!change.ViaPim || change.RoleId is null)
            return await GraphCheckedAsync(
                ct => graph.DeleteAsync($"{Roles}roleAssignments/{change.AssignmentId}", ct),
                ct => graph.GetAsync($"{Roles}roleAssignments/{change.AssignmentId}", ct),
                answer => answer is null,
                cancellationToken);
        var (path, request) = PimRemoveRequest(change);
        var result = await GraphCheckedAsync(ct => graph.PostAsync(path, request, ct), null, _ => true, cancellationToken);
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

    static JsonObject NewRoleBody(CreateCustomRole change)
    {
        var body = RoleBody(change.Role.Name, change.Description, change.Actions);
        body["isEnabled"] = true;
        return body;
    }

    Task<ApplyResult> CreateCustomRoleAsync(CreateCustomRole change, CancellationToken cancellationToken)
    {
        var body = NewRoleBody(change);
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

/// <summary>PIM changes (8a-2): role settings, extending an assignment, activating, and approving.</summary>
public sealed partial class GraphChangeExecutor
{
    Task<ApplyResult> SetRolePolicyRuleAsync(SetRolePolicyRule change, CancellationToken cancellationToken)
    {
        var rule = JsonNode.Parse(change.Rule)!.AsObject();
        return GraphCheckedAsync(
            ct => graph.PatchAsync($"policies/roleManagementPolicies/{change.PolicyId}/rules/{change.RuleId}", rule, ct),
            null, _ => true, cancellationToken);
    }

    /// <summary>A new end for a time-limited assignment (adminUpdate), starting at <paramref name="now"/>.</summary>
    static (string Path, JsonObject Body) ExtendRequest(ExtendRoleAssignment change, DateTimeOffset now)
    {
        var request = PimRequest("adminUpdate", change.Principal.Id, change.RoleId, change.ScopeId, "Extended in Tenantry");
        request["scheduleInfo"] = new JsonObject
        {
            ["startDateTime"] = UtcText(now),
            ["expiration"] = new JsonObject { ["type"] = "afterDateTime", ["endDateTime"] = UtcText(change.NewEnd) },
        };
        return (ScheduleRequests(change.Kind), request);
    }

    async Task<ApplyResult> ExtendRoleAssignmentAsync(ExtendRoleAssignment change, CancellationToken cancellationToken)
    {
        var (path, request) = ExtendRequest(change, DateTimeOffset.UtcNow);
        var result = await GraphCheckedAsync(ct => graph.PostAsync(path, request, ct), null, _ => true, cancellationToken);
        return result.Succeeded ? new ApplyResult(ChangeOutcome.Applied, "PIM accepted it; the new end shows within a minute.") : result;
    }

    async Task<ApplyResult> ActivateRoleAsync(ActivateRole change, CancellationToken cancellationToken)
    {
        // PIM wants the admin's object ID; a sign-in name (when the local copy didn't have them) is looked up.
        var me = change.Me.Id;
        if (me.Contains('@', StringComparison.Ordinal))
            me = (await graph.GetAsync("me?$select=id", cancellationToken)).GetProperty("id").GetString() ?? me;
        var request = ActivateRequest(change, me, DateTimeOffset.UtcNow);
        try
        {
            var answer = await graph.PostAsync($"{Roles}roleAssignmentScheduleRequests", request, cancellationToken);
            return answer.TryGetProperty("status", out var s) && s.GetString() == "PendingApproval"
                ? new ApplyResult(ChangeOutcome.Applied, "Sent for approval: it's active once an approver says yes.")
                : new ApplyResult(ChangeOutcome.Applied, change.On ? "Activated; it can take a minute to work everywhere." : null);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    /// <summary>Switching an eligible role on for a while (selfActivate) or off early (selfDeactivate), for the admin <paramref name="me"/>.</summary>
    static JsonObject ActivateRequest(ActivateRole change, string me, DateTimeOffset now)
    {
        var request = PimRequest(change.On ? "selfActivate" : "selfDeactivate", me, change.RoleId, change.ScopeId, change.Justification);
        if (change.On)
        {
            request["scheduleInfo"] = new JsonObject
            {
                ["startDateTime"] = UtcText(now),
                ["expiration"] = new JsonObject { ["type"] = "afterDuration", ["duration"] = RolePolicyRules.Iso(TimeSpan.FromHours(change.Hours)) },
            };
            if (change.Ticket is { Length: > 0 } ticket)
                request["ticketInfo"] = new JsonObject { ["ticketNumber"] = ticket, ["ticketSystem"] = "Tenantry" };
        }
        return request;
    }

    const string RoleApprovals = "https://graph.microsoft.com/beta/roleManagement/directory/roleAssignmentApprovals/";

    static JsonObject ReviewBody(ReviewRoleRequest change) =>
        new() { ["reviewResult"] = change.Approve ? "Approve" : "Deny", ["justification"] = change.Justification };

    /// <summary>Answers the approval's open step assigned to the signed-in admin (beta roleAssignmentApprovals).</summary>
    async Task<ApplyResult> ReviewRoleRequestAsync(ReviewRoleRequest change, CancellationToken cancellationToken)
    {
        try
        {
            var approval = await graph.GetAsync($"{RoleApprovals}{change.ApprovalId}?$expand=steps", cancellationToken);
            var step = approval.GetProperty("steps").EnumerateArray()
                .FirstOrDefault(s => s.TryGetProperty("status", out var st) && st.GetString() == "InProgress"
                                     && s.TryGetProperty("assignedToMe", out var me) && me.ValueKind == JsonValueKind.True);
            if (step.ValueKind != JsonValueKind.Object)
                return new ApplyResult(ChangeOutcome.Failed, "This request has no step waiting for you (someone else may have answered it).");
            await graph.PatchAsync($"{RoleApprovals}{change.ApprovalId}/steps/{step.GetProperty("id").GetString()}", ReviewBody(change), cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }
}

/// <summary>Conditional Access changes (8b).</summary>
public sealed partial class GraphChangeExecutor
{
    Task<ApplyResult> SetCaPolicyStateAsync(SetCaPolicyState change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.PatchAsync($"identity/conditionalAccess/policies/{change.Policy.Id}", new JsonObject { ["state"] = change.New }, ct),
            ct => graph.GetAsync($"identity/conditionalAccess/policies/{change.Policy.Id}?$select=state", ct),
            answer => answer is { } p && p.TryGetProperty("state", out var s) && s.GetString() == change.New,
            cancellationToken);

    Task<ApplyResult> DeleteCaPolicyAsync(DeleteCaPolicy change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.DeleteAsync($"identity/conditionalAccess/policies/{change.Policy.Id}", ct),
            ct => graph.GetAsync($"identity/conditionalAccess/policies/{change.Policy.Id}", ct),
            answer => answer is null,
            cancellationToken);
}
