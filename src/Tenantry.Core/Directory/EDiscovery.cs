using System.Globalization;
using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>An eDiscovery case: a container for searches, holds and the people who work on them.</summary>
public sealed record EdCase(string Id, string Name, string? Description, bool IsClosed, DateTimeOffset? Created, DateTimeOffset? Modified, string? ModifiedBy,
    DateTimeOffset? Closed, string? ClosedBy);

/// <summary>A content search as listed: no counts (Purview only gives those for one search at a time).</summary>
/// <param name="CaseName">The case it belongs to, or null for a search outside any case.</param>
/// <param name="Status">Purview's word: "NotStarted", "Starting", "InProgress", "Completed", "Stopping", "Stopped".</param>
public sealed record EdSearch(string Name, string? CaseName, string? Description, string Status, DateTimeOffset? Created, string? CreatedBy, DateTimeOffset? LastRun,
    string? RunBy);

/// <summary>What a search found in one place (a mailbox or a site).</summary>
public sealed record EdLocationHits(string Location, long Items, string? Size);

/// <summary>One search, read on its own: where it looks, what for, and what it found the last time it ran.</summary>
/// <param name="Mailboxes">Addresses, or "All".</param>
/// <param name="Sites">Site URLs (OneDrives included), or "All".</param>
public sealed record EdSearchDetail(EdSearch Search, string Query, IReadOnlyList<string> Mailboxes, IReadOnlyList<string> Sites, bool PublicFolders,
    long Items, long Bytes, long UnindexedItems, long UnindexedBytes, IReadOnlyList<EdLocationHits> Hits, string? Errors)
{
    public bool AllMailboxes => EDiscovery.IsAll(Mailboxes);
    public bool AllSites => EDiscovery.IsAll(Sites);
    public bool HasRun => Search.Status == "Completed";
}

/// <summary>A hold in a case: keeps everything (or what matches its query) in the chosen places from being deleted.</summary>
/// <param name="Query">The hold's query; empty holds everything.</param>
/// <param name="Distribution">Purview's word for whether the hold has reached its places ("Success", "Pending", "Error").</param>
public sealed record EdHold(string Id, string Name, string CaseName, bool Enabled, IReadOnlyList<string> Mailboxes, IReadOnlyList<string> Sites, bool PublicFolders,
    string Query, string? Distribution, DateTimeOffset? Created, DateTimeOffset? Modified, bool IsDeleting = false);

/// <summary>Someone who can work on a case.</summary>
public sealed record EdMember(string Name, string? Address);

/// <summary>A case's searches, holds and members, read when its page opens.</summary>
public sealed record EdCaseDetail(IReadOnlyList<EdSearch> Searches, IReadOnlyList<EdHold> Holds, IReadOnlyList<EdMember> Members);

/// <summary>Every case, and every search (in a case or not).</summary>
public sealed record EdDirectory(IReadOnlyList<EdCase> Cases, IReadOnlyList<EdSearch> Searches);

/// <summary>
/// eDiscovery (8f), through Purview's cmdlets: they answer in about a second, need no
/// pay-as-you-go billing (Graph's eDiscovery API does on Standard tenants), and cover cases,
/// searches and holds.
/// </summary>
public interface IEDiscoveryRecords
{
    Task<EdDirectory> GetEDiscoveryAsync(CancellationToken cancellationToken = default);

    Task<EdCaseDetail> GetCaseDetailAsync(EdCase eCase, CancellationToken cancellationToken = default);

    Task<EdSearchDetail> GetEdSearchAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Runs a search again to count what it finds now. Changes nothing, so it isn't a staged change.</summary>
    Task StartEdSearchAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>eDiscovery in words.</summary>
public static class EDiscovery
{
    public const string All = "All";

    public static bool IsAll(IReadOnlyList<string> places) => places.Count == 1 && string.Equals(places[0], All, StringComparison.OrdinalIgnoreCase);

    public static string SearchStatus(string status) => status switch
    {
        "Completed" => "Done",
        "NotStarted" => "Not Run",
        "Starting" or "InProgress" => "Running",
        "Stopping" or "Stopped" => "Stopped",
        "PartiallySucceeded" => "Partly Done",
        "Failed" => "Failed",
        _ => status,
    };

    public static bool IsRunning(string status) => status is "Starting" or "InProgress";

    /// <summary>"Success" → "On everywhere it should be"; anything else as Purview says it.</summary>
    public static string DistributionText(string? status) => status switch
    {
        null or "" => "Unknown",
        "Success" => "In place everywhere",
        "Pending" => "Still being put in place",
        "Error" => "Couldn't be put in place everywhere",
        _ => status,
    };

