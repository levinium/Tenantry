namespace Tenantry.Core.Directory;

/// <summary>A license given to a group (group-based licensing): every direct member gets it.</summary>
public sealed record GroupLicense(string SkuId, string Name, IReadOnlyList<string> DisabledPlanIds);

/// <summary>
/// A group's membership and lifecycle settings, read live (G3): when it expires (Microsoft 365
/// groups under an expiration policy), its sensitivity label, the licenses it gives its members, and
/// whether a distribution list can be upgraded to a Microsoft 365 group.
/// </summary>
/// <param name="PolicyDays">The tenant's group lifetime when this group is covered by it; null when it never expires.</param>
/// <param name="LicenseState">QueuedForProcessing, ProcessingInProgress or ProcessingComplete (null: no licenses).</param>
/// <param name="MembersWithErrors">Members the licenses couldn't be given to (null: not read).</param>
/// <param name="UpgradeBlocked">Why a list can't be upgraded (null: it can, or it isn't a list).</param>
public sealed record GroupLifecycle(
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RenewedAt,
    int? PolicyDays,
    string? Label,
    IReadOnlyList<GroupLicense> Licenses,
    IReadOnlyList<LicenseSku> Catalog,
    string? LicenseState,
    int? MembersWithErrors,
    bool CanUpgrade,
    string? UpgradeBlocked);

/// <summary>Reads a group's lifecycle (expiry, label, licenses, upgrade eligibility).</summary>
public interface IGroupLifecycleRecords
{
    Task<GroupLifecycle> GetGroupLifecycleAsync(GroupSummary group, CancellationToken cancellationToken = default);
}
