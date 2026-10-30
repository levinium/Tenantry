using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>A tab of a site's page.</summary>
public abstract class SiteSection(SiteDetailViewModel page) : LiveSection
{
    protected SiteDetailViewModel Page => page;

    protected SiteSummary Site => page.Site;

    protected ISiteRecords Records => page.Records ?? throw new InvalidOperationException("SharePoint isn't connected for this tenant.");

    /// <summary>A site the admin can't see inside: offer to make them a site admin.</summary>
    protected override string? NoteActionFor(UnauthorizedAccessException e) => e is SiteAccessException && Page.CanAddMe ? "Add Yourself as Site Admin" : null;

    protected override void OnNoteAction() => Page.AddMe();

    /// <summary>"Add me" was staged (from any tab): every tab's note says what happens next.</summary>
    internal void MeStaged()
    {
        if (NoteActionText is null)
            return;
        NoteActionText = null;
        Note = "Adding you as a site admin is waiting in Pending changes. Apply it, and this tab reads again.";
    }

    /// <summary>The site's row changed in the local copy (after a sync or applied changes).</summary>
    internal virtual void SiteChanged()
    {
    }
}

/// <summary>Someone with access, as a row: opens their record (or the group's) when it's in the local copy; may be removed.</summary>
public sealed partial class PrincipalRow(SitePrincipal principal, string? detail, UserSummary? user, GroupSummary? group,
    Action<UserSummary>? openUser, Action<GroupSummary>? openGroup)
{
    public SitePrincipal Principal => principal;

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

    /// <summary>"Adding" or "Removing" while a change to them waits in Pending changes.</summary>
    public string? StateText { get; set; }

    public bool HasState => StateText is not null;

    /// <summary>The row's button ("Remove", "Undo"), or null for none.</summary>
    public string? ActionText { get; set; }

    public string? ActionTip { get; set; }

    public bool HasAction => ActionText is not null && Act is not null;

    /// <summary>Why it can't be removed here, when that's worth saying ("Their own OneDrive").</summary>
    public string? Locked { get; set; }

    public bool IsLocked => Locked is not null;

    internal Action? Act { get; set; }

    [RelayCommand]
    void Do() => Act?.Invoke();

    /// <summary>A principal as a row, looked up in the local copy.</summary>
    public static PrincipalRow Of(SitePrincipal p, IDirectoryIndex index, Action<UserSummary>? openUser, Action<GroupSummary>? openGroup, string? detail = null)
    {
        var user = p.Upn is { } upn ? index.UserByUpn(upn) : null;
        var group = p.ObjectId is { } id ? index.Group(id) : null;
        return new PrincipalRow(p, detail ?? user?.UserPrincipalName ?? (p.Kind == PrincipalKind.Guest ? p.Upn?.Replace("#ext#", "", StringComparison.OrdinalIgnoreCase) : null),
            user, group, openUser, openGroup);
    }

    /// <summary>A person from the directory as SharePoint would name them.</summary>
    public static SitePrincipal PersonOf(UserSummary u) =>
        new(u.DisplayName, SetOneDriveAccess.LoginFor(u.UserPrincipalName), u.Kind == UserKind.Guest ? PrincipalKind.Guest : PrincipalKind.Person, Upn: u.UserPrincipalName);
}

// ---- Overview ---------------------------------------------------------------------------------

/// <summary>Storage, activity and what it is; its owner and group; and its actions.</summary>
public sealed class SiteOverviewSection(SiteDetailViewModel page) : SiteSection(page)
{
    public override string Key => "overview";
    public override string Title => "Overview";
    public override string Icon => "Home";
    public override string LoadingText => "Reading its activity from Microsoft's usage report…";

    public SitePreviewViewModel Details => Page.Summary;

    /// <summary>Sharing, storage, lock, archive, delete and site admins; null when the admin can't edit.</summary>
    public BulkEditorViewModel? Actions => Page.Actions;

    public bool CanEdit => Page.CanEdit;
    public string EditReason => Page.EditReason;

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

    internal override void SiteChanged()
    {
        foreach (var name in new[] { nameof(Details), nameof(UsedPercent), nameof(HasQuota), nameof(StorageText) })
            OnPropertyChanged(name);
    }

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        var read = await Records.GetUsageAsync(Site, cancellationToken);
        _usage = read.Value;
        Note = read.Unavailable;
        Summary = Site.StorageQuotaMb > 0 && Site.UsedPercent is { } p ? $"{p:0.#}% of storage used" : StorageText;
        OnPropertyChanged(nameof(Activity));
        OnPropertyChanged(nameof(HasActivity));
    }

    protected override void Forget()
    {
        _usage = null;
        OnPropertyChanged(nameof(Activity));
        OnPropertyChanged(nameof(HasActivity));
    }
}

