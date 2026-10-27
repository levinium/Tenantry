using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>A tab of a site's page.</summary>
public abstract class SiteSection(SiteDetailViewModel page) : LiveSection
{
    protected SiteDetailViewModel Page => page;

    protected SiteSummary Site => page.Site;

    protected ISiteRecords Records => page.Records ?? throw new InvalidOperationException("SharePoint isn't connected for this tenant.");
}

/// <summary>Someone with access, as a row: opens their record (or the group's) when it's in the local copy.</summary>
public sealed partial class PrincipalRow(SitePrincipal principal, string? detail, UserSummary? user, GroupSummary? group,
    Action<UserSummary>? openUser, Action<GroupSummary>? openGroup)
{
    public string Name => principal.Kind switch
    {
        PrincipalKind.GroupMembers when group is not null => group.DisplayName,
        PrincipalKind.GroupOwners when group is not null => group.DisplayName,
        PrincipalKind.Everyone => "Everyone",
        PrincipalKind.EveryoneExceptExternal => "Everyone Except External Users",
        _ => user?.DisplayName ?? principal.Name,
    };

    public string? Detail => detail;
    public bool HasDetail => !string.IsNullOrEmpty(detail);

    public string KindText => principal.Kind switch
    {
        PrincipalKind.Guest => "Guest",
        PrincipalKind.GroupMembers => "Group Members",
        PrincipalKind.GroupOwners => "Group Owners",
        PrincipalKind.SecurityGroup => "Security Group",
        PrincipalKind.Everyone => "Everyone",
        PrincipalKind.EveryoneExceptExternal => "Whole Organization",
        _ => "Person",
    };

    /// <summary>Guests and everyone-groups are what a review looks for.</summary>
    public BadgeKind KindKind => principal.Kind switch
    {
        PrincipalKind.Guest or PrincipalKind.Everyone => BadgeKind.Warning,
        PrincipalKind.EveryoneExceptExternal => BadgeKind.Info,
        PrincipalKind.Person => BadgeKind.Neutral,
        _ => BadgeKind.Info,
    };

    public bool CanOpen => (user is not null && openUser is not null) || (group is not null && openGroup is not null);

    [RelayCommand]
    void Open()
    {
        if (user is not null)
            openUser?.Invoke(user);
        else if (group is not null)
            openGroup?.Invoke(group);
    }

    /// <summary>A principal as a row, looked up in the local copy.</summary>
    public static PrincipalRow Of(SitePrincipal p, IDirectoryIndex index, Action<UserSummary>? openUser, Action<GroupSummary>? openGroup, string? detail = null)
    {
        var user = p.Upn is { } upn ? index.AllUsers.FirstOrDefault(u => string.Equals(u.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase)) : null;
        var group = p.ObjectId is { } id ? index.Group(id) : null;
        return new PrincipalRow(p, detail ?? user?.UserPrincipalName ?? (p.Kind == PrincipalKind.Guest ? p.Upn?.Replace("#ext#", "", StringComparison.OrdinalIgnoreCase) : null),
            user, group, openUser, openGroup);
    }
}

// ---- Overview ---------------------------------------------------------------------------------

/// <summary>Storage, activity and what it is; its owner and group.</summary>
public sealed class SiteOverviewSection(SiteDetailViewModel page) : SiteSection(page)
{
    public override string Key => "overview";
    public override string Title => "Overview";
    public override string Icon => "Home";
    public override string LoadingText => "Reading its activity from Microsoft's usage report…";

    public SitePreviewViewModel Details => Page.Summary;

    /// <summary>How full, as a 0–100 bar; null without a quota.</summary>
    public double? UsedPercent => Site.UsedPercent;

    public bool HasQuota => Site.StorageQuotaMb > 0;

    public string StorageText => SiteList.StorageText(Site);

    SiteUsage? _usage;

