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
        new("trace", "Message trace", "MailSearch"),
        new("rules", "Mail flow rules", "ArrowRouting", "Every mail flow rule in plain words, with a full editor for conditions, exceptions and actions."),
        new("lists", "Block and allow lists", "ShieldProhibited", "Blocked and allowed senders, domains, links and files."),
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
        foreach (var list in new[] { Quarantine, Trace })
        {
            list.PrimaryAction = null;
            list.HasRecordPages = false;
        }
        TraceStart = _now().Date.AddDays(-1);
        SelectedPage = Pages[0];
    }

    public override string Title => "Mail & security";

    public override bool CanClose => false;

    public DataListViewModel Quarantine { get; }

    public DataListViewModel Trace { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQuarantine), nameof(ShowTrace), nameof(IsPlannedPage))]
    public partial MailSecurityPage SelectedPage { get; set; }

    public bool ShowQuarantine => SelectedPage.Key == "quarantine";
    public bool ShowTrace => SelectedPage.Key == "trace";
    public bool IsPlannedPage => SelectedPage.IsPlanned;

    bool _quarantineRead;

    partial void OnSelectedPageChanged(MailSecurityPage value)
    {
        // Quarantine reads when first shown; trace waits for a search.
        if (value.Key == "quarantine" && !_quarantineRead)
        {
            _quarantineRead = true;
            _ = Quarantine.LoadAsync();
        }
    }

    // ---- Quarantine ---------------------------------------------------------------------------

    public static IReadOnlyList<TraceRange> QuarantineRanges { get; } =
        [new(1, "Last 24 hours"), new(7, "Last 7 days"), new(14, "Last 14 days"), new(30, "Last 30 days")];

    [ObservableProperty]
    public partial TraceRange QuarantineRange { get; set; } = QuarantineRanges[^1];

    partial void OnQuarantineRangeChanged(TraceRange value) => _ = Quarantine.ReloadAsync();

    async Task<IReadOnlyList<QuarantinedMessage>> LoadQuarantineAsync(CancellationToken cancellationToken) =>
        _records is null ? [] : await _records.GetQuarantineAsync(QuarantineRange.Days, cancellationToken);

    [RelayCommand]
    Task RefreshQuarantine() => Quarantine.ReloadAsync();

    // ---- Message trace ------------------------------------------------------------------------

    public static IReadOnlyList<TraceRange> TraceRanges { get; } =
        [new(1, "1 day"), new(2, "2 days"), new(7, "7 days"), new(TraceSearch.MaxDays, "10 days (the most per search)")];

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

    public static IReadOnlyList<string> TraceStatusChoices { get; } = ["Any status", .. MailSecurityLists.TraceStatuses.Select(MailSecurityLists.StatusText)];

    [ObservableProperty]
    public partial string TraceStatus { get; set; } = "Any status";

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
        await Trace.ReloadAsync();
        if (Trace.Total >= TraceSearch.MaxResults)
            TraceNote = $"Showing the first {TraceSearch.MaxResults:N0} messages: narrow the search (a sender, recipient or shorter range) to see the rest.";
    }

    async Task<IReadOnlyList<TracedMessage>> LoadTraceAsync(CancellationToken cancellationToken) =>
        _records is null || _search is null ? [] : await _records.TraceAsync(_search, cancellationToken);
}
