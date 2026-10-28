namespace Tenantry.Core.Permissions;

/// <summary>One API the app calls, and the delegated permissions it needs there.</summary>
/// <param name="ResourceAppId">The API's well-known application ID.</param>
/// <param name="ResourceName">Shown in the setup wizard.</param>
/// <param name="Audience">Token audience; a token request asks for <c>{Audience}/.default</c>.</param>
/// <param name="DelegatedScopes">Permission names, as the API's service principal publishes them.</param>
public sealed record ApiPermissions(
    string ResourceAppId,
    string ResourceName,
    string Audience,
    IReadOnlyList<string> DelegatedScopes)
{
    public string DefaultScope => $"{Audience}/.default";
}

/// <summary>
/// The single place that says what Tenantry's per-tenant app registration needs. The setup
/// wizard, the manual-setup script and the consent check all read this list, so they can't
/// drift apart. Scope GUIDs are deliberately absent: the wizard looks each name up on the
/// tenant's own copy of the API, which is authoritative and survives Microsoft reissuing IDs.
///
/// These are delegated permissions. The signed-in admin's own roles still limit what any call
/// can do, so a broad grant here does not widen anyone's access.
/// </summary>
public static class TenantryPermissions
{
    public const string GraphAppId = "00000003-0000-0000-c000-000000000000";
    public const string ExchangeAppId = "00000002-0000-0ff1-ce00-000000000000";
    public const string SharePointAppId = "00000003-0000-0ff1-ce00-000000000000";

    /// <summary>
    /// Microsoft Graph Command Line Tools (the client behind Connect-MgGraph): the setup
    /// fallback when no bootstrap app is configured. It may request any Graph permission
    /// dynamically. Azure PowerShell's client (1950a258-...) does NOT work for this: Entra
    /// refuses it with AADSTS65002 because Graph has not pre-authorised it for these scopes
    /// (seen 2026-10-20).
    /// </summary>
    public const string GraphCommandLineToolsClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    public static readonly ApiPermissions Graph = new(GraphAppId, "Microsoft Graph", "https://graph.microsoft.com",
    [
        "openid", "profile", "offline_access",
        "User.ReadWrite.All",
        "Group.ReadWrite.All",
        "Directory.ReadWrite.All",
        "Organization.Read.All",
        "AuditLog.Read.All",
        "Reports.Read.All",
        "UserAuthenticationMethod.ReadWrite.All",
        "MailboxSettings.ReadWrite",
        "Policy.Read.All",
        "Policy.ReadWrite.ConditionalAccess",
        "Application.ReadWrite.All",
        "RoleManagement.ReadWrite.Directory",
        "Sites.FullControl.All",
        "DeviceManagementManagedDevices.ReadWrite.All",
        "DeviceManagementConfiguration.ReadWrite.All",
        "DeviceManagementApps.ReadWrite.All",
        "ExchangeMessageTrace.Read.All",
        "eDiscovery.ReadWrite.All",
        "AuditLogsQuery.Read.All",
        "RecordsManagement.ReadWrite.All",
        "InformationProtectionPolicy.Read", // the .All form is application-only
        "SecurityAlert.ReadWrite.All",
        // Added for user management (Phase 4, 2026-10-21). Directory.ReadWrite.All does NOT
        // cover passwords: setting one needs this permission (found live: "Insufficient privileges").
        "User-PasswordProfile.ReadWrite.All",
        "IdentityRiskyUser.ReadWrite.All",
        "DelegatedPermissionGrant.ReadWrite.All",
        "AppRoleAssignment.ReadWrite.All",
        "BitlockerKey.Read.All",
        "DeviceLocalCredential.Read.All",
        // Devices (Phase 6, 2026-10-27): wipe, retire, locate, lost mode and BitLocker key
        // rotation are "privileged operations"; Autopilot is Intune's service configuration
        // (listing it was refused without this, seen live).
        "DeviceManagementManagedDevices.PrivilegedOperations.All",
        "DeviceManagementServiceConfig.ReadWrite.All",
        // Conditional Access (Phase 8, 2026-10-28): a policy can require accepting terms of use;
        // listing them was refused without this (seen live).
        "Agreement.Read.All",
        // Authentication methods and risk (Phase 8d, 2026-10-28): Policy.Read.All reads the
        // methods policy and security defaults but can't change them; risk detections and risky
        // apps answered "required scopes are missing" without the risk-event read (seen live).
        "Policy.ReadWrite.AuthenticationMethod",
        "Policy.ReadWrite.SecurityDefaults",
        "IdentityRiskEvent.Read.All",
        "IdentityRiskyServicePrincipal.ReadWrite.All",
    ]);

    public static readonly ApiPermissions Exchange = new(ExchangeAppId, "Office 365 Exchange Online", "https://outlook.office365.com",
    [
        "Exchange.Manage",
    ]);

    /// <summary>
    /// SharePoint's audience is per tenant (<c>https://{tenant}.sharepoint.com</c>), so the
    /// audience here is a placeholder; callers build the real one from the tenant's domain.
    /// </summary>
    public static readonly ApiPermissions SharePoint = new(SharePointAppId, "Office 365 SharePoint Online", "https://{tenant}.sharepoint.com",
    [
        "AllSites.FullControl",
    ]);

    public static IReadOnlyList<ApiPermissions> All { get; } = [Graph, Exchange, SharePoint];

    /// <summary>
    /// What a permission lets Tenantry do, in words, for the "grant missing permissions" banner. Ones
    /// added after the first release are listed, since those are the ones a tenant can be missing.
    /// </summary>
    public static string? Purpose(string scope) => scope switch
    {
        "User-PasswordProfile.ReadWrite.All" => "resetting passwords",
        "IdentityRiskyUser.ReadWrite.All" => "confirming or dismissing risky users",
        "DelegatedPermissionGrant.ReadWrite.All" => "revoking app consents",
        "AppRoleAssignment.ReadWrite.All" => "removing app assignments",
        "BitlockerKey.Read.All" => "showing BitLocker recovery keys",
        "DeviceLocalCredential.Read.All" => "showing local admin passwords",
        "DeviceManagementManagedDevices.PrivilegedOperations.All" => "wiping, retiring and locating devices",
        "DeviceManagementServiceConfig.ReadWrite.All" => "Autopilot",
        "Agreement.Read.All" => "requiring terms of use in Conditional Access",
        "Policy.ReadWrite.AuthenticationMethod" => "changing which sign-in methods people can use",
        "Policy.ReadWrite.SecurityDefaults" => "turning security defaults on or off",
        "IdentityRiskEvent.Read.All" => "showing risk detections",
        "IdentityRiskyServicePrincipal.ReadWrite.All" => "showing and dismissing risky apps",
        _ => null,
    };

    /// <summary>What the setup sign-in needs in order to create and consent the app registration.</summary>
    public static IReadOnlyList<string> BootstrapScopes { get; } =
    [
        "https://graph.microsoft.com/Application.ReadWrite.All",
        "https://graph.microsoft.com/DelegatedPermissionGrant.ReadWrite.All",
        "https://graph.microsoft.com/AppRoleAssignment.ReadWrite.All",
        "https://graph.microsoft.com/Organization.Read.All",
    ];
}