    public IReadOnlyList<SummaryLine> Activity => _usage is not { } u ? [] :
    [
        .. new (string, string?)[]
        {
            ("Files", $"{u.Files:N0}"),
            ("Active Files", $"{u.ActiveFiles:N0} in the last 30 days"),
            ("Page Views", u.PageViews is { } views ? $"{views:N0} in the last 30 days" : null),
            ("Pages Visited", u.VisitedPages is { } pages ? $"{pages:N0}" : null),
            ("Last Activity", u.LastActivity is { } day ? day.ToString("dddd, MMMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture) : null),
        }.Where(f => f.Item2 is not null).Select(f => new SummaryLine(f.Item1, f.Item2!)),
    ];

    public bool HasActivity => _usage is not null;

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        var read = await Records.GetUsageAsync(Site, cancellationToken);
        _usage = read.Value;
        Note = read.Unavailable;
        Summary = Site.StorageQuotaMb > 0 && Site.UsedPercent is { } p ? $"{p:0.#}% of storage used" : StorageText;
        OnPropertyChanged(nameof(Activity));
        OnPropertyChanged(nameof(HasActivity));
    }
}

// ---- Permissions ------------------------------------------------------------------------------

/// <summary>A SharePoint group on the site, with the people and groups in it.</summary>
public sealed class SiteGroupCard(string name, string? explanation, IReadOnlyList<PrincipalRow> members)
{
    public string Name => name;
    public string? Explanation => explanation;
    public bool HasExplanation => explanation is not null;
    public IReadOnlyList<PrincipalRow> Members => members;
    public bool IsEmpty => members.Count == 0;
    public string CountText => members.Count switch { 0 => "Nobody", 1 => "1", var n => $"{n:N0}" };
}

/// <summary>Site admins, then the Owners, Members and Visitors groups and the site's own groups.</summary>
public sealed class SitePermissionsSection(SiteDetailViewModel page) : SiteSection(page)
{
    public override string Key => "permissions";
    public override string Title => "Permissions";
    public override string Icon => "PeopleLock";
    public override string LoadingText => "Reading who has access from SharePoint…";

    public ObservableCollection<SiteGroupCard> Groups { get; } = [];

    static string? Explain(string? role, bool groupSite) => role switch
    {
        "Owners" => groupSite ? "Full control. On a group's site this is the Microsoft 365 group's owners." : "Full control of the site.",
        "Members" => groupSite ? "Can edit. On a group's site this is the Microsoft 365 group's members." : "Can add and edit content.",
        "Visitors" => "Can view.",
        _ => null,
    };

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        var permissions = await Records.GetPermissionsAsync(Site, cancellationToken);
        PrincipalRow Row(SitePrincipal p) => PrincipalRow.Of(p, Page.Index, Page.OpenUserAction, Page.OpenGroupAction);
        Groups.Clear();
        Groups.Add(new SiteGroupCard("Site Admins", "Full control, including over permissions and settings. They see everything in it.", [.. permissions.Admins.Select(Row)]));
        foreach (var g in permissions.Groups)
            Groups.Add(new SiteGroupCard(g.Role ?? g.Name, Explain(g.Role, Site.GroupId is not null) ?? (g.Role is null ? null : g.Name), [.. g.Members.Select(Row)]));
        var everyone = permissions.Groups.SelectMany(g => g.Members).Any(m => m.Kind is PrincipalKind.Everyone or PrincipalKind.EveryoneExceptExternal);
        Summary = everyone ? "Open to the whole organization" : $"{permissions.Admins.Count} admin{(permissions.Admins.Count == 1 ? "" : "s")}, {permissions.Groups.Count} groups";
    }
}

// ---- Sharing ----------------------------------------------------------------------------------

/// <summary>A kind of sharing link and how many there are.</summary>
public sealed class LinkSummaryRow(string title, string detail, int count, BadgeKind kind)
{
    public string Title => title;
    public string Detail => detail;
    public string CountText => $"{count:N0}";
    public BadgeKind Kind => kind;
}

/// <summary>Its external sharing setting, the sharing links on its files, and the guests who've been let in.</summary>
public sealed class SiteSharingSection(SiteDetailViewModel page) : SiteSection(page)
{
    public override string Key => "sharing";
    public override string Title => "Sharing";
    public override string Icon => "Share";
    public override string LoadingText => "Reading sharing links and guests from SharePoint…";

    public string SettingText => SiteList.SharingBadge(Site).Text;
    public BadgeKind SettingKind => SiteList.SharingBadge(Site).Kind;

    public string SettingExplanation => Site.Sharing switch
    {
        0 => "Files here can only be shared with people in the organization.",
        3 => "Files can be shared with guests already in the directory, but not with anyone new.",
        1 => "Files can be shared with new and existing guests: they sign in or get a code.",
        2 => "Files can be shared with anyone, including by links that need no sign-in.",
        _ => "",
    };

    public ObservableCollection<LinkSummaryRow> Links { get; } = [];
    public ObservableCollection<PrincipalRow> SharedWith { get; } = [];
    public ObservableCollection<PrincipalRow> Guests { get; } = [];

    public bool HasNoLinks => !IsLoading && Links.Count == 0 && Note is null;
    public bool HasSharedWith => SharedWith.Count > 0;
    public bool HasNoGuests => !IsLoading && Guests.Count == 0 && Note is null;

