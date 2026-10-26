using static Tenantry.Core.Capabilities.DirectoryRoles;

namespace Tenantry.Core.Capabilities;

/// <summary>The catalogue of gated features. Each area adds its own entries as it is built.</summary>
public static class Capabilities
{
    static readonly string[] EntraP1 = ["AAD_PREMIUM", "AAD_PREMIUM_P2"];
    const string EntraP1Label = "Microsoft Entra ID P1 or P2";

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

    public static IReadOnlyList<Capability> All { get; } =
        [ReadUsers, EditUsers, ResetPasswords, ManageAuthMethods, ReadSignInLogs, ReadAuditLogs, ManageMailboxes, ManageMailSecurity, ManageConditionalAccess];
}
