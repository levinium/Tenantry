namespace Tenantry.Core.Directory;

public enum UserKind
{
    Member,
    Guest,
}

/// <summary>
/// The fields a user list row needs. Detail pages load more; this stays small because a tenant's
/// whole directory of these is held in memory for instant filtering.
/// </summary>
public sealed record UserSummary(
    string Id,
    string DisplayName,
    string UserPrincipalName,
    string? Mail,
    string? Department,
    string? JobTitle,
    bool AccountEnabled,
    UserKind Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSignIn,
    IReadOnlyList<string> Licenses,
    MailboxInfo? Mailbox = null,
    UserSecurity? Security = null,
    IReadOnlyList<OfficeActivation>? Activations = null,
    bool SyncedFromOnPremises = false,
    string? UsageLocation = null)
{
    /// <summary>
    /// Why on-premises Active Directory, not Microsoft 365, decides this: synced people's names,
    /// details and sign-in name are changed there and copied up by Entra Connect.
    /// </summary>
    public const string SyncedReason = "Synced from on-premises Active Directory: change it there, and Entra Connect copies it up.";
}
