using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>
/// A site's or OneDrive's record page (7b): what it is, whose it is and how shared, then a tab per
/// area: Overview (storage, activity and actions), Permissions (editable), Sharing, and Libraries.
/// Each tab reads live from SharePoint when first shown, and again after changes to the site apply.
/// </summary>
public sealed partial class SiteDetailViewModel : TabViewModel, IDisposable
{
    readonly ChangePipeline? _pipeline;

    /// <param name="me">The signed-in admin's sign-in name, for "Add Yourself as Site Admin".</param>
    /// <param name="tenantSharing">The organization's sharing level, for the sharing choices.</param>
    public SiteDetailViewModel(SiteSummary site, ISiteRecords? records, IDirectoryIndex? index = null,
        Action<UserSummary>? openUser = null, Action<GroupSummary>? openGroup = null,
        ChangePipeline? pipeline = null, Availability? canEdit = null, string? me = null, Func<CancellationToken, Task<int>>? tenantSharing = null)
    {
        Site = site;
        Records = records;
        Index = index ?? EmptyIndex.Instance;
        OpenUserAction = openUser;
        OpenGroupAction = openGroup;
        _pipeline = pipeline;
        Me = me;
        CanEdit = pipeline is not null && canEdit is { IsAvailable: true };
        EditReason = pipeline is null ? "This tenant can't be edited" : canEdit?.Reason ?? "";
        Summary = new SitePreviewViewModel(site, Index, openUser, openGroup);
        Actions = CanEdit
            ? new BulkEditorViewModel(site.Title, BulkActions.ForSites(() => [Site], one: true, Index, me, tenantSharing), pipeline!)
            : null;
        Sections =
        [
            new SiteOverviewSection(this),
            new SitePermissionsSection(this),
            new SiteSharingSection(this),
            new SiteLibrariesSection(this),
        ];
        SelectedSection = Sections[0];
        if (_pipeline is not null)
        {
            _pipeline.Changed += OnPendingChanged;
            _pipeline.Applied += OnApplied;
        }
    }

    public SiteSummary Site { get; private set; }

    internal ISiteRecords? Records { get; }
    internal IDirectoryIndex Index { get; }
    internal Action<UserSummary>? OpenUserAction { get; }
    internal Action<GroupSummary>? OpenGroupAction { get; }
    internal ChangePipeline? Pipeline => _pipeline;
    internal string? Me { get; }

    public bool CanEdit { get; }
    public string EditReason { get; }

    /// <summary>Sharing, storage, lock, archive, delete and site admins; null when the admin can't edit.</summary>
    public BulkEditorViewModel? Actions { get; }

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

    /// <summary>A change that touches this site: its own, or its Microsoft 365 group's people.</summary>
    internal bool Touches(ChangeOp change) => change.Target.Id == Site.Id
                                             || (Site.GroupId is { } group && change is SetGroupMembership m && m.Group.Id == group);

    /// <summary>What's waiting in Pending changes for this site.</summary>
    public string? PendingText
    {
        get
        {
            var labels = _pipeline?.Pending.Where(Touches)
                .Select(p => p is SetGroupMembership m ? $"{(m.New ? "add" : "remove")} {m.Role.ToString().ToLowerInvariant()} {m.Person.Name}" : $"{p.Label} → {p.After}")
                .ToArray() ?? [];
            return labels.Length == 0 ? null : "Pending: " + string.Join(", ", labels);
        }
    }

    public bool HasPending => PendingText is not null;

    /// <summary>The admin can make themself a site admin here (and hasn't already staged it).</summary>
    internal bool CanAddMe => CanEdit && Me is not null && !(_pipeline?.Pending.OfType<SetSiteAdmin>().Any(a => a.Site.Id == Site.Id && a.New
        && string.Equals(a.Person.Address, Me, StringComparison.OrdinalIgnoreCase)) ?? false);

    /// <summary>Stages "add me as a site admin", so the tabs can read inside the site once it's applied.</summary>
    internal void AddMe()
    {
        if (!CanAddMe)
            return;
        var me = Index.UserByUpn(Me!) is { } user
            ? GroupAccess.Target(user)
            : new ChangeTarget("user", Me!, Me!, Me);
        _pipeline!.Stage(new SetSiteAdmin(SiteChange.TargetFor(Site), Site.Url, me, SetOneDriveAccess.LoginFor(Me!), false, true));
        foreach (var section in Sections.OfType<SiteSection>())
            section.MeStaged();
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(HasPending));
    }

    void OnApplied(object? sender, IReadOnlyList<ChangeOp> applied)
    {
        if (!applied.Any(Touches))
            return;
        foreach (var section in Sections.OfType<SiteSection>().Skip(1))
            section.ReadAgainIfShown();
    }

    /// <summary>Shows the site as the local copy has it now.</summary>
    public void Update(SiteSummary site)
    {
        Site = site;
        Summary = new SitePreviewViewModel(site, Index, OpenUserAction, OpenGroupAction);
        OnPropertyChanged(nameof(Site));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Title));
        foreach (var section in Sections.OfType<SiteSection>())
            section.SiteChanged();
    }

    public void Dispose()
    {
        if (_pipeline is not null)
        {
            _pipeline.Changed -= OnPendingChanged;
            _pipeline.Applied -= OnApplied;
        }
        foreach (var section in Sections.OfType<IDisposable>())
            section.Dispose();
    }
}
