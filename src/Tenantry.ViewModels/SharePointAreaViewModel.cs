using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.ViewModels;

/// <summary>
/// The SharePoint &amp; OneDrive area: one tab with three pages. Sites (team, communication, channel
/// and system sites) and OneDrives show the same local copy of every site collection; Deleted Sites
/// is read live from SharePoint when first shown (and on Refresh).
/// </summary>
public sealed partial class SharePointAreaViewModel : TabViewModel
{
    readonly IDeletedSiteRecords? _deleted;
    bool _deletedRead;

    public static IReadOnlyList<MailSecurityPage> Pages { get; } =
    [
        new("sites", "Sites", "Globe"),
        new("onedrives", "OneDrives", "Cloud"),
        new("deleted", "Deleted Sites", "Delete"),
    ];

    public SharePointAreaViewModel(DataListViewModel sites, DataListViewModel oneDrives, IDeletedSiteRecords? deleted = null, ChangePipeline? pipeline = null,
        Availability? canEdit = null, IListStateStore? listState = null, Func<DateTimeOffset>? now = null)
    {
        Sites = sites;
        OneDrives = oneDrives;
        _deleted = deleted;
        var edit = canEdit ?? Availability.Yes;
        Deleted = DataListViewModel.Create(DeletedSiteList.Definition(s => new Records.DeletedSitePreviewViewModel(s, pipeline, edit)),
            LoadDeletedAsync, row => Deleted!.PreviewRow(row), listState, now ?? (() => DateTimeOffset.Now));
        Deleted.HasRecordPages = false;
        Deleted.PrimaryAction = null;
        Deleted.EmptyText = "SharePoint's recycle bin is empty.";
        Deleted.SelectionPanel = (rows, uncheck) => Records.SelectionPanelViewModel.ForDeletedSites(rows, uncheck, pipeline, edit);
        if (pipeline is not null)
            pipeline.Applied += (_, applied) =>
            {
                if (_deletedRead && applied.Any(c => c is RestoreSite or DeleteSite))
                    _ = Deleted.ReloadAsync(busyText: "Reading deleted sites again…");
            };
        SelectedPage = Pages[0];
    }

    public override string Title => "SharePoint & OneDrive";

    public override bool CanClose => false;

    public DataListViewModel Sites { get; }

    public DataListViewModel OneDrives { get; }

    public DataListViewModel Deleted { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSites), nameof(ShowOneDrives), nameof(ShowDeleted))]
    public partial MailSecurityPage SelectedPage { get; set; }

    public bool ShowSites => SelectedPage.Key == "sites";
    public bool ShowOneDrives => SelectedPage.Key == "onedrives";
    public bool ShowDeleted => SelectedPage.Key == "deleted";

    partial void OnSelectedPageChanged(MailSecurityPage value)
    {
        if (value.Key == "deleted" && !_deletedRead)
        {
            _deletedRead = true;
            _ = Deleted.LoadAsync();
        }
    }

    /// <summary>Why deleted sites can't be listed (no SharePoint role), shown above the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeletedNote))]
    public partial string? DeletedNote { get; private set; }

    public bool HasDeletedNote => DeletedNote is not null;

    async Task<IReadOnlyList<DeletedSite>> LoadDeletedAsync(CancellationToken cancellationToken)
    {
        if (_deleted is null)
        {
            DeletedNote = "Deleted sites aren't available for this tenant.";
            return [];
        }
        try
        {
            DeletedNote = null;
            return await _deleted.GetDeletedSitesAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            DeletedNote = $"Couldn't read deleted sites: {Friendly.Message(e)}";
            return [];
        }
    }

    [RelayCommand]
    Task RefreshDeleted() => Deleted.ReloadAsync(busyText: "Reading deleted sites from SharePoint…");
}
