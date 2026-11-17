using System.Net;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// The OneDrive tab's reads (4d-1): the drive from Graph, or from the SharePoint admin site when
/// Graph won't show it; who has access from the admin site.
/// </summary>
public sealed partial class GraphUserRecords : IOneDriveRecords
{
    public async Task<Available<OneDriveInfo>> GetOneDriveAsync(string userId, CancellationToken cancellationToken = default)
    {
        JsonElement drive;
        try
        {
            drive = await graph.GetAsync($"users/{userId}/drive?$select=webUrl,quota,sharePointIds,lastModifiedDateTime", cancellationToken);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // Graph shows a OneDrive only to people who can open it: an admin who isn't one of its
            // owners gets 403 (seen live), and one not set up yet answers 404 ("mysite not found";
            // SharePoint creates it the first time they open it). The admin site sees every one.
            if (sharePoint is null)
                return e.Status == HttpStatusCode.NotFound ? new Available<OneDriveInfo>(null)
                    : Available<OneDriveInfo>.Because(MicrosoftReply.After("Reading OneDrive needs the SharePoint Administrator role.", e));
            return await GetOneDriveFromAdminSiteAsync(userId, cancellationToken);
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

    /// <summary>
    /// Their OneDrive as the SharePoint admin site describes it (Get-SPOSite): its address from their
    /// sign-in name, then its properties. Sizes there are in MB; "nearly full" is past the drive's
    /// own warning level.
    /// </summary>
    async Task<Available<OneDriveInfo>> GetOneDriveFromAdminSiteAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            var user = await graph.GetAsync($"users/{userId}?$select=userPrincipalName", cancellationToken);
            if (Text(user, "userPrincipalName") is not { } upn
                || await sharePoint!.GetPersonalSiteUrlAsync(upn, cancellationToken) is not { } url
                || await sharePoint.GetSitePropertiesAsync(url, cancellationToken) is not { } site)
                return new Available<OneDriveInfo>(null);
            const long Mb = 1024 * 1024;
            var used = Csom.Number(site, "StorageUsage");
            var total = Csom.Number(site, "StorageMaximumLevel");
            var warning = Csom.Number(site, "StorageWarningLevel");
            var state = total > 0 && used >= total ? "exceeded" : warning > 0 && used >= warning ? "nearing" : "normal";
            return new(new OneDriveInfo(
                (Csom.Text(site, "Url") ?? url).TrimEnd('/'),
                Csom.Guid(site, "SiteId") ?? "",
                used * Mb, total > 0 ? total * Mb : null, null,
                state,
                Csom.Date(site, "LastContentModifiedDate")));
        }
        catch (SharePointException e)
        {
            return Available<OneDriveInfo>.Because(MicrosoftReply.After("Reading OneDrive needs the SharePoint Administrator role.", e));
        }
    }

    public Task<IReadOnlyList<SiteAdmin>> GetOneDriveAdminsAsync(OneDriveInfo drive, CancellationToken cancellationToken = default) =>
        (sharePoint ?? throw new InvalidOperationException("SharePoint isn't connected for this tenant."))
        .GetSiteAdministratorsAsync(drive.SiteId, cancellationToken);

    static long? Size(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
}
