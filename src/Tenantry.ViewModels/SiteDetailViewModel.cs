using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>
/// A site's or OneDrive's record page (7b): what it is, whose it is and how shared, then a tab per
/// area: Overview (storage and activity), Permissions, Sharing, and Libraries. Each tab reads live
/// from SharePoint when first shown.
/// </summary>
public sealed partial class SiteDetailViewModel : TabViewModel
{
    public SiteDetailViewModel(SiteSummary site, ISiteRecords? records, IDirectoryIndex? index = null,
        Action<UserSummary>? openUser = null, Action<GroupSummary>? openGroup = null)
    {
        Site = site;
        Records = records;
        Index = index ?? EmptyIndex.Instance;
        OpenUserAction = openUser;
        OpenGroupAction = openGroup;
        Summary = new SitePreviewViewModel(site, Index, openUser, openGroup);
        Sections =
        [
            new SiteOverviewSection(this),
            new SitePermissionsSection(this),
            new SiteSharingSection(this),
            new SiteLibrariesSection(this),
        ];
        SelectedSection = Sections[0];
    }

    public SiteSummary Site { get; private set; }

    internal ISiteRecords? Records { get; }
    internal IDirectoryIndex Index { get; }
    internal Action<UserSummary>? OpenUserAction { get; }
    internal Action<GroupSummary>? OpenGroupAction { get; }

    /// <summary>The header's facts and badges, as the preview shows them.</summary>
    public SitePreviewViewModel Summary { get; private set; }

    public override string Title => Site.Title;

    public string Icon => Site.Kind == SiteKind.OneDrive ? "Cloud" : "Globe";

    public IReadOnlyList<RecordSection> Sections { get; }

    [ObservableProperty]
    public partial RecordSection? SelectedSection { get; set; }

    partial void OnSelectedSectionChanged(RecordSection? value) => _ = value?.LoadAsync();

    [RelayCommand]
    void OpenSite() => Platform.OpenUrl(Site.Url);

    /// <summary>Shows the site as the local copy has it now.</summary>
    public void Update(SiteSummary site)
    {
        Site = site;
        Summary = new SitePreviewViewModel(site, Index, OpenUserAction, OpenGroupAction);
        OnPropertyChanged(nameof(Site));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Title));
    }
}
