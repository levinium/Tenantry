using System.Text.RegularExpressions;
using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>How a file's name is matched.</summary>
public enum NameMatch
{
    Contains,
    StartsWith,
    /// <summary>A .NET regular expression, matched against the name (case doesn't matter).</summary>
    Regex,
}

/// <summary>A kind of file the search can be narrowed to, by extension.</summary>
public sealed record FileType(string Key, string Label, IReadOnlySet<string> Extensions)
{
    static FileType Of(string key, string label, params string[] extensions) =>
        new(key, label, new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<FileType> All { get; } =
    [
        Of("word", "Word", "doc", "docx", "docm", "dot", "dotx", "dotm", "rtf", "odt"),
        Of("excel", "Excel", "xls", "xlsx", "xlsm", "xlsb", "xlt", "xltx", "xltm", "csv", "ods"),
        Of("powerpoint", "PowerPoint", "ppt", "pptx", "pptm", "pot", "potx", "pps", "ppsx", "odp"),
        Of("pdf", "PDF", "pdf"),
        Of("onenote", "OneNote", "one", "onetoc2", "onepkg"),
        Of("images", "Images", "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "heic", "webp", "svg", "raw"),
        Of("video", "Video", "mp4", "mov", "avi", "wmv", "mkv", "m4v", "webm"),
        Of("audio", "Audio", "mp3", "wav", "m4a", "wma", "aac", "flac", "ogg"),
        Of("archives", "Archives", "zip", "7z", "rar", "tar", "gz", "cab"),
        Of("text", "Text", "txt", "md", "log", "json", "xml"),
        Of("visio", "Visio", "vsd", "vsdx", "vsdm"),
        Of("access", "Access", "accdb", "mdb"),
        Of("email", "Email", "msg", "eml", "pst"),
    ];
}

/// <summary>
/// What to look for. Everything given must match; blank parts match anything. The name is matched
/// here, not by Microsoft, so "contains" and regular expressions work the same in every place.
/// </summary>
/// <param name="Types">Kinds of file (<see cref="FileType.Key"/>); empty means any.</param>
/// <param name="Extensions">More extensions, without the dot ("dwg").</param>
/// <param name="ModifiedBy">Part of the name or address of whoever last changed it, or made it.</param>
/// <param name="PathContains">Part of the folder path (library and folders).</param>
public sealed record FileQuery(
    string Name = "",
    NameMatch Match = NameMatch.Contains,
    IReadOnlySet<string>? Types = null,
    IReadOnlyList<string>? Extensions = null,
    DateTimeOffset? ModifiedAfter = null,
    DateTimeOffset? ModifiedBefore = null,
    string? ModifiedBy = null,
    long? MinBytes = null,
    long? MaxBytes = null,
    string? PathContains = null)
{
    /// <summary>A regular expression that runs away is stopped instead of hanging the search.</summary>
    static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(200);

    Regex? _regex;

    /// <summary>Why it can't be run as it is (a broken regular expression); null when it can.</summary>
    public string? Problem
    {
        get
        {
            if (Match != NameMatch.Regex || Name.Length == 0)
                return null;
            try
            {
                _ = Pattern;
                return null;
            }
            catch (ArgumentException e)
            {
                return $"That regular expression doesn't work: {e.Message}";
            }
        }
    }

    Regex Pattern => _regex ??= new Regex(Name, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);

    /// <summary>Asks for something: a search for everything everywhere would list every file there is.</summary>
    public bool IsEmpty => Name.Trim().Length == 0 && (Types?.Count ?? 0) == 0 && (Extensions?.Count ?? 0) == 0 && ModifiedAfter is null && ModifiedBefore is null
                           && string.IsNullOrWhiteSpace(ModifiedBy) && MinBytes is null && MaxBytes is null && string.IsNullOrWhiteSpace(PathContains);

    HashSet<string>? _extensions;

    /// <summary>Every extension the types and the extra extensions allow; null when any will do.</summary>
    public IReadOnlySet<string>? AllowedExtensions
    {
        get
        {
            if ((Types?.Count ?? 0) == 0 && (Extensions?.Count ?? 0) == 0)
                return null;
            return _extensions ??= new HashSet<string>(
                FileType.All.Where(t => Types?.Contains(t.Key) == true).SelectMany(t => t.Extensions)
                    .Concat((Extensions ?? []).Select(e => e.Trim().TrimStart('*').TrimStart('.')).Where(e => e.Length > 0)),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The search as Purview's query (KQL) for Everywhere: broad on purpose, since every file in the
    /// places it reports is checked here exactly. Names go as a prefix of their longest word
    /// (Purview matches whole words and prefixes); a regular expression isn't sent at all. Types and
    /// dates go as they are; who changed it, folders and sizes aren't sent. Empty matches everything.
    /// </summary>
    public string PurviewQuery()
    {
        var parts = new List<string>();
        if (Match != NameMatch.Regex && Name.Trim().Length > 0)
        {
            var word = Regex.Split(Name.Trim(), @"[^\p{L}\p{N}]+").OrderByDescending(w => w.Length).FirstOrDefault(w => w.Length > 0);
            if (word is not null)
                parts.Add($"filename:{word.ToLowerInvariant()}*");
        }
        if (AllowedExtensions is { Count: > 0 } extensions)
        {
            var types = extensions.OrderBy(e => e, StringComparer.Ordinal).Select(e => $"filetype:{e.ToLowerInvariant()}").ToArray();
            parts.Add(types.Length == 1 ? types[0] : $"({string.Join(" OR ", types)})");
        }
        if (ModifiedAfter is { } after)
            parts.Add($"lastmodifiedtime>={after.UtcDateTime:yyyy-MM-dd}");
        if (ModifiedBefore is { } before)
            parts.Add($"lastmodifiedtime<{before.UtcDateTime:yyyy-MM-dd}");
        return string.Join(" AND ", parts);
    }

    public bool Matches(FoundFile file)
    {
        if (!NameMatches(file.Name))
            return false;
        if (AllowedExtensions is { } allowed && !allowed.Contains(file.Extension))
            return false;
        if (ModifiedAfter is { } after && !(file.Modified >= after))
            return false;
        if (ModifiedBefore is { } before && !(file.Modified < before))
            return false;
        if (MinBytes is { } min && file.Size < min)
            return false;
        if (MaxBytes is { } max && file.Size > max)
            return false;
        if (!string.IsNullOrWhiteSpace(PathContains) && !file.Path.Contains(PathContains.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(ModifiedBy))
        {
            var who = ModifiedBy.Trim();
            if (!new[] { file.ModifiedBy, file.ModifiedByEmail, file.CreatedBy, file.CreatedByEmail }
                    .Any(p => p?.Contains(who, StringComparison.OrdinalIgnoreCase) == true))
                return false;
        }
        return true;
    }

    bool NameMatches(string name)
    {
        var wanted = Name.Trim();
        if (wanted.Length == 0)
            return true;
        switch (Match)
        {
            case NameMatch.StartsWith:
                return name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase);
            case NameMatch.Regex:
                try
                {
                    return Pattern.IsMatch(name);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            default:
                return name.Contains(wanted, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>A file or folder found in a site or OneDrive.</summary>
/// <param name="Id">The file's ID in its library (Graph's drive item ID).</param>
/// <param name="Library">The document library it's in ("Documents").</param>
/// <param name="Folder">Its folder inside the library, "" at the top ("Projects/2026").</param>
/// <param name="PlaceId">The site's ID in the local copy (<see cref="SiteSummary.Id"/>).</param>
public sealed record FoundFile(
    string Id,
    string DriveId,
    string Name,
    string Library,
    string Folder,
    string PlaceId,
    string PlaceTitle,
    string PlaceUrl,
    long Size,
    DateTimeOffset? Modified,
    string? ModifiedBy,
    string? ModifiedByEmail,
    DateTimeOffset? Created,
    string? CreatedBy,
    string? CreatedByEmail,
    string? WebUrl,
    bool IsFolder = false)
{
    /// <summary>"xlsx"; "" for a folder (a folder named "v1.2" has no extension).</summary>
    public string Extension => IsFolder ? "" : System.IO.Path.GetExtension(Name).TrimStart('.');

    /// <summary>"Documents/Projects/2026": where it is inside the place.</summary>
    public string Path => Folder.Length == 0 ? Library : $"{Library}/{Folder}";

    /// <summary>The folder it's in, to open in the browser.</summary>
    public string? FolderUrl => WebUrl is { Length: > 0 } url && url.LastIndexOf('/') is var slash and > 8 ? url[..slash] : null;
}

/// <summary>Lists a site's or OneDrive's files, live from Microsoft 365.</summary>
public interface IFileSearchRecords
{
    /// <summary>The most places a Purview search's statistics list (a broad search over every site gave exactly 200, seen live 2026-11-19).</summary>
    const int EverywhereMostPlaces = 200;

    /// <summary>
    /// Every file in every document library of the place, a library at a time. Throws
    /// <see cref="SiteAccessException"/> when the admin can't see inside it, and
    /// <see cref="UnauthorizedAccessException"/> when it's archived.
    /// </summary>
    /// <param name="scanned">Told the running count of files looked at.</param>
    IAsyncEnumerable<FoundFile> ListFilesAsync(SiteSummary place, IProgress<int>? scanned = null, CancellationToken cancellationToken = default);

    /// <summary>Every file inside a found folder, its subfolders' too; <see cref="FoundFile.Folder"/> is the path below that folder.</summary>
    IAsyncEnumerable<FoundFile> ListFolderAsync(FoundFile folder, CancellationToken cancellationToken = default);

    /// <summary>Copies a file's content into <paramref name="to"/>, reporting bytes written.</summary>
    Task DownloadAsync(FoundFile file, Stream to, IProgress<long>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everywhere: which sites and OneDrives Purview counts matches in for <paramref name="query"/>
    /// (KQL; empty is everything). A content search over every site is made, run and read, then
    /// deleted; it only counts. Takes minutes; <paramref name="status"/> says how far it's got.
    /// </summary>
    /// <returns>Each place's address and how many items Purview counted there (places with none left out).
    /// Purview lists at most <see cref="EverywhereMostPlaces"/> places (seen live).</returns>
    Task<IReadOnlyList<EdLocationHits>> CountEverywhereAsync(string query, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Searching everywhere needs Microsoft Purview, which isn't connected for this tenant.");
}

/// <summary>The Find Files list's fields.</summary>
public static class FoundFileFields
{
    public const string Name = "name";
    public const string Type = "type";
    public const string Extension = "extension";
    public const string Place = "place";
    public const string Path = "path";
    public const string Size = "size";
    public const string Modified = "modified";
    public const string ModifiedBy = "modifiedby";
    public const string Created = "created";
    public const string CreatedBy = "createdby";

    /// <summary>"Excel", or the extension in capitals when it's none of the listed kinds.</summary>
    public static string TypeOf(FoundFile f) => f.IsFolder ? "Folder" :
        FileType.All.FirstOrDefault(t => t.Extensions.Contains(f.Extension))?.Label ?? (f.Extension.Length > 0 ? f.Extension.ToUpperInvariant() : "No Extension");

    /// <summary>".xlsx"; "Folder" for a folder, "No Extension" for a file without one.</summary>
    public static string ExtensionOf(FoundFile f) => f.IsFolder ? "Folder" : f.Extension.Length > 0 ? "." + f.Extension.ToLowerInvariant() : "No Extension";

    public static IReadOnlyList<Field<FoundFile>> All { get; } =
    [
        new() { Key = Name, Label = "Name", Kind = FieldKind.Text, Searchable = true, Aliases = ["file"], Text = f => f.Name },
        new() { Key = Type, Label = "Type", Kind = FieldKind.Choice, Aliases = ["kind"], Text = TypeOf },
        new() { Key = Extension, Label = "Extension", Kind = FieldKind.Choice, Aliases = ["ext"], Text = ExtensionOf },
        new() { Key = Place, Label = "Site or OneDrive", Kind = FieldKind.Choice, Searchable = true, Aliases = ["site", "onedrive"], Text = f => f.PlaceTitle },
        new() { Key = Path, Label = "Folder", Kind = FieldKind.Text, Searchable = true, Aliases = ["folder", "library"], Text = f => f.Path },
        new() { Key = Size, Label = "Size (MB)", Kind = FieldKind.Number, Number = f => Math.Round(f.Size / 1_048_576.0, 1) },
        new() { Key = Modified, Label = "Modified", Kind = FieldKind.Date, Date = f => f.Modified },
        new() { Key = ModifiedBy, Label = "Modified By", Kind = FieldKind.Text, Searchable = true, Aliases = ["editor"], Text = f => f.ModifiedBy ?? f.ModifiedByEmail },
        new() { Key = Created, Label = "Created", Kind = FieldKind.Date, Date = f => f.Created },
        new() { Key = CreatedBy, Label = "Created By", Kind = FieldKind.Text, Searchable = true, Aliases = ["author"], Text = f => f.CreatedBy ?? f.CreatedByEmail },
    ];
}
