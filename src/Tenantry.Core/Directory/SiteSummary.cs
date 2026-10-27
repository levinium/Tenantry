using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>What kind of site, as the Sites list groups them.</summary>
public enum SiteKind
{
    /// <summary>A Microsoft 365 group's site (Teams or not), or a classic team site.</summary>
    TeamSite,

    /// <summary>A Teams private or shared channel's own site.</summary>
    ChannelSite,

    CommunicationSite,
    OneDrive,

    /// <summary>SharePoint's own sites: search center, app catalog, redirects, content type hub…</summary>
    System,
}

/// <summary>
/// One site collection in the Sites or OneDrives list, as the SharePoint admin site describes it
/// (Get-SPOSite's view). Storage is in megabytes, as SharePoint counts it.
/// </summary>
/// <param name="Id">SharePoint's site ID.</param>
/// <param name="Sharing">External sharing: 0 off, 1 new and existing guests, 2 anyone, 3 existing guests only.</param>
/// <param name="Lock">"Unlock", "ReadOnly" or "NoAccess".</param>
/// <param name="Archive">
/// "NotArchived", "RecentlyArchived", "FullyArchived" or "Reactivating". Microsoft archives a
/// OneDrive left unlicensed for 93 days (seen live: most OneDrives of unlicensed people).
/// </param>
/// <param name="OwnerName">The person (a OneDrive's owner) or the Microsoft 365 group (a group's site), named from the directory.</param>
/// <param name="LinkType">The default sharing link (<see cref="SiteSettings.LinkTypeName"/>); 0 follows the organization.</param>
/// <param name="LinkPermission">The default link's permission (<see cref="SiteSettings.LinkPermissionName"/>); 0 follows the organization.</param>
/// <param name="GuestExpiryDays">Days before guests lose access, when the site sets its own; null follows the organization.</param>
public sealed record SiteSummary(
    string Id,
    string Url,
    string Title,
    string Template,
    SiteKind Kind,
    string? OwnerName,
    string? OwnerEmail,
    string? OwnerLogin,
    string? GroupId,
    bool IsTeam,
    bool IsHubSite,
    string? HubSiteId,
    long StorageUsedMb,
    long StorageQuotaMb,
    int Sharing,
    string Lock,
    string Archive,
    DateTimeOffset? Created,
    DateTimeOffset? LastActivity,
    string? SensitivityLabel = null,
    int? Subsites = null,
    int LinkType = 0,
    int LinkPermission = 0,
    int? GuestExpiryDays = null,
    long StorageWarningMb = 0)
{
    public double? UsedPercent => StorageQuotaMb > 0 ? Math.Min(100, StorageUsedMb * 100.0 / StorageQuotaMb) : null;

    public bool IsLocked => Lock is "ReadOnly" or "NoAccess";

    public bool IsArchived => Archive is not ("NotArchived" or "" or null);

    /// <summary>The last part of its address ("finance" in …/sites/finance): what the write guard judges.</summary>
    public string Alias => AliasOf(Url);

    /// <summary>The tenant's root site (…sharepoint.com/), which can't be deleted.</summary>
    public bool IsRoot => new Uri(Url).AbsolutePath.Trim('/').Length == 0;

    public static string AliasOf(string url)
    {
        var trimmed = url.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    /// <summary>External sharing levels from closed to open (0 off, 3 existing guests, 1 new guests, 2 anyone).</summary>
    public static readonly int[] SharingLevels = [0, 3, 1, 2];

    /// <summary>How open a sharing level is (0 closed … 3 anyone): a site can't be more open than its organization.</summary>
    public static int SharingRank(int sharing) => Math.Max(0, Array.IndexOf(SharingLevels, sharing));

    public static string KindName(SiteKind kind) => kind switch
    {
        SiteKind.TeamSite => "Team Site",
        SiteKind.ChannelSite => "Channel Site",
        SiteKind.CommunicationSite => "Communication Site",
        SiteKind.OneDrive => "OneDrive",
        _ => "System Site",
    };

    /// <summary>The kind a template makes ("GROUP#0" → team site, "SPSPERS#10" → OneDrive).</summary>
    public static SiteKind KindOf(string template) => template.ToUpperInvariant() switch
    {
        var t when t.StartsWith("SPSPERS", StringComparison.Ordinal) => SiteKind.OneDrive,
        var t when t.StartsWith("TEAMCHANNEL", StringComparison.Ordinal) => SiteKind.ChannelSite,
        var t when t.StartsWith("SITEPAGEPUBLISHING", StringComparison.Ordinal) => SiteKind.CommunicationSite,
        var t when t.StartsWith("GROUP", StringComparison.Ordinal) || t.StartsWith("STS", StringComparison.Ordinal) => SiteKind.TeamSite,
        _ => SiteKind.System,
    };

    public static string SharingName(int sharing) => sharing switch
    {
        0 => SiteFields.SharingOff,
        1 => SiteFields.SharingGuests,
        2 => SiteFields.SharingAnyone,
        3 => SiteFields.SharingExistingGuests,
        _ => SiteFields.Unknown,
    };

    public static string LockName(string lockState) => lockState switch
    {
        "ReadOnly" => "Read-Only",
        "NoAccess" => "No Access",
        _ => "Unlocked",
    };

    public static string ArchiveName(string archive) => archive switch
    {
        "RecentlyArchived" => "Recently Archived",
        "FullyArchived" => "Archived",
        "Reactivating" => "Reactivating",
        _ => "Active",
    };
}

/// <summary>The Sites and OneDrives lists' fields.</summary>
public static class SiteFields
{
    public const string Title = "title";
    public const string Url = "url";
    public const string Kind = "kind";
    public const string Owner = "owner";
    public const string Used = "used";
    public const string Quota = "quota";
    public const string Percent = "percent";
    public const string LastActivity = "activity";
    public const string Created = "created";
    public const string Sharing = "sharing";
    public const string Lock = "lock";
    public const string Archive = "archive";
    public const string Team = "team";
    public const string Hub = "hub";
    public const string Template = "template";

    public const string SharingOff = "Only People in the Organization";
    public const string SharingExistingGuests = "Existing Guests";
    public const string SharingGuests = "New and Existing Guests";
    public const string SharingAnyone = "Anyone";
    public const string Unknown = "Unknown";
    public const string Yes = "Yes";
    public const string HubSite = "Hub Site";
    public const string InHub = "In a Hub";
    public const string NoHub = "Not in a Hub";

    static double Gb(long mb) => Math.Round(mb / 1024.0, 2);

    public static IReadOnlyList<Field<SiteSummary>> All { get; } =
    [
        new() { Key = Title, Label = "Name", Kind = FieldKind.Text, Searchable = true, Aliases = ["name", "site"], Text = s => s.Title },
        new() { Key = Url, Label = "Address", Kind = FieldKind.Text, Searchable = true, Aliases = ["address", "link"], Text = s => s.Url },
        new()
        {
            Key = Kind, Label = "Type", Kind = FieldKind.Choice, Aliases = ["type"],
            ChoiceOrder = [.. Enum.GetValues<SiteKind>().Select(SiteSummary.KindName)], Text = s => SiteSummary.KindName(s.Kind),
        },
        new() { Key = Owner, Label = "Owner", Kind = FieldKind.Text, Searchable = true, Aliases = ["owner", "person"], EmptyLabel = "No Owner", Text = s => s.OwnerName ?? s.OwnerEmail },
        new() { Key = Used, Label = "Storage Used (GB)", Kind = FieldKind.Number, Aliases = ["storage", "size"], Number = s => Gb(s.StorageUsedMb) },
        new() { Key = Quota, Label = "Storage Quota (GB)", Kind = FieldKind.Number, Aliases = ["limit"], Number = s => s.StorageQuotaMb > 0 ? Gb(s.StorageQuotaMb) : null },
        new() { Key = Percent, Label = "Storage Used (%)", Kind = FieldKind.Number, Aliases = ["full"], Number = s => s.UsedPercent is { } p ? Math.Round(p, 1) : null },
        new() { Key = LastActivity, Label = "Last Activity", Kind = FieldKind.Date, Aliases = ["modified", "active"], EmptyLabel = "Never", Date = s => s.LastActivity },
        new() { Key = Created, Label = "Created", Kind = FieldKind.Date, Date = s => s.Created },
        new()
        {
            Key = Sharing, Label = "External Sharing", Kind = FieldKind.Choice, Aliases = ["external", "guests"],
            ChoiceOrder = [SharingOff, SharingExistingGuests, SharingGuests, SharingAnyone, Unknown], Text = s => SiteSummary.SharingName(s.Sharing),
        },
        new()
        {
            Key = Lock, Label = "Lock", Kind = FieldKind.Choice, Aliases = ["locked", "readonly"],
            ChoiceOrder = ["Unlocked", "Read-Only", "No Access"], Text = s => SiteSummary.LockName(s.Lock),
        },
        new()
        {
            Key = Archive, Label = "Archive", Kind = FieldKind.Choice, Aliases = ["archived"],
            ChoiceOrder = ["Active", "Recently Archived", "Archived", "Reactivating"], Text = s => SiteSummary.ArchiveName(s.Archive),
        },
        new() { Key = Team, Label = "Team", Kind = FieldKind.Choice, Aliases = ["teams"], EmptyLabel = "No", Text = s => s.IsTeam ? Yes : null },
        new()
        {
            Key = Hub, Label = "Hub", Kind = FieldKind.Choice, Aliases = ["hubsite"],
            ChoiceOrder = [HubSite, InHub, NoHub], Text = s => s.IsHubSite ? HubSite : s.HubSiteId is not null ? InHub : NoHub,
        },
        new() { Key = Template, Label = "Template", Kind = FieldKind.Text, Searchable = true, Text = s => s.Template },
    ];
}
