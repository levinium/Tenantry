using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>
/// A site in SharePoint's recycle bin (kept 93 days), or a deleted Microsoft 365 group whose site
/// went with it (kept 30 days; restoring the group brings its site back).
/// </summary>
/// <param name="Id">SharePoint's site ID, or the group's ID for a group SharePoint doesn't list.</param>
/// <param name="Url">Its address; for a group SharePoint doesn't list, where its site was.</param>
/// <param name="Name">The group's name, or the site's address name (SharePoint keeps no title).</param>
/// <param name="GroupId">The deleted Microsoft 365 group it belongs to, restored instead of the site.</param>
public sealed record DeletedSite(string Id, string Url, string Name, DateTimeOffset? DeletedAt, int? DaysLeft, long StorageQuotaMb, string? GroupId)
{
    public string Alias => SiteSummary.AliasOf(Url);

    public bool IsOneDrive => Url.Contains("/personal/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Deleted sites, read live from the SharePoint admin site and Graph (they aren't kept in the local copy).</summary>
public interface IDeletedSiteRecords
{
    Task<IReadOnlyList<DeletedSite>> GetDeletedSitesAsync(CancellationToken cancellationToken = default);
}

/// <summary>The Deleted Sites list's fields.</summary>
public static class DeletedSiteFields
{
    public const string Name = "name";
    public const string Url = "url";
    public const string Deleted = "deleted";
    public const string DaysLeft = "daysleft";
    public const string Kind = "kind";

    public const string Site = "Site";
    public const string OneDrive = "OneDrive";
    public const string Group = "Microsoft 365 Group";

    public static string KindOf(DeletedSite s) => s.GroupId is not null ? Group : s.IsOneDrive ? OneDrive : Site;

    public static IReadOnlyList<Field<DeletedSite>> All { get; } =
    [
        new() { Key = Name, Label = "Name", Kind = FieldKind.Text, Searchable = true, Aliases = ["site"], Text = s => s.Name },
        new() { Key = Url, Label = "Address", Kind = FieldKind.Text, Searchable = true, Aliases = ["address"], Text = s => s.Url },
        new() { Key = Kind, Label = "Type", Kind = FieldKind.Choice, Aliases = ["type"], ChoiceOrder = [Site, Group, OneDrive], Text = KindOf },
        new() { Key = Deleted, Label = "Deleted", Kind = FieldKind.Date, Date = s => s.DeletedAt },
        new() { Key = DaysLeft, Label = "Days Left", Kind = FieldKind.Number, Aliases = ["remaining"], Number = s => s.DaysLeft },
    ];
}