// ---- Permissions ------------------------------------------------------------------------------

/// <summary>A SharePoint group on the site (or its admins), with the people and groups in it; may take more.</summary>
public sealed class SiteGroupCard(string name, string? explanation, IReadOnlyList<PrincipalRow> members, PersonPicker? picker = null)
{
    public string Name => name;
    public string? Explanation => explanation;
    public bool HasExplanation => explanation is not null;
    public IReadOnlyList<PrincipalRow> Members => members;
    public bool IsEmpty => members.Count == 0;
    public string CountText => members.Count(m => m.StateText != "Adding") switch { 0 => "Nobody", 1 => "1", var n => $"{n:N0}" };

    /// <summary>"Add someone: type a name"; null when the admin can't change this group.</summary>
    public PersonPicker? Picker => picker;

    public bool HasPicker => picker is not null;
}

/// <summary>
/// Site admins, then the Owners, Members and Visitors groups and the site's own groups, each
/// editable: people are added with a picker and removed per row, each change staged to Pending
/// changes. A group-connected site's Owners and Members are its Microsoft 365 group's, so they
/// change the group.
/// </summary>
public sealed class SitePermissionsSection : SiteSection, IDisposable
{
    SitePermissions? _permissions;

    public SitePermissionsSection(SiteDetailViewModel page) : base(page)
    {
        if (page.Pipeline is { } pipeline)
            pipeline.Changed += OnPendingChanged;
    }

    public override string Key => "permissions";
    public override string Title => "Permissions";
    public override string Icon => "PeopleLock";
    public override string LoadingText => "Reading who has access from SharePoint…";

    public ObservableCollection<SiteGroupCard> Groups { get; } = [];

    static string? Explain(string? role, bool groupSite) => role switch
    {
        "Owners" => groupSite ? "Full control. These are the Microsoft 365 group's owners: changes here change the group." : "Full control of the site.",
        "Members" => groupSite ? "Can edit. These are the Microsoft 365 group's members: changes here change the group." : "Can add and edit content.",
        "Visitors" => "Can view.",
        _ => null,
    };

