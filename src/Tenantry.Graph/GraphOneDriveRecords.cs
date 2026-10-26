using System.Net;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>The OneDrive tab's reads (4d-1): the drive from Graph, who has access from the SharePoint admin site.</summary>
public sealed partial class GraphUserRecords : IOneDriveRecords
{
    public async Task<Available<OneDriveInfo>> GetOneDriveAsync(string userId, CancellationToken cancellationToken = default)
    {
        JsonElement drive;
        try
        {
            drive = await graph.GetAsync($"users/{userId}/drive?$select=webUrl,quota,sharePointIds,lastModifiedDateTime", cancellationToken);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
        {
            // "mysite not found": SharePoint creates a OneDrive the first time they open it.
            return new Available<OneDriveInfo>(null);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return Available<OneDriveInfo>.Because("Reading OneDrive needs the SharePoint Administrator role.");
        }
        var quota = drive.TryGetProperty("quota", out var q) ? q : default;
        var ids = drive.TryGetProperty("sharePointIds", out var s) ? s : default;
        return new(new OneDriveInfo(
            Text(drive, "webUrl")?.TrimEnd('/') ?? "",
            Text(ids, "siteId") ?? "",
            Size(quota, "used"), Size(quota, "total"), Size(quota, "deleted"),
            Text(quota, "state"),
            When(drive, "lastModifiedDateTime")));
    }

    public Task<IReadOnlyList<SiteAdmin>> GetOneDriveAdminsAsync(OneDriveInfo drive, CancellationToken cancellationToken = default) =>
        (sharePoint ?? throw new InvalidOperationException("SharePoint isn't connected for this tenant."))
        .GetSiteAdministratorsAsync(drive.SiteId, cancellationToken);

    static long? Size(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
}
