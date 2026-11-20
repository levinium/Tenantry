using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels;

/// <summary>A found file or folder in the preview pane: where it is, who last changed it, links to it, and what can be done with it.</summary>
public sealed partial class FoundFilePreviewViewModel : ObservableObject
{
    readonly FoundFile _file;

    public FoundFilePreviewViewModel(FoundFile file, FindFilesViewModel? owner = null)
    {
        _file = file;
        Actions = new FoundFileActionsViewModel([file], owner);
    }

    public FoundFile File => _file;

    /// <summary>Restore, Download, Give Access To and Copy To.</summary>
    public FoundFileActionsViewModel Actions { get; }

    public string Name => _file.Name;
    public bool IsFolder => _file.IsFolder;
    public bool IsDeleted => _file.IsDeleted;
    public string TypeText => FoundFileFields.TypeOf(_file);
    public string PlaceText => _file.PlaceTitle;
    public string PathText => _file.Path;
    public string SizeText => MailDetails.Size(_file.Size);
    public string BinText => _file.BinText;
    public string ModifiedText => _file.Modified is { } at ? $"{at.ToLocalTime():d MMM yyyy h:mm tt}" + (_file.ModifiedBy is { } by ? $" by {by}" : "") : "Not known";
    public string CreatedText => _file.Created is { } at ? $"{at.ToLocalTime():d MMM yyyy}" + (_file.CreatedBy is { } by ? $" by {by}" : "")
        : _file.CreatedBy is { } author ? $"By {author}" : "Not known";
    public string DeletedText => _file.Deleted is { } at ? $"{at.ToLocalTime():d MMM yyyy h:mm tt}" + (_file.DeletedBy is { } by ? $" by {by}" : "") : "Not known";

    /// <summary>When SharePoint empties it for good: 93 days after it was deleted, whichever stage it's in.</summary>
    public string GoneText => _file.Deleted is { } at ? $"{at.AddDays(93).ToLocalTime():d MMM yyyy}, unless it's restored or emptied sooner" : "Not known";

    public bool HasLink => _file.WebUrl is { Length: > 0 };
    public bool HasFolder => _file.FolderUrl is not null;

    [ObservableProperty]
    public partial bool Copied { get; private set; }

    [RelayCommand]
    void Open()
    {
        if (_file.WebUrl is { Length: > 0 } url)
            Platform.OpenUrl(url);
    }

    [RelayCommand]
    void OpenFolder()
    {
        if (_file.FolderUrl is { } url)
            Platform.OpenUrl(url);
    }

    [RelayCommand]
    void CopyLink()
    {
        if (_file.WebUrl is not { Length: > 0 } url)
            return;
        Platform.CopyText(url);
        Copied = true;
    }
}

/// <summary>Reports straight to the screen (the work runs on the screen's thread).</summary>
sealed class Reporter(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}

/// <summary>Someone (or a group) chosen to be given access, shown as a chip with an ×.</summary>
public sealed partial class PickedTarget(ChangeTarget target, string detail, Action<PickedTarget> remove) : ObservableObject
{
    public ChangeTarget Target => target;
    public string Name => target.Name;
    public string Detail => detail;

    [RelayCommand]
    void Remove() => remove(this);
}

/// <summary>
/// What can be done with found files, for one in the preview or several ticked: Restore (those in a
/// recycle bin), Download, Give Access To and Copy To (those that aren't). Giving access and copying
/// open a short form below the buttons. Each runs at once and is logged; Test mode guards it.
/// </summary>
public sealed partial class FoundFileActionsViewModel : ObservableObject
{
    /// <summary>Copies go in this folder unless another is typed.</summary>
    public const string DefaultCopyFolder = "Recovered Files";

    readonly FindFilesViewModel? _owner;

