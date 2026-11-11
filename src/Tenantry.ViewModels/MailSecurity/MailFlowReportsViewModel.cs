using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>One coloured piece of a bar: a kind of mail (the view colours it by <see cref="Kind"/>) and how many.</summary>
public sealed record BarSegment(string Kind, double Value);

/// <summary>A total at the top of the page: "Spam: 1,811".</summary>
public sealed record MailFlowTile(string Title, string CountText, string Explanation, string? Kind);

/// <summary>A day (or a week, over 90 days) of incoming mail, as a stacked bar.</summary>
public sealed record MailFlowBar(string Label, IReadOnlyList<BarSegment> Segments, double Maximum, string TotalText, string Tip);

/// <summary>A key to the bar colours.</summary>
public sealed record MailFlowLegend(string Kind, string Title, string Explanation);

/// <summary>One entry of a top list, with a bar for its share of the biggest.</summary>
public sealed partial class MailFlowTopRow(MailFlowTop top, double fraction, Action<string>? trace) : ObservableObject
{
    public string Name => top.Name;
    public string CountText => top.Count.ToString("N0", CultureInfo.CurrentCulture);
    public double Fraction { get; } = fraction;
    public bool CanTrace => trace is not null;

    [RelayCommand]
    void Trace() => trace?.Invoke(top.Name);
}

/// <summary>A top list (senders, spam targets, malware names, rule hits…) as a card: the first ten, or all on request.</summary>
public sealed partial class MailFlowTopList(string title, string explanation, string traceTip) : ObservableObject
{
    public string Title { get; } = title;
    public string Explanation { get; } = explanation;
    public string TraceTip { get; } = traceTip;

    IReadOnlyList<MailFlowTopRow> _all = [];

    public ObservableCollection<MailFlowTopRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllText))]
    public partial bool ShowAll { get; set; }

    partial void OnShowAllChanged(bool value) => Fill();

    public const int Shown = 10;

    public bool HasMore => _all.Count > Shown;

    public string ShowAllText => ShowAll ? "Show Fewer" : $"Show All {_all.Count}";

    internal void Set(IReadOnlyList<MailFlowTop> top, Action<string>? trace, string? note)
    {
        var biggest = top.Count == 0 ? 1 : Math.Max(1, top.Max(t => t.Count));
        _all = [.. top.Select(t => new MailFlowTopRow(t, (double)t.Count / biggest, trace))];
        Note = note ?? (top.Count == 0 ? "None in this period." : null);
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(ShowAllText));
        Fill();
    }

    void Fill()
    {
        Rows.Clear();
        foreach (var row in ShowAll ? _all : _all.Take(Shown))
            Rows.Add(row);
    }
}

/// <summary>One line of how threats were caught: "Phishing: URL detonation, 120".</summary>
public sealed record ThreatCatchRow(string Technology, string CountText, double Fraction);

/// <summary>A verdict's lines, as a group: Phishing, Spam, Malware…</summary>
public sealed record ThreatCatchGroup(string Title, string TotalText, IReadOnlyList<ThreatCatchRow> Rows);

/// <summary>
/// Mail Flow Reports: how much mail came and went over the last 7, 30 or 90 days, how much of it
/// was spam, phishing or malware, a bar per day, the top senders and recipients (each can be traced),
/// the malware seen, how often each mail flow rule acted, and (from Defender) what caught threats.
/// Read only: nothing here changes the tenant.
/// </summary>
public sealed partial class MailFlowReportsViewModel : LivePage
{
    readonly IMailFlowReports? _records;
    readonly Action<string>? _traceFrom;
    readonly Action<string>? _traceTo;
    readonly Func<DateTimeOffset> _now;

    /// <param name="traceFrom">Message trace for everything from an address; null where there's no trace page.</param>
    /// <param name="traceTo">Message trace for everything to an address.</param>
    public MailFlowReportsViewModel(IMailFlowReports? records, Action<string>? traceFrom, Action<string>? traceTo, Func<DateTimeOffset>? now = null)
        : base(null, Availability.Yes)
    {
        _records = records;
        _traceFrom = traceFrom;
        _traceTo = traceTo;
        _now = now ?? (() => DateTimeOffset.Now);
        TopLists = [.. MailFlowReport.TopLists.Select(t => new MailFlowTopList(t.Title, t.Explanation,
            t.IsAddress ? (t.Category == "TopMailSender" ? "Message trace: everything from this address" : "Message trace: everything to this address") : ""))];
    }

    public static IReadOnlyList<TraceRange> Ranges { get; } = [new(7, "Last 7 Days"), new(30, "Last 30 Days"), new(MailFlowReport.HistoryDays, "Last 90 Days")];

    [ObservableProperty]
    public partial TraceRange Range { get; set; } = Ranges[0];

    partial void OnRangeChanged(TraceRange value)
    {
        if (IsLoaded)
            _ = Refresh();
    }

    public ObservableCollection<MailFlowTile> Tiles { get; } = [];

    public ObservableCollection<MailFlowBar> Bars { get; } = [];

    public static IReadOnlyList<MailFlowLegend> Legend { get; } = [.. MailFlowReport.Kinds.Select(k => new MailFlowLegend(k.Key, k.Title, k.Explanation))];

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string BarsHeading { get; private set; } = "Incoming Mail by Day";

    public IReadOnlyList<MailFlowTopList> TopLists { get; }

    public ObservableCollection<ThreatCatchGroup> Threats { get; } = [];

