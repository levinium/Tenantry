using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.SiteFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>
/// The Sites and OneDrives lists: the same rows (every site collection) shown two ways. Sites
/// leaves OneDrives out; OneDrives shows nothing else.
/// </summary>
public static class SiteList
{
    static readonly ListQuery ByTitle = ListQuery.Empty with { Sort = [new SortLevel(F.Title)] };

    static ChoiceCondition Is(string field, params string[] values) => new(field, values, []);
    static ChoiceCondition IsNot(string field, params string[] values) => new(field, [], values);

    static string Kind(SiteKind kind) => SiteSummary.KindName(kind);

    static readonly Condition NotOneDrive = IsNot(F.Kind, Kind(SiteKind.OneDrive));
    static readonly Condition OnlyOneDrive = Is(F.Kind, Kind(SiteKind.OneDrive));

    static SavedView View(HashSet<string> pinned, string id, string name, params Condition[] where) =>
        new(id, name, ByTitle with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: pinned.Contains(id));

    static readonly HashSet<string> SitesPinned = ["all", "teams", "external", "inactive"];

    static readonly Condition Inactive180 = ConditionGroup.Any(new DateCondition(F.LastActivity, DateOp.OlderThanDays, 180), new DateCondition(F.LastActivity, DateOp.Never));

    // Every Sites view leaves OneDrives out (they have their own page).
    public static IReadOnlyList<SavedView> SiteViews { get; } =
    [
        View(SitesPinned, "all", "All Sites", NotOneDrive, IsNot(F.Kind, Kind(SiteKind.System))),
        View(SitesPinned, "teams", "Microsoft Teams", NotOneDrive, Is(F.Team, F.Yes)),
        View(SitesPinned, "team-sites", "Team Sites", Is(F.Kind, Kind(SiteKind.TeamSite))),
        View(SitesPinned, "communication", "Communication Sites", Is(F.Kind, Kind(SiteKind.CommunicationSite))),
        View(SitesPinned, "channels", "Private and Shared Channels", Is(F.Kind, Kind(SiteKind.ChannelSite))),
        View(SitesPinned, "hubs", "Hub Sites", NotOneDrive, Is(F.Hub, F.HubSite)),
        // Guests can be given access here: worth reviewing.
        View(SitesPinned, "external", "Shared Outside the Organization", NotOneDrive, IsNot(F.Sharing, F.SharingOff)),
        View(SitesPinned, "inactive", "No Activity in 180+ Days", NotOneDrive, IsNot(F.Kind, Kind(SiteKind.System)), Inactive180),
        View(SitesPinned, "nearly-full", "Storage Nearly Full", NotOneDrive, new NumberCondition(F.Percent, 90, null)),
        View(SitesPinned, "locked", "Locked", NotOneDrive, IsNot(F.Lock, "Unlocked")),
        View(SitesPinned, "archived", "Archived", NotOneDrive, IsNot(F.Archive, "Active")),
        View(SitesPinned, "system", "System Sites", Is(F.Kind, Kind(SiteKind.System))),
    ];

    static readonly HashSet<string> DrivesPinned = ["all", "archived", "inactive", "largest"];

    public static IReadOnlyList<SavedView> OneDriveViews { get; } =
    [
        View(DrivesPinned, "all", "All OneDrives", OnlyOneDrive),
        // Microsoft archives a OneDrive left unlicensed for 93 days; reactivating it is charged.
        View(DrivesPinned, "archived", "Archived", OnlyOneDrive, IsNot(F.Archive, "Active")),
        View(DrivesPinned, "inactive", "No Activity in 180+ Days", OnlyOneDrive, IsNot(F.Archive, "Archived"), Inactive180),
        View(DrivesPinned, "largest", "Over 10 GB", OnlyOneDrive, new NumberCondition(F.Used, 10, null)),
        View(DrivesPinned, "nearly-full", "Storage Nearly Full", OnlyOneDrive, new NumberCondition(F.Percent, 90, null)),
        View(DrivesPinned, "external", "Shared Outside the Organization", OnlyOneDrive, IsNot(F.Sharing, F.SharingOff)),
        View(DrivesPinned, "locked", "Locked", OnlyOneDrive, IsNot(F.Lock, "Unlocked")),
    ];

    public static (string Text, BadgeKind Kind) SharingBadge(SiteSummary s) => s.Sharing switch
    {
        0 => (F.SharingOff, BadgeKind.Neutral),
        3 => (F.SharingExistingGuests, BadgeKind.Info),
        1 => (F.SharingGuests, BadgeKind.Warning),
        2 => (F.SharingAnyone, BadgeKind.Danger),
        _ => (F.Unknown, BadgeKind.Neutral),
    };

