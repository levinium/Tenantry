using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// Search and Delete (8f-2): what a search found is removed from the mailboxes it looked in, soft
/// (recoverable for a while) or hard. Purview takes at most 10 items per mailbox per run, and never
/// touches sites, OneDrives or items it couldn't read. Add puts it in Pending changes, where the
/// search's name must be typed before it's applied.
/// </summary>
public sealed partial class EdPurgeViewModel : ObservableObject
{
    public static IReadOnlyList<EditorChoice> Kinds { get; } =
    [
        new("soft", "Soft Delete", "The items go to each mailbox's Recoverable Items, where the person (or an admin) can restore them until the deleted-items retention period ends."),
        new("hard", "Hard Delete", "The items are removed for good the next time Microsoft processes each mailbox. Use for something that must not be recovered."),
    ];

    readonly ChangePipeline _pipeline;
    readonly EdSearchDetail _detail;
    readonly IDirectoryIndex _index;
    readonly Action _close;

    public EdPurgeViewModel(ChangePipeline pipeline, EdSearchDetail detail, IDirectoryIndex index, Action close)
    {
        _pipeline = pipeline;
        _detail = detail;
        _index = index;
        _close = close;
    }

    public string SearchName => _detail.Search.Name;

    public string Found => $"{_detail.Items:N0} items ({EDiscovery.Size(_detail.Bytes)}) in {EDiscovery.PlacesText(_detail.Mailboxes, _detail.Sites, _detail.PublicFolders).ToLowerInvariant()}.";

    public string Limits =>
        $"Purview removes at most {PurgeSearch.PerMailbox} items from each mailbox per run, so a search that found more needs running again afterwards. " +
        "Only mail is removed: sites, OneDrives and items Purview couldn't read stay as they are.";

    public string Query => _detail.Query.Length == 0 ? "No query: everything in its mailboxes." : _detail.Query;

    [ObservableProperty]
    public partial EditorChoice Kind { get; set; } = Kinds[0];

    /// <summary>Why it can't be done for this search, or null.</summary>
    public string? Problem => !_detail.HasRun ? "Run the search first, so its counts are current."
        : _detail.Mailboxes.Count == 0 ? "The search doesn't look in any mailboxes; Search and Delete only removes mail."
        : _detail.Items == 0 ? "The search found nothing to delete."
        : _detail.Query.Trim().Length == 0 ? "The search has no query, so it would delete everything in its mailboxes. Give it a query first."
        : null;

    public bool HasProblem => Problem is not null;
    public bool CanAdd => Problem is null;

    [RelayCommand]
    void Add()
    {
        if (!CanAdd)
            return;
        _pipeline.Stage(new PurgeSearch(EdPlaces.SearchTarget(SearchName), EdPlaces.Mailboxes(_detail.Mailboxes, _index), Kind.Value == "hard", _detail.Items));
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}

/// <summary>
/// Export (8f-2): Purview makes the export (through Graph), then Tenantry downloads its files to a
/// folder on this computer, with a wheel and progress throughout. On Standard-licensed tenants
/// Microsoft charges for Graph exports (Purview pay-as-you-go), so the popup says so and offers to
/// export in Purview instead. Nothing in Microsoft 365 changes; the export is noted in the change log.
/// </summary>
public sealed partial class EdExportViewModel : ObservableObject, IDisposable
{
    /// <summary>How often a running export is checked (settable so checks needn't wait).</summary>
    public static TimeSpan CheckEvery { get; set; } = TimeSpan.FromSeconds(10);

    static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(2);

    public static IReadOnlyList<EditorChoice> Formats { get; } =
    [
        new("pst", "PST Files", "One Outlook data file per mailbox; open them in Outlook."),
        new("msg", "Individual Messages", "Each message as its own .msg file."),
    ];

    readonly IEdExports? _exports;
    readonly ChangePipeline? _pipeline;
    readonly EdSearch _search;
    readonly Action _close;
    readonly CancellationTokenSource _closing = new();

    public EdExportViewModel(IEdExports? exports, ChangePipeline? pipeline, EdSearch search, Action close)
    {
        _exports = exports;
        _pipeline = pipeline;
        _search = search;
        _close = close;
    }

    public string SearchName => _search.Name;

    public string BillingNote =>
        "Microsoft charges for exports made this way on tenants with Standard licenses (Microsoft Purview pay-as-you-go billing). " +
        "If your organization hasn't set that up, Purview refuses the export; Export in Purview works either way.";

    /// <summary>Why it can't be exported here, or null.</summary>
    public string? Unavailable => _exports is null ? "Exports aren't available for this tenant."
        : _search.CaseName is null ? "Only searches in a case can be exported here. Export this one in Purview."
        : _search.Status != "Completed" ? "Run the search first."
        : null;

