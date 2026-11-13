namespace Tenantry.Core.Permissions;

/// <summary>One API the app calls, and the delegated permissions it needs there.</summary>
/// <param name="ResourceAppId">The API's well-known application ID.</param>
/// <param name="ResourceName">Shown in the setup wizard.</param>
/// <param name="Audience">Token audience; a token request asks for <c>{Audience}/.default</c>.</param>
/// <param name="DelegatedScopes">Permission names, as the API's service principal publishes them.</param>
/// <param name="CreateIfMissing">Microsoft's service principal for the API may not exist in a tenant
/// yet; setup creates it (as Microsoft's own instructions do) rather than skipping the API.</param>
public sealed record ApiPermissions(
    string ResourceAppId,
    string ResourceName,
    string Audience,
    IReadOnlyList<string> DelegatedScopes,
    bool CreateIfMissing = false)
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

    /// <summary>
    /// Microsoft's "Exchange REST API Based Powershell" client, used only for Purview calls.
    /// Since 2025 Purview runs searches, search actions, holds and retention changes only for a
    /// "search-only" session (Connect-IPPSSession -EnableSearchOnlySession), which is a token for
    /// <see cref="SearchOnlyAudience"/>; an organization's own app can't be granted that audience
    /// (AADSTS650057, seen live), so this client signs in a second time for it.
    /// </summary>
    public const string ExchangePowerShellClientId = "fb78d390-0c51-40cd-8e17-fdbfab77341b";

    /// <summary>The audience of a search-only session's token (sent to the same Purview endpoint).</summary>
    public const string SearchOnlyAudience = "https://dataservice.o365filtering.com";

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
        // Reported messages (Phase 12d, 2026-11-10): the submissions list and sending a message,
        // link or file to Microsoft (security/threatSubmission answered 401 without it, seen live).
        "ThreatSubmission.ReadWrite.All",
        // Intune scripts and remediations, and Intune roles and scope tags (Phase 13, 2026-11-11):
        // their lists were refused (403) without these, seen live.
        "DeviceManagementScripts.ReadWrite.All",
        "DeviceManagementRBAC.ReadWrite.All",
        // Organization area (Phase 15, 2026-11-13): service health, Message Center, organization
        // settings, Teams and the rest of the admin centers. Each list below answered 403 without
        // its permission (seen live), and each name was checked against Graph's published scopes.
        "ServiceHealth.Read.All",
        "ServiceMessage.Read.All",
        "ServiceMessageViewpoint.Write",
        "OnPremDirectorySynchronization.ReadWrite.All",
        "SharePointTenantSettings.ReadWrite.All",
        "OrgSettings-Microsoft365Install.ReadWrite.All",
        "OrgSettings-Forms.ReadWrite.All",
        "OrgSettings-Todo.ReadWrite.All",
        "OrgSettings-AppsAndServices.ReadWrite.All",
        "OrgSettings-DynamicsVoice.ReadWrite.All",
        "ReportSettings.ReadWrite.All",
        "PeopleSettings.ReadWrite.All",
        "Organization.ReadWrite.All",
        "OrganizationalBranding.ReadWrite.All",
        "Policy.ReadWrite.Authorization",
        "Policy.ReadWrite.CrossTenantAccess",
        "Policy.ReadWrite.ExternalIdentities",
        "Policy.ReadWrite.ConsentRequest",
        "Policy.ReadWrite.PermissionGrant",
        "Policy.ReadWrite.ApplicationConfiguration",
        "Policy.ReadWrite.FeatureRollout",
        "Domain.ReadWrite.All",
        "AdministrativeUnit.ReadWrite.All",
        "Team.ReadBasic.All",
        "Team.Create",
        "TeamSettings.ReadWrite.All",
        "TeamMember.ReadWrite.All",
        "Channel.ReadBasic.All",
        "Channel.Create",
        "Channel.Delete.All",
        "ChannelSettings.ReadWrite.All",
        "ChannelMember.ReadWrite.All",
        "TeamworkAppSettings.ReadWrite.All",
        "AppCatalog.ReadWrite.All",
        "TeamTemplates.Read",
        "CustomSecAttributeDefinition.ReadWrite.All",
        "CustomSecAttributeAssignment.ReadWrite.All",
        "DirectoryRecommendations.ReadWrite.All",
        "AccessReview.ReadWrite.All",
        "EntitlementManagement.ReadWrite.All",
        "LifecycleWorkflows.ReadWrite.All",
        "BrowserSiteLists.ReadWrite.All",
        "WindowsUpdates.ReadWrite.All",
        "Place.Read.All",
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

    public const string PurviewEDiscoveryAppId = "b26e684c-5068-4120-a679-64a5d2c909d9";

    /// <summary>
    /// Microsoft Purview eDiscovery (8f-2): downloading an export's files needs a token for this
    /// app (its ID is its audience) with eDiscovery.Download.Read, sent with X-AllowWithAADToken.
    /// </summary>
    public static readonly ApiPermissions PurviewEDiscovery = new(PurviewEDiscoveryAppId, "Microsoft Purview eDiscovery", PurviewEDiscoveryAppId,
    [
        "eDiscovery.Download.Read",
    ], CreateIfMissing: true);

    public const string TeamsAdminAppId = "48ac35b8-9aa8-4d74-927d-1f4a14a0b239";

    /// <summary>
    /// Skype and Teams Tenant Admin API (Phase 15e): Teams policies and organization-wide Teams
    /// settings, which Graph doesn't cover. Its ID is its audience (docs/research/teams-admin-access.md).
    /// </summary>
    public static readonly ApiPermissions TeamsAdmin = new(TeamsAdminAppId, "Skype and Teams Tenant Admin API", TeamsAdminAppId,
    [
        "user_impersonation",
    ], CreateIfMissing: true);

    public static IReadOnlyList<ApiPermissions> All { get; } = [Graph, Exchange, SharePoint, PurviewEDiscovery, TeamsAdmin];

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
        "ThreatSubmission.ReadWrite.All" => "showing reported messages and sending messages to Microsoft for review",
        "DeviceManagementScripts.ReadWrite.All" => "Intune scripts and remediations",
        "DeviceManagementRBAC.ReadWrite.All" => "Intune roles and scope tags",
        "IdentityRiskyServicePrincipal.ReadWrite.All" => "showing and dismissing risky apps",
        "eDiscovery.Download.Read" => "downloading eDiscovery exports",
        "user_impersonation" => "Teams policies and organization-wide Teams settings",
        "ServiceHealth.Read.All" => "service health",
        "ServiceMessage.Read.All" => "the Message Center",
        "ServiceMessageViewpoint.Write" => "marking Message Center posts read, archived or favorite",
        "OnPremDirectorySynchronization.ReadWrite.All" => "Entra Connect sync status and settings",
        "SharePointTenantSettings.ReadWrite.All" => "SharePoint organization settings",
        "OrgSettings-Microsoft365Install.ReadWrite.All" => "Microsoft 365 apps installation options",
        "OrgSettings-Forms.ReadWrite.All" => "Microsoft Forms settings",
        "OrgSettings-Todo.ReadWrite.All" => "Microsoft To Do settings",
        "OrgSettings-AppsAndServices.ReadWrite.All" => "user-owned apps and services settings",
        "OrgSettings-DynamicsVoice.ReadWrite.All" => "Dynamics 365 voice settings",
        "ReportSettings.ReadWrite.All" => "hiding names in usage reports",
        "PeopleSettings.ReadWrite.All" => "profile cards and pronouns",
        "Organization.ReadWrite.All" => "the company profile and release preferences",
        "OrganizationalBranding.ReadWrite.All" => "sign-in page branding",
        "Policy.ReadWrite.Authorization" => "user settings such as who can invite guests or register apps",
        "Policy.ReadWrite.CrossTenantAccess" => "cross-tenant access settings",
        "Policy.ReadWrite.ExternalIdentities" => "external user leave settings",
        "Policy.ReadWrite.ConsentRequest" => "admin consent requests",
        "Policy.ReadWrite.PermissionGrant" => "user consent settings",
        "Policy.ReadWrite.ApplicationConfiguration" => "token lifetime and sign-out policies",
        "Policy.ReadWrite.FeatureRollout" => "staged rollout of cloud sign-in",
        "Domain.ReadWrite.All" => "adding, verifying and removing domains",
        "AdministrativeUnit.ReadWrite.All" => "administrative units",
        "Team.ReadBasic.All" => "listing teams",
        "Team.Create" => "creating teams",
        "TeamSettings.ReadWrite.All" => "team settings",
        "TeamMember.ReadWrite.All" => "team owners and members",
        "Channel.ReadBasic.All" => "listing channels",
        "Channel.Create" => "creating channels",
        "Channel.Delete.All" => "deleting channels",
        "ChannelSettings.ReadWrite.All" => "channel settings",
        "ChannelMember.ReadWrite.All" => "private and shared channel members",
        "TeamworkAppSettings.ReadWrite.All" => "Teams app settings",
        "AppCatalog.ReadWrite.All" => "your organization's Teams apps",
        "TeamTemplates.Read" => "team templates",
        "CustomSecAttributeDefinition.ReadWrite.All" => "custom security attributes",
        "CustomSecAttributeAssignment.ReadWrite.All" => "setting custom security attributes on people and apps",
        "DirectoryRecommendations.ReadWrite.All" => "Entra recommendations",
        "AccessReview.ReadWrite.All" => "access reviews",
        "EntitlementManagement.ReadWrite.All" => "access packages",
        "LifecycleWorkflows.ReadWrite.All" => "lifecycle workflows",
        "BrowserSiteLists.ReadWrite.All" => "Edge Internet Explorer mode site lists",
        "WindowsUpdates.ReadWrite.All" => "Windows Autopatch deployments",
        "Place.Read.All" => "room and workspace lists",
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
