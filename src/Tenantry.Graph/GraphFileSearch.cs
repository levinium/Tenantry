using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
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

    // ---- The kept file lists (step 4) ----------------------------------------------------------
    // Each library searched is kept in the tenant's encrypted local copy: its items (only what a
    // result needs) under one kind per library, and the delta link that reads what changed since.
    // A place is only ever read through Graph first, so a kept list never shows a place the admin
    // can't open now.

    const string IndexPlaces = "fileindex.place";

    static string IndexKind(string driveId) => "fileindex.drive:" + driveId;

    static string IndexMark(string driveId) => "fileindex.delta:" + driveId;

    public async IAsyncEnumerable<FoundFile> ListFilesAsync(SiteSummary place, IProgress<int>? scanned = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var siteId = await SiteIdAsync(place, cancellationToken);
        var drives = await FilesReadAsync(place, () => graph.GetAsync($"sites/{siteId}/drives?$select=id,name,driveType", cancellationToken));
        var before = IndexedPlaces().FirstOrDefault(p => p.PlaceId == place.Id);
        var driveIds = new List<string>();
        var changes = 0;
        var whole = false;
        var shown = 0;
        foreach (var drive in Rows(drives))
        {
            if (Text(drive, "id") is not { } driveId)
                continue;
            driveIds.Add(driveId);
            var (read, readWhole) = await RefreshDriveAsync(place, driveId, scanned, cancellationToken);
            changes += read;
            whole |= readWhole;
            foreach (var file in KeptFiles(driveId, Text(drive, "name") ?? "Documents", place))
            {
                yield return file;
                if (++shown % 500 == 0)
                    scanned?.Report(shown);
            }
        }
        scanned?.Report(shown);
        // Libraries gone since last time go from the kept list too.
        if (before is not null && store.Get(IndexPlaces, place.Id) is { } old && JsonNode.Parse(old)?["drives"] is JsonArray oldDrives)
            foreach (var gone in oldDrives.Select(d => d?.GetValue<string>()).OfType<string>().Except(driveIds, StringComparer.Ordinal))
                ForgetDrive(gone);
        store.Apply(IndexPlaces, [new EntityChange(place.Id, new JsonObject
        {
            ["id"] = place.Id,
            ["url"] = place.Url,
            ["title"] = place.Kind == SiteKind.OneDrive && place.OwnerName is { Length: > 0 } owner ? $"{owner}'s OneDrive" : place.Title,
            ["kind"] = place.Kind.ToString(),
            ["drives"] = new JsonArray([.. driveIds.Select(d => (JsonNode)JsonValue.Create(d)!)]),
            ["items"] = shown,
            ["indexedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["changes"] = changes,
            // Read whole (first time, or Microsoft asked for a fresh start): nothing came from the kept list.
            ["previousAt"] = whole || before is null ? null : before.IndexedAt.ToString("O"),
        })]);
    }

    /// <summary>Brings one library's kept list up to date: what changed since the delta link kept, or all of it.</summary>
    /// <returns>How many items were read, and whether it was read whole.</returns>
    async Task<(int Read, bool Whole)> RefreshDriveAsync(SiteSummary place, string driveId, IProgress<int>? scanned, CancellationToken cancellationToken)
    {
        if (store.GetMark(IndexMark(driveId))?.Cursor is { } since)
        {
            try
            {
                return (await ReadDeltaAsync(place, driveId, since, null, scanned, cancellationToken), false);
            }
            catch (GraphException e) when (e.Status == HttpStatusCode.Gone)
            {
                // Microsoft no longer has the changes since then (resyncRequired): read it whole.
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var read = await ReadDeltaAsync(place, driveId, $"drives/{driveId}/root/delta?$select={DeltaSelect}", seen, scanned, cancellationToken);
        // Read whole: anything kept that wasn't in it is gone.
        store.RemoveExcept(IndexKind(driveId), seen);
        return (read, true);
    }

    /// <summary>Reads delta pages into the kept list, then keeps the link for next time (only once every page is in).</summary>
    async Task<int> ReadDeltaAsync(SiteSummary place, string driveId, string start, HashSet<string>? seen, IProgress<int>? scanned, CancellationToken cancellationToken)
    {
        var kind = IndexKind(driveId);
        string? next = start, deltaLink = null;
        var read = 0;
        while (next is not null)
        {
            var url = next;
            var page = await FilesReadAsync(place, () => graph.GetAsync(url, cancellationToken));
            var changes = new List<EntityChange>();
            foreach (var item in Rows(page))
            {
                if (Text(item, "id") is not { } id)
                    continue;
                if (item.TryGetProperty("deleted", out _))
                {
                    changes.Add(EntityChange.Removed(id));
                    seen?.Remove(id);
                }
                else
                {
                    changes.Add(new EntityChange(id, Kept(item)));
                    seen?.Add(id);
                }
            }
            store.Apply(kind, changes);
            read += changes.Count;
            scanned?.Report(read);
            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            if (page.TryGetProperty("@odata.deltaLink", out var delta))
                deltaLink = delta.GetString();
        }
        if (deltaLink is not null)
            store.SetMark(IndexMark(driveId), new SyncMark(deltaLink, DateTimeOffset.UtcNow));
        return read;
    }

    /// <summary>Only what a result needs: no hashes, no sharing details, no content.</summary>
    static JsonObject Kept(JsonElement item)
    {
        var kept = new JsonObject { ["id"] = Text(item, "id"), ["name"] = Text(item, "name") };
        foreach (var marker in new[] { "file", "folder", "root" })
            if (item.TryGetProperty(marker, out _))
                kept[marker] = new JsonObject();
        if (item.TryGetProperty("parentReference", out var p) && p.ValueKind == JsonValueKind.Object && Text(p, "id") is { } parent)
            kept["parentReference"] = new JsonObject { ["id"] = parent };
        foreach (var name in new[] { "size" })
            if (item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
                kept[name] = v.GetInt64();
        foreach (var name in new[] { "lastModifiedDateTime", "createdDateTime", "webUrl" })
            if (Text(item, name) is { } text)
                kept[name] = text;
        foreach (var name in new[] { "lastModifiedBy", "createdBy" })
            if (item.TryGetProperty(name, out var by) && by.ValueKind == JsonValueKind.Object && by.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
                kept[name] = new JsonObject { ["user"] = new JsonObject { ["displayName"] = Text(u, "displayName"), ["email"] = Text(u, "email") } };
        return kept;
    }

    /// <summary>
    /// A library's kept files and folders, folders first, each with its folder path put together from
    /// the folders kept. Anything whose folders don't lead back to the library's top (left behind
    /// when a folder was deleted) is dropped from the list.
    /// </summary>
    IEnumerable<FoundFile> KeptFiles(string driveId, string library, SiteSummary place)
    {
        var kind = IndexKind(driveId);
        var items = new List<JsonElement>();
        var folders = new Dictionary<string, (string Name, string? Parent, bool IsRoot)>(StringComparer.Ordinal);
        foreach (var json in store.All(kind))
        {
            var item = JsonDocument.Parse(json).RootElement;
            if (Text(item, "id") is not { } id)
                continue;
            var isRoot = item.TryGetProperty("root", out _);
            var parent = item.TryGetProperty("parentReference", out var p) && p.ValueKind == JsonValueKind.Object ? Text(p, "id") : null;
            if (item.TryGetProperty("folder", out _) || isRoot)
                folders[id] = (Text(item, "name") ?? "", parent, isRoot);
            if (!isRoot && (item.TryGetProperty("file", out _) || item.TryGetProperty("folder", out _)))
                items.Add(item);
        }
        var orphans = new List<EntityChange>();
        var found = new List<FoundFile>();
        foreach (var item in items)
        {
            if (!ReachesTop(item, folders))
            {
                orphans.Add(EntityChange.Removed(Text(item, "id")!));
                continue;
            }
            found.Add(ToFound(item, driveId, library, FolderOf(item, folders), place));
        }
        if (orphans.Count > 0)
            store.Apply(kind, orphans);
        return found.OrderByDescending(f => f.IsFolder).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
    }

    static bool ReachesTop(JsonElement item, IReadOnlyDictionary<string, (string Name, string? Parent, bool IsRoot)> folders)
    {
        var at = item.TryGetProperty("parentReference", out var p) && p.ValueKind == JsonValueKind.Object ? Text(p, "id") : null;
        for (var depth = 0; at is not null && depth < 64; depth++)
        {
            if (!folders.TryGetValue(at, out var folder))
                return false;
            if (folder.IsRoot)
                return true;
            at = folder.Parent;
        }
        return false;
    }

    public IReadOnlyList<IndexedPlace> IndexedPlaces() =>
    [
        .. store.All(IndexPlaces).Select(json => JsonNode.Parse(json) as JsonObject).OfType<JsonObject>().Select(o => new IndexedPlace(
            o["id"]?.GetValue<string>() ?? "",
            o["url"]?.GetValue<string>() ?? "",
            o["title"]?.GetValue<string>() ?? "",
            Enum.TryParse<SiteKind>(o["kind"]?.GetValue<string>(), out var kind) ? kind : SiteKind.TeamSite,
            o["items"]?.GetValue<int>() ?? 0,
            DateTimeOffset.TryParse(o["indexedAt"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at) ? at : DateTimeOffset.MinValue,
            o["changes"]?.GetValue<int>() ?? 0,
            DateTimeOffset.TryParse(o["previousAt"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var was) ? was : null))
            .Where(p => p.PlaceId.Length > 0),
    ];

    public void ForgetIndexed()
    {
        foreach (var json in store.All(IndexPlaces))
            if (JsonNode.Parse(json)?["drives"] is JsonArray drives)
                foreach (var drive in drives.Select(d => d?.GetValue<string>()).OfType<string>())
                    ForgetDrive(drive);
        store.RemoveExcept(IndexPlaces, new HashSet<string>());
    }

    void ForgetDrive(string driveId)
    {
        store.RemoveExcept(IndexKind(driveId), new HashSet<string>());
        store.SetMark(IndexMark(driveId), new SyncMark(null, DateTimeOffset.UtcNow));
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

    /// <summary>Enough of a found file's place to read it again.</summary>
    static SiteSummary PlaceOf(FoundFile file) =>
        new(file.PlaceId, file.PlaceUrl, file.PlaceTitle, "", SiteKind.TeamSite, null, null, null, null, false, false, null, 0, 0, 0, "", "", null, null);

    public async IAsyncEnumerable<FoundFile> ListRecycleBinAsync(SiteSummary place, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (sharePoint is null)
            yield break;
        JsonElement bin;
        try
        {
            bin = await sharePoint.GetRecycleBinAsync(place.Url, cancellationToken);
        }
        catch (SharePointException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new SiteAccessException();
        }
        foreach (var row in Rows(bin))
            if (FromBin(row, place) is { } file)
                yield return file;
    }

    /// <summary>
    /// A recycle bin row as a found file. Only files (ItemType 1) and folders (5) are kept: versions,
    /// list items and whole lists are SharePoint's business. DirName is the folder it was in, from
    /// the host down ("sites/finance/Shared Documents/2026"), without the leading slash (seen live).
    /// </summary>
    internal static FoundFile? FromBin(JsonElement row, SiteSummary place)
    {
        var type = row.TryGetProperty("ItemType", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
        if (type is not (1 or 5) || Text(row, "Id") is not { } id)
            return null;
        var dir = "/" + (Text(row, "DirName") ?? "").Trim('/');
        var sitePath = Uri.UnescapeDataString(new Uri(place.Url).AbsolutePath).TrimEnd('/');
        var inside = dir.StartsWith(sitePath + "/", StringComparison.OrdinalIgnoreCase) ? dir[(sitePath.Length + 1)..] : dir.TrimStart('/');
        var slash = inside.IndexOf('/');
        var stage = row.TryGetProperty("ItemState", out var s) && s.ValueKind == JsonValueKind.Number && s.GetInt32() == 2 ? 2 : 1;
        return new FoundFile(id, "", Text(row, "LeafName") ?? "A file", slash < 0 ? inside : inside[..slash], slash < 0 ? "" : inside[(slash + 1)..],
            place.Id, place.Title, place.Url, Long(row, "Size") ?? 0, null, null, null, null,
            Text(row, "AuthorName") is { Length: > 0 } author ? author : null, Text(row, "AuthorEmail") is { Length: > 0 } email ? email : null,
            null, type == 5, stage, When(row, "DeletedDate"),
            Text(row, "DeletedByName") is { Length: > 0 } by ? by : Text(row, "DeletedByEmail") is { Length: > 0 } byEmail ? byEmail : null);
    }

    public async Task<FoundFile?> FindRestoredAsync(FoundFile deleted, CancellationToken cancellationToken = default)
    {
        var place = PlaceOf(deleted);
        var siteId = await SiteIdAsync(place, cancellationToken);
        var drives = await FilesReadAsync(place, () => graph.GetAsync($"sites/{siteId}/drives?$select=id,name,webUrl", cancellationToken));
        // The bin names a library by its address ("Shared Documents"); Graph by its title ("Documents").
        var drive = Rows(drives).FirstOrDefault(d => Text(d, "webUrl") is { } url
            && Uri.UnescapeDataString(new Uri(url).AbsolutePath).TrimEnd('/').EndsWith("/" + deleted.Library, StringComparison.OrdinalIgnoreCase));
        if (drive.ValueKind != JsonValueKind.Object || Text(drive, "id") is not { } driveId)
            return null;
        var path = string.Join('/', deleted.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Append(deleted.Name).Select(Uri.EscapeDataString));
        try
        {
            var item = await graph.GetAsync($"drives/{driveId}/root:/{path}?$select={DeltaSelect}", cancellationToken);
            return ToFound(item, driveId, Text(drive, "name") ?? deleted.Library, deleted.Folder, place) with { PlaceTitle = deleted.PlaceTitle };
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> CanOpenAsync(SiteSummary place, CancellationToken cancellationToken = default)
    {
        try
        {
            var siteId = await SiteIdAsync(place, cancellationToken);
            await FilesReadAsync(place, () => graph.GetAsync($"sites/{siteId}/drive?$select=id", cancellationToken));
            return true;
        }
        catch (Exception e) when (e is SiteAccessException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async IAsyncEnumerable<FoundFile> ListFolderAsync(FoundFile folder, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Breadth first, with each folder's path below the one asked for.
        var pending = new Queue<(string Id, string Path)>([(folder.Id, "")]);
        var place = PlaceOf(folder);
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
