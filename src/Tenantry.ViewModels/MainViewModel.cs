using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Directory;
using Tenantry.Core.Tenancy;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable, ISyncStatus, Workflows.IWorkflowLauncher, ITabHost
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
    /// <param name="preferences">App-wide choices such as whether the preview pane is open; kept in memory when null.</param>
    public MainViewModel(ConnectedTenant tenant, Action? switchTenant = null, Func<DateTimeOffset>? now = null, TimeSpan? syncInterval = null,
        IAppPreferencesStore? preferences = null)
    {
        _tenant = tenant;
        _switchTenant = switchTenant ?? (() => { });
        _now = now ?? (() => DateTimeOffset.Now);
        _syncInterval = syncInterval;
        LastSyncedAt = tenant.Sync?.LastSyncedAt;
        MissingPermissions = tenant.MissingPermissions ?? [];

        var createUsers = Evaluate(Capabilities.EditUsers);
        Users = DataListViewModel.Create(UserList.Definition(_now, u => NewUserPage(u)), LoadUsersAsync, OpenUser, tenant.ListState, _now);
        Users.PrimaryAction = tenant.Changes is { } creating && createUsers.IsAvailable
            ? new ListPrimaryAction("New User", "PersonAdd", true, "Add someone: name, sign-in, password, licenses, groups and manager", () =>
                ShowDialog(new Records.DialogViewModel("New User", TenantName,
                    new Records.NewUserViewModel(creating, _tenant.Records as IUserRecords, _index, CloseDialog))))
            : new ListPrimaryAction("New User", "PersonAdd", false, createUsers.Reason ?? "This tenant can't be edited");
        Users.SecondaryAction = tenant.Changes is { } importing && createUsers.IsAvailable
            ? new ListPrimaryAction("Import from CSV", "DocumentTable", true, "Create or update many people from a spreadsheet, using Tenantry's template", () =>
                ShowDialog(new Records.DialogViewModel("Import Users from CSV", TenantName,
                    new Records.ImportUsersViewModel(importing, _tenant.Records as IUserRecords, _index, CloseDialog))))
            : null;
        Groups = DataListViewModel.Create(GroupList.Definition(g => NewGroupPage(g)), LoadGroupsAsync, OpenGroup, tenant.ListState, _now);
        Groups.PrimaryAction = tenant.Changes is { } grouping && createUsers.IsAvailable
            ? new ListPrimaryAction("New Group", "PeopleAdd", true, "Make a Microsoft 365 group, security group, distribution list or shared mailbox", () =>
                ShowDialog(new Records.DialogViewModel("New Group", TenantName,
                    new Records.NewGroupViewModel(grouping, _tenant.Records as IUserRecords, _index, CloseDialog))))
            : new ListPrimaryAction("New Group", "PeopleAdd", false, createUsers.Reason ?? "This tenant can't be edited");
        var manageDevices = Evaluate(Capabilities.ManageDevices);
        Devices = DataListViewModel.Create(DeviceList.Definition(d => new Records.DevicePreviewViewModel(d, _tenant.Changes, manageDevices, _now, _index, OpenUser)),
            LoadDevicesAsync, OpenDevice, tenant.ListState, _now);
        Devices.PrimaryAction = HasSync ? new ListPrimaryAction("Refresh Devices", "ArrowSync", true,
            "Read devices from Entra and Intune again now (they're otherwise checked with everything else)", () => _ = RefreshDevicesAsync()) : null;
        Devices.SelectionPanel = (rows, uncheck) =>
            Records.SelectionPanelViewModel.ForDevices(rows, uncheck, _tenant.Changes, manageDevices, _now);
        var shared = new SharedListPreferences(preferences ?? new MemoryAppPreferencesStore());
        Users.FollowSharedPreferences(shared);
        Groups.FollowSharedPreferences(shared);
        Devices.FollowSharedPreferences(shared);
        if (tenant.Changes is { } changes)
        {
            Pending = new PendingChangesViewModel(changes, tenant.TenantName, showTestMode: !tenant.IsDemo, AfterChangesAppliedAsync);
        }
        // Several rows checked: the preview pane becomes a selection panel with bulk changes.
        var canEditMany = Evaluate(Capabilities.EditUsers);
        Users.SelectionPanel = (rows, uncheck) =>
            Records.SelectionPanelViewModel.ForUsers(rows, uncheck, _index, _tenant.Records, _tenant.Changes, canEditMany, this);
        Groups.SelectionPanel = (rows, uncheck) =>
            Records.SelectionPanelViewModel.ForGroups(rows, uncheck, _index, _tenant.Changes, canEditMany);

        var readUsers = Evaluate(Capabilities.ReadUsers);
        var mailSecurity = Evaluate(Capabilities.ManageMailSecurity);
        if (tenant.Records is IMailSecurity mail)
        {
            MailSecurity = new MailSecurity.MailSecurityViewModel(mail, tenant.Changes, mailSecurity, tenant.ListState, _now, this);
            // The preview pane is one choice for every list, remembered between runs.
            foreach (var list in MailSecurity.Lists)
                list.FollowSharedPreferences(shared);
        }
        Sections =
        [
            new NavSection("users", "Users", readUsers.Reason),
            new NavSection("groups", GroupList.AreaTitle, readUsers.Reason),
            new NavSection("mail", "Mail & Security", MailSecurity is null ? "Mail & Security needs Exchange Online" : mailSecurity.Reason),
            new NavSection("sharepoint", "SharePoint & OneDrive", Later),
            new NavSection("devices", "Devices", readUsers.Reason),
            new NavSection("security", "Security & Compliance", Later),
        ];
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecordTabs));
        CheckPreviewsWhenTheySettle();
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

    public DataListViewModel Devices { get; }

    /// <summary>Mail &amp; security (quarantine, message trace…); null when the tenant has no Exchange reads.</summary>
    public MailSecurity.MailSecurityViewModel? MailSecurity { get; }

    /// <summary>Lists in sidebar order, so each sidebar entry maps to one list tab.</summary>
    IEnumerable<(string Section, DataListViewModel List)> AreaLists => [("users", Users), ("groups", Groups), ("devices", Devices)];

    /// <summary>Each sidebar entry's tab: the lists, then areas with pages of their own.</summary>
    IEnumerable<(string Section, TabViewModel Tab)> AreaTabs =>
        AreaLists.Select(a => (a.Section, (TabViewModel)a.List)).Concat(MailSecurity is null ? [] : [("mail", (TabViewModel)MailSecurity)]);

    IReadOnlyList<DeviceSummary> _devices = [];

    async Task<IReadOnlyList<DeviceSummary>> LoadDevicesAsync(CancellationToken cancellationToken) =>
        _devices = await _tenant.Directory.GetDevicesAsync(cancellationToken);

    /// <summary>Opens a device's record page, or switches to it if it is already open.</summary>
    public void OpenDevice(DeviceSummary device)
    {
        var existing = Tabs.OfType<DeviceDetailViewModel>().FirstOrDefault(t => t.Device.Key == device.Key);
        if (existing is null)
        {
            existing = new DeviceDetailViewModel(device, _tenant.Changes, Evaluate(Capabilities.ManageDevices), _now,
                _tenant.Records as IDevicePageRecords, _tenant.Records as IDeviceRecords, _index, OpenUser, OpenGroup);
            Tabs.Add(existing);
        }
        SelectedTab = existing;
    }

    /// <summary>The Devices list's Refresh: Entra's devices (changes) and Intune's (read whole) now, then the list.</summary>
    async Task RefreshDevicesAsync()
    {
        if (IsSyncing)
            return;
        // The list keeps its rows; the wheel says what's being read.
        Devices.ShowLoading("Reading devices from Entra and Intune…");
        try
        {
            var progress = new Progress<string>(Devices.UpdateLoadingText);
            if (await SyncAsync(progress, SyncScope.Refresh, _lifetime.Token, devicesOnly: true) is null)
                Devices.ShowLoadError($"Couldn't read devices: {SyncError}");
            await Devices.ReloadAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // The tenant was closed.
        }
        finally
        {
            Devices.ShowLoading(null);
        }
    }

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
        await Devices.LoadAsync(cancellationToken);
    }

    async Task ReloadListsAsync(CancellationToken cancellationToken)
    {
        await Users.ReloadAsync(cancellationToken);
        await Groups.ReloadAsync(cancellationToken);
        await Devices.ReloadAsync(cancellationToken);
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

        // Background reads (mailbox sizes and replies) finish on a worker thread; the lists are
        // re-read back on this (the UI) thread.
        var ui = SynchronizationContext.Current;
        sync.BackgroundUpdated += (_, _) =>
        {
            if (ui is null)
                _ = AfterBackgroundUpdateAsync();
            else
                ui.Post(_ => _ = AfterBackgroundUpdateAsync(), null);
        };
        sync.BackgroundStatusChanged += (_, _) =>
        {
            if (ui is null)
                BackgroundText = sync.BackgroundStatus;
            else
                ui.Post(_ => BackgroundText = sync.BackgroundStatus, null);
        };

        if (sync.LastSyncedAt is null)
        {
            const string first = "Downloading the directory for the first time…";
            Users.ShowLoading(first);
            Groups.ShowLoading(first);
            Devices.ShowLoading(first);
            var progress = new Progress<string>(text =>
            {
                Users.UpdateLoadingText(text);
                Groups.UpdateLoadingText(text);
                Devices.UpdateLoadingText(text);
            });
            var ok = await SyncAsync(progress, SyncScope.Everything, cancellationToken) is not null;
            await LoadListsAsync(cancellationToken);
            if (!ok)
            {
                var message = $"Couldn't download the directory: {SyncError} Use Refresh (bottom left) to try again.";
                Users.ShowLoadError(message);
                Groups.ShowLoadError(message);
                Devices.ShowLoadError(message);
            }
        }
        else
        {
            await LoadListsAsync(cancellationToken);
            if (await SyncAsync(null, SyncScope.Changes, cancellationToken) > 0)
                await ReloadListsAsync(cancellationToken);
        }

        StartChecking();
    }

    async Task AfterBackgroundUpdateAsync()
    {
        try
        {
            await ReloadListsAsync(_lifetime.Token);
            OnPropertyChanged(nameof(SyncTip));
        }
        catch (OperationCanceledException)
        {
            // The tenant was closed meanwhile.
        }
    }

    CancellationTokenSource? _checking;

    /// <summary>How often to check for changes: the tenant's setting, else the app's default; null for never.</summary>
    TimeSpan? ChangesInterval => _tenant.SyncSettings?.Load() is { } s
        ? s.ChangesMinutes > 0 ? TimeSpan.FromMinutes(s.ChangesMinutes) : null
        : _syncInterval;

    /// <summary>(Re)starts the periodic check, e.g. after the interval is changed in Settings.</summary>
    void StartChecking()
    {
        _checking?.Cancel();
        _checking?.Dispose();
        _checking = null;
        if (_syncInterval is null || ChangesInterval is not { } interval)
            return;
        _checking = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = CheckPeriodicallyAsync(interval, _checking.Token);
        OnPropertyChanged(nameof(SyncTip));
    }

    // ---- Settings -------------------------------------------------------------------------

    public bool HasSettings => _tenant.SyncSettings is not null;

    [RelayCommand]
    void OpenSettings()
    {
        if (_tenant.SyncSettings is not { } store)
            return;
        ShowDialog(new Records.DialogViewModel("Settings", TenantName,
            new SettingsViewModel(TenantName, store, _tenant.Sync, SyncNowAsync, StartChecking, _now, this, _tenant.PasswordStyle), Footnote: null));
    }

    /// <summary>Settings' "Check everything now" and "Download everything again".</summary>
    async Task SyncNowAsync(SyncScope scope)
    {
        if (await SyncAsync(null, scope, _lifetime.Token) is null)
            throw new InvalidOperationException(SyncError ?? "The sync failed.");
        await ReloadListsAsync(_lifetime.Token);
    }

    // ---- Sync status ----------------------------------------------------------------------

    public bool HasSync => _tenant.Sync is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncText), nameof(SyncTip), nameof(HasSyncError), nameof(IsSyncOk), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsSyncing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncText), nameof(SyncTip), nameof(HasSyncError), nameof(IsSyncOk))]
    public partial string? SyncError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncText), nameof(SyncTip))]
    public partial DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>What the running sync is doing now; null between syncs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncText))]
    public partial string? SyncStep { get; private set; }

    /// <summary>The background read's progress ("Mailbox sizes: 120 of 604"), shown under the status line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackgroundText), nameof(IsBusy))]
    public partial string? BackgroundText { get; private set; }

    public bool HasBackgroundText => BackgroundText is not null;

    /// <summary>
    /// A sync or the background read is running. While it is, the status area keeps all its rows
    /// (step, bar, background line), so it doesn't jump as messages and steps come and go.
    /// </summary>
    public bool IsBusy => IsSyncing || HasBackgroundText;

    public bool HasSyncError => SyncError is not null && !IsSyncing;

    public bool IsSyncOk => SyncError is null && !IsSyncing;

    /// <summary>The status line: a clock time rather than "5 min ago", which would go stale on screen.</summary>
    public string SyncText =>
        IsSyncing ? SyncStep ?? "Checking for changes…"
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
            var every = ChangesInterval is { } i ? $"and every {i.TotalMinutes:0} minutes while it's open" : "and when you click Refresh";
            return $"{error}{notes}{when}Lists read a local copy of the directory, so they are instant. " +
                   $"Tenantry checks for changes when you open the tenant {every}; " +
                   $"mailboxes, their sizes and last sign-in times are re-read less often (see Settings). {where} " +
                   "Click Refresh to check everything now.";
        }
    }

    bool CanRefresh => HasSync && !IsSyncing;

    /// <summary>Checks everything now (including the hourly reads) and updates the lists if anything changed.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    async Task Refresh()
    {
        if (await SyncAsync(null, SyncScope.Refresh, _lifetime.Token) > 0)
            await ReloadListsAsync(_lifetime.Token);
    }

    /// <summary>Runs one sync; returns the number of changes, or null if it failed (see <see cref="SyncError"/>).</summary>
    /// <param name="devicesOnly">Re-read only the devices (the Devices list's Refresh).</param>
    async Task<int?> SyncAsync(IProgress<string>? progress, SyncScope scope, CancellationToken cancellationToken, bool devicesOnly = false)
    {
        if (_tenant.Sync is not { } sync)
            return 0;
        IsSyncing = true;
        // Every sync's steps reach the status line (and Settings), not only a first download's.
        var steps = new Progress<string>(step => SyncStep = step);
        IProgress<string> report = progress is null ? steps : new BothProgress(progress, steps);
        try
        {
            // Off the UI thread: a first download parses and writes thousands of objects.
            var changed = await Task.Run(() => devicesOnly ? sync.SyncDevicesAsync(report, cancellationToken) : sync.SyncAsync(report, scope, cancellationToken),
                cancellationToken);
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
            SyncStep = null;
        }
    }

    sealed class BothProgress(IProgress<string> first, IProgress<string> second) : IProgress<string>
    {
        public void Report(string value)
        {
            first.Report(value);
            second.Report(value);
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
        foreach (var (section, tab) in AreaTabs)
        {
            if (value?.Key != section)
                continue;
            if (Tabs.Count == 0)
                Tabs.Add(tab);
            else if (Tabs[0] != tab)
                Tabs[0] = tab;
            SelectedTab = tab;
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
            existing = NewUserPage(user);
            existing.LoadMailInBackground();
            Tabs.Add(existing);
        }
        SelectedTab = existing;
        _ = CheckAsync(existing, force: false);
    }

    /// <summary>A user's record (page or preview), editable when the tenant and the admin's role allow.</summary>
    UserDetailViewModel NewUserPage(UserSummary user) =>
        Fresh(new UserDetailViewModel(user, _now(), _tenant.Changes, Evaluate(Capabilities.EditUsers), _tenant.Records, ShowDialog, CloseDialog, _index,
            OpenUser, OpenGroup, this));

    // ---- Workflows --------------------------------------------------------------------------

    /// <summary>Opens the offboarding checklist for one person or several (record page, selection panel).</summary>
    public void Offboard(IReadOnlyList<UserSummary> people)
    {
        if (_tenant.Changes is not { } pipeline || people.Count == 0 || !Evaluate(Capabilities.EditUsers).IsAvailable)
            return;
        var workflow = new Workflows.OffboardingViewModel(people, _index, _tenant.Records, pipeline, TenantName, CloseDialog);
        ShowDialog(new Records.DialogViewModel(workflow.Heading, workflow.Subtitle, workflow));
    }

    /// <summary>Opens the compromised-account response for one person or several.</summary>
    public void RespondToCompromise(IReadOnlyList<UserSummary> people)
    {
        if (_tenant.Changes is not { } pipeline || people.Count == 0 || !Evaluate(Capabilities.EditUsers).IsAvailable)
            return;
        var workflow = new Workflows.CompromiseViewModel(people, _tenant.Records, pipeline, CloseDialog);
        ShowDialog(new Records.DialogViewModel(workflow.Heading, workflow.Subtitle, workflow));
    }

    // ---- Permissions ----------------------------------------------------------------------

    /// <summary>Permissions this version needs that the tenant hasn't granted yet (empty when all is well).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingPermissions), nameof(PermissionsText), nameof(PermissionsList))]
    public partial IReadOnlyList<string> MissingPermissions { get; private set; } = [];

    public bool HasMissingPermissions => MissingPermissions.Count > 0 && _tenant.UpdatePermissions is not null;

    public string PermissionsText =>
        $"Some features need {(MissingPermissions.Count == 1 ? "a permission" : $"{MissingPermissions.Count} permissions")} {TenantName} hasn't granted Tenantry yet " +
        "(for example, resetting passwords). A Global Administrator or Cloud Application Administrator can grant them in one step; you'll sign in to approve.";

    public string PermissionsList => "Missing: " + string.Join(", ", MissingPermissions);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GrantPermissionsCommand))]
    public partial bool IsGrantingPermissions { get; private set; }

    [ObservableProperty]
    public partial string? PermissionsStatus { get; private set; }

    bool CanGrantPermissions => !IsGrantingPermissions;

    /// <summary>Runs the one-step setup again for this tenant, which grants whatever is missing.</summary>
    [RelayCommand(CanExecute = nameof(CanGrantPermissions))]
    async Task GrantPermissions()
    {
        if (_tenant.UpdatePermissions is not { } update)
            return;
        IsGrantingPermissions = true;
        PermissionsStatus = "Sign in as an administrator in your browser to approve…";
        try
        {
            MissingPermissions = await update(new Progress<string>(s => PermissionsStatus = s), _lifetime.Token);
            PermissionsStatus = MissingPermissions.Count == 0 ? null : $"Still missing after setup: {string.Join(", ", MissingPermissions)}.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            PermissionsStatus = $"Couldn't grant the permissions: {Friendly.Message(e)}";
        }
        finally
        {
            IsGrantingPermissions = false;
        }
    }

    // ---- Popups ---------------------------------------------------------------------------

    /// <summary>The popup over the window (a record tab from the preview, Reset password, …), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial Records.DialogViewModel? Dialog { get; private set; }

    public bool HasDialog => Dialog is not null;

    public void ShowDialog(Records.DialogViewModel dialog) => Dialog = dialog;

    /// <summary>Settings watches the sync while open; it lets go when its popup closes.</summary>
    partial void OnDialogChanged(Records.DialogViewModel? oldValue, Records.DialogViewModel? newValue) =>
        (oldValue?.Content as SettingsViewModel)?.Dispose();

    [RelayCommand]
    public void CloseDialog() => Dialog = null;
    // ---- Changes --------------------------------------------------------------------------

    /// <summary>The Pending changes tray and Test mode banner; null where the tenant can't be edited.</summary>
    public PendingChangesViewModel? Pending { get; }

    /// <summary>After changes apply: lists re-read the local copy (already updated by read-back), open records follow.</summary>
    Task AfterChangesAppliedAsync() => FollowLocalCopyAsync();

    /// <summary>Lists re-read the local copy, and open records and previews show what's in it now.</summary>
    async Task FollowLocalCopyAsync()
    {
        await ReloadListsAsync(_lifetime.Token);
        foreach (var page in Tabs.OfType<UserDetailViewModel>())
        {
            if (_index.User(page.User.Id) is { } updated)
                page.Update(updated);
        }
        foreach (var page in Tabs.OfType<GroupDetailViewModel>())
        {
            if (_index.Group(page.Group.Id) is { } updated)
                page.Update(updated);
        }
        var devices = Tabs.OfType<DeviceDetailViewModel>().ToArray();
        if (devices.Length > 0)
        {
            var current = (await _tenant.Directory.GetDevicesAsync(_lifetime.Token)).ToDictionary(d => d.Key, StringComparer.Ordinal);
            foreach (var page in devices)
                if (current.TryGetValue(page.Device.Key, out var updated))
                    page.Update(updated);
        }
        // The previews follow the same way.
        if (Users.PreviewModel is UserDetailViewModel userPreview && _index.User(userPreview.User.Id) is { } u)
            userPreview.Update(u);
        if (Groups.PreviewModel is GroupDetailViewModel groupPreview && _index.Group(groupPreview.Group.Id) is { } g)
            groupPreview.Update(g);
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
        _ = CheckAsync(existing, force: false);
    }

    GroupDetailViewModel NewGroupPage(GroupSummary group) =>
        Fresh(new GroupDetailViewModel(group, _index, OpenUser, OpenGroup, _tenant.Changes, Evaluate(Capabilities.EditUsers), ShowDialog, CloseDialog));

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
            entries.Add(g.IsMailbox
                ? new PaletteEntry("Mailbox", g.DisplayName, $"{g.Mail} · {GroupSummary.KindName(g.Kind)}", "Mail", searchText, () => OpenGroup(g))
                : new PaletteEntry("Group", g.DisplayName, g.Mail ?? GroupSummary.KindName(g.Kind), "People", searchText, () => OpenGroup(g)));
        }
        foreach (var d in _devices)
        {
            var searchText = string.Join(' ', d.Name, d.UserName, d.UserPrincipalName, d.Serial, d.Model, d.Platform).ToLowerInvariant();
            entries.Add(new PaletteEntry("Device", d.Name, d.UserName is { } user ? $"{d.Platform} · {user}" : d.Platform,
                DeviceList.IconFor(d), searchText, () => OpenDevice(d)));
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

    public void OpenTab(Func<TabViewModel, bool> existing, Func<TabViewModel> create)
    {
        var tab = Tabs.FirstOrDefault(existing);
        if (tab is null)
        {
            tab = create();
            Tabs.Add(tab);
        }
        SelectedTab = tab;
    }

    [RelayCommand]
    public void CloseTab(TabViewModel tab)
    {
        if (!tab.CanClose)
            return;
        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;
        Tabs.RemoveAt(index);
        (tab as IDisposable)?.Dispose();
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