    internal FoundFileActionsViewModel(IReadOnlyList<FoundFile> files, FindFilesViewModel? owner)
    {
        Files = files;
        _owner = owner;
        Deleted = [.. files.Where(f => f.IsDeleted)];
        Current = [.. files.Where(f => !f.IsDeleted)];
        SharePicker = new PersonPicker("Person or group", () => owner?.ShareOptions().Where(o => ShareWith.All(s => s.Target.Id != o.Id)) ?? [], PickPerson) { Multiple = true };
        CopyPicker = new PersonPicker("Person's OneDrive or a site", () => owner?.DestinationOptions() ?? [], PickDestination);
        ShareWith.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsShareOpen));
    }

    public IReadOnlyList<FoundFile> Files { get; }
    IReadOnlyList<FoundFile> Deleted { get; }
    IReadOnlyList<FoundFile> Current { get; }

    public bool HasDeleted => Deleted.Count > 0;
    public bool HasCurrent => Current.Count > 0;
    public bool IsMany => Files.Count > 1;

    /// <summary>For one in a recycle bin: the other actions wait until it's back.</summary>
    public bool OnlyDeleted => Deleted.Count == Files.Count;

    bool CanChange => _owner?.CanChangeFiles == true && !IsBusy;
    public bool CanRestore => HasDeleted && CanChange;
    public bool CanDownload => HasCurrent && _owner is not null && !IsBusy;
    public bool CanShare => HasCurrent && CanChange;
    public bool CanCopy => HasCurrent && CanChange;

    public string RestoreText => IsMany ? $"Restore {Deleted.Count:N0}" : "Restore";
    public string DownloadText => IsMany ? $"Download {Current.Count:N0}" : Current is [{ IsFolder: true }] ? "Download Folder" : "Download";

    public string RestoreTip => _owner?.CanChangeFiles == false
        ? "Restoring needs the right to change sites"
        : IsMany ? "Put the ticked items that are in a recycle bin back where they were (logged)" : "Put it back where it was, with its folder if that was deleted too (logged)";

    public string DownloadTip => IsMany
        ? "Save the ticked items to a folder on this computer; folders come with everything in them (logged)"
        : Current is [{ IsFolder: true }] ? "Save everything in this folder, with its subfolders, to a folder on this computer (logged)" : "Save a copy to a folder on this computer (logged)";

    public string ShareTip => _owner?.CanChangeFiles == false ? "Giving access needs the right to change sites"
        : "Let people or groups open it where it is: they sign in to open it, and can be emailed a link";

    public string CopyTip => _owner?.CanChangeFiles == false ? "Copying needs the right to change sites"
        : "Put a copy in someone's OneDrive or a site; the original stays where it is";

    /// <summary>"3 of the 5 ticked are in a recycle bin: restore them first to download, share or copy them."</summary>
    public string? DeletedNote => !HasDeleted ? null
        : OnlyDeleted ? (IsMany ? "They're in a recycle bin: restore them to open, download, share or copy them." : "It's in a recycle bin: restore it to open, download, share or copy it.")
        : $"{Deleted.Count:N0} of the {Files.Count:N0} ticked {(Deleted.Count == 1 ? "is" : "are")} in a recycle bin: Restore puts {(Deleted.Count == 1 ? "it" : "them")} back. The rest can be downloaded, shared or copied.";

    public bool HasDeletedNote => DeletedNote is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestore), nameof(CanDownload), nameof(CanShare), nameof(CanCopy), nameof(CanGiveAccess), nameof(CanCopyHere))]
    public partial bool IsBusy { get; private set; }

    /// <summary>What's happening, then what happened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    /// <summary>Where a download went, to show it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaved))]
    public partial string? SavedIn { get; private set; }

    public bool HasSaved => SavedIn is not null;

    async Task RunAsync(Func<IProgress<string>, Task<string?>> work)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        SavedIn = null;
        try
        {
            // Said at once, not posted for later as Progress<T> does: a late "Copying…" would
            // otherwise replace what happened.
            var done = await work(new Reporter(text =>
            {
                if (IsBusy)
                    Note = text;
            }));
            if (done is not null)
                Note = done;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note = Friendly.Message(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    Task Restore() => !CanRestore ? Task.CompletedTask : RunAsync(async status => await _owner!.RestoreAsync(Deleted, status, CancellationToken.None));

    [RelayCommand]
    Task Download() => !CanDownload ? Task.CompletedTask : RunAsync(async status =>
    {
        var done = await _owner!.DownloadAsync(Current, status, CancellationToken.None);
        SavedIn = done?.Folder;
        return done?.Text;
    });

    [RelayCommand]
    void ShowSaved()
    {
        if (SavedIn is { } folder)
            Platform.OpenFolder(folder);
    }

    // ---- Give Access To -----------------------------------------------------------------------

    /// <summary>Chooses who gets access; each chosen shows as a chip below.</summary>
    public PersonPicker SharePicker { get; }

    public ObservableCollection<PickedTarget> ShareWith { get; } = [];

    /// <summary>The form shows once someone's chosen.</summary>
    public bool IsShareOpen => ShareWith.Count > 0;

    /// <summary>Edit rather than only view.</summary>
    [ObservableProperty]
    public partial bool ShareCanEdit { get; set; }

    [ObservableProperty]
    public partial bool ShareSendEmail { get; set; } = true;

    [ObservableProperty]
    public partial string ShareMessage { get; set; } = "";

    public bool CanGiveAccess => CanShare && ShareWith.Count > 0;

    void PickPerson(PickOption option)
    {
        if (ShareWith.Any(s => s.Target.Id == option.Id))
            return;
        ShareWith.Add(new PickedTarget(option.Target(option.TargetKind), option.Detail, p => { ShareWith.Remove(p); OnPropertyChanged(nameof(CanGiveAccess)); }));
        OnPropertyChanged(nameof(CanGiveAccess));
    }

    [RelayCommand]
    Task GiveAccess() => !CanGiveAccess ? Task.CompletedTask : RunAsync(async status =>
    {
        var text = await _owner!.ShareAsync(Current, [.. ShareWith.Select(s => s.Target)], ShareCanEdit, ShareSendEmail, ShareMessage, status, CancellationToken.None);
        ShareWith.Clear();
        ShareMessage = "";
        OnPropertyChanged(nameof(CanGiveAccess));
        return text;
    });

    [RelayCommand]
    void CancelShare()
    {
        ShareWith.Clear();
        OnPropertyChanged(nameof(CanGiveAccess));
    }

    // ---- Copy To ------------------------------------------------------------------------------

    /// <summary>Chooses where copies go.</summary>
    public PersonPicker CopyPicker { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCopyOpen), nameof(CopyDestinationText), nameof(CanCopyHere))]
    public partial SiteSummary? CopyDestination { get; private set; }

    public bool IsCopyOpen => CopyDestination is not null;

    public string CopyDestinationText => CopyDestination is not { } d ? ""
        : d.Kind == SiteKind.OneDrive && d.OwnerName is { Length: > 0 } owner ? $"{owner}'s OneDrive" : d.Title;

    /// <summary>The folder at the top of its main library the copies go in; blank for the top itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyFolderProblem), nameof(HasCopyFolderProblem), nameof(CanCopyHere))]
    public partial string CopyFolder { get; set; } = DefaultCopyFolder;

    /// <summary>SharePoint refuses these in a name, and a name can't start or end with a dot or space.</summary>
    static readonly char[] Unsafe = ['"', '*', ':', '<', '>', '?', '/', '\\', '|'];

    public string? CopyFolderProblem => CopyFolder.IndexOfAny(Unsafe) >= 0
        ? "A folder name can't have \" * : < > ? / \\ or | in it."
        : CopyFolder.Trim() is { Length: > 0 } name && (name.EndsWith('.') || name.StartsWith('.')) ? "A folder name can't start or end with a dot." : null;

    public bool HasCopyFolderProblem => CopyFolderProblem is not null;

    public bool CanCopyHere => CanCopy && CopyDestination is not null && CopyFolderProblem is null;

    void PickDestination(PickOption option) => CopyDestination = _owner?.SiteById(option.Id);

    [RelayCommand]
    Task CopyHere() => !CanCopyHere ? Task.CompletedTask : RunAsync(async status =>
    {
        var text = await _owner!.CopyAsync(Current, CopyDestination!, CopyFolder.Trim(), status, CancellationToken.None);
        CopyDestination = null;
        return text;
    });

    [RelayCommand]
    void CancelCopy() => CopyDestination = null;
}

