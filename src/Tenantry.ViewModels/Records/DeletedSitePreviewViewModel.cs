using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>A deleted site in the preview pane: what it was, how long it can still come back, and Restore.</summary>
public sealed class DeletedSitePreviewViewModel
{
    public DeletedSitePreviewViewModel(DeletedSite site, ChangePipeline? pipeline, Availability canEdit)
    {
        Site = site;
        CanEdit = pipeline is not null && canEdit.IsAvailable;
        EditReason = pipeline is null ? "This tenant can't be edited" : canEdit.Reason ?? "";
        if (CanEdit)
            Actions = new BulkEditorViewModel(site.Name, BulkActions.ForDeletedSites(() => [Site]), pipeline!);
    }

    public DeletedSite Site { get; }

    public string Name => Site.Name;
    public string Url => Site.Url;
    public string KindText => DeletedSiteFields.KindOf(Site);
    public string DaysText => DeletedSiteList.DaysBadge(Site).Text;
    public BadgeKind DaysKind => DeletedSiteList.DaysBadge(Site).Kind;

    public string Explanation => Site.GroupId is not null
        ? "It belongs to a deleted Microsoft 365 group. Restoring brings back the group with its site, Team, mailbox and calendar."
        : "Restoring puts it back as it was, with its files, permissions and sharing.";

    public IReadOnlyList<SummaryLine> Facts =>
    [
        .. new (string, string?)[]
        {
            ("Deleted", Site.DeletedAt is { } at ? Display.Exact(at) : null),
            ("Storage Limit", Site.StorageQuotaMb > 0 ? SiteSettings.Storage(Site.StorageQuotaMb) : null),
        }.Where(f => f.Item2 is not null).Select(f => new SummaryLine(f.Item1, f.Item2!)),
    ];

    public bool CanEdit { get; }
    public string EditReason { get; }

    /// <summary>Restore; null when the admin can't edit.</summary>
    public BulkEditorViewModel? Actions { get; }
}
