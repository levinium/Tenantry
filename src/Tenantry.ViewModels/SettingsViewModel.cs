using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels;

/// <summary>A choice of how often, in minutes (0 = never / only on demand).</summary>
public sealed record IntervalOption(int Minutes, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One part of the local copy as Settings lists it.</summary>
public sealed record SyncAreaRow(string Name, string Holds, string Last, string Every);

/// <summary>
/// Settings for the open tenant: how often each part of the local copy is brought up to date,
/// when each was last read, and the buttons to check everything now or download it all again.
/// Changes save at once (per tenant) and take effect on the next check.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    readonly ISyncSettingsStore _store;
    readonly ITenantSync? _sync;
    readonly Func<DateTimeOffset> _now;
    readonly Func<SyncScope, Task> _syncNow;
    readonly Action _changed;
    bool _loading;

    public static IReadOnlyList<IntervalOption> ChangesOptions { get; } =
    [
        new(5, "Every 5 minutes"), new(10, "Every 10 minutes"), new(15, "Every 15 minutes"), new(30, "Every 30 minutes"), new(60, "Every hour"),
        new(0, "Only when the tenant opens, and on Refresh"),
    ];

    public static IReadOnlyList<IntervalOption> DetailsOptions { get; } =
        [new(30, "Every 30 minutes"), new(60, "Every hour"), new(240, "Every 4 hours"), new(1440, "Once a day")];

    public static IReadOnlyList<IntervalOption> MailboxDetailsOptions { get; } =
        [new(60, "Every hour"), new(240, "Every 4 hours"), new(1440, "Once a day"), new(0, "Off")];

    /// <param name="syncNow">Runs a sync (Everything or StartOver) and updates the lists.</param>
    /// <param name="changed">Called after a setting is saved (the app restarts its timer).</param>
    public SettingsViewModel(string tenantName, ISyncSettingsStore store, ITenantSync? sync, Func<SyncScope, Task> syncNow, Action changed,
        Func<DateTimeOffset>? now = null)
    {
        TenantName = tenantName;
        _store = store;
        _sync = sync;
        _syncNow = syncNow;
        _changed = changed;
        _now = now ?? (() => DateTimeOffset.Now);
        _loading = true;
        var s = store.Load();
        Changes = Pick(ChangesOptions, s.ChangesMinutes);
        Details = Pick(DetailsOptions, s.DetailsMinutes);
        MailboxDetails = Pick(MailboxDetailsOptions, s.MailboxDetailsMinutes);
        _loading = false;
        RefreshAreas();
    }

    public string TenantName { get; }

    /// <summary>The option for a stored value; an unknown value (edited by hand) shows as its own option.</summary>
    static IntervalOption Pick(IReadOnlyList<IntervalOption> options, int minutes) =>
        options.FirstOrDefault(o => o.Minutes == minutes) ?? new IntervalOption(minutes, $"Every {minutes} minutes");

    [ObservableProperty]
    public partial IntervalOption Changes { get; set; }

    [ObservableProperty]
    public partial IntervalOption Details { get; set; }

    [ObservableProperty]
    public partial IntervalOption MailboxDetails { get; set; }

    partial void OnChangesChanged(IntervalOption value) => Save();
    partial void OnDetailsChanged(IntervalOption value) => Save();
    partial void OnMailboxDetailsChanged(IntervalOption value) => Save();

    void Save()
    {
        if (_loading)
            return;
        _store.Save(new SyncSettings(Changes.Minutes, Details.Minutes, MailboxDetails.Minutes));
        _changed();
        RefreshAreas();
    }

    [ObservableProperty]
    public partial IReadOnlyList<SyncAreaRow> Areas { get; private set; } = [];

    public bool HasAreas => Areas.Count > 0;

    void RefreshAreas()
    {
        Areas = (_sync?.Areas ?? []).Select(a => new SyncAreaRow(a.Name, a.Holds,
            a.LastSyncedAt is { } at ? $"Updated {Display.Clock(at, _now())}" : "Not read yet", a.Every)).ToArray();
        OnPropertyChanged(nameof(HasAreas));
    }

    // ---- Sync now -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncEverythingCommand), nameof(StartOverCommand))]
    public partial bool IsSyncing { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    /// <summary>The first click on "Download everything again" asks; the second does it.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingStartOver { get; private set; }

    bool CanSync => !IsSyncing && _sync is not null;

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
