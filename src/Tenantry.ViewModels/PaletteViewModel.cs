using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Tenantry.ViewModels;

/// <summary>Something Ctrl+K can find: a person, a group, a list view.</summary>
/// <param name="SearchText">Everything the entry can be found by, lower-cased once when the index is built.</param>
public sealed record PaletteEntry(string Kind, string Title, string? Subtitle, string Icon, string SearchText, Action Open);

/// <summary>One result row, ranked.</summary>
public sealed partial class PaletteResult(PaletteEntry entry) : ObservableObject
{
    public PaletteEntry Entry { get; } = entry;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// Ctrl+K: one box that finds anything in the tenant's local copy and opens it. Every word
/// typed must appear somewhere in an entry; names that start with the query rank first, then
/// names with a word that does, then everything else that matches.
/// </summary>
public sealed partial class PaletteViewModel(Func<IReadOnlyList<PaletteEntry>> entries, Action close) : ObservableObject
{
    const int PerKind = 6;

    IReadOnlyList<PaletteEntry>? _entries;

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<PaletteResult> Results { get; private set; } = [];

    public bool HasResults => Results.Count > 0;

    public bool ShowNoMatches => Query.Trim().Length > 0 && Results.Count == 0;

    public string Hint => "Type a name, address, department or view. ↑ ↓ to move, Enter to open, Esc to close.";

    /// <summary>Called each time the palette opens: a fresh query against the current data.</summary>
    public void Reset()
    {
        _entries = null;
        Query = "";
        Search();
    }

    partial void OnQueryChanged(string value) => Search();

    void Search()
    {
        var words = Query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            SetResults([]);
            return;
        }
        _entries ??= entries();
        var phrase = string.Join(' ', words);
        var ranked = _entries
            .Where(e => words.All(w => e.SearchText.Contains(w, StringComparison.Ordinal)))
            .Select(e => (Entry: e, Score: Score(e.Title.ToLowerInvariant(), phrase)))
            .OrderBy(r => r.Score)
            .ThenBy(r => r.Entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .GroupBy(r => r.Entry.Kind)
            .SelectMany(g => g.Take(PerKind))
            .OrderBy(r => r.Score)
            .ThenBy(r => KindOrder(r.Entry.Kind))
            .ThenBy(r => r.Entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => new PaletteResult(r.Entry))
            .ToArray();
        SetResults(ranked);
    }

    static int Score(string title, string phrase) =>
        title.StartsWith(phrase, StringComparison.Ordinal) ? 0
        : title.Contains(" " + phrase, StringComparison.Ordinal) ? 1
        : 2;

    static int KindOrder(string kind) => kind switch
    {
        "User" => 0,
        "Group" => 1,
        _ => 2,
    };

    void SetResults(IReadOnlyList<PaletteResult> results)
    {
        Results = results;
        if (results.Count > 0)
            results[0].IsSelected = true;
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowNoMatches));
    }

    PaletteResult? Selected => Results.FirstOrDefault(r => r.IsSelected);

    /// <summary>Moves the highlight up or down, wrapping at the ends.</summary>
    public void Move(int delta)
    {
        if (Results.Count == 0)
            return;
        var index = Results.ToList().FindIndex(r => r.IsSelected);
        foreach (var r in Results)
            r.IsSelected = false;
        Results[((index < 0 ? 0 : index + delta) % Results.Count + Results.Count) % Results.Count].IsSelected = true;
    }

    /// <summary>Opens the highlighted result (Enter).</summary>
    public void OpenSelected()
    {
        if (Selected is { } result)
            Open(result);
    }

    [RelayCommand]
    void Open(PaletteResult result)
    {
        close();
        result.Entry.Open();
    }

    [RelayCommand]
    void Close() => close();
}
