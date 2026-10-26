using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>One page of Mail &amp; security, as its chip reads.</summary>
/// <param name="Planned">Not built yet: what the page will hold.</param>
public sealed record MailSecurityPage(string Key, string Title, string Icon, string? Planned = null)
{
    public bool IsPlanned => Planned is not null;
}

/// <summary>A choice of how far back message trace looks.</summary>
public sealed record TraceRange(int Days, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Mail &amp; security area (Phase 5): one tab, with a page per job. Quarantine and message
/// trace are live lists (read from Exchange when shown, not kept in the local copy) with the usual
/// filters, views, columns and export; their preview pane holds the actions.
/// </summary>
public sealed partial class MailSecurityViewModel : TabViewModel
{
    readonly IMailSecurity? _records;
    readonly Func<DateTimeOffset> _now;

    public static IReadOnlyList<MailSecurityPage> Pages { get; } =
    [
        new("quarantine", "Quarantine", "ShieldError"),
        new("trace", "Message Trace", "MailSearch"),
        new("rules", "Mail Flow Rules", "ArrowRouting"),
        new("lists", "Block and Allow Lists", "ShieldProhibited"),
    ];

    public MailSecurityViewModel(IMailSecurity? records, ChangePipeline? pipeline, Availability canEdit, IListStateStore? listState, Func<DateTimeOffset>? now = null)
    {
        _records = records;
        _now = now ?? (() => DateTimeOffset.Now);
        Quarantine = DataListViewModel.Create(
            MailSecurityLists.Quarantine(m => new QuarantinePreviewViewModel(m, records, pipeline, canEdit)),
            LoadQuarantineAsync, _ => { }, listState, _now);
        Trace = DataListViewModel.Create(
            MailSecurityLists.Trace(m => new TracePreviewViewModel(m, records)),
            LoadTraceAsync, _ => { }, listState, _now);
        Entries = DataListViewModel.Create(
            MailSecurityLists.Entries(e => new ListEntryPreviewViewModel(e, pipeline, canEdit)),
            LoadEntriesAsync, _ => { }, listState, _now);
        AddEntries = new AddListEntriesViewModel(pipeline, canEdit, () => _entries, _now);
        Rules = DataListViewModel.Create(
            MailSecurityLists.Rules(r => new RulePreviewViewModel(r, _rules.Count, pipeline, canEdit)),
            LoadRulesAsync, _ => { }, listState, _now);
        if (pipeline is not null)
            pipeline.Applied += (_, applied) =>
            {
                if (applied.Any(c => c is ListEntryChange) && _entriesRead)
                    _ = Entries.ReloadAsync(busyText: "Reading the lists again…");
                if (applied.Any(c => c is RuleChange) && _rulesRead)
                    _ = Rules.ReloadAsync(busyText: "Reading the rules again…");
                if (applied.Any(c => c is QuarantineChange) && _quarantineRead)
                    _ = Quarantine.ReloadAsync();
            };
        foreach (var list in new[] { Quarantine, Trace, Entries, Rules })
        {
            list.PrimaryAction = null;
            list.HasRecordPages = false;
        }
        Trace.EmptyText = "Choose what to look for above, then press Search.";
        Quarantine.EmptyText = "Nothing in quarantine matches these filters.";
        TraceStart = _now().Date.AddDays(-1);
        SelectedPage = Pages[0];
    }

    public override string Title => "Mail & Security";

    public override bool CanClose => false;

    public DataListViewModel Quarantine { get; }

    public DataListViewModel Trace { get; }

    public DataListViewModel Entries { get; }

    public DataListViewModel Rules { get; }

    public AddListEntriesViewModel AddEntries { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQuarantine), nameof(ShowTrace), nameof(ShowLists), nameof(ShowRules), nameof(IsPlannedPage))]
    public partial MailSecurityPage SelectedPage { get; set; }

    public bool ShowQuarantine => SelectedPage.Key == "quarantine";
    public bool ShowTrace => SelectedPage.Key == "trace";
    public bool ShowLists => SelectedPage.Key == "lists";
    public bool ShowRules => SelectedPage.Key == "rules";
    public bool IsPlannedPage => SelectedPage.IsPlanned;

    bool _quarantineRead;
    bool _entriesRead;
    bool _rulesRead;

    partial void OnSelectedPageChanged(MailSecurityPage value)
    {
        // Quarantine and the lists read when first shown; trace waits for a search.
        if (value.Key == "quarantine" && !_quarantineRead)
        {
            _quarantineRead = true;
            _ = Quarantine.LoadAsync();
        }
        if (value.Key == "rules" && !_rulesRead)
        {
            _rulesRead = true;
            _ = Rules.LoadAsync();
        }
        if (value.Key == "lists" && !_entriesRead)
        {
            _entriesRead = true;
            _ = Entries.LoadAsync();
        }
    }

    // ---- Mail flow rules ----------------------------------------------------------------------

    IReadOnlyList<MailFlowRule> _rules = [];

    async Task<IReadOnlyList<MailFlowRule>> LoadRulesAsync(CancellationToken cancellationToken) =>
        _rules = _records is null ? [] : await _records.GetRulesAsync(cancellationToken);

    [RelayCommand]
    Task RefreshRules() => Rules.ReloadAsync(busyText: "Reading the mail flow rules…");

    // ---- Block and allow lists ----------------------------------------------------------------

    IReadOnlyList<ListEntry> _entries = [];

    async Task<IReadOnlyList<ListEntry>> LoadEntriesAsync(CancellationToken cancellationToken) =>
        _entries = _records is null ? [] : await _records.GetListEntriesAsync(cancellationToken);

    [RelayCommand]
    Task RefreshEntries() => Entries.ReloadAsync(busyText: "Reading the block and allow lists…");

    [ObservableProperty]
    public partial bool IsAddOpen { get; set; }

    [RelayCommand]
    void ToggleAdd() => IsAddOpen = !IsAddOpen;

    // ---- Quarantine ---------------------------------------------------------------------------

    public static IReadOnlyList<TraceRange> QuarantineRanges { get; } =
        [new(1, "Last 24 Hours"), new(7, "Last 7 Days"), new(14, "Last 14 Days"), new(30, "Last 30 Days")];

    [ObservableProperty]
    public partial TraceRange QuarantineRange { get; set; } = QuarantineRanges[^1];

    partial void OnQuarantineRangeChanged(TraceRange value) => _ = Quarantine.ReloadAsync(busyText: QuarantineBusy);

    string QuarantineBusy => $"Reading quarantine ({QuarantineRange.Label.ToLowerInvariant()})…";

    async Task<IReadOnlyList<QuarantinedMessage>> LoadQuarantineAsync(CancellationToken cancellationToken) =>
        _records is null ? [] : await _records.GetQuarantineAsync(QuarantineRange.Days, cancellationToken);

    [RelayCommand]
    Task RefreshQuarantine() => Quarantine.ReloadAsync(busyText: QuarantineBusy);

    // ---- Message trace ------------------------------------------------------------------------

    public static IReadOnlyList<TraceRange> TraceRanges { get; } =
        [new(1, "1 Day"), new(2, "2 Days"), new(7, "7 Days"), new(TraceSearch.MaxDays, "10 Days (the Most per Search)")];

    [ObservableProperty]
    public partial DateTime TraceStart { get; set; }

    [ObservableProperty]
    public partial TraceRange TraceLength { get; set; } = TraceRanges[0];

    [ObservableProperty]
    public partial string TraceSender { get; set; } = "";

    [ObservableProperty]
    public partial string TraceRecipient { get; set; } = "";

    [ObservableProperty]
    public partial string TraceSubject { get; set; } = "";

    public static IReadOnlyList<string> TraceStatusChoices { get; } = ["Any Status", .. MailSecurityLists.TraceStatuses.Select(MailSecurityLists.StatusText)];

    [ObservableProperty]
    public partial string TraceStatus { get; set; } = "Any Status";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTraceNote))]
    public partial string? TraceNote { get; private set; }

    public bool HasTraceNote => TraceNote is not null;

    /// <summary>The oldest day message trace can search.</summary>
    public DateTime EarliestTraceDay => _now().Date.AddDays(-TraceSearch.HistoryDays);

    public DateTime LatestTraceDay => _now().Date;

    TraceSearch? _search;

    /// <summary>The search as entered, kept to the limits Exchange allows (10 days at a time, up to 90 days back).</summary>
    public TraceSearch CurrentSearch()
    {
        var start = new DateTimeOffset(TraceStart < EarliestTraceDay ? EarliestTraceDay : TraceStart);
        var end = start.AddDays(TraceLength.Days);
        if (end > _now())
            end = _now();
        var status = TraceStatus == TraceStatusChoices[0] ? null
            : MailSecurityLists.TraceStatuses.FirstOrDefault(s => MailSecurityLists.StatusText(s) == TraceStatus);
        return new TraceSearch(start, end, Blank(TraceSender), Blank(TraceRecipient), Blank(TraceSubject), status);
    }

    static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [RelayCommand]
    async Task SearchTrace()
    {
        _search = CurrentSearch();
        TraceNote = null;
        Trace.EmptyText = "No messages match this search.";
        await Trace.ReloadAsync(busyText: "Searching message trace… This can take up to a minute for a busy day.");
        if (Trace.Total >= TraceSearch.MaxResults)
            TraceNote = $"Showing the first {TraceSearch.MaxResults:N0} messages: narrow the search (a sender, recipient or shorter range) to see the rest.";
    }

    async Task<IReadOnlyList<TracedMessage>> LoadTraceAsync(CancellationToken cancellationToken) =>
        _records is null || _search is null ? [] : await _records.TraceAsync(_search, cancellationToken);
}
