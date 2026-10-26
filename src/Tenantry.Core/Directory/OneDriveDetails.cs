namespace Tenantry.Core.Directory;

/// <summary>Their OneDrive: where it is and how full. Sizes are in bytes.</summary>
/// <param name="SiteId">The SharePoint site collection's ID (who has access is read by it).</param>
/// <param name="State">Graph's quota state: "normal", "nearing", "critical" or "exceeded".</param>
public sealed record OneDriveInfo(string Url, string SiteId, long? Used, long? Total, long? Deleted, string? State, DateTimeOffset? LastChanged)
{
    public bool IsNearlyFull => State is "nearing" or "critical" or "exceeded";
}

/// <summary>Someone with admin rights on a site: for a OneDrive, its owner and anyone given full access.</summary>
public sealed record SiteAdmin(string LoginName, string Name, string? Email, string? UserPrincipalName);

/// <summary>The OneDrive tab's reads.</summary>
public interface IOneDriveRecords
{
    /// <summary>
    /// Their OneDrive; a null value with no reason when it hasn't been set up yet (it's created on
    /// first sign-in, or on request); a reason when it can't be read.
    /// </summary>
    Task<Available<OneDriveInfo>> GetOneDriveAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Who can open it all: the owner and anyone given access. Needs the SharePoint Administrator role.</summary>
    Task<IReadOnlyList<SiteAdmin>> GetOneDriveAdminsAsync(OneDriveInfo drive, CancellationToken cancellationToken = default);
}