    IEnumerable<ChangeOp> Pending => Page.Pipeline?.Pending.Where(Page.Touches) ?? [];

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        _permissions = await Records.GetPermissionsAsync(Site, cancellationToken);
        Rebuild();
    }

    protected override void Forget()
    {
        _permissions = null;
        Groups.Clear();
    }

    // The pending changes the cards last showed: a change elsewhere in the app leaves them as they are.
    ChangeOp[] _shown = [];

    void OnPendingChanged(object? sender, EventArgs e)
    {
        if (_permissions is not null && !Pending.SequenceEqual(_shown, ReferenceEqualityComparer.Instance))
            Rebuild();
    }

    PrincipalRow Row(SitePrincipal p) => PrincipalRow.Of(p, Page.Index, Page.OpenUserAction, Page.OpenGroupAction);

    ChangeTarget PersonTarget(SitePrincipal p) =>
        p.Upn is { } upn && Page.Index.UserByUpn(upn) is { } user
            ? GroupAccess.Target(user)
            : new ChangeTarget(p.Kind is PrincipalKind.Person or PrincipalKind.Guest ? "user" : "principal", p.Upn ?? p.Login, p.Name, p.Upn);

    /// <summary>A row that removes (or undoes) through <paramref name="stage"/>; <paramref name="pending"/> is a change already waiting for it.</summary>
    PrincipalRow Editable(SitePrincipal p, ChangeOp? pending, Func<ChangeOp>? remove, string? locked = null)
    {
        var row = Row(p);
        if (!Page.CanEdit)
            return row;
        if (pending is not null)
        {
            row.StateText = "Removing";
            row.ActionText = "Undo";
            row.ActionTip = "Keep them (takes the change out of Pending changes)";
            row.Act = () => Page.Pipeline!.Unstage(pending);
        }
        else if (locked is not null)
            row.Locked = locked;
        else if (remove is not null)
        {
            row.ActionText = "Remove";
            row.ActionTip = "Take them out (added to Pending changes)";
            row.Act = () => Page.Pipeline!.Stage(remove());
        }
        return row;
    }

    /// <summary>Someone staged to be added, shown at the end of the card with Undo.</summary>
    PrincipalRow Adding(SitePrincipal p, ChangeOp pending)
    {
        var row = Row(p);
        row.StateText = "Adding";
        row.ActionText = "Undo";
        row.ActionTip = "Don't add them (takes the change out of Pending changes)";
        row.Act = () => Page.Pipeline!.Unstage(pending);
        return row;
    }

    /// <param name="sharePoint">Adds to SharePoint itself (not a Microsoft 365 group): people it won't take are labelled.</param>
    PersonPicker? Picker(IEnumerable<string> present, Action<UserSummary> add, bool sharePoint = true)
    {
        if (!Page.CanEdit)
            return null;
        var taken = present.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new PersonPicker("Add someone: type a name",
            () => (sharePoint ? GroupAccess.SitePeople(Page.Index) : GroupAccess.People(Page.Index)).Where(o => o.Address is null || !taken.Contains(o.Address)),
            o =>
            {
                if (Page.Index.User(o.Id) is { } user)
                    add(user);
            });
    }

    static SitePrincipal PersonFromTarget(ChangeTarget t) =>
        new(t.Name, SetOneDriveAccess.LoginFor(t.Address ?? t.Id), PrincipalKind.Person, Upn: t.Address);

    void Rebuild()
    {
        if (_permissions is not { } permissions)
            return;
        var pending = _shown = Pending.ToArray();
        var site = SiteChange.TargetFor(Site);
        var groupSite = Site.GroupId is { } gid ? Page.Index.Group(gid) : null;
        Groups.Clear();

        // Site admins.
        var adminChanges = pending.OfType<SetSiteAdmin>().ToArray();
        // A OneDrive's person, or a site's primary owner: SharePoint won't take them off the admins (seen live).
        var ownerUpn = Site.OwnerLogin is { } ownerLogin ? ownerLogin[(ownerLogin.LastIndexOf('|') + 1)..] : null;
        var adminRows = permissions.Admins.Select(p => Editable(p,
            adminChanges.FirstOrDefault(c => !c.New && string.Equals(c.Login, p.Login, StringComparison.OrdinalIgnoreCase)),
            p.Kind is PrincipalKind.Person or PrincipalKind.Guest ? () => new SetSiteAdmin(site, Site.Url, PersonTarget(p), p.Login, true, false) : null,
            p.Upn is { } upn && string.Equals(upn, ownerUpn, StringComparison.OrdinalIgnoreCase) ? (Site.Kind == SiteKind.OneDrive ? "Their own OneDrive" : "The site's primary owner")
            : p.Kind == PrincipalKind.GroupOwners ? "The group's owners" : null)).ToList();
        foreach (var add in adminChanges.Where(c => c.New && !permissions.Admins.Any(a => string.Equals(a.Login, c.Login, StringComparison.OrdinalIgnoreCase))))
            adminRows.Add(Adding(PersonFromTarget(add.Person), add));
        Groups.Add(new SiteGroupCard("Site Admins", "Full control, including over permissions and settings. They see everything in it.", adminRows,
            Picker(permissions.Admins.Select(a => a.Upn ?? "").Concat(adminChanges.Select(c => c.Person.Address ?? "")),
                u => Page.Pipeline!.Stage(new SetSiteAdmin(site, Site.Url, GroupAccess.Target(u), SetOneDriveAccess.LoginFor(u.UserPrincipalName), false, true)))));

        foreach (var g in permissions.Groups)
        {
            var spChanges = pending.OfType<SetSiteGroupMember>().Where(c => c.GroupId == g.Id).ToArray();
            // Anyone directly in the SharePoint group (not the Microsoft 365 group's claim) is removed from it.
            IEnumerable<PrincipalRow> Direct(IEnumerable<SitePrincipal> principals) => principals.Select(p => Editable(p,
                spChanges.FirstOrDefault(c => !c.New && string.Equals(c.Login, p.Login, StringComparison.OrdinalIgnoreCase)),
                p.Kind is PrincipalKind.System ? null : () => new SetSiteGroupMember(site, Site.Url, g.Id, g.Role ?? g.Name, PersonTarget(p), p.Login, true, false)));
            var addedHere = spChanges.Where(c => c.New && !g.Members.Any(m => string.Equals(m.Login, c.Login, StringComparison.OrdinalIgnoreCase)))
                .Select(c => Adding(PersonFromTarget(c.Person), c));

            if (groupSite is not null && g.Role is "Owners" or "Members")
            {
                // The group's own owners or members, changed in the group.
                var role = g.Role == "Owners" ? GroupRole.Owner : GroupRole.Member;
                var list = role == GroupRole.Owner ? groupSite.OwnerList : groupSite.Members;
                var groupChanges = pending.OfType<SetGroupMembership>().Where(c => c.Role == role).ToArray();
                var rows = new List<PrincipalRow>();
                // Owners already waiting to be removed don't count: Graph refuses taking the last one away.
                var ownersLeft = groupSite.OwnerList.Count - groupChanges.Count(c => !c.New);
                foreach (var m in list)
                {
                    if (Page.Index.User(m.Id) is not { } person)
                        continue;
                    var lastOwner = role == GroupRole.Owner && ownersLeft <= 1;
                    rows.Add(Editable(PrincipalRow.PersonOf(person), groupChanges.FirstOrDefault(c => !c.New && c.Person.Id == person.Id),
                        () => new SetGroupMembership(GroupAccess.Target(groupSite), GroupAccess.Target(person), "user", role, groupSite.IsExchangeManaged, true, false),
                        lastOwner ? "The group's last owner" : null));
                }
                foreach (var add in groupChanges.Where(c => c.New && list.All(m => m.Id != c.Person.Id)))
                    rows.Add(Adding(PersonFromTarget(add.Person), add));
                rows.AddRange(Direct(g.Members.Where(p => p.Kind is not (PrincipalKind.GroupMembers or PrincipalKind.GroupOwners) || p.ObjectId != groupSite.Id)));
                rows.AddRange(addedHere);
                var present = list.Select(m => Page.Index.User(m.Id)?.UserPrincipalName ?? "").Concat(groupChanges.Select(c => c.Person.Address ?? ""));
                Groups.Add(new SiteGroupCard(g.Role, Explain(g.Role, true), rows, groupSite.IsDynamic && role == GroupRole.Member ? null : Picker(present,
                    u => Page.Pipeline!.Stage(new SetGroupMembership(GroupAccess.Target(groupSite), GroupAccess.Target(u), "user", role, groupSite.IsExchangeManaged, false, true)), sharePoint: false)));
                continue;
            }

            if (Site.GroupId is not null && g.Role is "Owners" or "Members")
            {
                // The group isn't in the local copy (just made, or outside what's synced): its people
                // can only change through the group, so they're shown but not changed here.
                Groups.Add(new SiteGroupCard(g.Role,
                    $"These are its Microsoft 365 group's {g.Role.ToLowerInvariant()}. The group isn't in Tenantry's local copy yet, so they can be changed here after the next refresh.",
                    [.. g.Members.Select(Row)]));
                continue;
            }

            Groups.Add(new SiteGroupCard(g.Role ?? g.Name, Explain(g.Role, Site.GroupId is not null) ?? (g.Role is null ? null : g.Name),
                [.. Direct(g.Members), .. addedHere],
                Picker(g.Members.Select(m => m.Upn ?? "").Concat(spChanges.Select(c => c.Person.Address ?? "")),
                    u => Page.Pipeline!.Stage(new SetSiteGroupMember(site, Site.Url, g.Id, g.Role ?? g.Name, GroupAccess.Target(u),
                        SetOneDriveAccess.LoginFor(u.UserPrincipalName), false, true)))));
        }
        var everyone = permissions.Groups.SelectMany(g => g.Members).Any(m => m.Kind is PrincipalKind.Everyone or PrincipalKind.EveryoneExceptExternal);
        Summary = everyone ? "Open to the whole organization" : $"{permissions.Admins.Count} admin{(permissions.Admins.Count == 1 ? "" : "s")}, {permissions.Groups.Count} groups";
    }

    public void Dispose()
    {
        if (Page.Pipeline is { } pipeline)
            pipeline.Changed -= OnPendingChanged;
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

/// <summary>Its sharing settings, the sharing links on its files, and the guests who've been let in.</summary>
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

    /// <summary>The site's sharing settings (changed from the Overview tab's actions).</summary>
    public IReadOnlyList<SummaryLine> Settings =>
    [
        new("Default Link", SiteSettings.LinkTypeName(Site.LinkType)),
        new("Link Permission", SiteSettings.LinkPermissionName(Site.LinkPermission)),
        new("Guests Lose Access", SiteSettings.GuestExpiryName(Site.GuestExpiryDays)),
    ];

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

    internal override void SiteChanged()
    {
        foreach (var name in new[] { nameof(SettingText), nameof(SettingKind), nameof(SettingExplanation), nameof(Settings) })
            OnPropertyChanged(name);
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

    protected override void Forget()
    {
        Links.Clear();
        SharedWith.Clear();
        Guests.Clear();
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

    protected override void Forget()
    {
        Libraries.Clear();
        Lists.Clear();
        OnPropertyChanged(nameof(HasLists));
    }
}