    [ObservableProperty]
    public partial bool ThreatsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThreatsNote))]
    public partial string? ThreatsNote { get; private set; }

    public bool HasThreatsNote => ThreatsNote is not null;

    public string ThreatsHeading => $"How Threats Were Caught: Last {MailFlowReport.ThreatDays} Days";

    protected override bool Concerns(ChangeOp change) => false;

    int _generation;

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_records is null)
            return;
        var generation = ++_generation;
        var days = Range.Days;
        var tops = MailFlowReport.TopLists.Select(async t =>
        {
            try
            {
                return (t.Category, top: await _records.GetTopTrafficAsync(t.Category, days, cancellationToken), note: (string?)null);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return (t.Category, top: (IReadOnlyList<MailFlowTop>)[], note: (string?)$"Couldn't read this: {Friendly.Message(e)}");
            }
        }).ToArray();
        var counts = await _records.GetMailFlowStatusAsync(days, cancellationToken);
        if (generation != _generation)
            return;
        Fill(counts, days);
        var read = await Task.WhenAll(tops);
        if (generation != _generation)
            return;
        for (var i = 0; i < read.Length; i++)
        {
            var (category, top, note) = read[i];
            var trace = MailFlowReport.TopLists[i].IsAddress ? category == "TopMailSender" ? _traceFrom : _traceTo : null;
            TopLists[i].Set(top, trace, note);
        }
        if (!_threatsRead)
            _ = ReadThreatsAsync();
    }

    bool _threatsRead;

    /// <summary>Defender's breakdown, read after the rest because it's slow; always the last week.</summary>
    async Task ReadThreatsAsync()
    {
        if (_records is null || ThreatsLoading)
            return;
        ThreatsLoading = true;
        ThreatsNote = null;
        try
        {
            var catches = await _records.GetThreatCatchesAsync(MailFlowReport.ThreatDays);
            _threatsRead = true;
            Threats.Clear();
            foreach (var g in catches.Where(c => c.Direction == "Inbound" && c.Verdict is not ("NotSpam" or "Allow"))
                         .GroupBy(c => c.Verdict).OrderByDescending(g => g.Sum(c => c.Count)))
            {
                var biggest = Math.Max(1, g.Max(c => c.Count));
                Threats.Add(new ThreatCatchGroup(MailFlowReport.VerdictText(g.Key), g.Sum(c => c.Count).ToString("N0", CultureInfo.CurrentCulture),
                    [.. g.OrderByDescending(c => c.Count).Select(c => new ThreatCatchRow(c.Technology, c.Count.ToString("N0", CultureInfo.CurrentCulture), (double)c.Count / biggest))]));
            }
            if (Threats.Count == 0)
                ThreatsNote = "Defender caught nothing in the last week.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ThreatsNote = $"Couldn't read Defender's breakdown: {Friendly.Message(e)}";
        }
        finally
        {
            ThreatsLoading = false;
        }
    }

    [RelayCommand]
    Task RefreshThreats() => ReadThreatsAsync();

    void Fill(IReadOnlyList<MailFlowCount> counts, int days)
    {
        string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
        Tiles.Clear();
        Tiles.Add(new("Received", N(MailFlowReport.Total(counts, "GoodMail", "Inbound")), "From outside, delivered to mailboxes.", "GoodMail"));
        Tiles.Add(new("Sent Outside", N(MailFlowReport.Total(counts, "GoodMail", "Outbound")), "From your people to other organizations.", null));
        Tiles.Add(new("Internal", N(MailFlowReport.Total(counts, "GoodMail", "IntraOrg")), "Between your own people.", null));
        foreach (var kind in MailFlowReport.Kinds.Skip(1))
            Tiles.Add(new(kind.Title, N(MailFlowReport.Total(counts, kind.Key, "Inbound")), kind.Explanation, kind.Key));
        var outboundSpam = MailFlowReport.Total(counts, "SpamDetections", "Outbound");
        if (outboundSpam > 0)
            Tiles.Add(new("Spam Sent Out", N(outboundSpam), "Mail your people sent that Microsoft judged spam: a sign someone's account may be misused.", "SpamDetections"));
        var unwanted = MailFlowReport.UnwantedPercent(counts);
        Summary = counts.Count == 0 ? "No mail in this period." : $"{unwanted}% of the mail sent to your organization was unwanted: spam, phishing, malware, or turned away at the edge.";

        // A bar per day; per week over 90 days, so the chart stays readable.
        var today = DateOnly.FromDateTime(_now().UtcDateTime);
        var byDay = MailFlowReport.IncomingByDay(counts, today.AddDays(-days + 1), today);
        var weekly = days > 31;
        BarsHeading = weekly ? "Incoming Mail by Week" : "Incoming Mail by Day";
        var buckets = weekly
            ? byDay.GroupBy(d => d.Day.AddDays(-(((int)d.Day.DayOfWeek + 6) % 7))).Select(g =>
                (Label: $"{g.First().Day.ToString("d MMM", CultureInfo.CurrentCulture)} – {g.Last().Day.ToString("d MMM", CultureInfo.CurrentCulture)}",
                 Counts: g.SelectMany(d => d.Counts).GroupBy(c => c.Kind).Select(k => (Kind: k.Key, Count: k.Sum(c => c.Count))).ToArray()))
            : byDay.Select(d => (Label: d.Day.ToString("ddd d MMM", CultureInfo.CurrentCulture), Counts: d.Counts.ToArray()));
        var list = buckets.ToList();
        var maximum = Math.Max(1, list.Count == 0 ? 1 : list.Max(b => b.Counts.Sum(c => c.Count)));
        Bars.Clear();
        foreach (var (label, bucket) in Enumerable.Reverse(list))
            Bars.Add(new MailFlowBar(label, [.. bucket.Select(c => new BarSegment(c.Kind, c.Count))], maximum, N(bucket.Sum(c => c.Count)),
                string.Join(Environment.NewLine, bucket.Where(c => c.Count > 0).Select(c => $"{MailFlowReport.KindTitle(c.Kind)}: {N(c.Count)}"))));
    }
}
