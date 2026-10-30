using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Site changes (7c), through the SharePoint admin site (CSOM, as Set-SPOSite and its siblings
/// call it) and the site's own REST API; a group-connected site's group through Graph. After each,
/// the site's properties are read back into the local copy, so the Sites list shows the change.
/// </summary>
public sealed partial class GraphChangeExecutor
{
    async Task<ApplyResult> SiteAsync(Func<SharePointAdmin, Task<ApplyResult>> change)
    {
        if (sharePoint is null)
            return new ApplyResult(ChangeOutcome.Failed, NoSharePoint);
        try
        {
            return await change(sharePoint);
        }
        catch (SharePointException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                ? "SharePoint lets only a site's admins change what's inside it. Add yourself as a site admin first."
                // Seen live for people without a SharePoint license: SharePoint doesn't know them.
                : e.Message.Contains("Archiving", StringComparison.Ordinal) && e.Message.Contains("Billing", StringComparison.OrdinalIgnoreCase)
                    ? "Archiving needs Microsoft 365 Archive, which needs pay-as-you-go billing set up in the Microsoft 365 admin center (Settings › Org settings › Pay-as-you-go services)."
                : e.Message.Contains("could not be found", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("does not exist or is not unique", StringComparison.OrdinalIgnoreCase)
                    ? $"{e.Message} SharePoint only gives access to people with a SharePoint license (or guests already invited)."
                    : e.Message);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    /// <summary>Reads a site's properties again into the local copy; the properties, or null when it's gone.</summary>
    async Task<JsonElement?> RefreshSiteAsync(SharePointAdmin admin, string siteUrl, CancellationToken cancellationToken)
    {
        var properties = await admin.GetSitePropertiesAsync(siteUrl, cancellationToken);
        if (properties is { } p && DirectorySync.SiteRow(p) is { } row)
            store.Apply(DirectorySync.Sites, [new EntityChange(row["id"]!.GetValue<string>(), row)]);
        return properties;
    }

    /// <summary>Reads back until <paramref name="done"/> holds (SharePoint can take a moment), updating the local copy each time.</summary>
    async Task<ApplyResult> SiteReadBackAsync(SharePointAdmin admin, string siteUrl, Func<JsonElement?, bool> done, string notYet, CancellationToken cancellationToken)
    {
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            if (done(await RefreshSiteAsync(admin, siteUrl, cancellationToken)))
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, notYet);
    }

    Task<ApplyResult> SetSiteAdminAsync(SetSiteAdmin change, CancellationToken cancellationToken) => SiteAsync(async admin =>
    {
        await admin.SetSiteAdminAsync(change.SiteUrl, change.Login, change.New, cancellationToken);
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            var admins = await admin.GetSiteAdministratorsAsync(change.Site.Id, cancellationToken);
            if (admins.Any(a => string.Equals(a.LoginName, change.Login, StringComparison.OrdinalIgnoreCase)) == change.New)
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "SharePoint accepted the change but still shows the old admins; it usually appears within a minute.");
    });

    Task<ApplyResult> SetSiteGroupMemberAsync(SetSiteGroupMember change, CancellationToken cancellationToken) => SiteAsync(async admin =>
    {
        await admin.SetSiteGroupMemberAsync(change.SiteUrl, change.GroupId, change.Login, change.New, cancellationToken);
        var logins = await admin.GetSiteGroupLoginsAsync(change.SiteUrl, change.GroupId, cancellationToken);
        return logins.Contains(change.Login, StringComparer.OrdinalIgnoreCase) == change.New
            ? new ApplyResult(ChangeOutcome.Applied)
            : new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "SharePoint accepted the change but still shows the group as it was.");
    });

    /// <summary>A setting's values as SharePoint takes them ("2", "true", "ReadOnly").</summary>
    static IReadOnlyList<(string Name, SitePropertyType Type, string Value)> SiteValues(string json) =>
    [
        .. (JsonNode.Parse(json) as JsonObject ?? []).Select(p => (p.Key, SiteSettings.TypeOf(p.Key), p.Value?.GetValueKind() switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => p.Value!.GetValue<long>().ToString(CultureInfo.InvariantCulture),
            _ => p.Value?.GetValue<string>() ?? "",
        })),
    ];

    Task<ApplyResult> SetSiteSettingAsync(SetSiteSetting change, CancellationToken cancellationToken) => SiteAsync(async admin =>
    {
        var values = SiteValues(change.Values);
        await admin.SetSitePropertiesAsync(change.SiteUrl, values, cancellationToken);
        return await SiteReadBackAsync(admin, change.SiteUrl,
            p => p is { } props && values.All(v => props.TryGetProperty(v.Name, out var now) && string.Equals(
                now.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.String => now.GetString(), _ => now.GetRawText() },
                v.Value, StringComparison.OrdinalIgnoreCase)),
            "SharePoint accepted the change but still shows the old setting; it usually appears within a minute.", cancellationToken);
    });

    Task<ApplyResult> SetSiteArchivedAsync(SetSiteArchived change, CancellationToken cancellationToken) => SiteAsync(async admin =>
    {
        await admin.RunSiteMethodAsync(change.Archive ? "ArchiveSiteByUrl" : "UnarchiveSiteByUrl", change.SiteUrl, cancellationToken);
        return await SiteReadBackAsync(admin, change.SiteUrl,
            p => p is { } props && Csom.Text(props, "ArchiveStatus") is { } state && (change.Archive ? state is "RecentlyArchived" or "FullyArchived" : state is "NotArchived" or "Reactivating"),
            change.Archive ? "SharePoint is archiving it; that can take a while for a big site." : "SharePoint is reactivating it; that can take a while for a big site.",
            cancellationToken);
    });

    /// <summary>
    /// A group-connected site goes with its group (SharePoint refuses to delete the site alone); any
    /// other site goes to SharePoint's recycle bin. The local copy drops it at once.
    /// </summary>
    Task<ApplyResult> DeleteSiteAsync(DeleteSite change, CancellationToken cancellationToken) => SiteAsync(async admin =>
    {
        if (change.GroupId is { } groupId)
        {
            await DeleteIfThereAsync($"groups/{groupId}", cancellationToken);
            store.Apply(DirectorySync.Groups, [EntityChange.Removed(groupId)]);
        }
        else
            await admin.RunSiteMethodAsync("RemoveSite", change.SiteUrl, cancellationToken);
        store.Apply(DirectorySync.Sites, [EntityChange.Removed(change.Site.Id)]);
        return new ApplyResult(ChangeOutcome.Applied, change.GroupId is null ? null : "SharePoint moves its site to the recycle bin within a few minutes.");
    });

    Task<ApplyResult> RestoreSiteAsync(RestoreSite change, CancellationToken cancellationToken) => SiteAsync(async admin =>
    {
        if (change.GroupId is { } groupId)
        {
            await graph.PostAsync($"directory/deletedItems/{groupId}/restore", new JsonObject(), cancellationToken);
            await IntoLocalCopyAsync(sync => sync.RefreshGroupAsync(groupId, cancellationToken));
        }
        else
            await admin.RunSiteMethodAsync("RestoreDeletedSite", change.SiteUrl, cancellationToken);
        return await SiteReadBackAsync(admin, change.SiteUrl, p => p is not null,
            change.GroupId is null ? "SharePoint is restoring it; it shows in Sites within a few minutes." : "The group is back; SharePoint brings its site back within a few minutes.",
            cancellationToken);
    });

    /// <summary>
    /// A team site is made as a Microsoft 365 group (SharePoint makes its site within moments; asking
    /// Graph for the group's site is refused for an admin who isn't a member, seen live, so the admin
    /// site is asked instead); a communication site through SharePoint's site manager.
    /// </summary>
    /// <summary>The Microsoft 365 group a new team site is made as (its owners are also its first members).</summary>
    static CreateGroup TeamSiteGroup(CreateSite change) =>
        new(new ChangeTarget("group", change.Site.Address!, change.Site.Name, null), GroupKind.Microsoft365,
            change.Site.Address!, change.Description, null, change.IsPrivate, false, change.Owners, change.Owners);

    Task<ApplyResult> CreateSiteAsync(CreateSite change, CancellationToken cancellationToken) => SiteAsync(async admin =>
    {
        if (change.Kind == SiteKind.TeamSite)
        {
            var made = await CreateGraphGroupAsync(TeamSiteGroup(change), cancellationToken);
            if (!made.Succeeded)
                return made;
            foreach (var wait in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15) })
            {
                if (wait > TimeSpan.Zero)
                    await _delay(wait, cancellationToken);
                if (await RefreshSiteAsync(admin, change.SiteUrl, cancellationToken) is not null)
                    return made;
            }
            return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "The group was made; SharePoint is still making its site. It shows in Sites after the next refresh.");
        }

        if (await admin.GetSiteStatusAsync(change.SiteUrl, cancellationToken) != 0)
            return new ApplyResult(ChangeOutcome.Failed, $"{change.SiteUrl} is already taken (by a site, or a deleted one still in the recycle bin).");
        var status = await admin.CreateCommunicationSiteAsync(change.SiteUrl, change.Site.Name, change.Description, cancellationToken);
        if (status == 3)
            return new ApplyResult(ChangeOutcome.Failed, "SharePoint couldn't make the site.");
        var unfinished = new List<string>();
        if (status == 2 && change.Owners.Count > 0 && await admin.GetOwnerGroupIdAsync(change.SiteUrl, cancellationToken) is { } owners)
            foreach (var other in change.Owners)
            {
                try
                {
                    await admin.SetSiteGroupMemberAsync(change.SiteUrl, owners, SetOneDriveAccess.LoginFor(other.Address ?? other.Name), true, cancellationToken);
                }
                catch (SharePointException e)
                {
                    unfinished.Add($"{other.Name} ({e.Message})");
                }
            }
        await RefreshSiteAsync(admin, change.SiteUrl, cancellationToken);
        return status == 2
            ? new ApplyResult(ChangeOutcome.Applied, unfinished.Count == 0 ? null : "The site was made, but these owners weren't added: " + string.Join("; ", unfinished) + ".")
            : new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "SharePoint is still making the site; it shows in Sites after the next refresh.");
    });
}
