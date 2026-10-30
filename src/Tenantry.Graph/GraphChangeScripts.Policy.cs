using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>The PowerShell for admin roles and PIM (8a), Conditional Access (8b) and sign-in methods (8d).</summary>
public sealed partial class GraphChangeExecutor
{
    static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    const string StartsNow = "PIM's start time is the moment the request is sent; Tenantry fills it in when it applies.";

    /// <summary>Admin role assignments, custom roles, PIM settings and requests, and a policy's on/off state or deletion.</summary>
    static PsScript? RoleScript(ChangeOp change) => change switch
    {
        AddRoleAssignment { ViaPim: false } a => PsScript.Graph(Ps.Graph("POST", $"{Roles}roleAssignments", DirectAssignmentBody(a))),
        AddRoleAssignment a => PimAssignRequest(a, DateTimeOffset.UtcNow) is var (path, body) ? PsScript.Graph(Ps.Graph("POST", path, body), StartsNow) : null,
        RemoveRoleAssignment r when !r.ViaPim || r.RoleId is null => PsScript.Graph(Ps.Graph("DELETE", $"{Roles}roleAssignments/{r.AssignmentId}")),
        RemoveRoleAssignment r => PimRemoveRequest(r) is var (path, body) ? PsScript.Graph(Ps.Graph("POST", path, body)) : null,
        CreateCustomRole c => PsScript.Graph(Ps.Graph("POST", $"{Roles}roleDefinitions", NewRoleBody(c))),
        UpdateCustomRole u => PsScript.Graph(Ps.Graph("PATCH", $"{Roles}roleDefinitions/{u.Role.Id}", RoleBody(u.NewName, u.NewDescription, u.NewActions))),
        DeleteCustomRole d => PsScript.Graph(Ps.Graph("DELETE", $"{Roles}roleDefinitions/{d.Role.Id}")),
        SetRolePolicyRule r => PsScript.Graph(Ps.Graph("PATCH", $"policies/roleManagementPolicies/{r.PolicyId}/rules/{r.RuleId}", JsonNode.Parse(r.Rule))),
        ExtendRoleAssignment e => ExtendRequest(e, DateTimeOffset.UtcNow) is var (path, body) ? PsScript.Graph(Ps.Graph("POST", path, body), StartsNow) : null,
        ActivateRole a => ActivateScript(a),
        ReviewRoleRequest r => ReviewScript(r),
        SetCaPolicyState s => PsScript.Graph(Ps.Graph("PATCH", $"identity/conditionalAccess/policies/{s.Policy.Id}", new JsonObject { ["state"] = s.New })),
        DeleteCaPolicy d => PsScript.Graph(Ps.Graph("DELETE", $"identity/conditionalAccess/policies/{d.Policy.Id}")),
        _ => null,
    };

    /// <summary>
    /// PIM needs the admin's object ID. When only a sign-in name is known, the executor looks the ID
    /// up first, and so does the script (putting it in place of &lt;me&gt;).
    /// </summary>
    static PsScript ActivateScript(ActivateRole change)
    {
        var path = $"{Roles}roleAssignmentScheduleRequests";
        var note = change.On ? StartsNow : null;
        if (!change.Me.Id.Contains('@', StringComparison.Ordinal))
            return PsScript.Graph(Ps.Graph("POST", path, ActivateRequest(change, change.Me.Id, DateTimeOffset.UtcNow)), note);
        var body = ActivateRequest(change, "<me>", DateTimeOffset.UtcNow);
        return PsScript.Graph(Ps.Lines(
            Ps.Graph("GET", "me?$select=id", assignTo: "me"),
            $"$body = {Ps.HereString(body.ToJsonString(IndentedJson))}",
            "$body = $body.Replace('<me>', $me.id)",
            $"Invoke-MgGraphRequest -Method POST -Uri {Ps.Quote(Ps.GraphRoot + path)} -Body $body"), note);
    }

    /// <summary>Finds the approval's open step assigned to the signed-in admin, then answers it, as the executor does.</summary>
    static PsScript ReviewScript(ReviewRoleRequest change)
    {
        var approval = $"{RoleApprovals}{change.ApprovalId}";
        return PsScript.Graph(Ps.Lines(
            Ps.Graph("GET", $"{approval}?$expand=steps", assignTo: "approval"),
            "$step = $approval.steps | Where-Object { $_.status -eq 'InProgress' -and $_.assignedToMe } | Select-Object -First 1",
            $"$body = {Ps.HereString(ReviewBody(change).ToJsonString(IndentedJson))}",
            $"Invoke-MgGraphRequest -Method PATCH -Uri ({Ps.Quote(approval + "/steps/")} + $step.id) -Body $body"));
    }

    /// <summary>Conditional Access policies from the editor, named locations, and the sign-in methods policy and security defaults.</summary>
    static PsScript? AccessPolicyScript(ChangeOp change) => change switch
    {
        CreateCaPolicy c => PsScript.Graph(Ps.Graph("POST", "identity/conditionalAccess/policies", CaJson.ForWrite(JsonNode.Parse(c.Json)!.AsObject()))),
        UpdateCaPolicy u => PsScript.Graph(Ps.Graph("PATCH", $"identity/conditionalAccess/policies/{u.Policy.Id}", CaJson.ForWrite(JsonNode.Parse(u.Json)!.AsObject()))),
        CreateNamedLocation c => PsScript.Graph(Ps.Graph("POST", Locations, JsonNode.Parse(c.Json))),
        UpdateNamedLocation u => PsScript.Graph(Ps.Graph("PATCH", $"{Locations}/{u.Location.Id}", JsonNode.Parse(u.Json))),
        DeleteNamedLocation d => PsScript.Graph(Ps.Graph("DELETE", $"{Locations}/{d.Location.Id}")),
        SetAuthMethodConfig m => PsScript.Graph(Ps.Graph("PATCH", AuthMethodConfigPath(m), JsonNode.Parse(m.Json))),
        SetSecurityDefaults d => PsScript.Graph(Ps.Graph("PATCH", "policies/identitySecurityDefaultsEnforcementPolicy", new JsonObject { ["isEnabled"] = d.New })),
        _ => null,
    };
}
