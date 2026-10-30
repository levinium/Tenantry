using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>The PowerShell for new users and groups, sign-in security changes, and device actions.</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>New users (with their licenses, manager and groups) and new groups of every kind.</summary>
    static PsScript? CreateScript(ChangeOp change) => change switch
    {
        CreateUser c => NewUserScript(c),
        CreateGroup { Kind: GroupKind.Microsoft365 or GroupKind.Security } g => NewGraphGroupScript(g),
        CreateGroup { Kind: GroupKind.Distribution or GroupKind.MailEnabledSecurity } g => PsScript.Exchange(Ps.Cmdlet("New-DistributionGroup", NewDistributionListParameters(g))),
        CreateGroup { Kind: GroupKind.SharedMailbox } g => PsScript.Exchange(Ps.Lines(
            [
                Ps.Cmdlet("New-Mailbox", NewSharedMailboxParameters(g)),
                .. g.Members.SelectMany(m => SharedMailboxGrants(g, m.Address ?? m.Name)).Select(call => Ps.Cmdlet(call.Cmdlet, call.Parameters)),
            ]),
            g.Members.Count > 0 ? "A brand-new mailbox can take a few seconds to accept permissions; Tenantry retries each grant briefly." : null),
        _ => null,
    };

    static PsScript NewUserScript(CreateUser create)
    {
        var steps = new List<string> { Ps.Graph("POST", "users", NewUserBody(create, "<new password>"), assignTo: "user") };
        if (create.Licenses.Count > 0)
            steps.Add(Ps.Graph("POST", $"users/{Ps.Ref("user", "id")}/assignLicense", NewUserLicensesBody(create)));
        if (create.ManagerId is { } manager)
            steps.Add(Ps.Graph("PUT", $"users/{Ps.Ref("user", "id")}/manager/$ref", new JsonObject { ["@odata.id"] = DirectoryObjects + manager }));
        var exchange = false;
        foreach (var group in create.Groups)
        {
            if (group.ExchangeManaged)
            {
                exchange = true;
                steps.Add(Ps.Cmdlet("Add-DistributionGroupMember", NewUserListParameters(create, group)));
            }
            else
                steps.Add(Ps.Graph("POST", $"groups/{group.Group.Id}/members/$ref", new JsonObject { ["@odata.id"] = DirectoryObjects + Ps.Ref("user", "id") }));
        }
        var note = create.Chosen is null
            ? "Put a password in place of <new password>. Tenantry makes one in the tenant's password style and shows it once."
            : "Put the password you chose in place of <new password>.";
        if (exchange)
            note += "\nExchange only knows a new person a few minutes after the account is made, so a distribution list step can fail if run straight away.";
        return new PsScript(Ps.Lines(steps), exchange ? PsService.Graph | PsService.Exchange : PsService.Graph, note);
    }

    static PsScript NewGraphGroupScript(CreateGroup create)
    {
        var steps = new List<string> { Ps.Graph("POST", "groups", NewGraphGroupBody(create), assignTo: "group") };
        foreach (var (relation, people) in GroupPeopleAfterCreate(create))
            foreach (var person in people)
                steps.Add(Ps.Graph("POST", $"groups/{Ps.Ref("group", "id")}/{relation}/$ref", new JsonObject { ["@odata.id"] = DirectoryObjects + person.Id }));
        return PsScript.Graph(Ps.Lines(steps));
    }

    /// <summary>Sign-in methods, Temporary Access Passes, app consents and user risk.</summary>
    static PsScript? SecurityScript(ChangeOp change) => change switch
    {
        RemoveAuthMethod m => PsScript.Graph(Ps.Graph("DELETE", $"users/{m.User.Id}/authentication/{m.Segment}/{m.MethodId}")),
        AddPhoneMethod p => PsScript.Graph(Ps.Graph("POST", $"users/{p.User.Id}/authentication/phoneMethods",
            new JsonObject { ["phoneNumber"] = p.Number, ["phoneType"] = p.PhoneType })),
        CreateTemporaryAccessPass t => PsScript.Graph(Ps.Graph("POST", $"users/{t.User.Id}/authentication/temporaryAccessPassMethods",
                new JsonObject { ["lifetimeInMinutes"] = t.Minutes, ["isUsableOnce"] = t.OneTime }, assignTo: "pass"),
            "Microsoft makes the code and returns it once, in $pass.temporaryAccessPass."),
        RevokeAppConsent c => PsScript.Graph(Ps.Graph("DELETE", $"oauth2PermissionGrants/{c.GrantId}")),
        SetUserRisk r => PsScript.Graph(Ps.Graph("POST", $"identityProtection/riskyUsers/{(r.Compromised ? "confirmCompromised" : "dismiss")}",
            new JsonObject { ["userIds"] = new JsonArray(r.User.Id) })),
        _ => null,
    };

    /// <summary>Entra and Intune device changes, and Autopilot entries.</summary>
    static PsScript? DeviceScript(ChangeOp change) => change switch
    {
        SetDeviceEnabled d => PsScript.Graph(Ps.Graph("PATCH", $"devices/{d.Device.Id}", new JsonObject { ["accountEnabled"] = d.New })),
        RunDeviceAction a => DeviceActionCall(a) is var (path, body) ? PsScript.Graph(Ps.Graph("POST", path, body)) : null,
        DeleteDevice d => DeleteDeviceScript(d),
        RenameDevice r => PsScript.Graph(Ps.Graph("POST", $"{BetaDevices}{r.Device.Id}/setDeviceName", new JsonObject { ["deviceName"] = r.New })),
        SetPrimaryUser p => PsScript.Graph(Ps.Graph("POST", $"{BetaDevices}{p.Device.Id}/users/$ref",
            new JsonObject { ["@odata.id"] = $"https://graph.microsoft.com/beta/users/{p.NewUser.Id}" })),
        SetDeviceOwnership o => PsScript.Graph(Ps.Graph("PATCH", $"deviceManagement/managedDevices/{o.Device.Id}",
            new JsonObject { ["managedDeviceOwnerType"] = o.NewCompany ? "company" : "personal" })),
        SetLostMode l => LostModeCall(l) is var (path, body) ? PsScript.Graph(Ps.Graph("POST", path, body)) : null,
        SetAutopilotGroupTag t => PsScript.Graph(Ps.Graph("POST", $"deviceManagement/windowsAutopilotDeviceIdentities/{t.Entry.Id}/updateDeviceProperties",
            new JsonObject { ["groupTag"] = t.New ?? "" })),
        DeleteAutopilotDevice a => PsScript.Graph(Ps.Graph("DELETE", $"deviceManagement/windowsAutopilotDeviceIdentities/{a.Entry.Id}")),
        _ => null,
    };

    /// <summary>Intune's record first (so Intune doesn't bring the Entra one back), then Entra's.</summary>
    static PsScript DeleteDeviceScript(DeleteDevice change)
    {
        var managedId = change.Device.Kind == "manageddevice" ? change.Device.Id : change.ManagedId;
        return PsScript.Graph(Ps.Lines(
            managedId is null ? null : Ps.Graph("DELETE", $"deviceManagement/managedDevices/{managedId}"),
            change.Device.Kind == "device" ? Ps.Graph("DELETE", $"devices/{change.Device.Id}") : null));
    }
}
