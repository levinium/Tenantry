using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels;

/// <summary>A choice of how often, in minutes (0 = never / only on demand).</summary>
public sealed record IntervalOption(int Minutes, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An area of Settings, listed down the left ("Sync"); more join as settings grow.</summary>
public sealed record SettingsArea(string Key, string Title, string Icon, string Description);

/// <summary>One part of the local copy as Settings lists it.</summary>
public sealed record SyncAreaRow(string Name, string Holds, string Last, string Every);

/// <summary>
/// Settings for the open tenant: how often each part of the local copy is brought up to date,
/// when each was last read, and the buttons to check everything now or download it all again.
/// Changes save at once (per tenant) and take effect on the next check.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    readonly ISyncSettingsStore _store;
    readonly ITenantSync? _sync;
    readonly Func<DateTimeOffset> _now;
    readonly Func<SyncScope, Task> _syncNow;
    readonly Action _changed;
    readonly ISyncStatus? _status;
    bool _loading;

    public static IReadOnlyList<IntervalOption> ChangesOptions { get; } =
    [
        new(5, "Every 5 Minutes"), new(10, "Every 10 Minutes"), new(15, "Every 15 Minutes"), new(30, "Every 30 Minutes"), new(60, "Every Hour"),
        new(0, "Only When the Tenant Opens, and on Refresh"),
    ];

    public static IReadOnlyList<IntervalOption> DetailsOptions { get; } =
        [new(30, "Every 30 Minutes"), new(60, "Every Hour"), new(240, "Every 4 Hours"), new(1440, "Once a Day")];

    public static IReadOnlyList<IntervalOption> MailboxDetailsOptions { get; } =
        [new(60, "Every Hour"), new(240, "Every 4 Hours"), new(1440, "Once a Day"), new(0, "Off")];

    public static IReadOnlyList<IntervalOption> MailboxSizesOptions { get; } =
        [new(60, "Every Hour"), new(240, "Every 4 Hours"), new(720, "Every 12 Hours"), new(1440, "Once a Day"), new(0, "Off")];

    /// <param name="syncNow">Runs a sync (Everything or StartOver) and updates the lists.</param>
    /// <param name="changed">Called after a setting is saved (the app restarts its timer).</param>
    /// <param name="status">The sync as it happens (the sidebar's), so Settings shows a sync already running.</param>
    /// <param name="passwordStyle">The tenant's password style (the Passwords area); none hides the area.</param>
    public SettingsViewModel(string tenantName, ISyncSettingsStore store, ITenantSync? sync, Func<SyncScope, Task> syncNow, Action changed,
        Func<DateTimeOffset>? now = null, ISyncStatus? status = null, Tenantry.Core.Changes.IPasswordStyleStore? passwordStyle = null)
    {
        TenantName = tenantName;
        Passwords = passwordStyle is null ? null : new PasswordStyleViewModel(passwordStyle);
        SettingsAreas = Passwords is null ? [SyncArea] : [SyncArea, PasswordsArea];
        SelectedArea = SyncArea;
        _store = store;
        _sync = sync;
        _syncNow = syncNow;
        _changed = changed;
        _now = now ?? (() => DateTimeOffset.Now);
        _status = status;
        if (_status is not null)
            _status.PropertyChanged += OnStatusChanged;
        _loading = true;
        var s = store.Load();
        Changes = Pick(ChangesOptions, s.ChangesMinutes);
        Details = Pick(DetailsOptions, s.DetailsMinutes);
        MailboxDetails = Pick(MailboxDetailsOptions, s.MailboxDetailsMinutes);
        MailboxSizes = Pick(MailboxSizesOptions, s.MailboxSizesMinutes);
        _loading = false;
        RefreshAreas();
    }

    public string TenantName { get; }

    static readonly SettingsArea SyncArea = new("sync", "Sync", "ArrowSync", "How often this tenant's local copy is brought up to date");

    static readonly SettingsArea PasswordsArea = new("passwords", "Passwords", "Password", "How Tenantry makes new passwords for this tenant");

    /// <summary>Settings' areas, down the left.</summary>
    public IReadOnlyList<SettingsArea> SettingsAreas { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncArea), nameof(IsPasswordsArea))]
    public partial SettingsArea SelectedArea { get; set; }

    public bool IsSyncArea => SelectedArea.Key == "sync";

    public bool IsPasswordsArea => SelectedArea.Key == "passwords";

    /// <summary>The Passwords area; null when the tenant has no password style to set.</summary>
    public PasswordStyleViewModel? Passwords { get; }

    /// <summary>The option for a stored value; an unknown value (edited by hand) shows as its own option.</summary>
    static IntervalOption Pick(IReadOnlyList<IntervalOption> options, int minutes) =>
        options.FirstOrDefault(o => o.Minutes == minutes) ?? new IntervalOption(minutes, $"Every {minutes} Minutes");

    [ObservableProperty]
    public partial IntervalOption Changes { get; set; }

    [ObservableProperty]
    public partial IntervalOption Details { get; set; }

    [ObservableProperty]
    public partial IntervalOption MailboxDetails { get; set; }

    [ObservableProperty]
    public partial IntervalOption MailboxSizes { get; set; }

    partial void OnChangesChanged(IntervalOption value) => Save();
    partial void OnDetailsChanged(IntervalOption value) => Save();
    partial void OnMailboxDetailsChanged(IntervalOption value) => Save();
    partial void OnMailboxSizesChanged(IntervalOption value) => Save();

    void Save()
    {
        if (_loading)
            return;
        _store.Save(new SyncSettings(Changes.Minutes, Details.Minutes, MailboxDetails.Minutes, MailboxSizes.Minutes));
        _changed();
        RefreshAreas();
    }

    [ObservableProperty]
    public partial IReadOnlyList<SyncAreaRow> Areas { get; private set; } = [];

    public bool HasAreas => Areas.Count > 0;

    void RefreshAreas()
    {
        Areas = (_sync?.Areas ?? []).Select(a => new SyncAreaRow(a.Name, a.Holds,
            a.LastSyncedAt is { } at ? $"Updated {Display.Clock(at, _now())}" : "Not Read Yet", a.Every)).ToArray();
        OnPropertyChanged(nameof(HasAreas));
    }

    // ---- Live status --------------------------------------------------------------------------

    /// <summary>"Updating now: Reading mailboxes…" while any sync runs (started here or not).</summary>
    public string? LiveText => _status is { IsSyncing: true } s ? $"Updating now: {s.SyncStep ?? "checking for changes…"}" : IsSyncing ? Status : null;

    public bool HasLiveText => LiveText is not null;

    /// <summary>The background read's progress ("Mailbox sizes: 120 of 604").</summary>
    public string? BackgroundText => _status?.BackgroundText;

    public bool HasBackgroundText => BackgroundText is not null;

    bool AnySyncRunning => IsSyncing || _status?.IsSyncing == true;

    void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(LiveText));
        OnPropertyChanged(nameof(HasLiveText));
        OnPropertyChanged(nameof(ShowStatus));
        OnPropertyChanged(nameof(BackgroundText));
        OnPropertyChanged(nameof(HasBackgroundText));
        SyncEverythingCommand.NotifyCanExecuteChanged();
        StartOverCommand.NotifyCanExecuteChanged();
        // A sync or background read just ended: show the new "last read" times.
        if (e.PropertyName is nameof(ISyncStatus.IsSyncing) && _status?.IsSyncing == false
            || e.PropertyName is nameof(ISyncStatus.BackgroundText))
            RefreshAreas();
    }

    public void Dispose()
    {
        if (_status is not null)
            _status.PropertyChanged -= OnStatusChanged;
    }

    // ---- Sync now -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncEverythingCommand), nameof(StartOverCommand))]
    [NotifyPropertyChangedFor(nameof(LiveText), nameof(HasLiveText), nameof(ShowStatus))]
    public partial bool IsSyncing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
    public partial string? Status { get; private set; }

    /// <summary>The last outcome ("Up to date."), when no sync is running to say more.</summary>
    public bool ShowStatus => !string.IsNullOrEmpty(Status) && !HasLiveText;

    /// <summary>The first click on "Download everything again" asks; the second does it.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingStartOver { get; private set; }

    /// <summary>One sync at a time: the buttons wait while any sync is running.</summary>
    bool CanSync => !AnySyncRunning && _sync is not null;

    [RelayCommand(CanExecute = nameof(CanSync))]
    Task SyncEverything() => RunAsync(SyncScope.Everything, "Checking everything…");

    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task StartOver()
    {
        if (!IsConfirmingStartOver)
        {
            IsConfirmingStartOver = true;
            return;
        }
        IsConfirmingStartOver = false;
        await RunAsync(SyncScope.StartOver, "Downloading everything again…");
    }

    [RelayCommand]
    void CancelStartOver() => IsConfirmingStartOver = false;

    async Task RunAsync(SyncScope scope, string status)
    {
        IsSyncing = true;
        Status = status;
        try
        {
            await _syncNow(scope);
            Status = "Up to date.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Status = $"Couldn't update: {Friendly.Message(e)}";
        }
        finally
        {
            IsSyncing = false;
            RefreshAreas();
        }
    }
}