    protected override void Loaded()
    {
        OnPropertyChanged(nameof(HasNoLinks));
        OnPropertyChanged(nameof(HasNoGuests));
        OnPropertyChanged(nameof(HasSharedWith));
    }

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        var sharing = await Records.GetSharingAsync(Site, cancellationToken);
        Links.Clear();
        void Add(LinkKind kind, bool edit, string title, string detail, BadgeKind badge)
        {
            var count = sharing.Links.Count(l => l.Kind == kind && l.CanEdit == edit);
            if (count > 0)
                Links.Add(new LinkSummaryRow(title, detail, count, badge));
        }
        Add(LinkKind.Anyone, true, "Anyone With the Link Can Edit", "No sign-in needed: whoever has the link can change the file.", BadgeKind.Danger);
        Add(LinkKind.Anyone, false, "Anyone With the Link Can View", "No sign-in needed.", BadgeKind.Warning);
        Add(LinkKind.Organization, true, "People in the Organization Can Edit", "Anyone who signs in here and has the link.", BadgeKind.Info);
        Add(LinkKind.Organization, false, "People in the Organization Can View", "Anyone who signs in here and has the link.", BadgeKind.Neutral);
        Add(LinkKind.SpecificPeople, true, "Specific People", "Only the people it was shared with.", BadgeKind.Neutral);
        Add(LinkKind.SpecificPeople, false, "Specific People (View Only)", "Only the people it was shared with.", BadgeKind.Neutral);
        SharedWith.Clear();
        foreach (var p in sharing.Links.SelectMany(l => l.People).DistinctBy(p => p.Login))
            SharedWith.Add(PrincipalRow.Of(p, Page.Index, Page.OpenUserAction, Page.OpenGroupAction));
        Guests.Clear();
        foreach (var g in sharing.Guests)
            Guests.Add(PrincipalRow.Of(g, Page.Index, Page.OpenUserAction, Page.OpenGroupAction));
        var anyone = sharing.Links.Count(l => l.Kind == LinkKind.Anyone);
        Summary = anyone > 0 ? $"{anyone} anyone link{(anyone == 1 ? "" : "s")}" : sharing.Links.Count == 0 ? "No sharing links" : $"{sharing.Links.Count} sharing links";
    }
}

// ---- Libraries --------------------------------------------------------------------------------

/// <summary>A library or list; a library opens to its latest files.</summary>
public sealed partial class LibraryRow : ObservableObject
{
    readonly Func<Task<Available<IReadOnlyList<RecentFile>>>> _recent;

    public LibraryRow(SiteLibrary library, Func<Task<Available<IReadOnlyList<RecentFile>>>> recent)
    {
        Library = library;
        _recent = recent;
    }

    public SiteLibrary Library { get; }
    public string Name => Library.Name;
    public string Icon => Library.IsLibrary ? "Folder" : "TextBulletListSquare";
    public string Detail => string.Join(" · ", new[]
    {
        Library.IsLibrary ? $"{Library.Items:N0} files and folders" : $"{Library.Items:N0} items",
        Library.LastModified is { } at ? $"changed {Display.Exact(at)}" : null,
    }.OfType<string>());
    public bool CanExpand => Library.IsLibrary && Library.Items > 0;

    public ObservableCollection<RecentFile> Recent { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    bool _read;

    [RelayCommand]
    void Toggle() => IsExpanded = !IsExpanded;

    [RelayCommand]
    void Open()
    {
        if (Library.Url is { } url)
            Platform.OpenUrl(url);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_read)
            _ = ReadAsync();
    }

    async Task ReadAsync()
    {
        _read = true;
        IsLoading = true;
        try
        {
            var read = await _recent();
            foreach (var f in read.Value ?? [])
                Recent.Add(f);
            Note = read.Unavailable ?? (Recent.Count == 0 ? "No files yet." : null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note = $"Couldn't read its files: {Friendly.Message(e)}";
            _read = false;
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>Its document libraries (biggest first, each opening to its latest files) and lists.</summary>
public sealed class SiteLibrariesSection(SiteDetailViewModel page) : SiteSection(page)
{
    public override string Key => "libraries";
    public override string Title => "Libraries";
    public override string Icon => "Library";
    public override string LoadingText => "Reading its libraries and lists from SharePoint…";

    public ObservableCollection<LibraryRow> Libraries { get; } = [];
    public ObservableCollection<LibraryRow> Lists { get; } = [];

    public bool HasLists => Lists.Count > 0;

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        var all = await Records.GetLibrariesAsync(Site, cancellationToken);
        Libraries.Clear();
        Lists.Clear();
        foreach (var l in all)
            (l.IsLibrary ? Libraries : Lists).Add(new LibraryRow(l, () => Records.GetRecentFilesAsync(Site, l)));
        Summary = $"{Libraries.Count} libraries, {all.Where(l => l.IsLibrary).Sum(l => l.Items):N0} files and folders";
        OnPropertyChanged(nameof(HasLists));
    }
}
