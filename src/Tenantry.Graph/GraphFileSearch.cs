using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Find Files (16): a place's files, every document library read in full through Graph's drive
/// delta. Delta lists every file and folder in a library, nested or not, and doesn't hit
/// SharePoint's list threshold the way sorting a big library does. It doesn't give paths, so
/// each file's folder is put together from the folders it lists.
/// </summary>
public sealed partial class GraphUserRecords : IFileSearchRecords
{
    const string DeltaSelect = "id,name,size,file,folder,root,parentReference,lastModifiedDateTime,lastModifiedBy,createdDateTime,createdBy,webUrl,deleted";

    public async IAsyncEnumerable<FoundFile> ListFilesAsync(SiteSummary place, IProgress<int>? scanned = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var siteId = await SiteIdAsync(place, cancellationToken);
        var drives = await FilesReadAsync(place, () => graph.GetAsync($"sites/{siteId}/drives?$select=id,name,driveType", cancellationToken));
        var seen = 0;
        foreach (var drive in Rows(drives))
        {
            if (Text(drive, "id") is not { } driveId)
                continue;
            var library = Text(drive, "name") ?? "Documents";
            var folders = new Dictionary<string, (string Name, string? Parent, bool IsRoot)>(StringComparer.Ordinal);
            var files = new List<JsonElement>();
            string? next = $"drives/{driveId}/root/delta?$select={DeltaSelect}";
            while (next is not null)
            {
                var url = next;
                var page = await FilesReadAsync(place, () => graph.GetAsync(url, cancellationToken));
                foreach (var item in Rows(page))
                {
                    if (item.TryGetProperty("deleted", out _) || Text(item, "id") is not { } id)
                        continue;
                    var parent = item.TryGetProperty("parentReference", out var p) && p.ValueKind == JsonValueKind.Object ? Text(p, "id") : null;
                    var isRoot = item.TryGetProperty("root", out _);
                    if (item.TryGetProperty("folder", out _) || isRoot)
                        folders[id] = (Text(item, "name") ?? "", parent, isRoot);
                    if ((item.TryGetProperty("file", out _) || item.TryGetProperty("folder", out _)) && !isRoot)
                        files.Add(item.Clone());
                }
                seen += Rows(page).Count();
                scanned?.Report(seen);
                next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            }
            // Folders are results too (a whole folder can be downloaded); the library's top isn't one.
            foreach (var f in files)
                yield return ToFound(f, driveId, library, FolderOf(f, folders), place);
        }
    }

    /// <summary>Graph's ID for the site at this address ("sites/{host}:/sites/finance").</summary>
    async Task<string> SiteIdAsync(SiteSummary place, CancellationToken cancellationToken)
    {
        var uri = new Uri(place.Url);
        var path = uri.AbsolutePath.TrimEnd('/');
        var site = await FilesReadAsync(place, () => graph.GetAsync(path.Length == 0 ? $"sites/{uri.Host}?$select=id" : $"sites/{uri.Host}:{path}?$select=id", cancellationToken));
        return Text(site, "id") ?? throw new InvalidOperationException("Graph didn't say which site that is.");
    }

    /// <summary>SharePoint lets only a site's members and admins read its files (seen live): say that, not "forbidden".</summary>
    static async Task<JsonElement> FilesReadAsync(SiteSummary place, Func<Task<JsonElement>> read)
    {
        try
        {
            return await read();
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                                       || (e.Status == HttpStatusCode.NotFound && place.IsArchived))
        {
            throw place.IsArchived
                ? new UnauthorizedAccessException("It's archived, so nothing inside it can be read until it's reactivated.")
                : new SiteAccessException();
        }
    }

    /// <summary>"Projects/2026", from the folders delta listed; "" for the library's top.</summary>
    internal static string FolderOf(JsonElement file, IReadOnlyDictionary<string, (string Name, string? Parent, bool IsRoot)> folders)
    {
        var names = new List<string>();
        var at = file.TryGetProperty("parentReference", out var p) && p.ValueKind == JsonValueKind.Object ? Text(p, "id") : null;
        // A folder loop can't happen in a real library; the limit only guards against a bad answer.
        for (var depth = 0; at is not null && depth < 64 && folders.TryGetValue(at, out var folder) && !folder.IsRoot; depth++)
        {
            names.Add(folder.Name);
            at = folder.Parent;
        }
        names.Reverse();
        return string.Join('/', names);
    }

