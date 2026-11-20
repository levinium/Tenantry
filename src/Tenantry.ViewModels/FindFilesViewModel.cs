using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels;

/// <summary>How far a place has got in a search.</summary>
public enum PlaceState
{
    Waiting,
    Searching,
    Done,
    /// <summary>The admin can't see inside it (not a member or site admin).</summary>
    NoAccess,
    /// <summary>Adding the admin as a site admin, so it can be searched.</summary>
    AddingMe,
    Failed,
}

/// <summary>A choice in one of the search's drop-downs.</summary>
public sealed record SearchChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A kind of file, ticked to narrow the search to it.</summary>
public sealed partial class FileTypeToggle(FileType type, Action changed) : ObservableObject
{
    public FileType Type => type;

    public string Label => type.Label;

    /// <summary>".doc, .docx, .docm…": what ticking it looks for, shown beside it.</summary>
    public string ExtensionsText => string.Join(", ", type.Extensions.Select(e => "." + e));

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value) => changed();

    [RelayCommand]
    void Untick() => IsOn = false;
}

/// <summary>An extension typed in, shown as a badge (".dwg") with an × to take it off.</summary>
public sealed partial class ExtensionBadge(string extension, Action<ExtensionBadge> remove) : ObservableObject
{
    /// <summary>Without the dot: "dwg".</summary>
    public string Extension => extension;

    public string Text => "." + extension;

    [RelayCommand]
    void Remove() => remove(this);
}

/// <summary>One site or OneDrive the search looks in, with how it's going.</summary>
public sealed partial class FilePlaceRow : ObservableObject
{
    readonly Action<FilePlaceRow> _remove;
    readonly Func<FilePlaceRow, Task> _addMe;

    internal FilePlaceRow(SiteSummary site, string detail, Action<FilePlaceRow> remove, Func<FilePlaceRow, Task> addMe)
    {
        Site = site;
        _detail = detail;
        _remove = remove;
        _addMe = addMe;
    }

    public SiteSummary Site { get; }

    public string Title => Site.Kind == SiteKind.OneDrive && Site.OwnerName is { Length: > 0 } owner ? $"{owner}'s OneDrive" : Site.Title;

    /// <summary>"OneDrive · sign-in blocked", "Team site"; with Purview's count when Everywhere found it.</summary>
    public string Detail => PurviewCount is { } n ? $"{_detail} · Purview counted {n:N0}" : _detail;

    readonly string _detail;

    /// <summary>How many items Purview counted here (Everywhere); null for a place chosen by hand.</summary>
    public long? PurviewCount { get; init; }

