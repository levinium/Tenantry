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
        Detail = detail;
        _remove = remove;
        _addMe = addMe;
    }

    public SiteSummary Site { get; }

    public string Title => Site.Kind == SiteKind.OneDrive && Site.OwnerName is { Length: > 0 } owner ? $"{owner}'s OneDrive" : Site.Title;

    /// <summary>"OneDrive · sign-in blocked", "Team site".</summary>
    public string Detail { get; }

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
        Results = DataListViewModel.Create(FoundFileList.Definition(f => new FoundFilePreviewViewModel(f, DownloadAsync)),
            _ => Task.FromResult<IReadOnlyList<FoundFile>>([.. _found]), f => Results!.PreviewRow(f), listState, now ?? (() => DateTimeOffset.Now));
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
            if (Places.Count == 0)
                return "Add a person's OneDrive or a site to look in.";
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
        foreach (var name in new[] { nameof(TickedTypes), nameof(HasTickedTypes), nameof(HasBadges), nameof(HasTypeFilter), nameof(TypeButtonText), nameof(TypeSummary) })
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

    /// <summary>Keep the site admin access added for the search, instead of it being removed afterwards.</summary>
    [ObservableProperty]
    public partial bool KeepAccess { get; set; }

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
        _query = Query(DateTimeOffset.Now);
        _found.Clear();
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

    async Task SearchPlaceAsync(FilePlaceRow place, CancellationToken token)
    {
        place.State = PlaceState.Searching;
        UpdateStatus(done: false);
        var lastShown = DateTime.UtcNow;
        try
        {
            var progress = new Progress<int>(n => place.Scanned = n);
            await foreach (var file in _records!.ListFilesAsync(place.Site, progress, token))
            {
                if (!_query.Matches(file) || _found.Count >= MaxResults)
                    continue;
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
            place.State = PlaceState.Done;
        }
        catch (SiteAccessException)
        {
            place.State = PlaceState.NoAccess;
            place.CanAddMe = CanAddMeAnywhere && !place.AddedMe;
            place.Note = place.CanAddMe
                ? KeepAccess ? "Add yourself as a site admin to search it. You'll keep the access." : "Add yourself as a site admin to search it. The access is removed again when the search is done."
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
                await SearchPlaceAsync(place, _run.Token);
                if (place.State != PlaceState.NoAccess || attempt >= 2)
                    break;
                place.AddedMe = true;
                await Task.Delay(TimeSpan.FromSeconds(5), _run.Token);
            }
        }
        catch (OperationCanceledException)
        {
            place.State = PlaceState.Failed;
            place.Note = "Stopped before it finished.";
            await RemoveAddedAccessAsync(place);
        }
        finally
        {
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
        // While retrying a refusal, the access stays until the last try.
        if (place.State == PlaceState.NoAccess)
            return;
        place.AddedMe = false;
        var result = await _pipeline.ApplyNowAsync(SiteAdminChange(place.Site, add: false));
        place.Note = Append(place.Note, result.Succeeded
            ? "The site admin access added for the search was removed."
            : $"The site admin access added for the search couldn't be removed: {result.Detail} Remove it from the site's page.");
    }

    static string Append(string? note, string more) => note is null ? more : $"{note} {more}";

    // ---- Downloading --------------------------------------------------------------------------

    /// <summary>
    /// Saves a found file, or everything in a found folder (its subfolders as folders), into a
    /// folder the admin chooses, and logs it. A place searched only after adding the admin gets that
    /// access again just for the download, then loses it, unless they chose to keep it.
    /// </summary>
    /// <returns>Where it went, or why it didn't; null when no folder was chosen.</returns>
    internal async Task<(string Text, string? Folder)?> DownloadAsync(FoundFile item, IProgress<string> status, CancellationToken cancellationToken)
    {
        if (_records is null || await Platform.PickFolder() is not { } into)
            return null;
        var place = Places.FirstOrDefault(p => p.Site.Id == item.PlaceId);
        var regain = place is { NeededAccess: true, AddedMe: false } && !KeepAccess && _pipeline is not null && _me is not null;
        if (regain)
        {
            status.Report("Adding you as a site admin again for the download…");
            var added = await _pipeline!.ApplyNowAsync(SiteAdminChange(place!.Site, add: true), cancellationToken);
            if (!added.Succeeded)
                return ($"You couldn't be added to download it: {added.Detail}", null);
        }
        var files = 0;
        long bytes = 0;
        string saved;
        try
        {
            if (item.IsFolder)
            {
                saved = UniquePath(into, item.Name, folder: true);
                Directory.CreateDirectory(saved);
                await foreach (var file in _records.ListFolderAsync(item, cancellationToken))
                {
                    var folder = file.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate(saved, (at, part) => Path.Combine(at, Safe(part)));
                    Directory.CreateDirectory(folder);
                    status.Report($"Downloading {file.Name} ({files + 1:N0} so far)…");
                    bytes += await SaveAsync(file, UniquePath(folder, file.Name, folder: false), cancellationToken);
                    files++;
                }
            }
            else
            {
                saved = UniquePath(into, item.Name, folder: false);
                status.Report($"Downloading {item.Name}…");
                bytes = await SaveAsync(item, saved, cancellationToken);
                files = 1;
            }
        }
        catch (SiteAccessException)
        {
            return ("You can't open where it is. Tick Keep Site Admin Access, use Add Me and Search on its place, then download it.", null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ($"It couldn't be downloaded: {Friendly.Message(e)}", null);
        }
        finally
        {
            if (regain)
                await _pipeline!.ApplyNowAsync(SiteAdminChange(place!.Site, add: false), CancellationToken.None);
        }
        var what = item.IsFolder ? $"Folder \"{item.Path}/{item.Name}\" ({files:N0} files, {MailDetails.Size(bytes)})" : $"\"{item.Path}/{item.Name}\" ({MailDetails.Size(bytes)})";
        _pipeline?.Record(new FilesDownloaded(place is null ? new ChangeTarget("site", item.PlaceId, item.PlaceTitle, item.PlaceUrl) : SiteChange.TargetFor(place.Site),
            $"{what} from {item.PlaceTitle} to this computer"));
        return (item.IsFolder ? $"Saved {files:N0} {(files == 1 ? "file" : "files")} ({MailDetails.Size(bytes)}) to {saved}." : $"Saved to {saved}.",
            item.IsFolder ? saved : into);
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
        Status = $"Done · {found}{capped}" + (extras.Count > 0 ? " · " + string.Join(", ", extras) : "");
        Results.EmptyText = "Nothing matched in the places searched.";
    }
}

/// <summary>A found file or folder in the preview pane: where it is, who last changed it, links to it, and Download.</summary>
public sealed partial class FoundFilePreviewViewModel(FoundFile file, Func<FoundFile, IProgress<string>, CancellationToken, Task<(string Text, string? Folder)?>>? download = null)
    : ObservableObject
{
    public FoundFile File => file;

    public string Name => file.Name;
    public bool IsFolder => file.IsFolder;
    public string DownloadText => file.IsFolder ? "Download Folder" : "Download";
    public string DownloadTip => file.IsFolder
        ? "Save everything in this folder, with its subfolders, to a folder on this computer (logged)"
        : "Save a copy to a folder on this computer (logged)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsDownloading { get; private set; }

    public bool CanDownload => download is not null && !IsDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownloadNote))]
    public partial string? DownloadNote { get; private set; }

    public bool HasDownloadNote => DownloadNote is not null;

    /// <summary>Where the download went, to show it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaved))]
    public partial string? SavedIn { get; private set; }

    public bool HasSaved => SavedIn is not null;

    [RelayCommand]
    async Task Download()
    {
        if (!CanDownload)
            return;
        IsDownloading = true;
        SavedIn = null;
        try
        {
            var done = await download!(file, new Progress<string>(text => DownloadNote = text), CancellationToken.None);
            DownloadNote = done?.Text;
            SavedIn = done?.Folder;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    void ShowSaved()
    {
        if (SavedIn is { } folder)
            Platform.OpenFolder(folder);
    }
    public string TypeText => FoundFileFields.TypeOf(file);
    public string PlaceText => file.PlaceTitle;
    public string PathText => file.Path;
    public string SizeText => MailDetails.Size(file.Size);
    public string ModifiedText => file.Modified is { } at ? $"{at.ToLocalTime():d MMM yyyy h:mm tt}" + (file.ModifiedBy is { } by ? $" by {by}" : "") : "Not known";
    public string CreatedText => file.Created is { } at ? $"{at.ToLocalTime():d MMM yyyy}" + (file.CreatedBy is { } by ? $" by {by}" : "") : "Not known";
    public bool HasLink => file.WebUrl is { Length: > 0 };
    public bool HasFolder => file.FolderUrl is not null;

    [ObservableProperty]
    public partial bool Copied { get; private set; }

    [RelayCommand]
    void Open()
    {
        if (file.WebUrl is { Length: > 0 } url)
            Platform.OpenUrl(url);
    }

    [RelayCommand]
    void OpenFolder()
    {
        if (file.FolderUrl is { } url)
            Platform.OpenUrl(url);
    }

    [RelayCommand]
    void CopyLink()
    {
        if (file.WebUrl is not { Length: > 0 } url)
            return;
        Platform.CopyText(url);
        Copied = true;
    }
}
