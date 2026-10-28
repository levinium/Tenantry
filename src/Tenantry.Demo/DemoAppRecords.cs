using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's apps: a handful of the organization's own app registrations (one with an expired
/// secret, one about to expire, one asking for permissions nobody has consented to), their
/// enterprise apps, a few of Microsoft's, and third-party apps people or admins have consented to.
/// </summary>
public sealed partial class DemoUserRecords : IAppRecords
{
    const string GraphApi = "00000003-0000-0000-c000-000000000000";
    const string GraphSp = "sp-graph";

    /// <summary>Microsoft Graph's permissions the demo apps use: ID → (name, application?, Microsoft's words).</summary>
    static readonly Dictionary<string, (string Name, bool App, string Description)> GraphPermissions = new()
    {
        ["p-user-read"] = ("User.Read", false, "Sign in and read user profile"),
        ["p-mail-send"] = ("Mail.Send", false, "Send mail as a user"),
        ["p-files-rw"] = ("Files.ReadWrite", false, "Have full access to user files"),
        ["p-offline"] = ("offline_access", false, "Maintain access to data you have given it access to"),
        ["p-users-all"] = ("User.Read.All", true, "Read all users' full profiles"),
        ["p-sites-all"] = ("Sites.Read.All", true, "Read items in all site collections"),
        ["p-mail-all"] = ("Mail.Read", true, "Read mail in all mailboxes"),
        ["p-cal-all"] = ("Calendars.ReadWrite", true, "Read and write calendars in all mailboxes"),
    };

    List<AppRegistration>? _registrations;
    List<EnterpriseApp>? _enterprise;
    readonly Dictionary<string, EnterpriseAppDetail> _enterpriseDetails = [];

    static AppCredential Secret(string id, string name, int startDays, int endDays) =>
        new(id, true, name, Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..3], DateTimeOffset.UtcNow.AddDays(startDays),
            DateTimeOffset.UtcNow.AddDays(endDays));

    static AppCredential Certificate(string id, string name, int startDays, int endDays) =>
        new(id, false, name, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(id))), DateTimeOffset.UtcNow.AddDays(startDays),
            DateTimeOffset.UtcNow.AddDays(endDays));

    static RequestedPermission[] Asks(params string[] ids) => [.. ids.Select(i => new RequestedPermission(GraphApi, i, GraphPermissions[i].App))];

    async Task EnsureAppsAsync(CancellationToken cancellationToken)
    {
        if (_registrations is not null)
            return;
        var people = (await directory.GetUsersAsync(cancellationToken)).Where(u => u.Kind == UserKind.Member).Take(4).ToArray();
        AppOwner Owner(int i) => new(people[i % people.Length].Id, people[i % people.Length].DisplayName, people[i % people.Length].UserPrincipalName);
        DateTimeOffset Ago(int days) => DateTimeOffset.UtcNow.AddDays(-days);
        _registrations =
        [
            new("app-payroll", "6f1c2a10-0000-4000-8000-000000000001", "Payroll Export", "AzureADMyOrg", Ago(700),
                [Secret("k-payroll-1", "Nightly export", -730, -20), Certificate("k-payroll-2", "CN=payroll-export", -40, 325)], Asks("p-users-all"), [], [Owner(0)]),
            new("app-helpdesk", "6f1c2a10-0000-4000-8000-000000000002", "Help Desk Bot", "AzureADMyOrg", Ago(400),
                [Secret("k-helpdesk-1", "Bot secret", -353, 12)], Asks("p-user-read", "p-mail-send"), [("Web", "https://helpdesk.northgate.example/auth")], []),
            new("app-website", "6f1c2a10-0000-4000-8000-000000000003", "Website Sign-In", "AzureADandPersonalMicrosoftAccount", Ago(300),
                [], Asks("p-user-read", "p-offline"), [("Single-Page App", "https://www.northgate.example/signin"), ("Single-Page App", "http://localhost:5173")], [Owner(1), Owner(2)],
                "Public website sign-in. Contact the web team before changing redirect URIs."),
            new("app-backup", "6f1c2a10-0000-4000-8000-000000000004", "Backup Service", "AzureADMyOrg", Ago(90),
                [Certificate("k-backup-1", "CN=backup-service", -90, 640)], Asks("p-sites-all", "p-mail-all"), [], [Owner(0)]),
            new("app-reports", "6f1c2a10-0000-4000-8000-000000000005", "Old Reporting Tool", "AzureADMyOrg", Ago(1500),
                [Secret("k-reports-1", "Reports", -1500, -800), Secret("k-reports-2", "Reports 2", -800, -70)], Asks("p-users-all"), [("Web", "https://reports.northgate.example/")], []),
            new("app-scanner", "6f1c2a10-0000-4000-8000-000000000006", "Scanner to Email", "AzureADMyOrg", Ago(60),
                [Secret("k-scanner-1", "Scanner", -60, 670)], Asks("p-mail-send"), [("Mobile and Desktop", "https://login.microsoftonline.com/common/oauth2/nativeclient")], [Owner(3)]),
        ];
        EnterpriseApp Ours(AppRegistration r, bool enabled = true, bool required = false) =>
            new($"sp-{r.Id[4..]}", r.AppId, r.Name, "Northgate", false, AppSource.ThisOrganization, "Application", enabled, required, true, true, r.Created);
        _enterprise =
        [
            .. _registrations.Select(r => Ours(r, enabled: r.Id != "app-reports", required: r.Id == "app-payroll")),
            new(GraphSp, GraphApi, "Microsoft Graph", "Microsoft Services", true, AppSource.Microsoft, "Application", true, false, false, false, Ago(1800)),
            new("sp-exo", "00000002-0000-0ff1-ce00-000000000000", "Office 365 Exchange Online", "Microsoft Services", true, AppSource.Microsoft, "Application", true, false, false, false, Ago(1800)),
            new("sp-teams", "1fec8e78-bce4-4aaf-ab1b-5451cc387264", "Microsoft Teams", "Microsoft Services", true, AppSource.Microsoft, "Application", true, false, true, false, Ago(1800)),
            new("sp-graphcli", "14d82eec-204b-4c2f-b7e8-296a70dab67e", "Microsoft Graph Command Line Tools", "Microsoft Services", true, AppSource.Microsoft, "Application", true, false, true, true, Ago(500)),
            new("sp-acme", "a1a1a1a1-0000-4000-8000-00000000acme", "Acme Scheduling", "Acme Inc.", true, AppSource.ThirdParty, "Application", true, true, true, true, Ago(380),
                "saml", "https://scheduling.acme.example"),
            new("sp-fabrikam", "f0f0f0f0-0000-4000-8000-0000fabrikam", "Fabrikam Whiteboard", "Fabrikam", false, AppSource.ThirdParty, "Application", true, false, true, true, Ago(45)),
            new("sp-tailspin", "7a7a7a7a-0000-4000-8000-00000tailspin", "Tailspin Survey", "Tailspin Toys", false, AppSource.ThirdParty, "Application", false, false, false, true, Ago(900),
                Notes: "Turned off after the survey ended."),
            new("sp-litware", "1e1e1e1e-0000-4000-8000-0000litware", "Litware Calendar Sync", "Litware", true, AppSource.ThirdParty, "Application", true, false, true, true, Ago(200)),
        ];
        var groups = await directory.GetGroupsAsync(cancellationToken);
        var everyone = groups.FirstOrDefault(g => g.Kind == GroupKind.Security) ?? groups.FirstOrDefault();
        IReadOnlyList<(string, string)> Plain = [(AppUserAssignment.DefaultAccess, "Default Access")];
        _enterpriseDetails["sp-acme"] = new(
            [
                .. everyone is null ? Array.Empty<AppUserAssignment>() : [new AppUserAssignment("as-acme-1", everyone.Id, everyone.DisplayName, "Group", "r-acme-user", "User", Ago(380))],
                new AppUserAssignment("as-acme-2", people[0].Id, people[0].DisplayName, "User", "r-acme-admin", "Scheduler Admin", Ago(200)),
            ],
            [new DelegatedGrant("g-acme", GraphSp, "Microsoft Graph", null, null, ["User.Read"])], [], [Owner(0)],
            [(AppUserAssignment.DefaultAccess, "Default Access"), ("r-acme-user", "User"), ("r-acme-admin", "Scheduler Admin")]);
        _enterpriseDetails["sp-fabrikam"] = new([],
            [
                new DelegatedGrant("g-fab-1", GraphSp, "Microsoft Graph", people[1].Id, people[1].DisplayName, ["User.Read", "Files.ReadWrite", "offline_access"]),
                new DelegatedGrant("g-fab-2", GraphSp, "Microsoft Graph", people[2].Id, people[2].DisplayName, ["User.Read", "Files.ReadWrite"]),
            ], [], [], Plain);
        _enterpriseDetails["sp-litware"] = new([], [], [new AppPermissionGrant("ar-lit-1", GraphSp, "Microsoft Graph", "p-cal-all", "Calendars.ReadWrite", Ago(200))], [], Plain);
        _enterpriseDetails["sp-payroll"] = new([new AppUserAssignment("as-pay-1", people[0].Id, people[0].DisplayName, "User", AppUserAssignment.DefaultAccess, "Default Access", Ago(600))],
            [], [new AppPermissionGrant("ar-pay-1", GraphSp, "Microsoft Graph", "p-users-all", "User.Read.All", Ago(690))], [Owner(0)], Plain);
        _enterpriseDetails["sp-helpdesk"] = new([], [new DelegatedGrant("g-help", GraphSp, "Microsoft Graph", null, null, ["User.Read", "Mail.Send"])], [], [], Plain);
    }

    public async Task<AppDirectory> GetAppsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAppsAsync(cancellationToken);
        return new AppDirectory([.. _registrations!], [.. _enterprise!]);
    }

    public async Task<AppRegistrationDetail> GetRegistrationDetailAsync(AppRegistration app, CancellationToken cancellationToken = default)
    {
        await EnsureAppsAsync(cancellationToken);
        var sp = _enterprise!.FirstOrDefault(e => e.AppId == app.AppId);
        var detail = sp is null ? null : _enterpriseDetails.GetValueOrDefault(sp.Id);
        return new AppRegistrationDetail([.. app.Requested.Select(p =>
        {
            var (name, isApp, description) = GraphPermissions[p.PermissionId];
            var consented = isApp
                ? detail?.AppPermissions.Any(a => a.RoleId == p.PermissionId) == true
                : detail?.DelegatedGrants.Any(g => g.PrincipalId is null && g.Scopes.Contains(name)) == true;
            return new NamedPermission(GraphSp, "Microsoft Graph", p.PermissionId, name, isApp, description, consented);
        })]);
    }

    public async Task<EnterpriseAppDetail> GetEnterpriseDetailAsync(EnterpriseApp app, CancellationToken cancellationToken = default)
    {
        await EnsureAppsAsync(cancellationToken);
        return _enterpriseDetails.GetValueOrDefault(app.Id) ?? new EnterpriseAppDetail([], [], [], [], [(AppUserAssignment.DefaultAccess, "Default Access")]);
    }

    public Task<DateTimeOffset?> GetLastSignInAsync(EnterpriseApp app, CancellationToken cancellationToken = default) =>
        Task.FromResult<DateTimeOffset?>(app.IsEnabled && app.Source != AppSource.Microsoft ? DateTimeOffset.UtcNow.AddHours(-(app.Name.Length * 7 % 90)) : null);
}
