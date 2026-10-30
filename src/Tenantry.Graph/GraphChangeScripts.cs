using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>
/// The PowerShell for each change: the same Graph requests (through Invoke-MgGraphRequest) and
/// the same Exchange, Purview and PnP cmdlets the executor sends, built from the same parameter
/// helpers where it has them, so what's shown is what Tenantry does. The read-back that follows
/// each change isn't shown.
/// </summary>
public sealed class GraphChangeScripts : IChangeScripts
{
    public PsScript? For(ChangeOp change) => GraphChangeExecutor.ScriptFor(change);
}

public sealed partial class GraphChangeExecutor
{
    /// <summary>
    /// The PowerShell for a change, or null for changes that are Tenantry's own records (Test
    /// mode, a secret viewed). Each area answers for its own changes, or null to pass it on.
    /// </summary>
    public static PsScript? ScriptFor(ChangeOp change) =>
        PeopleScript(change)
        ?? MailScript(change)
        ?? CreateScript(change)
        ?? SecurityScript(change)
        ?? DeviceScript(change)
        ?? RoleScript(change)
        ?? AccessPolicyScript(change)
        ?? MailSecurityScript(change)
        ?? SiteScript(change)
        ?? AppScript(change)
        ?? EDiscoveryScript(change)
        ?? RetentionScript(change);

    /// <summary>Profile, sign-in, password, licenses, group membership and mailbox access.</summary>
    static PsScript? PeopleScript(ChangeOp change) => change switch
    {
        SetUserText { Property: "businessPhones" } t => PatchUserScript(t.User, t.Property,
            string.IsNullOrWhiteSpace(t.New) ? new JsonArray() : new JsonArray(t.New.Trim())),
        SetUserText t => PatchUserScript(t.User, t.Property, string.IsNullOrWhiteSpace(t.New) ? null : JsonValue.Create(t.New.Trim())),
        SetSignInAllowed s => PatchUserScript(s.User, "accountEnabled", JsonValue.Create(s.New)),
        ResetPassword r => PsScript.Graph(Ps.Graph("PATCH", $"users/{r.User.Id}", new JsonObject
            {
                ["passwordProfile"] = new JsonObject
                {
                    ["password"] = "<new password>",
                    ["forceChangePasswordNextSignIn"] = r.MustChangeAtNextSignIn,
                },
            }),
            "Put the new password in place of <new password>. Tenantry makes one in the tenant's password style and shows it once."),
        SetLicenseAssigned l => LicenseScript(l.User,
            l.New ? new JsonArray(new JsonObject { ["skuId"] = l.SkuId, ["disabledPlans"] = new JsonArray() }) : [],
            l.New ? [] : new JsonArray(l.SkuId)),
        SetLicenseApps a => LicenseScript(a.User,
            new JsonArray(new JsonObject { ["skuId"] = a.SkuId, ["disabledPlans"] = new JsonArray(a.NewDisabled.Select(p => (JsonNode)p).ToArray()) }), []),
        RemoveAppAssignment r => PsScript.Graph(Ps.Graph("DELETE", $"users/{r.User.Id}/appRoleAssignments/{r.AssignmentId}")),
        SetGroupMembership { ExchangeManaged: true } m => PsScript.Exchange(Ps.Cmdlet(ExchangeMembershipCmdlet(m), ExchangeMembershipParameters(m))),
        SetGroupMembership m => PsScript.Graph(m.New
            ? Ps.Graph("POST", $"groups/{m.Group.Id}/{(m.Role == GroupRole.Owner ? "owners" : "members")}/$ref",
                new JsonObject { ["@odata.id"] = DirectoryObjects + m.Person.Id })
            : Ps.Graph("DELETE", $"groups/{m.Group.Id}/{(m.Role == GroupRole.Owner ? "owners" : "members")}/{m.Person.Id}/$ref")),
        SetMailboxAccess a => MailboxAccessCall(a) is var (cmdlet, parameters) ? PsScript.Exchange(Ps.Cmdlet(cmdlet, parameters)) : null,
        _ => null,
    };

    static PsScript PatchUserScript(ChangeTarget user, string property, JsonNode? value) =>
        PsScript.Graph(Ps.Graph("PATCH", $"users/{user.Id}", new JsonObject { [property] = value }));

    static PsScript LicenseScript(ChangeTarget user, JsonArray add, JsonArray remove) =>
        PsScript.Graph(Ps.Graph("POST", $"users/{user.Id}/assignLicense", new JsonObject { ["addLicenses"] = add, ["removeLicenses"] = remove }));

    /// <summary>Set-Mailbox (or another cmdlet) on one mailbox, named as the executor names it.</summary>
    static PsScript MailboxCmdlet(ChangeTarget mailbox, string cmdlet, Dictionary<string, object?> parameters, string? note = null) =>
        PsScript.Exchange(Ps.Cmdlet(cmdlet, new Dictionary<string, object?> { ["Identity"] = mailbox.Address ?? mailbox.Id }.Concat(parameters.Where(p => p.Key != "Identity"))), note);
}
