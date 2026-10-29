using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels;

/// <summary>
/// One archived log as a page (10): a date range, what the archive holds, Read New Events Now, and the
/// events in that range as a standard list (search, filters, views, CSV export).
/// </summary>
public sealed partial class LogHistoryViewModel : ObservableObject
{
    /// <summary>At most this many events are shown at once; a narrower range shows the rest.</summary>
    public const int ShowAtMost = 100_000;

    public static IReadOnlyList<EditorChoice> Ranges { get; } =
    [
        new("1", "Last 24 Hours", ""),
        new("7", "Last 7 Days", ""),
        new("30", "Last 30 Days", ""),
        new("90", "Last 90 Days", ""),
        new("365", "Last Year", ""),
        new("0", "Everything Kept", ""),
    ];

    readonly ILogArchive? _archive;
    readonly string _kind;
    readonly Func<DateTimeOffset> _now;
    readonly Func<Task>? _readNew;
    string _noun = "events";
    bool _shown;

    LogHistoryViewModel(ILogArchive? archive, string kind, string description, Func<DateTimeOffset> now, Func<Task>? readNew)
    {
        _archive = archive;
        _kind = kind;
        _now = now;
        _readNew = readNew;
        Description = description;
        Range = Ranges[1];
    }

    /// <param name="archive">The archive, or null where there is none (then the page says so).</param>
    /// <param name="readNew">Adds new events now (the sync's archive run), or null.</param>
    public static LogHistoryViewModel Create<T>(ILogArchive? archive, string kind, string description, ListDefinition<T> definition, IListStateStore? listState,
        Func<DateTimeOffset> now, Func<Task>? readNew) where T : class
    {
        var page = new LogHistoryViewModel(archive, kind, description, now, readNew) { _noun = definition.Noun };
        page.List = DataListViewModel.Create(definition, async ct =>
        {
            // A year of sign-ins is a lot of rows: read them off the window's thread.
            var days = int.Parse(page.Range.Value, CultureInfo.InvariantCulture);
            var (rows, summary) = await Task.Run(() => page.Read<T>(days), ct);
            page.Summary = summary;
            return rows;
        }, _ => { }, listState, now);
        page.List.HasRecordPages = false;
        page.List.EmptyText = archive is null ? "There's no history on this computer for this tenant." : "Nothing in this range.";
        return page;
    }

    public DataListViewModel List { get; private set; } = null!;

    public string Description { get; }

    [ObservableProperty]
    public partial EditorChoice Range { get; set; }

    partial void OnRangeChanged(EditorChoice value)
    {
        if (_shown)
            _ = List.ReloadAsync(busyText: "Reading the history…");
    }

    /// <summary>"Holds 16,204 sign-ins from 30 Sep 2026 to now"; why the list is short; or that there's no archive.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanReadNew))]
    public partial string? BusyText { get; private set; }

    public bool IsBusy => BusyText is not null;

    public bool CanReadNew => _readNew is not null && !IsBusy;

    /// <summary>Only where there's a sync to read with (not the demo).</summary>
    public bool HasReadNew => _readNew is not null;

    (IReadOnlyList<T> Rows, string Summary) Read<T>(int days) where T : class
    {
        if (_archive is null)
            return ([], "This tenant's history isn't kept on this computer.");
        var now = _now();
        var events = _archive.ReadEvents(_kind, days == 0 ? DateTimeOffset.MinValue.AddDays(1) : now.AddDays(-days), now.AddDays(1), ShowAtMost);
        var span = _archive.Span(_kind);
        var summary = span.Count == 0
            ? "Nothing archived yet. The archive fills as Tenantry syncs (starting with the 30 days Microsoft keeps), or use Read New Events Now."
            : $"This computer holds {span.Count.ToString("N0", CultureInfo.CurrentCulture)} {_noun} from {span.Oldest!.Value.ToLocalTime():d MMM yyyy} to {span.Newest!.Value.ToLocalTime():d MMM yyyy h:mm tt}." +
              (events.Count >= ShowAtMost ? $" Showing the newest {ShowAtMost:N0} in this range; choose a shorter range to see older ones." : "");
        return ([.. events.Select(LogKinds.Read<T>).OfType<T>()], summary);
    }

    /// <summary>Reads the range the first time the page is shown.</summary>
    public void Shown() => _ = ShownAsync();

    public Task ShownAsync()
    {
        if (_shown)
            return Task.CompletedTask;
        _shown = true;
        return List.LoadAsync();
    }

    [RelayCommand]
    async Task ReadNew()
    {
        if (_readNew is null)
            return;
        BusyText = "Reading new sign-ins and audit events from Microsoft 365…";
        try
        {
            await _readNew();
        }
        finally
        {
            BusyText = null;
        }
        await List.ReloadAsync(busyText: "Reading the history…");
    }

    /// <summary>After the sync archived new events.</summary>
    public void Archived()
    {
        if (_shown)
            _ = List.ReloadAsync(busyText: "Reading the history…");
    }
}
