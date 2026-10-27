using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Sites and OneDrives (7a), from the SharePoint admin site, read whole at most hourly or on
/// Refresh (SharePoint has no delta for this). Each is stored as a small row of what the lists use,
/// not the 150-odd properties SharePoint sends. Without the SharePoint Administrator role the part is
/// skipped with a note.
/// </summary>
public sealed partial class DirectorySync
{
    public const string Sites = "site";

    async Task<int> SyncSitesAsync(List<string> notes, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (sharePoint is null)
            return 0;
        try
        {
            progress?.Report("Reading SharePoint sites and OneDrives…");
            var changes = new List<EntityChange>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var site in sharePoint.GetSitesAsync(includePersonal: true, cancellationToken))
            {
                if (SiteRow(site) is not { } row)
                    continue;
                var id = row["id"]!.GetValue<string>();
                if (!seen.Add(id))
                    continue;
                changes.Add(new EntityChange(id, row));
                if (seen.Count % 500 == 0)
                    progress?.Report($"Reading SharePoint sites and OneDrives… {seen.Count:N0}");
            }
            var changed = store.Apply(Sites, changes) + store.RemoveExcept(Sites, seen);
            store.SetMark(Sites, new SyncMark(null, _now()));
            return changed;
        }
        catch (Exception e) when (e is SharePointException { Status: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest }
                                       or GraphException { Status: HttpStatusCode.Forbidden or HttpStatusCode.NotFound })
        {
            notes.Add("Sites weren't read: listing SharePoint sites needs the SharePoint Administrator role.");
            store.SetMark(Sites, new SyncMark(null, _now()));
            return 0;
        }
    }

    /// <summary>The Sites list's Refresh: sites and OneDrives only.</summary>
    public async Task<int> SyncSitesAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            var notes = new List<string>();
            var changed = await SyncSitesAsync(notes, progress, cancellationToken);
            _notes = [.. _notes.Where(n => !n.StartsWith("Sites weren't", StringComparison.Ordinal)), .. notes];
            return changed;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>One site as stored: what the Sites and OneDrives lists show, in plain JSON types.</summary>
    internal static JsonObject? SiteRow(JsonElement s)
    {
        if (Csom.Guid(s, "SiteId") is not { } id || Csom.Text(s, "Url") is not { } url)
            return null;
        return new JsonObject
        {
            ["id"] = id,
            ["url"] = url,
            ["title"] = Csom.Text(s, "Title") ?? url,
            ["template"] = Csom.Text(s, "Template") ?? "",
            ["ownerName"] = Csom.Text(s, "OwnerName"),
            ["ownerEmail"] = Csom.Text(s, "OwnerEmail"),
            ["ownerLogin"] = Csom.Text(s, "OwnerLoginName") ?? Csom.Text(s, "Owner"),
            ["groupId"] = Csom.Guid(s, "GroupId"),
            ["isTeam"] = Csom.Flag(s, "IsTeamsConnected"),
            ["isHub"] = Csom.Flag(s, "IsHubSite"),
            ["hubSiteId"] = Csom.Guid(s, "HubSiteId"),
            ["storageMb"] = Csom.Number(s, "StorageUsage"),
            ["quotaMb"] = Csom.Number(s, "StorageMaximumLevel"),
            ["sharing"] = Csom.Number(s, "SharingCapability"),
            ["lock"] = Csom.Text(s, "LockState") ?? "Unlock",
            ["archive"] = Csom.Text(s, "ArchiveStatus") ?? "NotArchived",
            ["created"] = Csom.Date(s, "CreatedTime")?.ToString("o", CultureInfo.InvariantCulture),
            ["lastActivity"] = Csom.Date(s, "LastContentModifiedDate")?.ToString("o", CultureInfo.InvariantCulture),
            ["label"] = Csom.Text(s, "SensitivityLabel2") ?? Csom.Guid(s, "SensitivityLabel"),
            ["webs"] = Csom.Number(s, "WebsCount"),
        };
    }
}

/// <summary>Reading values out of a CSOM answer, which has its own spellings for dates and IDs.</summary>
internal static partial class Csom
{
    public static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    public static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static long Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>"/Guid(…)/" (or a bare GUID) → the GUID; the empty GUID → null.</summary>
    public static string? Guid(JsonElement e, string name)
    {
        if (Text(e, name) is not { } text)
            return null;
        var inner = text.StartsWith("/Guid(", StringComparison.Ordinal) ? text[6..text.IndexOf(')')] : text;
        return System.Guid.TryParse(inner, out var g) && g != System.Guid.Empty ? g.ToString() : null;
    }

    [GeneratedRegex(@"^/Date\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)/$")]
    private static partial Regex Parts();

    [GeneratedRegex(@"^/Date\((-?\d+)\)/$")]
    private static partial Regex Ticks();

    /// <summary>CSOM writes dates as "/Date(2023,9,5,10,20,30,0)/" (UTC, months from 0) or "/Date(milliseconds)/"; year 1 means none.</summary>
    public static DateTimeOffset? Date(JsonElement e, string name)
    {
        if (Text(e, name) is not { } text)
            return null;
        if (Parts().Match(text) is { Success: true } m)
        {
            int P(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
            if (P(1) < 1900)
                return null;
            return new DateTimeOffset(P(1), P(2) + 1, P(3), P(4), P(5), P(6), P(7), TimeSpan.Zero);
        }
        if (Ticks().Match(text) is { Success: true } t && long.TryParse(t.Groups[1].Value, CultureInfo.InvariantCulture, out var ms) && ms > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1900 ? d : null;
    }
}