    public string Icon => Site.Kind == SiteKind.OneDrive ? "Cloud" : "Globe";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsBusy), nameof(IsNoAccess), nameof(IsFailed), nameof(IsDone))]
    public partial PlaceState State { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial int Scanned { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial int Found { get; internal set; }

    /// <summary>Why it failed, or what happened to the access added for the search.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; internal set; }

    public bool HasNote => Note is not null;

    /// <summary>The admin was made a site admin for this search (removed afterwards unless kept).</summary>
    internal bool AddedMe { get; set; }

    /// <summary>Searched only after adding the admin: downloading from it needs that access again.</summary>
    internal bool NeededAccess { get; set; }

    /// <summary>Its files were searched but its recycle bins were refused (only site admins see them).</summary>
    internal bool BinsLocked { get; set; }

    /// <summary>Offered "Add Me and Search" (a pipeline, an account and the right to change sites).</summary>
    [ObservableProperty]
    public partial bool CanAddMe { get; internal set; }

    /// <summary>Not searched yet: it can still be taken off the list.</summary>
    [ObservableProperty]
    public partial bool CanRemove { get; internal set; } = true;

    public bool IsBusy => State is PlaceState.Searching or PlaceState.AddingMe;
    public bool IsNoAccess => State == PlaceState.NoAccess;
    public bool IsFailed => State == PlaceState.Failed;
    public bool IsDone => State == PlaceState.Done;

    public string StateText => State switch
    {
        PlaceState.Waiting => "Waiting",
        PlaceState.Searching => Scanned == 0 ? "Searching…" : $"Searching… {Scanned:N0} looked at",
        PlaceState.AddingMe => "Adding you as a site admin…",
        PlaceState.NoAccess => "You can't see inside it",
        PlaceState.Failed => "Couldn't be searched",
        _ => $"{Found:N0} found, {Scanned:N0} looked at",
    };

    [RelayCommand]
    void Remove() => _remove(this);

    [RelayCommand]
    Task AddMe() => _addMe(this);
}

/// <summary>
/// Find Files (16): look for lost files in chosen sites and OneDrives, including those of people
/// who have left. Each place's libraries are read in full and every file is checked here, so
/// "contains" and regular expressions work the same everywhere. A place the admin can't see inside
/// offers Add Me and Search: the admin becomes a site admin (logged; Test mode still guards it),
/// the place is searched, and the access is taken away again unless they chose to keep it.
/// </summary>
public sealed partial class FindFilesViewModel : ObservableObject
{
    /// <summary>Places read at once: enough to keep busy, few enough that Microsoft doesn't throttle.</summary>
    const int AtOnce = 3;

    /// <summary>The results list stops growing past this; the note says to narrow the search.</summary>
    public const int MaxResults = 20_000;

    readonly IFileSearchRecords? _records;
    readonly Func<IReadOnlyList<SiteSummary>> _sites;
    readonly IDirectoryIndex _index;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly string? _me;
    readonly List<FoundFile> _found = [];
    FileQuery _query = new();
    CancellationTokenSource? _run;

    public FindFilesViewModel(IFileSearchRecords? records, Func<IReadOnlyList<SiteSummary>> sites, IDirectoryIndex index, ChangePipeline? pipeline,
        Availability? canEdit, string? me, IListStateStore? listState = null, Func<DateTimeOffset>? now = null)
    {
        _records = records;
        _sites = sites;
        _index = index;
        _pipeline = pipeline;
        _canEdit = canEdit ?? Availability.Yes;
        _me = me;
        NameMatch = NameMatches[0];
        Modified = ModifiedChoices[0];
        Types = [.. FileType.All.Select(t => new FileTypeToggle(t, CriteriaChanged))];
        AddPerson = new PersonPicker("Person's name or address", () => PlaceOptions(oneDrives: true), Pick) { Multiple = true };
        AddSite = new PersonPicker("Site name or address", () => PlaceOptions(oneDrives: false), Pick) { Multiple = true };
        Results = DataListViewModel.Create(FoundFileList.Definition(f => new FoundFilePreviewViewModel(f, this)),
            _ => Task.FromResult<IReadOnlyList<FoundFile>>([.. _found]), f => Results!.PreviewRow(f), listState, now ?? (() => DateTimeOffset.Now));
        Results.SelectionPanel = (rows, _) => new FoundFilesSelectionViewModel(rows, this);
        Results.HasRecordPages = false;
        Results.PrimaryAction = null;
        Results.EmptyText = "Choose where to look and what to look for, then Find Files.";
        _ = Results.LoadAsync();
    }

    public bool IsAvailable => _records is not null;

    // ---- What to look for ---------------------------------------------------------------------

    public static IReadOnlyList<SearchChoice<NameMatch>> NameMatches { get; } =
        [new(Core.Directory.NameMatch.Contains, "Name Contains"), new(Core.Directory.NameMatch.StartsWith, "Name Starts With"), new(Core.Directory.NameMatch.Regex, "Name Matches (Regex)")];

    /// <summary>Modified within the last so many days (positive), or longer ago than that (negative); 0 for any time.</summary>
    public static IReadOnlyList<SearchChoice<int>> ModifiedChoices { get; } =
        [new(0, "Any Time"), new(30, "In the Last 30 Days"), new(182, "In the Last 6 Months"), new(365, "In the Last Year"), new(-365, "Over a Year Ago"), new(-1095, "Over 3 Years Ago")];

    [ObservableProperty]
    public partial string NameText { get; set; } = "";

    [ObservableProperty]
    public partial SearchChoice<NameMatch> NameMatch { get; set; }

    public ObservableCollection<FileTypeToggle> Types { get; }

    /// <summary>
    /// What's being typed in the extensions box. A comma, space or semicolon turns what's before it
    /// into a badge (<see cref="Extensions"/>, by <see cref="TakeFinishedExtensions"/>); Enter, Tab or
    /// leaving the box does the same for the rest.
    /// </summary>
    [ObservableProperty]
    public partial string ExtensionDraft { get; set; } = "";

    /// <summary>The extensions picked, as badges with an × each.</summary>
    public ObservableCollection<ExtensionBadge> Extensions { get; } = [];

    public bool HasExtensions => Extensions.Count > 0;

    /// <summary>"dwg", from ".DWG", "*.dwg" or "dwg"; null when nothing usable is left.</summary>
    internal static string? CleanExtension(string typed)
    {
        var cleaned = new string(typed.Where(IsExtensionCharacter).ToArray()).ToLowerInvariant();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>What an extension is made of: letters, numbers, - and _.</summary>
    public static bool IsExtensionCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';

    /// <summary>What separates extensions in the box.</summary>
    public static bool IsExtensionSeparator(char c) => c is ',' or ' ' or ';';

    /// <summary>
    /// What the extensions box keeps of typed or pasted text: extension characters and separators.
    /// A dot or star (".dwg", "*.dwg") goes quietly, since the dot is shown for you; anything else
    /// that can't be in an extension says so (<see cref="ExtensionNote"/>).
    /// </summary>
    public string KeepForExtensionBox(string text)
    {
        var kept = new string(text.Where(c => IsExtensionCharacter(c) || IsExtensionSeparator(c)).ToArray());
        var refused = text.Where(c => !IsExtensionCharacter(c) && !IsExtensionSeparator(c) && c is not ('.' or '*') && !char.IsControl(c)).Distinct().ToArray();
        ExtensionNote = refused.Length == 0 ? null
            : $"{string.Join(" ", refused)} can't be in an extension: use letters, numbers, - and _. The dot is added for you.";
        return kept;
    }

    /// <summary>Why something just typed in the extensions box didn't appear; cleared by the next keystroke.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExtensionNote))]
    public partial string? ExtensionNote { get; private set; }

    public bool HasExtensionNote => ExtensionNote is not null;

    void AddExtension(string typed)
    {
        if (CleanExtension(typed) is not { } extension || Extensions.Any(e => e.Extension == extension))
            return;
        Extensions.Add(new ExtensionBadge(extension, RemoveExtension));
        OnPropertyChanged(nameof(HasExtensions));
        CriteriaChanged();
    }

    void RemoveExtension(ExtensionBadge badge)
    {
        Extensions.Remove(badge);
        OnPropertyChanged(nameof(HasExtensions));
        CriteriaChanged();
    }

    /// <summary>Enter, or leaving the box: what's typed becomes badges.</summary>
    [RelayCommand]
    public void AddExtensionDraft()
    {
        var typed = ExtensionDraft;
        ExtensionDraft = "";
        foreach (var part in typed.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
            AddExtension(part);
    }

    /// <summary>Backspace in an empty box takes the last badge off.</summary>
    public void RemoveLastExtension()
    {
        if (ExtensionDraft.Length == 0 && Extensions.Count > 0)
            RemoveExtension(Extensions[^1]);
    }

    partial void OnExtensionDraftChanged(string value) => CriteriaChanged();

    /// <summary>
    /// After a comma, space or semicolon is typed: everything before the last one becomes badges and
    /// the rest stays in the box. The view calls it once the box has finished its own update (changing
    /// the text from inside that update isn't shown in the box).
    /// </summary>
    public void TakeFinishedExtensions()
    {
        var value = ExtensionDraft;
        var cut = value.LastIndexOfAny([',', ' ', ';']);
        if (cut < 0)
            return;
        ExtensionDraft = value[(cut + 1)..];
        foreach (var part in value[..cut].Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
            AddExtension(part);
    }

    /// <summary>The ticked types, shown as chips beside the Type button (each with its ×).</summary>
    public IReadOnlyList<FileTypeToggle> TickedTypes => [.. Types.Where(t => t.IsOn)];

    public bool HasTickedTypes => TickedTypes.Count > 0;

    /// <summary>The row of badges under the Type and Extensions controls has something to show.</summary>
    public bool HasBadges => HasTickedTypes || HasExtensions;

    /// <summary>Kinds ticked or extensions typed: Any Type clears them.</summary>
    public bool HasTypeFilter => HasTickedTypes || TypedExtensions.Count > 0;

    /// <summary>The Type button: "Any Type", "Excel", or "3 Types".</summary>
    public string TypeButtonText => TickedTypes.Count switch
    {
        0 => "Any Type",
        1 => TickedTypes[0].Label,
        var n => $"{n} Types",
    };

    /// <summary>The badges, and what's still being typed (it counts before it becomes a badge).</summary>
    IReadOnlyList<string> TypedExtensions =>
        [.. Extensions.Select(e => e.Extension).Concat(CleanExtension(ExtensionDraft) is { } draft ? [draft] : []).Distinct()];

    /// <summary>"Looking for .xls, .xlsx, .csv and .dwg"; "Any type of file" when nothing narrows it.</summary>
    public string TypeSummary
    {
        get
        {
            var all = TickedTypes.SelectMany(t => t.Type.Extensions).Concat(TypedExtensions).Distinct(StringComparer.OrdinalIgnoreCase).Select(e => "." + e).ToArray();
            return all.Length switch
            {
                0 => "Any type of file (and folders).",
                1 => $"Only {all[0]} files.",
                _ => $"Only {string.Join(", ", all[..^1])} and {all[^1]} files.",
            };
        }
    }

    [RelayCommand]
    void ClearTypes()
    {
        foreach (var t in Types)
            t.IsOn = false;
        ExtensionDraft = "";
        Extensions.Clear();
        OnPropertyChanged(nameof(HasExtensions));
        CriteriaChanged();
    }

    [ObservableProperty]
    public partial SearchChoice<int> Modified { get; set; }

    [ObservableProperty]
    public partial string ModifiedByText { get; set; } = "";

    [ObservableProperty]
    public partial string FolderText { get; set; } = "";

    /// <summary>At least this many MB (blank: any size).</summary>
    [ObservableProperty]
    public partial string MinSizeText { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowMore { get; set; }

    [RelayCommand]
    void ToggleMore() => ShowMore = !ShowMore;

    /// <summary>
    /// The full search form shows; after Find Files it folds to one line (<see cref="SearchSummary"/>)
    /// so the results get the room, until Edit Search.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSearchOpen { get; set; } = true;

    [RelayCommand]
    void EditSearch() => IsSearchOpen = true;

    /// <summary>A search has run, so the form can fold back to its one line.</summary>
    [ObservableProperty]
    public partial bool HasSearched { get; private set; }

    [RelayCommand]
    void HideSearch()
    {
        if (HasSearched)
            IsSearchOpen = false;
    }

    /// <summary>'Name contains "budget" · Word, Excel, .dwg · Modified in the last year · 3 places'.</summary>
    public string SearchSummary
    {
        get
        {
            var parts = new List<string>();
            if (NameText.Trim() is { Length: > 0 } name)
                parts.Add(NameMatch.Value switch
                {
                    Core.Directory.NameMatch.StartsWith => $"Name starts with \"{name}\"",
                    Core.Directory.NameMatch.Regex => $"Name matches {name}",
                    _ => $"Name contains \"{name}\"",
                });
            var kinds = TickedTypes.Select(k => k.Label).Concat(TypedExtensions.Select(e => "." + e)).ToArray();
            parts.Add(kinds.Length == 0 ? "Any type" : string.Join(", ", kinds));
            if (Modified.Value != 0)
                parts.Add("Modified " + Modified.Label.ToLowerInvariant());
            if (ModifiedByText.Trim() is { Length: > 0 } by)
                parts.Add($"By {by}");
            if (FolderText.Trim() is { Length: > 0 } folder)
                parts.Add($"In folders with \"{folder}\"");
            if (MinSizeText.Trim() is { Length: > 0 } size)
                parts.Add($"At least {size} MB");
            parts.Add(IsEverywhere ? "everywhere" : Places.Count == 1 ? $"in {Places[0].Title}" : $"in {Places.Count} places");
            return string.Join(" · ", parts);
        }
    }

    partial void OnNameTextChanged(string value) => CriteriaChanged();
    partial void OnNameMatchChanged(SearchChoice<NameMatch> value) => CriteriaChanged();
    partial void OnModifiedChanged(SearchChoice<int> value) => CriteriaChanged();
    partial void OnModifiedByTextChanged(string value) => CriteriaChanged();
    partial void OnFolderTextChanged(string value) => CriteriaChanged();
    partial void OnMinSizeTextChanged(string value) => CriteriaChanged();

    /// <summary>The search as typed, read when Find Files is pressed.</summary>
    public FileQuery Query(DateTimeOffset now)
    {
        var days = Modified.Value;
        long? minBytes = double.TryParse(MinSizeText.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var mb) && mb > 0
            ? (long)(mb * 1_048_576) : null;
        return new FileQuery(NameText.Trim(), NameMatch.Value,
            Types.Where(t => t.IsOn).Select(t => t.Type.Key).ToHashSet(),
            TypedExtensions,
            days > 0 ? now.AddDays(-days) : null, days < 0 ? now.AddDays(days) : null,
            ModifiedByText.Trim() is { Length: > 0 } who ? who : null, minBytes, null,
            FolderText.Trim() is { Length: > 0 } folder ? folder : null);
    }

    /// <summary>Why Find Files can't run yet; null when it can.</summary>
    public string? Problem
    {
        get
        {
            if (!IsEverywhere && Places.Count == 0)
                return "Add a person's OneDrive or a site to look in, or choose Everywhere.";
            var query = Query(DateTimeOffset.Now);
            if (query.IsEmpty)
                return "Say what to look for: part of a name, a type of file, a date or a person.";
            if (query.Problem is { } broken)
                return broken;
            if (MinSizeText.Trim().Length > 0 && query.MinBytes is null)
                return "The smallest size is a number of MB (for example 5).";
            return null;
        }
    }

    public bool HasProblem => Problem is not null;

    void CriteriaChanged()
    {
        foreach (var name in new[] { nameof(TickedTypes), nameof(HasTickedTypes), nameof(HasBadges), nameof(HasTypeFilter), nameof(SearchSummary), nameof(TypeButtonText), nameof(TypeSummary) })
            OnPropertyChanged(name);
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));
        FindCommand.NotifyCanExecuteChanged();
    }

    // ---- Where to look ------------------------------------------------------------------------

    public ObservableCollection<FilePlaceRow> Places { get; } = [];

    public bool HasPlaces => Places.Count > 0;

    /// <summary>Adds a person's OneDrive (including people who have left).</summary>
    public PersonPicker AddPerson { get; }

    public PersonPicker AddSite { get; }

    /// <summary>
    /// Look everywhere: Purview counts which sites and OneDrives have matches, and those are looked
    /// through. Otherwise only the places chosen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChosenPlaces))]
    public partial bool IsEverywhere { get; set; }

    public bool IsChosenPlaces => !IsEverywhere;

    // The two choices set this one setting (each radio button only shows it, so they can't undo each other).
    [RelayCommand]
    void ChooseEverywhere() => IsEverywhere = true;

    [RelayCommand]
    void ChoosePlaces() => IsEverywhere = false;

    /// <summary>Everywhere can be offered: the records can ask Purview.</summary>
    public bool CanSearchEverywhere => _records is not null;

    partial void OnIsEverywhereChanged(bool value)
    {
        // The places Everywhere found aren't a hand-picked list: switching back starts that list empty.
        if (!value && Places.Any(p => p.PurviewCount is not null) && !IsSearching)
            Places.Clear();
        PlacesChanged();
        OnPropertyChanged(nameof(SearchSummary));
    }

    /// <summary>Keep the site admin access added for the search, instead of it being removed afterwards.</summary>
    [ObservableProperty]
    public partial bool KeepAccess { get; set; }

    /// <summary>
    /// Look in each place's recycle bins too, both stages (deleted files are kept 93 days). Only site
    /// admins can see them, so a place you're only a member of offers Add Me to include them.
    /// </summary>
    [ObservableProperty]
    public partial bool IncludeRecycleBins { get; set; } = true;

    /// <summary>Making yourself a site admin is offered (and how it ends is worth saying).</summary>
    public bool CanAddMeAnywhere => _pipeline is not null && _canEdit.IsAvailable && _me is not null;

    /// <summary>"Sign-in blocked" or "Account deleted" for a OneDrive whose owner has gone; null otherwise.</summary>
    internal string? Departed(SiteSummary site)
    {
        if (site.Kind != SiteKind.OneDrive)
            return null;
        var owner = OwnerOf(site);
        return owner is null ? "Account deleted" : !owner.AccountEnabled ? "Sign-in blocked" : null;
    }

    UserSummary? OwnerOf(SiteSummary site)
    {
        var address = site.OwnerEmail ?? site.OwnerLogin?[(site.OwnerLogin.LastIndexOf('|') + 1)..];
        return address is { Length: > 0 } a
            ? _index.AllUsers.FirstOrDefault(u => string.Equals(u.UserPrincipalName, a, StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(u.Mail, a, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    string DetailOf(SiteSummary site) => site.Kind == SiteKind.OneDrive
        ? Departed(site) is { } gone ? $"OneDrive · {gone}" : "OneDrive"
        : SiteSummary.KindName(site.Kind);

    IEnumerable<PickOption> PlaceOptions(bool oneDrives)
    {
        var chosen = Places.Select(p => p.Site.Id).ToHashSet(StringComparer.Ordinal);
        return _sites()
            .Where(s => (s.Kind == SiteKind.OneDrive) == oneDrives && !chosen.Contains(s.Id))
            .Select(s =>
            {
                var name = oneDrives ? s.OwnerName ?? s.Title : s.Title;
                var gone = Departed(s);
                var detail = string.Join(" · ", new[] { oneDrives ? s.OwnerEmail ?? s.Url : s.Url, gone, s.IsArchived ? "Archived" : null }.Where(x => x is not null));
                return new PickOption(s.Id, name, detail, "site", s.Url,
                    string.Join(' ', name, s.Title, s.Url, s.OwnerEmail, gone, gone is null ? null : "left former").ToLowerInvariant());
            });
    }

    void Pick(PickOption option)
    {
        if (_sites().FirstOrDefault(s => s.Id == option.Id) is { } site)
            Add(site);
    }

    /// <summary>Adds a place to look in (once).</summary>
    public void Add(SiteSummary site)
    {
        if (Places.Any(p => p.Site.Id == site.Id))
            return;
        Places.Add(new FilePlaceRow(site, DetailOf(site), RemovePlace, AddMeAndSearchAsync));
        PlacesChanged();
    }

    void RemovePlace(FilePlaceRow row)
    {
        if (row.IsBusy)
            return;
        Places.Remove(row);
        PlacesChanged();
    }

    void PlacesChanged()
    {
        OnPropertyChanged(nameof(HasPlaces));
        CriteriaChanged();
    }

    [RelayCommand]
    void ClearPlaces()
    {
        if (IsSearching)
            return;
        Places.Clear();
        PlacesChanged();
    }

    /// <summary>From a person's OneDrive tab: start over, looking in their OneDrive. False when they have none.</summary>
    public bool StartFor(UserSummary person)
    {
        var drive = _sites().FirstOrDefault(s => s.Kind == SiteKind.OneDrive && OwnerOf(s)?.Id == person.Id);
        if (drive is null || IsSearching)
            return false;
        Places.Clear();
        Add(drive);
        // A new place to look in: the search starts open, to say what to look for.
        IsSearchOpen = true;
        return true;
    }

    // ---- Searching ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditPlaces))]
    [NotifyCanExecuteChangedFor(nameof(FindCommand), nameof(StopCommand))]
    public partial bool IsSearching { get; private set; }

    public bool CanEditPlaces => !IsSearching;

    /// <summary>"Searching 2 of 5 places · 41 files found", then a summary when done.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; private set; }

    public bool HasStatus => Status is not null;

    public DataListViewModel Results { get; }

    bool CanFind() => !IsSearching && IsAvailable && Problem is null;

    [RelayCommand(CanExecute = nameof(CanFind))]
    async Task Find()
    {
        if (!CanFind())
            return;
        // What's still in the extensions box counts: show it as a badge too.
        AddExtensionDraft();
        OnPropertyChanged(nameof(SearchSummary));
        HasSearched = true;
        IsSearchOpen = false;
        _query = Query(DateTimeOffset.Now);
        _found.Clear();
        if (IsEverywhere && !await FindPlacesEverywhereAsync())
            return;
        foreach (var place in Places)
        {
            place.State = PlaceState.Waiting;
            place.Scanned = place.Found = 0;
            place.Note = null;
            place.CanAddMe = false;
            place.CanRemove = false;
        }
        await RefreshResultsAsync();
        IsSearching = true;
        _run = new CancellationTokenSource();
        var token = _run.Token;
        try
        {
            using var gate = new SemaphoreSlim(AtOnce);
            await Task.WhenAll(Places.ToArray().Select(async place =>
            {
                await gate.WaitAsync(token);
                try
                {
                    await SearchPlaceAsync(place, token);
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            foreach (var place in Places.Where(p => p.State is PlaceState.Waiting or PlaceState.Searching))
            {
                place.State = PlaceState.Failed;
                place.Note = "Stopped before it finished.";
            }
        }
        finally
        {
            IsSearching = false;
            foreach (var place in Places)
                place.CanRemove = !place.IsBusy;
            await RefreshResultsAsync();
            UpdateStatus(done: true);
        }
    }

    [RelayCommand(CanExecute = nameof(IsSearching))]
    void Stop() => _run?.Cancel();

    /// <summary>
    /// Everywhere's first step: Purview counts where matches are (minutes), and those places fill the
    /// list, biggest first. False when there's nothing to look through (none found, stopped, refused).
    /// </summary>
    async Task<bool> FindPlacesEverywhereAsync()
    {
        _everywhereCapped = false;
        Places.Clear();
        PlacesChanged();
        await RefreshResultsAsync();
        IsSearching = true;
        _run = new CancellationTokenSource();
        var query = _query.PurviewQuery();
        Status = "Asking Microsoft Purview which sites and OneDrives have matches. This usually takes a few minutes…";
        try
        {
            var hits = await _records!.CountEverywhereAsync(query, new Progress<string>(s => Status = s), _run.Token);
            var known = _sites().GroupBy(s => Normal(s.Url)).ToDictionary(g => g.Key, g => g.First());
            var found = hits.Where(h => h.Items > 0).OrderByDescending(h => h.Items).ToArray();
            foreach (var hit in found)
            {
                var site = known.GetValueOrDefault(Normal(hit.Location)) ?? Unlisted(hit.Location);
                if (Places.Any(p => p.Site.Url.Equals(site.Url, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Places.Add(new FilePlaceRow(site, DetailOf(site), RemovePlace, AddMeAndSearchAsync) { PurviewCount = hit.Items });
            }
            PlacesChanged();
            _everywhereCapped = found.Length >= IFileSearchRecords.EverywhereMostPlaces;
            _pipeline?.Record(new FilesCountedEverywhere(new ChangeTarget("tenant", "everywhere", "Every site and OneDrive", null),
                $"Purview counted matches in {found.Length:N0} {(found.Length == 1 ? "place" : "places")} for: {(query.Length == 0 ? "everything" : query)}"));
            if (found.Length == 0)
            {
                Status = "Purview found no matches in any site or OneDrive.";
                Results.EmptyText = "Purview found nothing matching anywhere. Files it hasn't indexed yet (very new ones) can be missed: try the places you expect by hand.";
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (_run.IsCancellationRequested)
        {
            Status = "Stopped before Purview finished.";
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Status = $"Purview couldn't search everywhere: {Friendly.Message(e)}";
            return false;
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Purview listed as many places as it does: there may be more with matches.</summary>
    bool _everywhereCapped;

    static string Normal(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>A place Purview named that the local copy doesn't have yet (new since the last sync).</summary>
    static SiteSummary Unlisted(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        var name = Uri.UnescapeDataString(trimmed[(trimmed.LastIndexOf('/') + 1)..]);
        var kind = trimmed.Contains("/personal/", StringComparison.OrdinalIgnoreCase) ? SiteKind.OneDrive : SiteKind.TeamSite;
        return new SiteSummary(trimmed, trimmed, name, "", kind, null, null, null, null, false, false, null, 0, 0, 0, "Unlock", "NotArchived", null, null);
    }

    /// <param name="keepAddedAccess">Retrying after Add Me: the access stays until the last try.</param>
    async Task SearchPlaceAsync(FilePlaceRow place, CancellationToken token, bool keepAddedAccess = false)
    {
        place.State = PlaceState.Searching;
        place.BinsLocked = false;
        UpdateStatus(done: false);
        var lastShown = DateTime.UtcNow;
        async Task TakeAsync(FoundFile file)
        {
            if (!_query.Matches(file) || _found.Count >= MaxResults)
                return;
            _found.Add(file);
            place.Found++;
            // Results show as they're found, without redrawing the list for every file.
            if (DateTime.UtcNow - lastShown > TimeSpan.FromSeconds(1))
            {
                lastShown = DateTime.UtcNow;
                await RefreshResultsAsync();
                UpdateStatus(done: false);
            }
        }
        try
        {
            var progress = new Progress<int>(n => place.Scanned = n);
            await foreach (var file in _records!.ListFilesAsync(place.Site, progress, token))
                await TakeAsync(file);
            if (IncludeRecycleBins)
            {
                try
                {
                    await foreach (var file in _records.ListRecycleBinAsync(place.Site, token))
                        await TakeAsync(file);
                }
                catch (SiteAccessException)
                {
                    place.BinsLocked = true;
                    place.CanAddMe = CanAddMeAnywhere && !place.AddedMe && place.Site.Id != place.Site.Url;
                    place.Note = place.CanAddMe
                        ? "Its recycle bins weren't searched: only site admins can see them. Add yourself to include them."
                        : "Its recycle bins weren't searched: only site admins can see them.";
                }
            }
            place.State = PlaceState.Done;
        }
        catch (SiteAccessException)
        {
            place.State = PlaceState.NoAccess;
            // A place the local copy doesn't have yet has no site ID to make you an admin of: Refresh Sites first.
            place.CanAddMe = CanAddMeAnywhere && !place.AddedMe && place.Site.Id != place.Site.Url;
            place.Note = place.CanAddMe
                ? KeepAccess ? "Add yourself as a site admin to search it. You'll keep the access." : "Add yourself as a site admin to search it. The access is removed again when the search is done."
                : place.Site.Id == place.Site.Url
                    ? "It's new since Tenantry last read the sites: Refresh Sites, then search again to add yourself here."
                    : "Only its members and site admins can see its files.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            place.State = PlaceState.Failed;
            place.Note = Friendly.Message(e);
        }
        finally
        {
            if (!keepAddedAccess)
                await RemoveAddedAccessAsync(place);
            await RefreshResultsAsync();
            UpdateStatus(done: false);
        }
    }

    ChangeOp SiteAdminChange(SiteSummary site, bool add)
    {
        var me = _index.UserByUpn(_me!) is { } user ? Records.GroupAccess.Target(user) : new ChangeTarget("user", _me!, _me!, _me);
        return new SetSiteAdmin(SiteChange.TargetFor(site), site.Url, me, SetOneDriveAccess.LoginFor(_me!), !add, add);
    }

    /// <summary>Add Me and Search: make the admin a site admin now (logged), then search the place.</summary>
    async Task AddMeAndSearchAsync(FilePlaceRow place)
    {
        if (!place.CanAddMe || _pipeline is null || _me is null || place.IsBusy)
            return;
        place.CanAddMe = false;
        place.CanRemove = false;
        place.State = PlaceState.AddingMe;
        place.Note = null;
        var result = await _pipeline.ApplyNowAsync(SiteAdminChange(place.Site, add: true));
        if (!result.Succeeded)
        {
            place.State = PlaceState.NoAccess;
            place.Note = $"You couldn't be added: {result.Detail}";
            place.CanRemove = true;
            return;
        }
        place.AddedMe = true;
        place.NeededAccess = true;
        var wasSearching = IsSearching;
        IsSearching = true;
        _run ??= new CancellationTokenSource();
        try
        {
            // SharePoint can take a moment to let a new site admin in: try again briefly before giving up.
            for (var attempt = 0; ; attempt++)
            {
                await SearchPlaceAsync(place, _run.Token, keepAddedAccess: true);
                if (place.State != PlaceState.NoAccess && !place.BinsLocked || attempt >= 2)
                    break;
                await Task.Delay(TimeSpan.FromSeconds(5), _run.Token);
            }
        }
        catch (OperationCanceledException)
        {
            place.State = PlaceState.Failed;
            place.Note = "Stopped before it finished.";
        }
        finally
        {
            // After the last try, whatever it found: the access added for it goes (unless kept).
            await RemoveAddedAccessAsync(place);
            // Still refused after the tries (SharePoint can be slow to let a new admin in): it can be tried again.
            place.CanAddMe = CanAddMeAnywhere && (place.State == PlaceState.NoAccess || place.BinsLocked);
            if (!wasSearching)
            {
                IsSearching = false;
                UpdateStatus(done: true);
            }
            place.CanRemove = !place.IsBusy;
        }
    }

    /// <summary>Takes away the site admin access added for this search, unless the admin chose to keep it.</summary>
    async Task RemoveAddedAccessAsync(FilePlaceRow place)
    {
        if (!place.AddedMe || _pipeline is null || _me is null)
            return;
        if (KeepAccess)
        {
            place.AddedMe = false;
            place.Note = Append(place.Note, "You're still a site admin here, as you chose.");
            return;
        }
        place.AddedMe = false;
        var result = await _pipeline.ApplyNowAsync(SiteAdminChange(place.Site, add: false));
        place.Note = Append(place.Note, result.Succeeded
            ? "The site admin access added for the search was removed."
            : $"The site admin access added for the search couldn't be removed: {result.Detail} Remove it from the site's page.");
    }

    static string Append(string? note, string more) => note is null ? more : $"{note} {more}";

    // ---- Downloading --------------------------------------------------------------------------

    /// <summary>
    /// Saves found files and folders (a folder with everything in it, its subfolders as folders)
    /// into a folder the admin chooses, and logs each. Files in a recycle bin are left out: they
    /// can't be read until they're restored.
    /// </summary>
    /// <returns>Where they went, or why they didn't; null when no folder was chosen.</returns>
    internal async Task<(string Text, string? Folder)?> DownloadAsync(IReadOnlyList<FoundFile> items, IProgress<string> status, CancellationToken cancellationToken)
    {
        items = [.. items.Where(i => !i.IsDeleted)];
        if (_records is null || items.Count == 0 || await Platform.PickFolder() is not { } into)
            return null;
        var files = 0;
        long bytes = 0;
        string? savedAs = null;
        var problems = new List<string>();
        foreach (var group in items.GroupBy(i => i.PlaceId))
        {
            var refused = await WithAccessAsync(group.Key, status, async () =>
            {
                foreach (var item in group)
                {
                    try
                    {
                        var (count, size, saved) = await DownloadOneAsync(item, into, status, cancellationToken);
                        files += count;
                        bytes += size;
                        savedAs = saved;
                        var what = item.IsFolder ? $"Folder \"{item.Path}/{item.Name}\" ({count:N0} files, {MailDetails.Size(size)})" : $"\"{item.Path}/{item.Name}\" ({MailDetails.Size(size)})";
                        _pipeline?.Record(new FilesDownloaded(PlaceTarget(item), $"{what} from {item.PlaceTitle} to this computer"));
                    }
                    catch (SiteAccessException)
                    {
                        return "You can't open where it is. Tick Keep Site Admin Access, use Add Me and Search on its place, then download it.";
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        problems.Add(items.Count == 1 ? $"It couldn't be downloaded: {Friendly.Message(e)}" : $"{item.Name} couldn't be downloaded: {Friendly.Message(e)}");
                    }
                }
                return null;
            }, cancellationToken);
            if (refused is not null)
                problems.Add(refused);
        }
        if (savedAs is null)
            return (problems.FirstOrDefault() ?? "Nothing was downloaded.", null);
        var text = items is [{ IsFolder: false }]
            ? $"Saved to {savedAs}."
            : $"Saved {files:N0} {(files == 1 ? "file" : "files")} ({MailDetails.Size(bytes)}) to {(items is [{ IsFolder: true }] ? savedAs : into)}.";
        if (problems.Count > 0)
            text += $" Not everything: {problems[0]}";
        return (text, items is [{ IsFolder: true }] ? savedAs : into);
    }

    /// <summary>One file, or a folder with everything in it; how many files, their size, and where it went.</summary>
    async Task<(int Files, long Bytes, string Saved)> DownloadOneAsync(FoundFile item, string into, IProgress<string> status, CancellationToken cancellationToken)
    {
        if (!item.IsFolder)
        {
            var path = UniquePath(into, item.Name, folder: false);
            status.Report($"Downloading {item.Name}…");
            return (1, await SaveAsync(item, path, cancellationToken), path);
        }
        var saved = UniquePath(into, item.Name, folder: true);
        Directory.CreateDirectory(saved);
        var files = 0;
        long bytes = 0;
        await foreach (var file in _records!.ListFolderAsync(item, cancellationToken))
        {
            var folder = file.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate(saved, (at, part) => Path.Combine(at, Safe(part)));
            Directory.CreateDirectory(folder);
            status.Report($"Downloading {file.Name} ({files + 1:N0} so far)…");
            bytes += await SaveAsync(file, UniquePath(folder, file.Name, folder: false), cancellationToken);
            files++;
        }
        return (files, bytes, saved);
    }

    /// <summary>
    /// Runs <paramref name="work"/> where the admin can see inside the place: one searched only after
    /// adding them gets that access again just for this, then loses it (unless they chose to keep it).
    /// </summary>
    /// <returns>Why it couldn't be done, or null when it was.</returns>
    async Task<string?> WithAccessAsync(string placeId, IProgress<string> status, Func<Task<string?>> work, CancellationToken cancellationToken)
    {
        var place = Places.FirstOrDefault(p => p.Site.Id == placeId);
        var regain = place is { NeededAccess: true, AddedMe: false } && !KeepAccess && _pipeline is not null && _me is not null;
        if (regain)
        {
            status.Report($"Adding you as a site admin of {place!.Title} again for this…");
            var added = await _pipeline!.ApplyNowAsync(SiteAdminChange(place.Site, add: true), cancellationToken);
            if (!added.Succeeded)
                return $"You couldn't be added to {place.Title}: {added.Detail}";
        }
        try
        {
            return await work();
        }
        finally
        {
            if (regain)
                await _pipeline!.ApplyNowAsync(SiteAdminChange(place!.Site, add: false), CancellationToken.None);
        }
    }

    /// <summary>The change target for a found file's place, as the Sites list knows it (the guard judges its address).</summary>
    ChangeTarget PlaceTarget(FoundFile file)
    {
        var site = Places.FirstOrDefault(p => p.Site.Id == file.PlaceId)?.Site ?? _sites().FirstOrDefault(s => s.Id == file.PlaceId);
        if (site is not null)
            return SiteChange.TargetFor(site);
        var url = file.PlaceUrl.TrimEnd('/');
        return new ChangeTarget("site", file.PlaceId, file.PlaceTitle, Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]));
    }

    /// <summary>"Ada Lovelace's OneDrive" or the site's title.</summary>
    static string PlaceName(SiteSummary site) => site.Kind == SiteKind.OneDrive && site.OwnerName is { Length: > 0 } owner ? $"{owner}'s OneDrive" : site.Title;

    static FileRef RefOf(FoundFile file) => new(file.DriveId, file.Id, file.Name, file.IsFolder);

    static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";

    /// <summary>Restoring, giving access and copying change Microsoft 365: they need the right to change sites.</summary>
    internal bool CanChangeFiles => CanAddMeAnywhere && _records is not null;

    /// <summary>
    /// Puts recycle bin items back where they were (logged; Test mode guards the place). Each restored
    /// one is shown as it is back in its library, so it can be opened, shared or copied straight away.
    /// </summary>
    internal async Task<string> RestoreAsync(IReadOnlyList<FoundFile> items, IProgress<string> status, CancellationToken cancellationToken)
    {
        items = [.. items.Where(i => i.IsDeleted)];
        if (items.Count == 0 || _pipeline is null || _records is null)
            return "Nothing to restore.";
        var back = 0;
        var problems = new List<string>();
        foreach (var group in items.GroupBy(i => i.PlaceId))
        {
            var first = group.First();
            var refused = await WithAccessAsync(group.Key, status, async () =>
            {
                status.Report($"Restoring {(group.Count() == 1 ? first.Name : Count(group.Count(), "item", "items"))} in {first.PlaceTitle}…");
                var result = await _pipeline.ApplyNowAsync(new RestoreFiles(PlaceTarget(first), first.PlaceUrl, [.. group.Select(i => i.Id)], [.. group.Select(i => i.Name)]),
                    cancellationToken);
                var notBack = result.Remaining is RestoreFiles left ? left.BinIds.ToHashSet(StringComparer.Ordinal)
                    : result.Succeeded ? [] : group.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var item in group.Where(i => !notBack.Contains(i.Id)))
                {
                    back++;
                    FoundFile? restored = null;
                    try
                    {
                        restored = await _records.FindRestoredAsync(item, cancellationToken);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // It's back; it just can't be shown as it is now. Searching again finds it.
                    }
                    var index = _found.IndexOf(item);
                    if (index >= 0)
                    {
                        if (restored is null)
                            _found.RemoveAt(index);
                        else
                            _found[index] = restored;
                    }
                }
                return result.Succeeded ? null : result.Detail;
            }, cancellationToken);
            if (refused is not null)
                problems.Add(refused);
        }
        await RefreshResultsAsync();
        var text = back == 0 ? "" : $"Restored {Count(back, "item", "items")} to where {(back == 1 ? "it was" : "they were")}.";
        return problems.Count == 0 ? text : $"{text} {(back == 0 ? "" : "Not everything: ")}{problems[0]}".Trim();
    }

    /// <summary>People and groups files can be shared with: people, and groups SharePoint knows (not distribution lists or mailboxes).</summary>
    internal IEnumerable<PickOption> ShareOptions() =>
        _index.AllUsers.Where(u => u.AccountEnabled).Select(PickOption.Of)
            .Concat(_index.AllGroups.Where(g => g.Kind is GroupKind.Microsoft365 or GroupKind.Security or GroupKind.MailEnabledSecurity).Select(PickOption.Of));

    /// <summary>Gives people or groups access to files and folders where they are (logged; Test mode guards the place and everyone given access).</summary>
    internal async Task<string> ShareAsync(IReadOnlyList<FoundFile> items, IReadOnlyList<ChangeTarget> people, bool canEdit, bool sendEmail, string? message,
        IProgress<string> status, CancellationToken cancellationToken)
    {
        items = [.. items.Where(i => !i.IsDeleted)];
        if (items.Count == 0 || people.Count == 0 || _pipeline is null)
            return "Nothing to share.";
        var shared = 0;
        var problems = new List<string>();
        foreach (var group in items.GroupBy(i => i.PlaceId))
        {
            var first = group.First();
            var refused = await WithAccessAsync(group.Key, status, async () =>
            {
                status.Report($"Giving access in {first.PlaceTitle}…");
                var refs = group.Select(RefOf).ToArray();
                var result = await _pipeline.ApplyNowAsync(new ShareFiles(PlaceTarget(first), refs, people, canEdit, sendEmail, message), cancellationToken);
                shared += result.Succeeded ? refs.Length : refs.Length - ((result.Remaining as ShareFiles)?.Files.Count ?? refs.Length);
                return result.Succeeded ? null : result.Detail;
            }, cancellationToken);
            if (refused is not null)
                problems.Add(refused);
        }
        var who = FileRef.Describe([.. people.Select(p => p.Name)]);
        var text = shared == 0 ? "" : $"{who} can now {(canEdit ? "edit" : "open")} {(items.Count == 1 && shared == 1 ? "it" : Count(shared, "item", "items"))}{(sendEmail ? ", and were emailed a link" : "")}.";
        return problems.Count == 0 ? text : $"{text} {(shared == 0 ? "" : "Not everything: ")}{problems[0]}".Trim();
    }

    /// <summary>Every site and OneDrive copies can go to.</summary>
    internal IEnumerable<PickOption> DestinationOptions() =>
        _sites().Where(s => s.Kind is not SiteKind.System && !s.IsArchived)
            .OrderBy(s => s.Kind == SiteKind.OneDrive)
            .Select(s =>
            {
                var name = PlaceName(s);
                var detail = string.Join(" · ", new[] { s.Kind == SiteKind.OneDrive ? "OneDrive" : SiteSummary.KindName(s.Kind), s.Url, Departed(s) }.Where(x => x is not null));
                return new PickOption(s.Id, name, detail, "site", s.Url, string.Join(' ', name, s.Title, s.Url, s.OwnerEmail).ToLowerInvariant());
            });

    internal SiteSummary? SiteById(string id) => _sites().FirstOrDefault(s => s.Id == id);

    /// <summary>How long SharePoint is given to let a newly added site admin in, between checks (tests shorten it).</summary>
    internal static TimeSpan AccessWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Copies files and folders into a folder at the top of another place's main library (logged;
    /// Test mode guards where they go). If the admin can't open that place, they're added as its
    /// site admin for the copy and removed again (unless they chose to keep it).
    /// </summary>
    internal async Task<string> CopyAsync(IReadOnlyList<FoundFile> items, SiteSummary destination, string folder, IProgress<string> status, CancellationToken cancellationToken)
    {
        items = [.. items.Where(i => !i.IsDeleted)];
        if (items.Count == 0 || _pipeline is null || _records is null)
            return "Nothing to copy.";
        var there = PlaceName(destination);
        var addedThere = false;
        if (!await _records.CanOpenAsync(destination, cancellationToken))
        {
            if (!CanAddMeAnywhere || destination.Id == destination.Url)
                return $"You can't open {there}, so nothing can be put there.";
            status.Report($"Adding you as a site admin of {there} for the copy…");
            var added = await _pipeline.ApplyNowAsync(SiteAdminChange(destination, add: true), cancellationToken);
            if (!added.Succeeded)
                return $"You couldn't be added to {there}: {added.Detail}";
            addedThere = true;
            for (var attempt = 0; attempt < 3 && !await _records.CanOpenAsync(destination, cancellationToken); attempt++)
                await Task.Delay(AccessWait, cancellationToken);
        }
        var copied = 0;
        var problems = new List<string>();
        try
        {
            foreach (var group in items.GroupBy(i => i.PlaceId))
            {
                var first = group.First();
                var refused = await WithAccessAsync(group.Key, status, async () =>
                {
                    var refs = group.Select(RefOf).ToArray();
                    status.Report($"Copying {(refs.Length == 1 ? first.Name : Count(refs.Length, "item", "items"))} to {there}…");
                    var result = await _pipeline.ApplyNowAsync(new CopyFiles(SiteChange.TargetFor(destination), destination.Url, folder, refs, first.PlaceTitle), cancellationToken);
                    copied += result.Succeeded ? refs.Length : refs.Length - ((result.Remaining as CopyFiles)?.Files.Count ?? refs.Length);
                    return result.Succeeded ? null : result.Detail;
                }, cancellationToken);
                if (refused is not null)
                    problems.Add(refused);
            }
        }
        finally
        {
            if (addedThere && !KeepAccess)
                await _pipeline.ApplyNowAsync(SiteAdminChange(destination, add: false), CancellationToken.None);
        }
        var text = copied == 0 ? "" : $"Copied {(items.Count == 1 && copied == 1 ? "it" : Count(copied, "item", "items"))} to {(folder.Length == 0 ? there : $"\"{folder}\" in {there}")}.";
        return problems.Count == 0 ? text : $"{text} {(copied == 0 ? "" : "Not everything: ")}{problems[0]}".Trim();
    }

    async Task<long> SaveAsync(FoundFile file, string path, CancellationToken cancellationToken)
    {
        try
        {
            await using (var stream = File.Create(path))
                await _records!.DownloadAsync(file, stream, null, cancellationToken);
            return new FileInfo(path).Length;
        }
        catch
        {
            // Half a file is worse than none.
            File.Delete(path);
            throw;
        }
    }

    static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>"Budget.xlsx", or "Budget (2).xlsx" when that's taken: nothing on the computer is overwritten.</summary>
    static string UniquePath(string into, string name, bool folder)
    {
        var safe = Safe(name);
        var path = Path.Combine(into, safe);
        static bool Taken(string p) => File.Exists(p) || Directory.Exists(p);
        for (var n = 2; Taken(path); n++)
            path = Path.Combine(into, folder ? $"{safe} ({n})" : $"{Path.GetFileNameWithoutExtension(safe)} ({n}){Path.GetExtension(safe)}");
        return path;
    }

    Task RefreshResultsAsync() => Results.ReloadAsync();

    void UpdateStatus(bool done)
    {
        var finished = Places.Count(p => p.State is PlaceState.Done or PlaceState.NoAccess or PlaceState.Failed);
        var found = $"{_found.Count:N0} found";
        var capped = _found.Count >= MaxResults ? $" (the first {MaxResults:N0}: narrow the search for the rest)" : "";
        if (!done)
        {
            Status = $"Searching {Math.Min(finished + 1, Places.Count)} of {Places.Count} · {found}{capped}";
            return;
        }
        var locked = Places.Count(p => p.State == PlaceState.NoAccess);
        var failed = Places.Count(p => p.State == PlaceState.Failed);
        var extras = new List<string>();
        if (locked > 0)
            extras.Add(locked == 1 ? "1 place you can't see inside" : $"{locked} places you can't see inside");
        if (failed > 0)
            extras.Add(failed == 1 ? "1 couldn't be searched" : $"{failed} couldn't be searched");
        if (IsEverywhere && _everywhereCapped)
            extras.Add($"Purview lists only {IFileSearchRecords.EverywhereMostPlaces} places with matches, so there may be more: narrow the search to find them");
        Status = $"Done · {found}{capped}" + (extras.Count > 0 ? " · " + string.Join(", ", extras) : "");
        Results.EmptyText = "Nothing matched in the places searched.";
    }
}
