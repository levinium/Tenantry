using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    const string Later = "Coming in a later release";

    /// <summary>How often an open tenant checks for changes.</summary>
    public static readonly TimeSpan DefaultSyncInterval = TimeSpan.FromMinutes(10);

    readonly ConnectedTenant _tenant;
    readonly Action _switchTenant;
    readonly Func<DateTimeOffset> _now;
    readonly TimeSpan? _syncInterval;
    readonly CancellationTokenSource _lifetime = new();
    readonly DirectoryIndex _index = new();

    /// <param name="syncInterval">How often to check for changes while open; null checks only on open and on Refresh.</param>
    public MainViewModel(ConnectedTenant tenant, Action? switchTenant = null, Func<DateTimeOffset>? now = null, TimeSpan? syncInterval = null)
    {
        _tenant = tenant;
        _switchTenant = switchTenant ?? (() => { });
        _now = now ?? (() => DateTimeOffset.Now);
        _syncInterval = syncInterval;
        LastSyncedAt = tenant.Sync?.LastSyncedAt;

        var createUsers = Evaluate(Capabilities.EditUsers);
        Users = DataListViewModel.Create(UserList.Definition(_now), LoadUsersAsync, OpenUser, tenant.ListState, _now);
        Users.PrimaryAction = new ListPrimaryAction("New user", "PersonAdd", false,
            createUsers.IsAvailable ? "Creating users arrives with the change pipeline (Phase 3)" : createUsers.Reason!);
        Groups = DataListViewModel.Create(GroupList.Definition(g => NewGroupPage(g)), LoadGroupsAsync, OpenGroup, tenant.ListState, _now);
        Groups.PrimaryAction = new ListPrimaryAction("New group", "PeopleAdd", false, "Creating groups arrives with the change pipeline (Phase 3)");

        var readUsers = Evaluate(Capabilities.ReadUsers);
        Sections =
        [
            new NavSection("users", "Users", readUsers.Reason),
            new NavSection("groups", "Groups & teams", readUsers.Reason),
            new NavSection("mail", "Mail & security", Later),
            new NavSection("sharepoint", "SharePoint & OneDrive", Later),
            new NavSection("devices", "Devices", Later),
            new NavSection("security", "Security & compliance", Later),
        ];
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecordTabs));
        SelectedSection = Sections[0];
    }

    public string TenantName => _tenant.TenantName;

    public string Account => _tenant.Account;

    public string TenantInitials => Display.Initials(_tenant.TenantName);

    public bool IsDemo => _tenant.IsDemo;

    public string WindowTitle => $"Tenantry · {TenantName}";

    public IReadOnlyList<NavSection> Sections { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public DataListViewModel Users { get; }

    public DataListViewModel Groups { get; }

    /// <summary>Lists in sidebar order, so each sidebar entry maps to one list tab.</summary>
    IEnumerable<(string Section, DataListViewModel List)> AreaLists => [("users", Users), ("groups", Groups)];

    async Task<IReadOnlyList<UserSummary>> LoadUsersAsync(CancellationToken cancellationToken)
    {
        var users = await _tenant.Directory.GetUsersAsync(cancellationToken);
        _index.SetUsers(users);
        return users;
    }

    async Task<IReadOnlyList<GroupSummary>> LoadGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = await _tenant.Directory.GetGroupsAsync(cancellationToken);
        _index.SetGroups(groups);
        return groups;
    }

    async Task LoadListsAsync(CancellationToken cancellationToken)
    {
        await Users.LoadAsync(cancellationToken);
        await Groups.LoadAsync(cancellationToken);
    }

    async Task ReloadListsAsync(CancellationToken cancellationToken)
    {
        await Users.ReloadAsync(cancellationToken);
        await Groups.ReloadAsync(cancellationToken);
    }

    /// <summary>Every gated feature and whether this admin can use it here, for the "Your access" panel.</summary>
    public IReadOnlyList<CapabilityRow> Access => Capabilities.All
        .Select(c => new CapabilityRow(c.Title, Evaluate(c)))
        .ToArray();

    [ObservableProperty]
    public partial TabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    public partial NavSection? SelectedSection { get; set; }

    public Availability Evaluate(Capability capability) => CapabilityEvaluator.Evaluate(capability, _tenant.Context);

    /// <summary>
    /// Shows the tenant. A tenant opened before shows its local copy at once and checks for
    /// changes behind it; one opened for the first time downloads the directory first, with
    /// progress in the list.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_tenant.Sync is not { } sync)
        {
            await LoadListsAsync(cancellationToken);
            return;
        }

        if (sync.LastSyncedAt is null)
        {
            const string first = "Downloading the directory for the first time…";
            Users.ShowLoading(first);
            Groups.ShowLoading(first);
            var progress = new Progress<string>(text =>
            {
                Users.UpdateLoadingText(text);
                Groups.UpdateLoadingText(text);
            });
            var ok = await SyncAsync(progress, SyncScope.Everything, cancellationToken) is not null;
            await LoadListsAsync(cancellationToken);
            if (!ok)
            {
                var message = $"Couldn't download the directory: {SyncError} Use Refresh (bottom left) to try again.";
                Users.ShowLoadError(message);
                Groups.ShowLoadError(message);
            }
        }
        else
        {
            await LoadListsAsync(cancellationToken);
            if (await SyncAsync(null, SyncScope.Changes, cancellationToken) > 0)
                await ReloadListsAsync(cancellationToken);
        }

        if (_syncInterval is { } interval)
            _ = CheckPeriodicallyAsync(interval, _lifetime.Token);
    }

    // ---- Sync status ----------------------------------------------------------------------

    public bool HasSync => _tenant.Sync is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncText), nameof(SyncTip), nameof(HasSyncError), nameof(IsSyncOk))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsSyncing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncText), nameof(SyncTip), nameof(HasSyncError), nameof(IsSyncOk))]
    public partial string? SyncError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncText), nameof(SyncTip))]
    public partial DateTimeOffset? LastSyncedAt { get; private set; }

    public bool HasSyncError => SyncError is not null && !IsSyncing;

    public bool IsSyncOk => SyncError is null && !IsSyncing;

    /// <summary>The status line: a clock time rather than "5 min ago", which would go stale on screen.</summary>
    public string SyncText =>
        IsSyncing ? "Checking for changes…"
        : SyncError is not null ? "Couldn't update"
        : LastSyncedAt is { } at ? $"Updated {Display.Clock(at, _now())}"
        : "Not downloaded yet";

    public string SyncTip
    {
        get
        {
            var where = _tenant.Sync?.IsPersistent == false
                ? "This computer has no secure key store, so the copy is kept in memory and downloaded again each time."
                : "The copy is encrypted on this computer.";
            var when = LastSyncedAt is { } at ? $"Last brought up to date {Display.Exact(at)}. " : "";
            var error = SyncError is not null && !IsSyncing ? $"The last check failed: {SyncError} " : "";
            var notes = _tenant.Sync?.Notes is { Count: > 0 } n ? string.Join(" ", n) + " " : "";
            return $"{error}{notes}{when}Lists read a local copy of the directory, so they are instant. " +
                   $"Tenantry checks for changes when you open the tenant and every {DefaultSyncInterval.TotalMinutes:0} minutes while it's open; " +
                   $"mailboxes and last sign-in times are re-read hourly. {where} " +
                   "Click Refresh to check everything now.";
        }
    }

    bool CanRefresh => HasSync && !IsSyncing;

    /// <summary>Checks everything now (including the hourly reads) and updates the lists if anything changed.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    async Task Refresh()
    {
        if (await SyncAsync(null, SyncScope.Everything, _lifetime.Token) > 0)
            await ReloadListsAsync(_lifetime.Token);
    }

    /// <summary>Runs one sync; returns the number of changes, or null if it failed (see <see cref="SyncError"/>).</summary>
    async Task<int?> SyncAsync(IProgress<string>? progress, SyncScope scope, CancellationToken cancellationToken)
    {
        if (_tenant.Sync is not { } sync)
            return 0;
        IsSyncing = true;
        try
        {
            // Off the UI thread: a first download parses and writes thousands of objects.
            var changed = await Task.Run(() => sync.SyncAsync(progress, scope, cancellationToken), cancellationToken);
            SyncError = null;
            return changed;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SyncError = Friendly.Message(e);
            return null;
        }
        finally
        {
            LastSyncedAt = sync.LastSyncedAt;
            IsSyncing = false;
        }
    }

    async Task CheckPeriodicallyAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!IsSyncing && await SyncAsync(null, SyncScope.Changes, cancellationToken) > 0)
                    await ReloadListsAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The tenant was closed.
        }
    }

    /// <summary>Stops the periodic check and closes the tenant's local database.</summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _tenant.Resources?.Dispose();
    }

    /// <summary>
    /// The sidebar picks the area; the first tab is always that area's list, so the two never
    /// show the same thing twice. Record tabs after it stay open across areas.
    /// </summary>
    partial void OnSelectedSectionChanged(NavSection? value)
    {
        foreach (var (section, list) in AreaLists)
        {
            if (value?.Key != section)
                continue;
            if (Tabs.Count == 0)
                Tabs.Add(list);
            else if (Tabs[0] != list)
                Tabs[0] = list;
            SelectedTab = list;
        }
    }

    /// <summary>The tab strip only appears once a record is open beside the list.</summary>
    public bool HasRecordTabs => Tabs.Count > 1;

    /// <summary>Opens a user's record page, or switches to it if it is already open.</summary>
    public void OpenUser(UserSummary user)
    {
        var existing = Tabs.OfType<UserDetailViewModel>().FirstOrDefault(t => t.User.Id == user.Id);
        if (existing is null)
        {
            existing = new UserDetailViewModel(user, _now());
            Tabs.Add(existing);
        }
        SelectedTab = existing;
    }

    /// <summary>Opens a group's record page, or switches to it if it is already open.</summary>
    public void OpenGroup(GroupSummary group)
    {
        var existing = Tabs.OfType<GroupDetailViewModel>().FirstOrDefault(t => t.Group.Id == group.Id);
        if (existing is null)
        {
            existing = NewGroupPage(group);
            Tabs.Add(existing);
        }
        SelectedTab = existing;
    }

    GroupDetailViewModel NewGroupPage(GroupSummary group) => new(group, _index, OpenUser, OpenGroup);

    // ---- Ctrl+K ---------------------------------------------------------------------------

    PaletteViewModel? _palette;

    public PaletteViewModel Palette => _palette ??= new PaletteViewModel(PaletteEntries, () => IsPaletteOpen = false);

    [ObservableProperty]
    public partial bool IsPaletteOpen { get; set; }

    [RelayCommand]
    void OpenPalette()
    {
        Palette.Reset();
        IsPaletteOpen = true;
    }

    /// <summary>Everyone, every group and every list view, built when the palette first searches.</summary>
    IReadOnlyList<PaletteEntry> PaletteEntries()
    {
        var entries = new List<PaletteEntry>();
        foreach (var u in _index.AllUsers)
        {
            var address = u.Kind == UserKind.Guest && u.Mail is { Length: > 0 } m ? m : u.UserPrincipalName;
            var searchText = string.Join(' ', [u.DisplayName, u.UserPrincipalName, u.Mail, u.Department, u.JobTitle,
                .. u.Mailbox?.Aliases ?? []]).ToLowerInvariant();
            entries.Add(new PaletteEntry("User", u.DisplayName, u.Department is { Length: > 0 } d ? $"{address} · {d}" : address,
                "Person", searchText, () => OpenUser(u)));
        }
        foreach (var g in _index.AllGroups)
        {
            var searchText = string.Join(' ', g.DisplayName, g.Mail, g.Description, GroupSummary.KindName(g.Kind)).ToLowerInvariant();
            entries.Add(new PaletteEntry("Group", g.DisplayName, g.Mail ?? GroupSummary.KindName(g.Kind), "People", searchText, () => OpenGroup(g)));
        }
        foreach (var (section, list) in AreaLists)
        {
            foreach (var view in list.Views)
            {
                entries.Add(new PaletteEntry("View", view.Name, $"{list.Title} view", "Filter",
                    $"{view.Name} {list.Title} view".ToLowerInvariant(), () =>
                    {
                        SelectedSection = Sections.First(s => s.Key == section);
                        list.ApplyView(view);
                    }));
            }
        }
        return entries;
    }

    /// <summary>The local copy by id, rebuilt on each load, so group pages can name their members.</summary>
    sealed class DirectoryIndex : IDirectoryIndex
    {
        Dictionary<string, UserSummary> _users = new(StringComparer.Ordinal);
        Dictionary<string, GroupSummary> _groups = new(StringComparer.Ordinal);

        public void SetUsers(IReadOnlyList<UserSummary> users) => _users = users.ToDictionary(u => u.Id, StringComparer.Ordinal);

        public void SetGroups(IReadOnlyList<GroupSummary> groups) => _groups = groups.ToDictionary(g => g.Id, StringComparer.Ordinal);

        public IEnumerable<UserSummary> AllUsers => _users.Values;

        public IEnumerable<GroupSummary> AllGroups => _groups.Values;

        public UserSummary? User(string id) => _users.GetValueOrDefault(id);

        public GroupSummary? Group(string id) => _groups.GetValueOrDefault(id);
    }

    [RelayCommand]
    void CloseTab(TabViewModel tab)
    {
        if (!tab.CanClose)
            return;
        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;
        Tabs.RemoveAt(index);
        if (SelectedTab == tab || SelectedTab is null)
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    [RelayCommand]
    void SwitchTenant() => _switchTenant();
}

public sealed record CapabilityRow(string Title, Availability Availability)
{
    public string State => Availability.IsAvailable ? "Available" : Availability.Reason ?? "Unavailable";
    public string BadgeText => Availability.IsAvailable ? "Available" : "Unavailable";
    public BadgeKind BadgeKind => Availability.IsAvailable ? BadgeKind.Success : BadgeKind.Neutral;
    public string? Reason => Availability.Reason;
}
