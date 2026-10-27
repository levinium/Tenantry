using System.Globalization;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

public static partial class BulkActions
{
    // ---- Sites (7c) -------------------------------------------------------------------------

    const string SiteAccess = "Access";
    const string SiteSharing = "Sharing";
    const string SiteStorage = "Storage and Lock";
    const string SiteRemove = "Archive or Delete";

    /// <summary>Guest expiry choices offered, in days; null follows the organization.</summary>
    static readonly int?[] GuestExpiryChoices = [null, 30, 60, 90, 180, 365];

    /// <summary>A person as a site change's target: from the local copy, or by sign-in name alone.</summary>
    static ChangeTarget PersonTarget(string upn, IDirectoryIndex? index) =>
        index?.AllUsers.FirstOrDefault(u => string.Equals(u.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase)) is { } user
            ? GroupAccess.Target(user)
            : new ChangeTarget("user", upn, upn, upn);

    static SetSiteSetting Setting(SiteSummary s, string setting, string label, string old, string @new, JsonObject values, bool caution = false) =>
        new(SiteChange.TargetFor(s), s.Url, setting, label, old, @new, values.ToJsonString(), caution);

    /// <param name="selected">The sites checked right now (read each time).</param>
    /// <param name="one">One site's own page or preview: deleting is offered there only.</param>
    /// <param name="index">People, for choosing site admins.</param>
    /// <param name="me">The signed-in admin's sign-in name, for "Add Yourself as Site Admin".</param>
    /// <param name="tenantSharing">The organization's sharing level (no site can be more open), read when needed.</param>
    public static IReadOnlyList<BulkAction> ForSites(Func<IReadOnlyList<SiteSummary>> selected, bool one = false, IDirectoryIndex? index = null,
        string? me = null, Func<CancellationToken, Task<int>>? tenantSharing = null)
    {
        var actions = new List<BulkAction>();
        if (me is not null)
            actions.Add(new("Add Yourself as Site Admin", "Become a site admin so you can see inside: its permissions, sharing links and libraries. You can remove yourself after.",
                "PersonAdd",
                (_, _) => Task.FromResult(Plan(selected(), s => (new SetSiteAdmin(SiteChange.TargetFor(s), s.Url, PersonTarget(me, index), SetOneDriveAccess.LoginFor(me), false, true), null))),
                group: SiteAccess));
        if (index is not null)
        {
            actions.Add(new("Add a Site Admin", "Give someone full control of the site, including its permissions and settings.", "PersonAdd",
                (a, _) => Task.FromResult(Plan(selected(), s => (new SetSiteAdmin(SiteChange.TargetFor(s), s.Url, a.Picked!.Target("user"),
                    SetOneDriveAccess.LoginFor(a.Picked.Address ?? a.Picked.Name), false, true), null))),
                pickPlaceholder: "Choose a person: type a name", pickOptions: () => GroupAccess.People(index), group: SiteAccess));
            actions.Add(new("Remove a Site Admin", "Take someone's site admin rights away. A OneDrive's own owner and a group's owners keep theirs.", "PersonDelete",
                (a, _) => Task.FromResult(Plan(selected(), s =>
                    s.Kind == SiteKind.OneDrive && s.OwnerLogin is { } owner && owner.EndsWith("|" + a.Picked!.Address, StringComparison.OrdinalIgnoreCase)
                        ? (null, $"{s.Title}: it's their own OneDrive")
                        : (new SetSiteAdmin(SiteChange.TargetFor(s), s.Url, a.Picked!.Target("user"), SetOneDriveAccess.LoginFor(a.Picked.Address ?? a.Picked.Name), true, false), null))),
                pickPlaceholder: "Choose a person: type a name", pickOptions: () => GroupAccess.People(index), isCaution: true, group: SiteAccess));
        }

        actions.Add(new("External Sharing", "Who files here can be shared with. A site can't be more open than the organization allows.", "Share",
            (a, _) =>
            {
                var level = (int)a.Choice!.Value;
                return Task.FromResult(Plan(selected(), s => s.Sharing == level
                    ? (null, $"{s.Title}: already {SiteSummary.SharingName(level).ToLowerInvariant()}")
                    : (Setting(s, "sharing", "External Sharing", SiteSummary.SharingName(s.Sharing), SiteSummary.SharingName(level),
                        new JsonObject { [SiteSettings.Sharing] = level }, SiteSummary.SharingRank(level) > SiteSummary.SharingRank(s.Sharing)), null)));
            },
            choiceLabel: "Can Be Shared With",
            loadChoices: async ct =>
            {
                var most = tenantSharing is null ? 2 : await tenantSharing(ct);
                return
                [
                    .. SiteSummary.SharingLevels.Where(l => SiteSummary.SharingRank(l) <= SiteSummary.SharingRank(most))
                        .Select(l => new BulkChoice(SiteSummary.SharingName(l), l)),
                ];
            },
            group: SiteSharing));
        actions.Add(new("Default Sharing Link", "The kind of link people get first when they share a file here.", "Link",
            (a, _) =>
            {
                var type = (int)a.Choice!.Value;
                return Task.FromResult(Plan(selected(), s => s.LinkType == type
                    ? (null, $"{s.Title}: already {SiteSettings.LinkTypeName(type)}")
                    : (Setting(s, "linktype", "Default Sharing Link", SiteSettings.LinkTypeName(s.LinkType), SiteSettings.LinkTypeName(type),
                        new JsonObject { [SiteSettings.LinkType] = type }, type == 3), null)));
            },
            choiceLabel: "Default Link",
            loadChoices: async ct =>
            {
                var anyone = tenantSharing is null || await tenantSharing(ct) == 2;
                return [.. new[] { 0, 1, 2, 3 }.Where(t => t != 3 || anyone).Select(t => new BulkChoice(SiteSettings.LinkTypeName(t), t))];
            },
            group: SiteSharing));
        actions.Add(new("Default Link Permission", "Whether a new sharing link lets people edit, or only view.", "Eye",
            (a, _) =>
            {
                var permission = (int)a.Choice!.Value;
                return Task.FromResult(Plan(selected(), s => s.LinkPermission == permission
                    ? (null, $"{s.Title}: already {SiteSettings.LinkPermissionName(permission)}")
                    : (Setting(s, "linkpermission", "Default Link Permission", SiteSettings.LinkPermissionName(s.LinkPermission),
                        SiteSettings.LinkPermissionName(permission), new JsonObject { [SiteSettings.LinkPermission] = permission }), null)));
            },
            choiceLabel: "Permission", choices: [.. new[] { 0, 1, 2 }.Select(p => new BulkChoice(SiteSettings.LinkPermissionName(p), p))], group: SiteSharing));
        actions.Add(new("Guest Access Expiry", "How long guests keep access to the site after they're let in, before an owner must invite them again.", "PersonClock",
            (a, _) =>
            {
                var days = (int?)a.Choice!.Value;
                return Task.FromResult(Plan(selected(), s => s.GuestExpiryDays == days
                    ? (null, $"{s.Title}: already {SiteSettings.GuestExpiryName(days).ToLowerInvariant()}")
                    : (Setting(s, "guestexpiry", "Guest Access Expiry", SiteSettings.GuestExpiryName(s.GuestExpiryDays), SiteSettings.GuestExpiryName(days),
                        days is { } d
                            ? new JsonObject { [SiteSettings.GuestExpiryOverride] = true, [SiteSettings.GuestExpiryDays] = d }
                            : new JsonObject { [SiteSettings.GuestExpiryOverride] = false }), null)));
            },
            choiceLabel: "Guests Lose Access",
            choices: [.. GuestExpiryChoices.Select(d => new BulkChoice(SiteSettings.GuestExpiryName(d), d!))],
            group: SiteSharing));

        actions.Add(new("Storage Limit", "The most the site can hold. You're warned at 90%. SharePoint refuses this when the organization manages storage automatically.",
            "Storage",
            (a, _) =>
            {
                if (!double.TryParse(a.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var gb) || gb <= 0)
                    return Task.FromResult(BulkPlan.Invalid("Type the new limit in GB, such as 100."));
                var mb = (long)Math.Round(gb * 1024);
                return Task.FromResult(Plan(selected(), s =>
                    s.StorageQuotaMb == mb ? (null, $"{s.Title}: already {SiteSettings.Storage(mb)}")
                    : mb < s.StorageUsedMb ? (null, $"{s.Title}: already holds {SiteSettings.Storage(s.StorageUsedMb)}")
                    : (Setting(s, "quota", "Storage Limit", SiteSettings.Storage(s.StorageQuotaMb), SiteSettings.Storage(mb),
                        new JsonObject { [SiteSettings.Quota] = mb, [SiteSettings.QuotaWarning] = (long)(mb * 0.9) }, mb < s.StorageQuotaMb), null)));
            },
            textLabel: "New Limit (GB)", group: SiteStorage));
        actions.Add(new("Lock", "Read-only lets people open files but not change them; No Access shuts everyone out, admins included.", "LockClosed",
            (a, _) =>
            {
                var state = (string)a.Choice!.Value;
                return Task.FromResult(Plan(selected(), s =>
                    s.Lock == state ? (null, $"{s.Title}: already {SiteSummary.LockName(state).ToLowerInvariant()}")
                    : s.IsRoot && state == "NoAccess" ? (null, $"{s.Title}: the organization's main site can't be shut")
                    : (Setting(s, "lock", "Lock", SiteSummary.LockName(s.Lock), SiteSummary.LockName(state), new JsonObject { [SiteSettings.Lock] = state },
                        state != "Unlock"), null)));
            },
            choiceLabel: "Lock",
            choices: [new BulkChoice("Unlocked", "Unlock"), new BulkChoice("Read-Only", "ReadOnly"), new BulkChoice("No Access", "NoAccess")],
            group: SiteStorage));

        actions.Add(new("Archive or Reactivate",
            "Archived sites are kept, stored more cheaply, and can't be opened. Needs Microsoft 365 Archive; reactivating a site archived more than 7 days ago is charged.",
            "Archive",
            (a, _) =>
            {
                var archive = (bool)a.Choice!.Value;
                return Task.FromResult(Plan(selected(), s =>
                    s.Kind is SiteKind.OneDrive or SiteKind.System || s.IsRoot ? (null, $"{s.Title}: only team, communication and channel sites can be archived")
                    : s.IsArchived == archive ? (null, $"{s.Title}: already {(archive ? "archived" : "active")}")
                    : (new SetSiteArchived(SiteChange.TargetFor(s), s.Url, archive), null)));
            },
            choiceLabel: "Archive", choices: [new BulkChoice("Archive", true), new BulkChoice("Reactivate", false)], isCaution: true, group: SiteRemove));
        if (one)
            actions.Add(new("Delete Site",
                "Moves it to SharePoint's recycle bin for 93 days. A group's site goes with its Microsoft 365 group (and its Team, mailbox and calendar) for 30 days. You type its name to confirm.",
                "Delete",
                (_, _) => Task.FromResult(Plan(selected(), s =>
                    s.IsRoot || s.Kind == SiteKind.System ? (null, $"{s.Title}: SharePoint's own sites can't be deleted")
                    : s.Kind == SiteKind.OneDrive ? (null, $"{s.Title}: a OneDrive is deleted with its person's account")
                    : s.Kind == SiteKind.ChannelSite ? (null, $"{s.Title}: delete the channel in Teams instead")
                    : (new DeleteSite(SiteChange.TargetFor(s), s.Url, s.GroupId,
                        $"{SiteSummary.KindName(s.Kind)} · {SiteSettings.Storage(s.StorageUsedMb)} used"), null))),
                isCaution: true, group: SiteRemove));
        return actions;
    }

    // ---- Deleted sites (7c) -----------------------------------------------------------------

    /// <summary>A deleted site as a change target, named as the list shows it.</summary>
    public static ChangeTarget DeletedTarget(DeletedSite d) => new("site", d.Id, d.Name, d.Alias);

    /// <param name="selected">The deleted sites checked right now (read each time).</param>
    public static IReadOnlyList<BulkAction> ForDeletedSites(Func<IReadOnlyList<DeletedSite>> selected) =>
    [
        new("Restore", "Bring it back as it was. A Microsoft 365 group's site comes back with its group (and its Team, mailbox and calendar).", "ArrowUndo",
            (_, _) => Task.FromResult(Plan(selected(), d => (new RestoreSite(DeletedTarget(d), d.Url, d.GroupId,
                d.DeletedAt is { } at ? $"Deleted {Display.Exact(at)}" : "Deleted"), null)))),
    ];
}