    public bool IsAvailable => Unavailable is null;

    [ObservableProperty]
    public partial EditorChoice Format { get; set; } = Formats[0];

    [ObservableProperty]
    public partial bool PartlyIndexed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolder), nameof(FolderText))]
    public partial string? Folder { get; set; }

    public bool HasFolder => Folder is not null;

    public string FolderText => Folder ?? "No folder chosen yet";

    /// <summary>What it's doing now, with the wheel; null when idle.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanStart))]
    public partial string? BusyText { get; private set; }

    public bool IsBusy => BusyText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? Result { get; private set; }

    public bool HasResult => Result is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial bool IsDone { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    public bool CanStart => IsAvailable && !IsBusy && !IsDone;

    [RelayCommand]
    async Task ChooseFolder()
    {
        if (await Platform.PickFolder() is { } folder)
            Folder = folder;
    }

    [RelayCommand]
    void OpenInPurview() => Platform.OpenUrl(EdContext.PurviewCases);

    [RelayCommand]
    void ShowFolder()
    {
        if (Folder is not null)
            Platform.OpenFolder(Folder);
    }

    [RelayCommand]
    async Task Start()
    {
        if (!CanStart || _exports is null)
            return;
        if (Folder is null)
        {
            Problem = "Choose a folder to save the export in.";
            return;
        }
        Problem = null;
        var folder = Folder;
        try
        {
            BusyText = "Asking Purview to export what the search found…";
            var id = await _exports.StartExportAsync(_search.CaseName!, _search.Name, new EdExportOptions(Format.Value == "pst", PartlyIndexed), _closing.Token);
            var started = DateTimeOffset.UtcNow;
            EdExportProgress progress;
            while (true)
            {
                progress = await _exports.GetExportAsync(id, _closing.Token);
                if (progress.IsDone || progress.IsFailed)
                    break;
                if (DateTimeOffset.UtcNow - started > GiveUpAfter)
                    throw new TimeoutException("Purview is still preparing the export after two hours; it carries on in Purview's Process manager.");
                BusyText = $"Purview is preparing the export ({progress.Percent}%). This can take a while for big searches.";
                await Task.Delay(CheckEvery, _closing.Token);
            }
            if (progress.IsFailed)
            {
                Problem = $"Purview couldn't make the export{(progress.Problem is { } why ? $": {why}" : ".")}";
                return;
            }
            long total = 0;
            for (var i = 0; i < progress.Files.Count; i++)
            {
                var file = progress.Files[i];
                var path = UniquePath(folder, file.Name);
                var number = i + 1;
                var reporter = new Progress<long>(bytes => BusyText = $"Downloading file {number} of {progress.Files.Count}: {EDiscovery.Size(bytes)}" +
                                                                      (file.Size > 0 ? $" of {EDiscovery.Size(file.Size)}" : ""));
                BusyText = $"Downloading file {number} of {progress.Files.Count}…";
                await using (var stream = File.Create(path))
                    await _exports.DownloadAsync(file, stream, reporter, _closing.Token);
                total += new FileInfo(path).Length;
            }
            Result = $"Saved {progress.Files.Count} {(progress.Files.Count == 1 ? "file" : "files")} ({EDiscovery.Size(total)}) to {folder}.";
            IsDone = true;
            _pipeline?.Record(new SearchExported(EdPlaces.SearchTarget(_search.Name),
                $"Exported to this computer: {progress.Files.Count} files, {EDiscovery.Size(total)}, as {Format.Label.ToLower(CultureInfo.CurrentCulture)}"));
        }
        catch (OperationCanceledException)
        {
        }
        // Seen live on a tenant without pay-as-you-go: Graph 400 "Purview Billing account is not enabled".
        catch (Exception e) when (e.Message.Contains("Billing account is not enabled", StringComparison.OrdinalIgnoreCase))
        {
            Problem = "Microsoft won't export through Tenantry here: this organization hasn't set up Microsoft Purview pay-as-you-go billing. Use Export in Purview instead.";
        }
        catch (Exception e)
        {
            Problem = $"Couldn't export: {Friendly.Message(e)}";
        }
        finally
        {
            BusyText = null;
        }
    }

    /// <summary>The file's name in the folder, numbered when that name is taken.</summary>
    static string UniquePath(string folder, string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(folder, safe);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(safe)} ({n}){Path.GetExtension(safe)}");
        return path;
    }

    [RelayCommand]
    void Close()
    {
        _closing.Cancel();
        _close();
    }

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }
}
