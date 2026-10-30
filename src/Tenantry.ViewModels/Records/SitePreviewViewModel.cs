using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>A site or OneDrive in the preview pane: what it is, whose it is, how full, how shared.</summary>
public sealed partial class SitePreviewViewModel
{
    readonly IDirectoryIndex _index;
    readonly Action<UserSummary>? _openUser;
    readonly Action<GroupSummary>? _openGroup;

    /// <param name="actions">The site's actions (sharing, storage, lock, site admins), or null when the admin can't edit.</param>
    public SitePreviewViewModel(SiteSummary site, IDirectoryIndex? index = null, Action<UserSummary>? openUser = null, Action<GroupSummary>? openGroup = null,
        Editing.BulkEditorViewModel? actions = null, string? editReason = null)
    {
        Site = site;
        _index = index ?? EmptyIndex.Instance;
        _openUser = openUser;
        _openGroup = openGroup;
        Actions = actions;
        EditReason = editReason ?? "";
    }

    /// <summary>Sharing, storage, lock, archive and site admins; null when the admin can't edit (or on the page's header).</summary>
    public Editing.BulkEditorViewModel? Actions { get; }

    public bool CanEdit => Actions is not null;

    public string EditReason { get; }

    public bool HasEditReason => Actions is null && EditReason.Length > 0;

    public SiteSummary Site { get; }

    public string Title => Site.Title;
    public string Url => Site.Url;
    public string KindText => SiteSummary.KindName(Site.Kind);
    public bool IsTeam => Site.IsTeam;
    public string StateText => SiteList.StateBadge(Site).Text;
    public BadgeKind StateKind => SiteList.StateBadge(Site).Kind;
    public string SharingText => SiteList.SharingBadge(Site).Text;
    public BadgeKind SharingKind => SiteList.SharingBadge(Site).Kind;

    /// <summary>Microsoft charges to bring back an archived OneDrive or site; say so where it applies.</summary>
    public string? ArchiveNote => !Site.IsArchived ? null
        : Site.Kind == SiteKind.OneDrive
            ? "Microsoft archives a OneDrive left unlicensed for 93 days. Its files are kept but can't be opened until it's reactivated, which Microsoft charges for."
            : "Archived sites can't be opened until they're reactivated. Reactivating within 7 days of archiving is free; after that Microsoft charges for it.";

    public bool HasArchiveNote => ArchiveNote is not null;

    /// <summary>A OneDrive's owner, or a group's site's group, when it's in the local copy.</summary>
    UserSummary? OwnerUser => Site.Kind == SiteKind.OneDrive && Site.OwnerLogin is { } login
        ? _index.UserByUpn(login[(login.LastIndexOf('|') + 1)..])
        : null;

    GroupSummary? OwnerGroup => Site.GroupId is { } id ? _index.Group(id) : null;

    public string OwnerText => Site.OwnerName ?? "No owner";

    public string OwnerLabel => Site.Kind == SiteKind.OneDrive ? "OWNER" : Site.GroupId is not null ? "MICROSOFT 365 GROUP" : "OWNER";

    public bool CanOpenOwner => (OwnerUser is not null && _openUser is not null) || (OwnerGroup is not null && _openGroup is not null);

    [RelayCommand]
    void OpenOwner()
    {
        if (OwnerUser is { } user)
            _openUser?.Invoke(user);
        else if (OwnerGroup is { } group)
            _openGroup?.Invoke(group);
    }

    [RelayCommand]
    void OpenSite() => Platform.OpenUrl(Site.Url);

    /// <summary>The facts without storage, for the site page's Details card (its storage card shows it already).</summary>
    public IReadOnlyList<SummaryLine> FactsBesideStorage => [.. Facts.Where(f => f.Label != "Storage")];

    public IReadOnlyList<SummaryLine> Facts =>
    [
        .. new (string, string?)[]
        {
            ("Storage", SiteList.StorageText(Site) + (Site.UsedPercent is { } p && Site.StorageQuotaMb > 0 ? $" ({p:0.#}%)" : "")),
            ("Last Activity", Site.LastActivity is { } at ? Display.Exact(at) : "Never"),
            ("Created", Site.Created is { } c ? Display.Exact(c) : null),
            ("Lock", Site.IsLocked ? SiteSummary.LockName(Site.Lock) : null),
            ("Hub", Site.IsHubSite ? "This is a hub site" : Site.HubSiteId is not null ? "Part of a hub" : null),
            ("Subsites", Site.Subsites is > 1 ? $"{Site.Subsites - 1:N0}" : null),
            ("Template", SiteSummary.TemplateName(Site.Template)),
        }.Where(f => !string.IsNullOrEmpty(f.Item2)).Select(f => new SummaryLine(f.Item1, f.Item2!)),
    ];
}
