using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.MailFlow;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>The PowerShell for mail security, SharePoint site and app changes.</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>Quarantine, block and allow lists, and mail flow rules: the same Exchange cmdlets the executor runs.</summary>
    static PsScript? MailSecurityScript(ChangeOp change) => change switch
    {
        ReleaseQuarantined r => PsScript.Exchange(Ps.Cmdlet("Release-QuarantineMessage", ReleaseParameters(r))),
        DeleteQuarantined d => PsScript.Exchange(Ps.Cmdlet("Delete-QuarantineMessage", new Dictionary<string, object?> { ["Identity"] = d.Message.Id, ["Confirm"] = false })),
        SetSenderListed s => PsScript.Exchange(Ps.Cmdlet("New-TenantAllowBlockListItems", SenderListedParameters(s))),
        AddListEntry a => AddListEntryCall(a) is var (cmdlet, parameters) ? PsScript.Exchange(Ps.Cmdlet(cmdlet, parameters)) : null,
        RemoveListEntry r => RemoveListEntryCall(r) is var (cmdlet, parameters) ? PsScript.Exchange(Ps.Cmdlet(cmdlet, parameters)) : null,
        UpdateListEntry u => PsScript.Exchange(Ps.Cmdlet("Set-TenantAllowBlockListItems", UpdateListEntryParameters(u))),
        SetRuleEnabled e => PsScript.Exchange(Ps.Cmdlet(e.New ? "Enable-TransportRule" : "Disable-TransportRule",
            new Dictionary<string, object?> { ["Identity"] = e.Rule.Id, ["Confirm"] = false })),
        SetRuleMode m => PsScript.Exchange(Ps.Cmdlet("Set-TransportRule",
            new Dictionary<string, object?> { ["Identity"] = m.Rule.Id, ["Mode"] = MailFlowRule.ExchangeMode(m.New) })),
        SetRulePriority p => PsScript.Exchange(Ps.Cmdlet("Set-TransportRule", new Dictionary<string, object?> { ["Identity"] = p.Rule.Id, ["Priority"] = p.New })),
        RemoveRule r => PsScript.Exchange(Ps.Cmdlet("Remove-TransportRule", new Dictionary<string, object?> { ["Identity"] = r.Rule.Id, ["Confirm"] = false })),
        CreateRule c => PsScript.Exchange(Ps.Cmdlet("New-TransportRule", RuleArguments(CreateRuleParameters(c)))),
        UpdateRule u => PsScript.Exchange(Ps.Cmdlet("Set-TransportRule", RuleArguments(UpdateRuleParameters(u)))),
        _ => null,
    };

    /// <summary>
    /// Rule parameters come from the rule's JSON; true/false values become real booleans so they're
    /// written -Name:$true like every other cmdlet's.
    /// </summary>
    static IEnumerable<KeyValuePair<string, object?>> RuleArguments(Dictionary<string, object?> parameters) =>
        parameters.Select(p => new KeyValuePair<string, object?>(p.Key,
            p.Value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : p.Value));

    /// <summary>
    /// Sites: Tenantry talks to the SharePoint admin site (CSOM, as Set-SPOSite does) and each site's
    /// REST API; here that's PnP.PowerShell's cmdlets with the same effect, or its REST call where
    /// no cmdlet fits. A group-connected site's group goes through Graph, as Tenantry does it.
    /// </summary>
    static PsScript? SiteScript(ChangeOp change) => change switch
    {
        SetSiteAdmin { New: true } a => PsScript.SharePoint(Ps.Cmdlet("Set-PnPTenantSite",
            new Dictionary<string, object?> { ["Identity"] = a.SiteUrl, ["Owners"] = new[] { a.Person.Address ?? a.Login } })),
        SetSiteAdmin a => PsScript.SharePoint(Ps.Lines(
                SiteConnection(a.SiteUrl),
                Ps.Cmdlet("Remove-PnPSiteCollectionAdmin", new Dictionary<string, object?> { ["Owners"] = new[] { a.Person.Address ?? a.Login } }) + " -Connection $site"),
            "PnP can only take admin rights away on the site itself, so this connects to it first."),
        SetSiteGroupMember m => PsScript.SharePoint(m.New
            ? Ps.Lines(SiteRest(m.SiteUrl, "/_api/web/ensureuser", new Dictionary<string, object?> { ["logonName"] = m.Login }),
                SiteRest(m.SiteUrl, $"/_api/web/sitegroups/GetById({m.GroupId})/users", new Dictionary<string, object?> { ["LoginName"] = m.Login }))
            : SiteRest(m.SiteUrl,
                $"/_api/web/sitegroups/GetById({m.GroupId})/users/removeByLoginName(@v)?@v='{Uri.EscapeDataString(m.Login.Replace("'", "''", StringComparison.Ordinal))}'", null)),
        SetSiteSetting s => PsScript.SharePoint(Ps.Cmdlet("Set-PnPTenantSite",
            new Dictionary<string, object?> { ["Identity"] = s.SiteUrl }.Concat(SiteValues(s.Values).Select(v => new KeyValuePair<string, object?>(v.Name, SiteValue(v)))))),
        SetSiteArchived a => PsScript.SharePoint(Ps.Cmdlet("Set-PnPSiteArchiveState",
            new Dictionary<string, object?> { ["Identity"] = a.SiteUrl, ["ArchiveState"] = a.Archive ? "Archived" : "Active" })),
        DeleteSite { GroupId: { } groupId } => PsScript.Graph(Ps.Graph("DELETE", $"groups/{groupId}"),
            "The site goes with its Microsoft 365 group; SharePoint moves it to the recycle bin within a few minutes."),
        DeleteSite d => PsScript.SharePoint(Ps.Cmdlet("Remove-PnPTenantSite", new Dictionary<string, object?> { ["Url"] = d.SiteUrl, ["Force"] = true }),
            "The site goes to SharePoint's recycle bin."),
        RestoreSite { GroupId: { } groupId } => PsScript.Graph(Ps.Graph("POST", $"directory/deletedItems/{groupId}/restore", new JsonObject()),
            "Restoring the group brings its site back within a few minutes."),
        RestoreSite r => PsScript.SharePoint(Ps.Cmdlet("Restore-PnPTenantRecycleBinItem", new Dictionary<string, object?> { ["Url"] = r.SiteUrl, ["Force"] = true })),
        CreateSite { Kind: SiteKind.TeamSite } c => TeamSiteScript(c),
        CreateSite c => CommunicationSiteScript(c),
        _ => null,
    };

    /// <summary>A connection to one site, made with the SharePoint token PnP already holds.</summary>
    static string SiteConnection(string siteUrl) =>
        $"$site = Connect-PnPOnline -Url {Ps.Quote(siteUrl)} -AccessToken (Get-PnPAccessToken -ResourceTypeName SharePoint) -ReturnConnection";

    /// <summary>A POST to a site's REST API through PnP, as Tenantry sends it.</summary>
    static string SiteRest(string siteUrl, string path, Dictionary<string, object?>? content) =>
        $"Invoke-PnPSPRestMethod -Method Post -Url {Ps.Quote(siteUrl + path)}" + (content is null ? "" : $" -Content {Ps.Value(content)}");

    /// <summary>A setting's value as Set-PnPTenantSite takes it: true/false, a number (enums too) or text.</summary>
    static object SiteValue((string Name, SitePropertyType Type, string Value) v) => v.Type switch
    {
        SitePropertyType.Boolean => bool.Parse(v.Value),
        SitePropertyType.Int32 or SitePropertyType.Int64 or SitePropertyType.Enum when long.TryParse(v.Value, out var n) => n,
        _ => v.Value,
    };

    /// <summary>A team site is made as a Microsoft 365 group, with its owners as its first members.</summary>
    static PsScript TeamSiteScript(CreateSite change)
    {
        var group = TeamSiteGroup(change);
        var steps = new List<string> { Ps.Graph("POST", "groups", NewGraphGroupBody(group), assignTo: "group") };
        foreach (var (relation, people) in GroupPeopleAfterCreate(group))
            foreach (var person in people)
                steps.Add(Ps.Graph("POST", $"groups/{Ps.Ref("group", "id")}/{relation}/$ref", new JsonObject { ["@odata.id"] = DirectoryObjects + person.Id }));
        return PsScript.Graph(Ps.Lines(steps), "SharePoint makes the group's team site within moments.");
    }

    /// <summary>A communication site through SharePoint's site manager; other owners join its Owners group once it's made.</summary>
    static PsScript CommunicationSiteScript(CreateSite change)
    {
        var host = new Uri(change.SiteUrl).Host;
        var steps = new List<string>
        {
            $"Invoke-PnPSPRestMethod -Method Post -Url {Ps.Quote($"https://{host}/_api/SPSiteManager/create")} -Content " + Ps.Value(new Dictionary<string, object?>
            {
                ["request"] = new Dictionary<string, object?>
                {
                    ["Title"] = change.Site.Name,
                    ["Url"] = change.SiteUrl,
                    ["Lcid"] = 1033,
                    ["ShareByEmailEnabled"] = false,
                    ["Description"] = change.Description ?? "",
                    ["WebTemplate"] = "SITEPAGEPUBLISHING#0",
                },
            }),
        };
        if (change.Owners.Count > 0)
        {
            steps.Add(Ps.Comment("Once SharePoint has made the site, add the other owners to its Owners group:"));
            steps.Add($"$web = Invoke-PnPSPRestMethod -Url {Ps.Quote(change.SiteUrl + "/_api/web?$select=AssociatedOwnerGroup/Id&$expand=AssociatedOwnerGroup")}");
            foreach (var owner in change.Owners)
            {
                var login = SetOneDriveAccess.LoginFor(owner.Address ?? owner.Name);
                steps.Add(SiteRest(change.SiteUrl, "/_api/web/ensureuser", new Dictionary<string, object?> { ["logonName"] = login }));
                steps.Add($"Invoke-PnPSPRestMethod -Method Post -Url ({Ps.Quote(change.SiteUrl + "/_api/web/sitegroups/GetById(")} + $web.AssociatedOwnerGroup.Id + ')/users') -Content "
                          + Ps.Value(new Dictionary<string, object?> { ["LoginName"] = login }));
            }
        }
        return PsScript.SharePoint(Ps.Lines(steps), "Whoever runs this becomes the site's owner.");
    }

    /// <summary>App registrations and enterprise apps: the same Graph requests the executor sends.</summary>
    static PsScript? AppScript(ChangeOp change) => change switch
    {
        RemoveAppCredential { IsSecret: true } r => PsScript.Graph(Ps.Graph("POST", $"applications/{r.App.Id}/removePassword", new JsonObject { ["keyId"] = r.KeyId })),
        RemoveAppCredential r => PsScript.Graph(Ps.Lines(
                Ps.Graph("GET", $"applications/{r.App.Id}?$select=keyCredentials", assignTo: "app"),
                $"$keep = @($app.keyCredentials | Where-Object {{ $_.keyId -ne {Ps.Quote(r.KeyId)} }})",
                "foreach ($key in $keep) { $key.Remove('@odata.type') }",
                $"Invoke-MgGraphRequest -Method PATCH -Uri {Ps.Quote(Ps.GraphRoot + $"applications/{r.App.Id}")} -Body @{{ keyCredentials = $keep }}"),
            "A certificate is removed by sending back the app's other certificates."),
        AddAppSecret a => PsScript.Graph(Ps.Lines(
                "$body = @{ passwordCredential = @{ displayName = " + Ps.Quote(a.Description)
                + $"; endDateTime = (Get-Date).ToUniversalTime().AddMonths({a.Months}).ToString('o') }} }}",
                $"$secret = Invoke-MgGraphRequest -Method POST -Uri {Ps.Quote(Ps.GraphRoot + $"applications/{a.App.Id}/addPassword")} -Body $body"),
            "Graph returns the new secret's value once, as $secret.secretText. Copy it then: it can't be read again."),
        SetAppOwner { Add: true } o => PsScript.Graph(Ps.Graph("POST", $"{AppPath(o)}/owners/$ref", new JsonObject { ["@odata.id"] = DirectoryObject + o.Owner.Id })),
        SetAppOwner o => PsScript.Graph(Ps.Graph("DELETE", $"{AppPath(o)}/owners/{o.Owner.Id}/$ref")),
        SetEnterpriseAppSetting { Setting: "visible" } s => PsScript.Graph(Ps.Lines(
                Ps.Graph("GET", $"servicePrincipals/{s.App.Id}?$select=tags", assignTo: "sp"),
                "$tags = @($sp.tags | Where-Object { $_ -ne 'HideApp' })" + (s.New ? "" : " + 'HideApp'"),
                $"Invoke-MgGraphRequest -Method PATCH -Uri {Ps.Quote(Ps.GraphRoot + $"servicePrincipals/{s.App.Id}")} -Body @{{ tags = $tags }}"),
            "Whether people see the app is the HideApp tag; its other tags stay as they are."),
        SetEnterpriseAppSetting s => PsScript.Graph(Ps.Graph("PATCH", $"servicePrincipals/{s.App.Id}", new JsonObject { [EnterpriseAppProperty(s.Setting)] = s.New })),
        AddAppUserAssignment a => PsScript.Graph(Ps.Graph("POST", $"servicePrincipals/{a.App.Id}/appRoleAssignedTo", AppUserAssignmentBody(a))),
        RemoveAppUserAssignment r => PsScript.Graph(Ps.Graph("DELETE", $"servicePrincipals/{r.App.Id}/appRoleAssignedTo/{r.AssignmentId}")),
        RevokeAppGrant { IsApplicationPermission: true } g => PsScript.Graph(Ps.Graph("DELETE", $"servicePrincipals/{g.App.Id}/appRoleAssignments/{g.GrantId}")),
        RevokeAppGrant g => PsScript.Graph(Ps.Graph("DELETE", $"oauth2PermissionGrants/{g.GrantId}")),
        GrantAdminConsent c => AdminConsentScript(c),
        DeleteApp d => PsScript.Graph(Ps.Graph("DELETE", AppPath(d))),
        RestoreApp r => PsScript.Graph(Ps.Graph("POST", $"directory/deletedItems/{r.App.Id}/restore", new JsonObject())),
        PurgeApp p => PsScript.Graph(Ps.Graph("DELETE", $"directory/deletedItems/{p.App.Id}")),
        CreateAppRegistration c => PsScript.Graph(Ps.Lines(
            Ps.Graph("POST", "applications", AppRegistrationBody(c), assignTo: "app"),
            $"Invoke-MgGraphRequest -Method POST -Uri {Ps.Quote(Ps.GraphRoot + "servicePrincipals")} -Body @{{ appId = $app.appId }}")),
        SetAppRisk r => PsScript.Graph(Ps.Graph("POST", AppRiskPath(r), AppRiskBody(r))),
        _ => null,
    };

    /// <summary>
    /// What Grant admin consent does: the enterprise app (made if missing), one organization-wide
    /// grant per API with the delegated permissions added, and each application permission not yet held.
    /// </summary>
    static PsScript AdminConsentScript(GrantAdminConsent change)
    {
        var steps = new List<string>
        {
            $"$sp = ({Ps.Graph("GET", $"servicePrincipals?$filter=appId eq '{change.AppId}'&$select=id")}).value | Select-Object -First 1",
            $"if (-not $sp) {{ $sp = Invoke-MgGraphRequest -Method POST -Uri {Ps.Quote(Ps.GraphRoot + "servicePrincipals")} -Body @{{ appId = {Ps.Quote(change.AppId)} }} }}",
        };
        foreach (var api in change.Items.Where(i => !i.IsApplication).GroupBy(i => i.ResourceId))
        {
            var names = api.Select(i => i.Name).ToArray();
            steps.Add(Ps.Comment(api.First().Resource));
            steps.Add($"$grant = ({Ps.Graph("GET", $"oauth2PermissionGrants?$filter=clientId eq '{Ps.Ref("sp", "id")}' and consentType eq 'AllPrincipals' and resourceId eq '{api.Key}'")}).value | Select-Object -First 1");
            steps.Add(Ps.Lines(
                "if ($grant) {",
                "    $scopes = @($grant.scope -split ' ' | Where-Object { $_ })",
                $"    foreach ($name in {Ps.Value(names)}) {{ if ($scopes -notcontains $name) {{ $scopes += $name }} }}",
                $"    Invoke-MgGraphRequest -Method PATCH -Uri {Ps.Expandable(Ps.GraphRoot + "oauth2PermissionGrants/" + Ps.Ref("grant", "id"))} -Body @{{ scope = $scopes -join ' ' }}",
                "} else {",
                $"    Invoke-MgGraphRequest -Method POST -Uri {Ps.Quote(Ps.GraphRoot + "oauth2PermissionGrants")} -Body @{{ clientId = $sp.id; consentType = 'AllPrincipals'; resourceId = {Ps.Quote(api.Key)}; scope = {Ps.Quote(string.Join(' ', names))} }}",
                "}"));
        }
        var roles = change.Items.Where(i => i.IsApplication).ToArray();
        if (roles.Length > 0)
        {
            var assignments = Ps.Expandable(Ps.GraphRoot + "servicePrincipals/" + Ps.Ref("sp", "id") + "/appRoleAssignments");
            steps.Add($"$held = @((Invoke-MgGraphRequest -Method GET -Uri {assignments}).value | ForEach-Object {{ \"$($_.resourceId)/$($_.appRoleId)\" }})");
            foreach (var role in roles)
                steps.Add($"if ($held -notcontains {Ps.Quote($"{role.ResourceId}/{role.PermissionId}")}) {{ Invoke-MgGraphRequest -Method POST -Uri {assignments} "
                          + $"-Body @{{ principalId = $sp.id; resourceId = {Ps.Quote(role.ResourceId)}; appRoleId = {Ps.Quote(role.PermissionId)} }} }}");
        }
        return PsScript.Graph(Ps.Lines(steps));
    }
}