    /// <summary>"3 mailboxes, 1 site" or "All mailboxes"; "Nowhere" when empty.</summary>
    public static string PlacesText(IReadOnlyList<string> mailboxes, IReadOnlyList<string> sites, bool publicFolders)
    {
        var parts = new List<string>();
        if (IsAll(mailboxes))
            parts.Add("All mailboxes");
        else if (mailboxes.Count > 0)
            parts.Add(Count(mailboxes.Count, "mailbox", "mailboxes"));
        if (IsAll(sites))
            parts.Add("All sites and OneDrives");
        else if (sites.Count > 0)
            parts.Add(Count(sites.Count, "site", "sites"));
        if (publicFolders)
            parts.Add("Public folders");
        return parts.Count == 0 ? "Nowhere" : string.Join(", ", parts);
    }

    static string Count(int n, string one, string many) => $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? one : many)}";

    /// <summary>Bytes as "12.3 MB".</summary>
    public static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes" : $"{value.ToString("0.#", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    /// <summary>Why a name can't be a case, search or hold name, or null. Purview refuses these characters and long names.</summary>
    public static string? NameProblem(string name, IEnumerable<string> taken)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "Give it a name.";
        if (trimmed.Length > 64)
            return "Keep the name to 64 characters or fewer.";
        if (trimmed.IndexOfAny(['\\', '/', '"', '<', '>', '|', '*', '?', ':', '%', '&', '#']) >= 0)
            return "Leave out these characters: \\ / \" < > | * ? : % & #";
        return taken.Contains(trimmed, StringComparer.OrdinalIgnoreCase) ? "That name is already used." : null;
    }

    /// <summary>
    /// A problem in a keyword query (KQL) Purview would refuse or misread: unbalanced quotes or
    /// brackets, or AND/OR/NOT written in lower case (which then search for those words).
    /// </summary>
    public static string? QueryProblem(string query)
    {
        if (query.Count(c => c == '"') % 2 != 0)
            return "A quotation mark isn't closed.";
        var depth = 0;
        foreach (var c in query)
        {
            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            if (depth < 0)
                return "A closing bracket comes before its opening one.";
        }
        if (depth != 0)
            return "A bracket isn't closed.";
        var outsideQuotes = string.Concat(query.Split('"').Where((_, i) => i % 2 == 0));
        foreach (var word in outsideQuotes.Split([' ', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
            if (word is "and" or "or" or "not" or "near")
                return $"Write \"{word.ToUpperInvariant()}\" in capitals to combine terms; in lower case it's searched for as a word.";
        return null;
    }
}

/// <summary>The eDiscovery lists' fields.</summary>
public static class EdFields
{
    public const string Name = "name";
    public const string Status = "status";
    public const string Case = "case";
    public const string Created = "created";
    public const string CreatedBy = "createdBy";
    public const string Modified = "modified";
    public const string ModifiedBy = "modifiedBy";
    public const string LastRun = "lastRun";
    public const string Description = "description";

    public const string Active = "Active";
    public const string Closed = "Closed";
    public const string NoCase = "Not in a Case";

    public static IReadOnlyList<Field<EdCase>> Cases { get; } =
    [
        new() { Key = Name, Label = "Case", Kind = FieldKind.Text, Searchable = true, Aliases = ["case"], Text = c => c.Name },
        new() { Key = Status, Label = "Status", Kind = FieldKind.Choice, ChoiceOrder = [Active, Closed], Text = c => c.IsClosed ? Closed : Active },
        new() { Key = Description, Label = "Description", Kind = FieldKind.Text, Searchable = true, Text = c => c.Description },
        new() { Key = Created, Label = "Created", Kind = FieldKind.Date, Date = c => c.Created },
        new() { Key = Modified, Label = "Last Changed", Kind = FieldKind.Date, Date = c => c.Modified },
        new() { Key = ModifiedBy, Label = "Last Changed By", Kind = FieldKind.Text, Searchable = true, Aliases = ["by"], Text = c => c.ModifiedBy },
    ];

    public static IReadOnlyList<Field<EdSearch>> Searches { get; } =
    [
        new() { Key = Name, Label = "Search", Kind = FieldKind.Text, Searchable = true, Text = s => s.Name },
        new() { Key = Case, Label = "Case", Kind = FieldKind.Choice, Searchable = true, Text = s => s.CaseName ?? NoCase },
        new() { Key = Status, Label = "Status", Kind = FieldKind.Choice, ChoiceOrder = ["Done", "Running", "Not Run", "Stopped", "Partly Done", "Failed"],
            Text = s => EDiscovery.SearchStatus(s.Status) },
        new() { Key = Description, Label = "Description", Kind = FieldKind.Text, Searchable = true, Text = s => s.Description },
        new() { Key = Created, Label = "Created", Kind = FieldKind.Date, Date = s => s.Created },
        new() { Key = CreatedBy, Label = "Created By", Kind = FieldKind.Text, Searchable = true, Aliases = ["by"], Text = s => s.CreatedBy },
        new() { Key = LastRun, Label = "Last Run", Kind = FieldKind.Date, Date = s => s.LastRun },
    ];
}