    static FoundFile ToFound(JsonElement f, string driveId, string library, string folder, SiteSummary place)
    {
        static (string? Name, string? Email) Who(JsonElement e, string property) =>
            e.TryGetProperty(property, out var by) && by.ValueKind == JsonValueKind.Object && by.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                ? (Text(u, "displayName"), Text(u, "email"))
                : (null, null);
        var modified = Who(f, "lastModifiedBy");
        var created = Who(f, "createdBy");
        return new FoundFile(Text(f, "id")!, driveId, Text(f, "name") ?? "A file", library, folder, place.Id, place.Title, place.Url,
            Long(f, "size") ?? 0, When(f, "lastModifiedDateTime"), modified.Name, modified.Email, When(f, "createdDateTime"), created.Name, created.Email,
            Text(f, "webUrl"), f.TryGetProperty("folder", out _));
    }

    public async IAsyncEnumerable<FoundFile> ListFolderAsync(FoundFile folder, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Breadth first, with each folder's path below the one asked for.
        var pending = new Queue<(string Id, string Path)>([(folder.Id, "")]);
        var place = new SiteSummary(folder.PlaceId, folder.PlaceUrl, folder.PlaceTitle, "", SiteKind.TeamSite, null, null, null, null, false, false, null, 0, 0, 0, "", "", null, null);
        while (pending.TryDequeue(out var at))
        {
            string? next = $"drives/{folder.DriveId}/items/{at.Id}/children?$select={DeltaSelect}&$top=999";
            while (next is not null)
            {
                var url = next;
                var page = await FilesReadAsync(place, () => graph.GetAsync(url, cancellationToken));
                foreach (var item in Rows(page))
                {
                    if (Text(item, "id") is not { } id)
                        continue;
                    if (item.TryGetProperty("folder", out _))
                        pending.Enqueue((id, at.Path.Length == 0 ? Text(item, "name") ?? id : $"{at.Path}/{Text(item, "name")}"));
                    else if (item.TryGetProperty("file", out _))
                        yield return ToFound(item, folder.DriveId, folder.Library, at.Path, place) with { PlaceTitle = folder.PlaceTitle };
                }
                next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            }
        }
    }

    /// <summary>How long Everywhere waits for Purview before giving up (its search carries on, then is deleted).</summary>
    static readonly TimeSpan EverywhereGiveUp = TimeSpan.FromMinutes(30);

    /// <summary>How often Everywhere asks Purview whether its search is done (tests shorten it).</summary>
    public static TimeSpan EverywhereCheckEvery { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Everywhere: a Purview content search over every site and OneDrive that only counts, read for its
    /// per-place counts, then deleted (even when stopped). Its name says what made it, in case it's seen.
    /// </summary>
    public async Task<IReadOnlyList<EdLocationHits>> CountEverywhereAsync(string query, IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        var name = $"Tenantry Find Files {DateTime.UtcNow:yyyyMMdd-HHmmss}";
        await Compliance("New-ComplianceSearch", new()
        {
            ["Name"] = name,
            ["SharePointLocation"] = new[] { "All" },
            ["ContentMatchQuery"] = query,
            ["Description"] = "Made by Tenantry's Find Files to count where matching files are. Deleted once read.",
        }, cancellationToken);
        try
        {
            await StartEdSearchAsync(name, cancellationToken);
            var started = DateTimeOffset.UtcNow;
            while (true)
            {
                await Task.Delay(EverywhereCheckEvery, cancellationToken);
                var detail = await GetEdSearchAsync(name, cancellationToken);
                var waited = DateTimeOffset.UtcNow - started;
                if (detail.HasRun)
                    return [.. detail.Hits.Where(h => h.Items > 0 && h.Location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))];
                if (detail.Search.Status is "Failed" or "Stopped")
                    throw new InvalidOperationException($"Purview's search {detail.Search.Status.ToLowerInvariant()}{(detail.Errors is { } e ? $": {e}" : ".")}");
                if (waited > EverywhereGiveUp)
                    throw new TimeoutException("Purview hadn't finished after half an hour. Try again later, or choose the places to look in.");
                status?.Report($"Asking Microsoft Purview which sites and OneDrives have matches ({(int)waited.TotalMinutes} min {waited.Seconds:00} s so far; usually a few minutes)…");
            }
        }
        finally
        {
            try
            {
                await Compliance("Remove-ComplianceSearch", new() { ["Identity"] = name, ["Confirm"] = false }, CancellationToken.None);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Left behind: it only counted, and its name and description say what it was.
            }
        }
    }

    /// <summary>The file's content: Graph answers with a short-lived download address, followed without the token.</summary>
    public async Task DownloadAsync(FoundFile file, Stream to, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await graph.DownloadAsync($"https://graph.microsoft.com/v1.0/drives/{file.DriveId}/items/{file.Id}/content", "https://graph.microsoft.com/.default",
                new Dictionary<string, string>(), to, progress, cancellationToken);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new SiteAccessException();
        }
    }
}
