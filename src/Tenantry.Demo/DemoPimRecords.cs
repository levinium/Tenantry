using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's PIM, when it acts as a tenant with Entra ID P2: Microsoft's default role settings
/// (activations of up to 8 hours with MFA and a reason, eligible assignments for a year), a few
/// people eligible for privileged roles, the demo admin eligible for two, and one request waiting
/// for the admin's approval.
/// </summary>
public sealed partial class DemoUserRecords
{
    readonly Dictionary<string, Dictionary<string, string>> _policies = new(StringComparer.Ordinal);
    List<RoleRequest>? _requests;

    static Dictionary<string, string> DefaultRules()
    {
        static string Rule(string type, string id, JsonObject body)
        {
            body["@odata.type"] = $"#microsoft.graph.unifiedRoleManagementPolicy{type}Rule";
            body["id"] = id;
            return body.ToJsonString();
        }
        return new(StringComparer.Ordinal)
        {
            [RolePolicyRules.ActivationExpiry] = Rule("Expiration", RolePolicyRules.ActivationExpiry, new() { ["isExpirationRequired"] = true, ["maximumDuration"] = "PT8H" }),
            [RolePolicyRules.ActivationRequirements] = Rule("Enablement", RolePolicyRules.ActivationRequirements,
                new() { ["enabledRules"] = new JsonArray(RolePolicyRules.Mfa, RolePolicyRules.Justification) }),
            [RolePolicyRules.Approval] = Rule("Approval", RolePolicyRules.Approval, new()
            {
                ["setting"] = new JsonObject
                {
                    ["isApprovalRequired"] = false,
                    ["approvalMode"] = "SingleStage",
                    ["approvalStages"] = new JsonArray(new JsonObject
                    {
                        ["approvalStageTimeOutInDays"] = 1, ["isApproverJustificationRequired"] = true, ["escalationTimeInMinutes"] = 0, ["isEscalationEnabled"] = false,
                        ["primaryApprovers"] = new JsonArray(), ["escalationApprovers"] = new JsonArray(),
                    }),
                },
            }),
            [RolePolicyRules.EligibleExpiry] = Rule("Expiration", RolePolicyRules.EligibleExpiry, new() { ["isExpirationRequired"] = true, ["maximumDuration"] = "P365D" }),
            [RolePolicyRules.ActiveExpiry] = Rule("Expiration", RolePolicyRules.ActiveExpiry, new() { ["isExpirationRequired"] = true, ["maximumDuration"] = "P180D" }),
            [RolePolicyRules.ActiveRequirements] = Rule("Enablement", RolePolicyRules.ActiveRequirements,
                new() { ["enabledRules"] = new JsonArray(RolePolicyRules.Justification) }),
        };
    }

    /// <summary>With PIM: eligible people, and the demo admin eligible for two roles.</summary>
    async Task AddPimGrantsAsync(List<RoleGrant> grants, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var people = (await directory.GetUsersAsync(cancellationToken)).Where(u => u.Kind == UserKind.Member && u.Security?.IsAdmin != true).Take(3).ToArray();
        string[] roles = [Catalog[7].Id, Catalog[2].Id, Catalog[13].Id];
        for (var i = 0; i < people.Length; i++)
            grants.Add(new RoleGrant($"el-{people[i].Id}", roles[i], people[i].Id, people[i].DisplayName, people[i].UserPrincipalName, RolePrincipalType.User, "/", "Organization",
                RoleGrantKind.Eligible, now.AddDays(-40), now.AddDays(i == 0 ? 6 : 200), ViaPim: true));
        foreach (var role in new[] { Catalog[7], Catalog[6] })
            grants.Add(new RoleGrant($"el-me-{role.Id}", role.Id, Account, "Demo Admin", Account, RolePrincipalType.User, "/", "Organization",
                RoleGrantKind.Eligible, now.AddDays(-90), now.AddDays(275), ViaPim: true));
    }

    public async Task<Available<RoleSettings>> GetRoleSettingsAsync(string roleId, CancellationToken cancellationToken = default)
    {
        if (!pim)
            return Available<RoleSettings>.Because(RoleDirectory.PimNeedsP2);
        await Task.CompletedTask;
        if (!_policies.TryGetValue(roleId, out var rules))
            _policies[roleId] = rules = DefaultRules();
        return new(RolePolicyRules.Read($"policy-{roleId}", rules));
    }

    public async Task<Available<IReadOnlyList<RoleGrant>>> GetMyRolesAsync(CancellationToken cancellationToken = default)
    {
        if (!pim)
            return Available<IReadOnlyList<RoleGrant>>.Because(RoleDirectory.PimNeedsP2);
        var grants = await GrantsAsync(cancellationToken);
        return new([.. grants.Where(g => g.PrincipalId == Account)
            .Select(g => g with { PrincipalName = _roleCatalog!.FirstOrDefault(r => r.Id == g.RoleId)?.Name ?? "A role" })]);
    }

    public async Task<Available<IReadOnlyList<RoleRequest>>> GetApprovalsAsync(CancellationToken cancellationToken = default)
    {
        if (!pim)
            return Available<IReadOnlyList<RoleRequest>>.Because(RoleDirectory.PimNeedsP2);
        if (_requests is null)
        {
            var person = (await directory.GetUsersAsync(cancellationToken)).First(u => u.Kind == UserKind.Member && u.Security?.IsAdmin != true);
            _requests = [new RoleRequest("req-1", "approval-1", Catalog[7].Id, Catalog[7].Name, person.Id, person.DisplayName, person.UserPrincipalName,
                "Reviewing last night's sign-in alerts", DateTimeOffset.UtcNow.AddMinutes(-25), TimeSpan.FromHours(4), "Organization")];
        }
        return new(_requests.ToArray());
    }

    /// <summary>Applies a PIM change to the demo; false when it isn't one.</summary>
    async Task<bool> ApplyPimAsync(ChangeOp change, List<RoleGrant> grants, CancellationToken cancellationToken)
    {
        switch (change)
        {
            case SetRolePolicyRule rule:
                if (!_policies.TryGetValue(rule.Role.Id, out var rules))
                    _policies[rule.Role.Id] = rules = DefaultRules();
                rules[rule.RuleId] = rule.Rule;
                return true;
            case ExtendRoleAssignment extend:
                var index = grants.FindIndex(g => g.PrincipalId == extend.Principal.Id && g.RoleId == extend.RoleId && g.Kind == extend.Kind);
                if (index >= 0)
                    grants[index] = grants[index] with { End = extend.NewEnd };
                return true;
            case ActivateRole { On: true } on:
                grants.Add(new RoleGrant($"act-{on.RoleId}", on.RoleId, Account, "Demo Admin", Account, RolePrincipalType.User, on.ScopeId, "Organization",
                    RoleGrantKind.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(on.Hours), ViaPim: true, Activated: true));
                return true;
            case ActivateRole off:
                grants.RemoveAll(g => g.PrincipalId == Account && g.RoleId == off.RoleId && g.Activated);
                return true;
            case ReviewRoleRequest review:
                var request = (await GetApprovalsAsync(cancellationToken)).Value?.FirstOrDefault(r => r.Id == review.Request.Id);
                _requests?.RemoveAll(r => r.Id == review.Request.Id);
                if (review.Approve && request is not null)
                    grants.Add(new RoleGrant($"act-{request.Id}", request.RoleId, request.PrincipalId, request.PrincipalName, request.PrincipalUpn, RolePrincipalType.User,
                        "/", "Organization", RoleGrantKind.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + (request.Duration ?? TimeSpan.FromHours(1)), true, true));
                return true;
            default:
                return false;
        }
    }
}