/// <summary>Several found files ticked: how many, where, and the same actions as for one.</summary>
public sealed partial class FoundFilesSelectionViewModel : ObservableObject, ISelectionPanel
{
    readonly FindFilesViewModel _owner;

    internal FoundFilesSelectionViewModel(IReadOnlyList<object> rows, FindFilesViewModel owner)
    {
        _owner = owner;
        Update(rows);
    }

    [ObservableProperty]
    public partial FoundFileActionsViewModel Actions { get; private set; } = null!;

    [ObservableProperty]
    public partial string Heading { get; private set; } = "";

    [ObservableProperty]
    public partial string Detail { get; private set; } = "";

    public void Update(IReadOnlyList<object> rows)
    {
        var files = rows.OfType<FoundFile>().ToArray();
        Actions = new FoundFileActionsViewModel(files, _owner);
        Heading = $"{files.Length:N0} Ticked";
        var places = files.Select(f => f.PlaceId).Distinct(StringComparer.Ordinal).Count();
        var deleted = files.Count(f => f.IsDeleted);
        Detail = string.Join(" · ", new[]
        {
            places == 1 ? $"In {files[0].PlaceTitle}" : $"In {places:N0} places",
            MailDetails.Size(files.Sum(f => f.Size)),
            deleted == 0 ? null : $"{deleted:N0} in a recycle bin",
        }.Where(s => s is not null));
    }
}