    public static (string Text, BadgeKind Kind) StateBadge(SiteSummary s) =>
        s.IsArchived ? (SiteSummary.ArchiveName(s.Archive), BadgeKind.Neutral)
        : s.Lock == "NoAccess" ? ("No Access", BadgeKind.Danger)
        : s.Lock == "ReadOnly" ? ("Read-Only", BadgeKind.Warning)
        : ("Active", BadgeKind.Success);

    /// <summary>"1.2 GB of 25 TB".</summary>
    public static string StorageText(SiteSummary s) =>
        s.StorageQuotaMb > 0 ? $"{Records.DeviceText.Size(s.StorageUsedMb << 20)} of {Records.DeviceText.Size(s.StorageQuotaMb << 20)}"
            : Records.DeviceText.Size(s.StorageUsedMb << 20);

    static ColumnDef<SiteSummary> Name(string header) => new()
    {
        Key = F.Title, Header = header, Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 300,
        Text = s => s.Title,
        Subtitle = s => s.Url,
        Initials = s => Display.Initials(s.Title),
    };

    static IReadOnlyList<ColumnDef<SiteSummary>> Common(bool oneDrive) =>
    [
        new() { Key = F.Used, Header = "Storage Used (GB)", Cell = CellKind.Number, MinWidth = 150, DefaultWidth = 160, Number = s => Math.Round(s.StorageUsedMb / 1024.0, 2) },
        new() { Key = F.Percent, Header = "Used (%)", Cell = CellKind.Number, MinWidth = 90, DefaultWidth = 100, Number = s => s.UsedPercent is { } p ? Math.Round(p, 1) : null },
        new() { Key = F.LastActivity, Header = "Last Activity", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 140, Date = s => s.LastActivity, EmptyText = "Never",
            AgeTone = UserList.SignInTone },
        new() { Key = F.Archive, Header = "State", Cell = CellKind.Badge, MinWidth = 120, DefaultWidth = 140, Badge = StateBadge },
        new() { Key = F.Sharing, Header = "External Sharing", Cell = CellKind.Badge, MinWidth = 170, DefaultWidth = 210, Badge = SharingBadge, DefaultVisible = !oneDrive },
        new() { Key = F.Created, Header = "Created", Cell = CellKind.Date, MinWidth = 120, DefaultWidth = 130, DefaultVisible = false, Date = s => s.Created },
        new() { Key = F.Quota, Header = "Quota (GB)", Cell = CellKind.Number, MinWidth = 100, DefaultWidth = 110, DefaultVisible = false,
            Number = s => s.StorageQuotaMb > 0 ? Math.Round(s.StorageQuotaMb / 1024.0, 0) : null },
        new() { Key = F.Url, Header = "Address", Cell = CellKind.Text, MinWidth = 240, DefaultWidth = 320, DefaultVisible = false, Text = s => s.Url },
        new() { Key = F.Template, Header = "Template", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 160, DefaultVisible = false, Text = s => s.Template },
    ];

    public static ListDefinition<SiteSummary> Sites(Func<SiteSummary, object> preview) => new()
    {
        Key = "sites",
        Title = "Sites",
        Noun = "sites",
        Fields = F.All,
        Id = s => s.Id,
        Views = SiteViews,
        SearchHint = "Search names, addresses, owners… or try type:\"team site\" sharing:anyone",
        Preview = preview,
        Actions = [new RowAction<SiteSummary>("Open Site", "Globe", s => Platform.OpenUrl(s.Url))],
        Columns =
        [
            Name("Name"),
            new() { Key = F.Kind, Header = "Type", Cell = CellKind.Text, MinWidth = 130, DefaultWidth = 150, Text = s => SiteSummary.KindName(s.Kind) },
            new() { Key = F.Owner, Header = "Owner", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 200, Text = s => s.OwnerName, EmptyText = "No Owner" },
            new() { Key = F.Team, Header = "Team", Cell = CellKind.Text, MinWidth = 70, DefaultWidth = 80, Text = s => s.IsTeam ? F.Yes : null },
            .. Common(oneDrive: false),
            new() { Key = F.Hub, Header = "Hub", Cell = CellKind.Text, MinWidth = 110, DefaultWidth = 120, DefaultVisible = false,
                Text = s => s.IsHubSite ? F.HubSite : s.HubSiteId is not null ? F.InHub : null },
        ],
    };

    public static ListDefinition<SiteSummary> OneDrives(Func<SiteSummary, object> preview) => new()
    {
        Key = "onedrives",
        Title = "OneDrives",
        Noun = "OneDrives",
        Fields = F.All,
        Id = s => s.Id,
        Views = OneDriveViews,
        SearchHint = "Search names and addresses… or try used>10 archive:archived",
        Preview = preview,
        Actions = [new RowAction<SiteSummary>("Open OneDrive", "Globe", s => Platform.OpenUrl(s.Url))],
        Columns =
        [
            Name("Owner"),
            .. Common(oneDrive: true),
        ],
    };
}
