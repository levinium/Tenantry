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

    /// <param name="syncInterval">How often to check for changes while open; null checks only on open and on Refresh.</param>
    public MainViewModel(ConnectedTenant tenant, Action? switchTenant = null, Func<DateTimeOffset>? now = null, TimeSpan? syncInterval = null)
    {
        _tenant = tenant;
        _switchTenant = switchTenant ?? (() => { });
        _now = now ?? (() => DateTimeOffset.Now);
        _syncInterval = syncInterval;
        LastSyncedAt = tenant.Sync?.LastSyncedAt;

        var createUsers = Evaluate(Capabilities.EditUsers);
        Users = DataListViewModel.Create(UserList.Definition(_now), ct => tenant.Directory.GetUsersAsync(ct), OpenUser, tenant.ListState, _now);
        Users.PrimaryAction = new ListPrimaryAction("New user", "PersonAdd", false,
            createUsers.IsAvailable ? "Creating users arrives with the change pipeline (Phase 6)" : createUsers.Reason!);

        var readUsers = Evaluate(Capabilities.ReadUsers);
        Sections =
        [
            new NavSection("users", "Users", readUsers.Reason),
            new NavSection("groups", "Groups & teams", Later),
            new NavSection("mail", "Mail & security", Later),
            new NavSection("sharepoint", "SharePoint & OneDrive", Later),
            new NavSection("devices", "Devices", Later),
            new NavSection("security", "Security & compliance", Later),
        ];
        Tabs.Add(Users);
        SelectedTab = Users;
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
            await Users.LoadAsync(cancellationToken);
            return;
        }

        if (sync.LastSyncedAt is null)
        {
            Users.ShowLoading("Downloading the directory for the first time…");
            var ok = await SyncAsync(new Progress<string>(Users.UpdateLoadingText), cancellationToken) is not null;
            await Users.LoadAsync(cancellationToken);
            if (!ok)
                Users.ShowLoadError($"Couldn't download the directory: {SyncError} Use Refresh (bottom left) to try again.");
        }
        else
        {
            await Users.LoadAsync(cancellationToken);
            if (await SyncAsync(null, cancellationToken) > 0)
                await Users.ReloadAsync(cancellationToken);
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
            return $"{error}{when}Lists read a local copy of the directory, so they are instant. " +
                   $"Tenantry checks for changes when you open the tenant and every {DefaultSyncInterval.TotalMinutes:0} minutes while it's open. {where} " +
                   "Click Refresh to check now.";
        }
    }

    bool CanRefresh => HasSync && !IsSyncing;

    /// <summary>Checks for changes now and updates the lists if any arrived.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    async Task Refresh()
    {
        if (await SyncAsync(null, _lifetime.Token) > 0)
            await Users.ReloadAsync(_lifetime.Token);
    }

    /// <summary>Runs one sync; returns the number of changes, or null if it failed (see <see cref="SyncError"/>).</summary>
    async Task<int?> SyncAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (_tenant.Sync is not { } sync)
            return 0;
        IsSyncing = true;
        try
        {
            // Off the UI thread: a first download parses and writes thousands of objects.
            var changed = await Task.Run(() => sync.SyncAsync(progress, cancellationToken: cancellationToken), cancellationToken);
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
                if (!IsSyncing && await SyncAsync(null, cancellationToken) > 0)
                    await Users.ReloadAsync(cancellationToken);
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

    partial void OnSelectedSectionChanged(NavSection? value)
    {
        if (value?.Key == "users")
            SelectedTab = Users;
    }

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
