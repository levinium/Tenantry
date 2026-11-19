using System.Runtime.CompilerServices;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's files for Find Files: each site and OneDrive gets its own files from its seed, in
/// a few folders, changed by its owner or colleagues. Sites the admin can't see inside refuse,
/// as on the site page, until the admin adds themself as a site admin.
/// </summary>
public sealed partial class DemoUserRecords : IFileSearchRecords
{
    static readonly string[] FileWords = ["Budget", "Plan", "Report", "Minutes", "Invoice", "Contract", "Schedule", "Roster", "Proposal", "Checklist", "Handbook", "Survey", "Inventory", "Timeline"];
    static readonly string[] FileYears = ["2023", "2024", "2025", "2026"];
    static readonly string[] Extensions = ["docx", "xlsx", "pptx", "pdf", "xlsx", "docx", "png", "csv", "txt", "zip"];
    static readonly string[] FolderNames = ["", "", "Projects", "Projects/2026", "Archive", "Finance", "Shared With Me"];

    public async IAsyncEnumerable<FoundFile> ListFilesAsync(SiteSummary place, IProgress<int>? scanned = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CanSeeInside(place);
        var random = new Random(StableSeed(place.Id) + 16);
        var people = (await directory.GetUsersAsync(cancellationToken)).Take(40).ToArray();
        var count = random.Next(20, 90);
        var now = DateTimeOffset.UtcNow;
        var root = $"{place.Url}/{(place.Kind == SiteKind.OneDrive ? "Documents" : "Shared Documents")}";
        // Its folders first (they're results too), then the files in them.
        foreach (var path in FolderNames.Where(f => f.Length > 0).Distinct())
        {
            var slash = path.LastIndexOf('/');
            yield return new FoundFile($"demo-folder-{place.Id}-{path}", $"demo-d-{place.Id}", path[(slash + 1)..], "Documents", slash < 0 ? "" : path[..slash],
                place.Id, place.Title, place.Url, random.Next(1_000_000, 90_000_000), now.AddDays(-random.Next(1, 400)), place.OwnerName, place.OwnerEmail,
                now.AddDays(-random.Next(400, 1200)), place.OwnerName, place.OwnerEmail, $"{root}/{path}", IsFolder: true);
        }
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A short pause now and then, so the screen shows a search in progress.
            if (i % 30 == 0)
                await Task.Delay(40, cancellationToken);
            var editor = place.OwnerName is { } owner && random.NextDouble() < 0.6 ? (owner, place.OwnerEmail) : people[random.Next(people.Length)] is var p ? (p.DisplayName, p.UserPrincipalName) : default;
            var name = $"{FileWords[random.Next(FileWords.Length)]} {FileYears[random.Next(FileYears.Length)]}{(random.NextDouble() < 0.2 ? " (old)" : "")}.{Extensions[random.Next(Extensions.Length)]}";
            var folder = FolderNames[random.Next(FolderNames.Length)];
            var modified = now.AddDays(-random.Next(1, 900)).AddMinutes(-random.Next(1440));
            var library = place.Kind == SiteKind.OneDrive ? "Documents" : random.NextDouble() < 0.8 ? "Documents" : "Site Assets";
            var url = $"{root}/{(folder.Length > 0 ? folder + "/" : "")}{Uri.EscapeDataString(name)}";
            yield return new FoundFile($"demo-f-{place.Id}-{i}", $"demo-d-{place.Id}", name, library, folder, place.Id, place.Title, place.Url,
                random.Next(8_000, 40_000_000), modified, editor.Item1, editor.Item2, modified.AddDays(-random.Next(0, 200)), editor.Item1, editor.Item2, url);
            scanned?.Report(i + 1);
        }
    }

    /// <summary>The demo's Everywhere: a short wait, then about half the sites and a few OneDrives with counts.</summary>
    public async Task<IReadOnlyList<EdLocationHits>> CountEverywhereAsync(string query, IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        status?.Report("Asking Microsoft Purview which sites and OneDrives have matches (0 min 05 s so far; usually a few minutes)…");
        await Task.Delay(300, cancellationToken);
        var sites = await directory.GetSitesAsync(cancellationToken);
        var random = new Random(StableSeed(query) + 17);
        return [.. sites.Where(s => s.Kind != SiteKind.System && !s.IsArchived && random.NextDouble() < (s.Kind == SiteKind.OneDrive ? 0.15 : 0.5))
            .Select(s => new EdLocationHits(s.Url, random.Next(1, 40), null))];
    }

    /// <summary>A demo folder holds a few files, and one subfolder with one more.</summary>
    public async IAsyncEnumerable<FoundFile> ListFolderAsync(FoundFile folder, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var random = new Random(StableSeed(folder.Id));
        string[] names = [.. Enumerable.Range(0, random.Next(2, 5)).Select(i => $"{FileWords[random.Next(FileWords.Length)]} {i + 1}.{Extensions[random.Next(Extensions.Length)]}"), "Notes.txt"];
        for (var i = 0; i < names.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return folder with { Id = $"{folder.Id}-{i}", Name = names[i], Folder = i == names.Length - 1 ? "Older" : "", IsFolder = false, Size = 2_000 + i };
        }
    }

    /// <summary>The demo's files are a line of text saying what they stand in for.</summary>
    public async Task DownloadAsync(FoundFile file, Stream to, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var text = System.Text.Encoding.UTF8.GetBytes($"Demo copy of \"{file.Name}\" from {file.PlaceTitle} ({file.Path}). The real file comes from Microsoft 365.{Environment.NewLine}");
        await to.WriteAsync(text, cancellationToken);
        progress?.Report(text.Length);
    }
}
