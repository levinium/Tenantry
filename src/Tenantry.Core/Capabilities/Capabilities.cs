using static Tenantry.Core.Capabilities.DirectoryRoles;

namespace Tenantry.Core.Capabilities;

/// <summary>The catalogue of gated features. Each area adds its own entries as it is built.</summary>
public static class Capabilities
{
    static readonly string[] EntraP1 = ["AAD_PREMIUM", "AAD_PREMIUM_P2"];
    const string EntraP1Label = "Microsoft Entra ID P1 or P2";
    static readonly string[] EntraP2 = ["AAD_PREMIUM_P2"];
    const string EntraP2Label = "Microsoft Entra ID P2";

    public static readonly Capability ReadUsers = new("users.read", "View Users", []);

    public static readonly Capability EditUsers = new("users.edit", "Create and Edit Users",
        [UserAdministrator]);

    public static readonly Capability ResetPasswords = new("users.resetPassword", "Reset Passwords",
        [UserAdministrator, HelpdeskAdministrator, AuthenticationAdministrator, PrivilegedAuthenticationAdministrator]);

    public static readonly Capability ManageAuthMethods = new("users.authMethods", "Manage Authentication Methods",
        [AuthenticationAdministrator, PrivilegedAuthenticationAdministrator]);

    public static readonly Capability ReadSignInLogs = new("logs.signIns", "View Sign-In Logs",
        [ReportsReader, SecurityReader, SecurityAdministrator, GlobalReader],
        EntraP1, EntraP1Label);

    public static readonly Capability ReadAuditLogs = new("logs.audit", "View Directory Audit Logs",
        [ReportsReader, SecurityReader, SecurityAdministrator, GlobalReader]);

    public static readonly Capability ManageMailboxes = new("mail.mailboxes", "Manage Mailboxes",
        [ExchangeAdministrator, ExchangeRecipientAdministrator]);

    public static readonly Capability ManageConditionalAccess = new("security.conditionalAccess", "Manage Conditional Access",
        [ConditionalAccessAdministrator, SecurityAdministrator],
        EntraP1, EntraP1Label);

    /// <summary>Mail & security: quarantine, message trace, mail flow rules, block and allow lists.</summary>
    public static readonly Capability ManageMailSecurity = new("mail.security", "Manage Mail Security (Quarantine, Rules, Block Lists)",
        [ExchangeAdministrator, SecurityAdministrator]);

    /// <summary>Devices: Intune remote actions, and enabling, disabling or deleting devices in Entra.</summary>
    public static readonly Capability ManageDevices = new("devices.manage", "Manage Devices",
        [IntuneAdministrator, CloudDeviceAdministrator]);

    /// <summary>SharePoint: site admins and members, sharing, storage, lock, archive, and creating, deleting and restoring sites.</summary>
    public static readonly Capability ManageSites = new("sites.manage", "Manage SharePoint Sites and OneDrives",
        [SharePointAdministrator]);

    /// <summary>Admin roles: giving and taking away roles, and custom roles (Entra ID P1 for custom roles).</summary>
    public static readonly Capability ManageRoles = new("roles.manage", "Manage Admin Roles",
        [PrivilegedRoleAdministrator]);

    /// <summary>Apps: credentials, owners, consents, assignments, sign-in settings, and making, deleting and restoring apps.</summary>
    public static readonly Capability ManageApps = new("apps.manage", "Manage App Registrations and Enterprise Apps",
        [ApplicationAdministrator, CloudApplicationAdministrator]);

    /// <summary>The authentication methods policy (which sign-in methods, for whom) and security defaults.</summary>
    public static readonly Capability ManageAuthPolicy = new("security.authMethodsPolicy", "Manage Sign-In Methods",
        [AuthenticationPolicyAdministrator]);

    /// <summary>Identity Protection: confirming people and apps compromised, or dismissing their risk.</summary>
    public static readonly Capability ManageRisk = new("security.risk", "Manage Risky Users and Apps",
        [SecurityAdministrator], EntraP2, EntraP2Label);

    /// <summary>
    /// eDiscovery: cases, searches, holds. Purview also checks its own role groups (eDiscovery
    /// Manager), so a refusal from Purview names what's missing.
    /// </summary>
    public static readonly Capability ManageEDiscovery = new("compliance.ediscovery", "Manage eDiscovery Cases, Searches and Holds",
        [ComplianceAdministrator]);

    public static IReadOnlyList<Capability> All { get; } =
        [ReadUsers, EditUsers, ResetPasswords, ManageAuthMethods, ReadSignInLogs, ReadAuditLogs, ManageMailboxes, ManageMailSecurity, ManageConditionalAccess, ManageDevices,
            ManageSites, ManageRoles, ManageApps, ManageAuthPolicy, ManageRisk, ManageEDiscovery];
}
