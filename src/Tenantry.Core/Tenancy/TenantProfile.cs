namespace Tenantry.Core.Tenancy;

/// <summary>
/// One tenant the app can sign in to. Stored only in the OS app-data folder, never in the repo.
/// </summary>
/// <param name="Id">Local identifier for this profile.</param>
/// <param name="DisplayName">What the admin calls this tenant in the picker.</param>
/// <param name="TenantId">Entra tenant ID (GUID).</param>
/// <param name="PrimaryDomain">The tenant's default domain, shown for recognition.</param>
/// <param name="ClientId">The tenant's own Tenantry app registration (application ID).</param>
/// <param name="LastAccount">The account last used, offered as the sign-in hint.</param>
/// <param name="IsDefault">Opens automatically when Tenantry starts. At most one profile has it.</param>
public sealed record TenantProfile(
    string Id,
    string DisplayName,
    string TenantId,
    string PrimaryDomain,
    string ClientId,
    DateTimeOffset CreatedAt,
    string? LastAccount = null,
    DateTimeOffset? LastUsedAt = null,
    bool IsDefault = false);
