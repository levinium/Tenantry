using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>A trace report in the list: what it covers, how far along it is, and Open when done.</summary>
public sealed partial class TraceReportRow(TraceReport report, Func<TraceReportRow, Task> cancel) : ObservableObject
{
    public TraceReport Report { get; } = report;

    public string Title => Report.Title;

    public string Kind => Report.KindLabel;

    /// <summary>"1 Oct to 30 Oct · from billing@…".</summary>
    public string Covers
    {
        get
        {
            var range = Report.Start is { } s && Report.End is { } e ? $"{s.ToLocalTime():d MMM} to {e.ToLocalTime():d MMM yyyy}" : "";
            var who = new List<string>();
            if (Report.Senders.Count > 0)
                who.Add("from " + Names(Report.Senders));
            if (Report.Recipients.Count > 0)
                who.Add("to " + Names(Report.Recipients));
            return string.Join(" · ", new[] { range, string.Join(", ", who) }.Where(x => x.Length > 0));
        }
    }

    static string Names(IReadOnlyList<string> addresses) => addresses.Count <= 2 ? string.Join(", ", addresses) : $"{addresses[0]} and {addresses.Count - 1} more";

    public string StatusText => Report.Status switch
    {
        "Done" => Report.Rows == 1 ? "Ready: 1 row" : $"Ready: {Report.Rows:N0} rows",
        "NotStarted" or "Queued" => "Waiting to start",
        "InProgress" => "Being built",
        "Cancelled" => "Cancelled",
        "Failed" => "Failed",
        _ => Report.Status,
    };

    public BadgeKind StatusKind => Report.Status switch
    {
        "Done" => BadgeKind.Success,
        "Failed" => BadgeKind.Danger,
        "Cancelled" => BadgeKind.Neutral,
        _ => BadgeKind.Info,
    };

    public string SubmittedText => Report.Submitted is { } at ? $"Asked for {Display.Relative(at, DateTimeOffset.Now)}" : "";

    public string? Error => Report.Error;

    public bool HasError => Report.Error is not null;

    public bool CanOpen => Report.IsDone && Report.FileUrl is not null;

    public bool CanCancel => Report.IsWaiting;

    [RelayCommand]
    void Open()
    {
        if (Report.FileUrl is { } url)
            Platform.OpenUrl(url);
    }

    [RelayCommand]
    Task Cancel() => cancel(this);
}

/// <summary>
/// Trace reports (Exchange's historical searches): up to 90 days and 100,000 rows each, built in
/// the background (minutes to hours) as a CSV to download, optionally emailed when ready. Asking
/// for one is a read, like a search, so it doesn't go through Pending Changes. Exchange keeps them
/// 10 days and allows 250 a day.
/// </summary>
public sealed partial class TraceReportsViewModel : ObservableObject
{
    readonly IMailSecurity? _records;
    readonly Func<DateTimeOffset> _now;

    public TraceReportsViewModel(IMailSecurity? records, string? account, Func<DateTimeOffset>? now = null)
    {
        _records = records;
        _now = now ?? (() => DateTimeOffset.Now);
        NotifyAddress = account ?? "";
        Kind = TraceReportKind.All[0];
        StartDay = _now().Date.AddDays(-30);
        EndDay = _now().Date;
    }

    public static IReadOnlyList<TraceReportKind> Kinds => TraceReportKind.All;

    public ObservableCollection<TraceReportRow> Reports { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReports), nameof(IsEmpty))]
    public partial bool IsLoaded { get; private set; }

    public bool HasReports => Reports.Count > 0;

    public bool IsEmpty => IsLoaded && Reports.Count == 0;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string? LoadError { get; private set; }

    public bool HasLoadError => LoadError is not null;

    // ---- Asking for one -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindDescription))]
    public partial TraceReportKind Kind { get; set; }

    public string KindDescription => Kind.Description;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial DateTime StartDay { get; set; }

    [ObservableProperty]
    public partial DateTime EndDay { get; set; }

    public DateTime EarliestDay => _now().Date.AddDays(-TraceSearch.HistoryDays);

    public DateTime LatestDay => _now().Date;

    /// <summary>Senders, one per line or separated by commas or semicolons.</summary>
    [ObservableProperty]
    public partial string Senders { get; set; } = "";

    [ObservableProperty]
    public partial string Recipients { get; set; } = "";

    /// <summary>Who to email when it's ready (an address in the organization); blank for nobody.</summary>
    [ObservableProperty]
    public partial string NotifyAddress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartNote))]
    public partial string? StartNote { get; private set; }

    public bool HasStartNote => StartNote is not null;

    [ObservableProperty]
    public partial bool IsStarting { get; private set; }

    static IReadOnlyList<string> Addresses(string text) =>
        [.. text.Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The report as entered: the whole of the last day included (Exchange reads a date alone as midnight).</summary>
    public TraceReportRequest Request()
    {
        var start = new DateTimeOffset(StartDay.Date);
        var end = new DateTimeOffset(EndDay.Date.AddDays(1));
        if (end > _now())
            end = _now();
        return new TraceReportRequest(Title.Trim().Length > 0 ? Title.Trim() : $"{Kind.Label}, {StartDay:d MMM} to {EndDay:d MMM}", Kind.Type, start, end,
            Addresses(Senders), Addresses(Recipients), string.IsNullOrWhiteSpace(NotifyAddress) ? null : NotifyAddress.Trim());
    }

    [RelayCommand]
    async Task Start()
    {
        if (_records is null || IsStarting)
            return;
        var request = Request();
        Problems = request.Problems(_now());
        StartNote = null;
        if (HasProblems)
            return;
        IsStarting = true;
        try
        {
            var report = await _records.StartTraceReportAsync(request);
            Reports.Insert(0, new TraceReportRow(report, CancelAsync));
            OnPropertyChanged(nameof(HasReports));
            OnPropertyChanged(nameof(IsEmpty));
            StartNote = request.NotifyAddress is { } to
                ? $"Asked for. Exchange builds it in the background (usually within a few hours) and emails {to} when it's ready."
                : "Asked for. Exchange builds it in the background (usually within a few hours); it shows as Ready here.";
            Title = "";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            StartNote = $"Exchange didn't start it: {Friendly.Message(e)}";
        }
        finally
        {
            IsStarting = false;
        }
    }

    // ---- The list -----------------------------------------------------------------------------

    [RelayCommand]
    public async Task Refresh()
    {
        if (_records is null || IsLoading)
            return;
        IsLoading = true;
        LoadError = null;
        try
        {
            var reports = await _records.GetTraceReportsAsync();
            Reports.Clear();
            foreach (var r in reports)
                Reports.Add(new TraceReportRow(r, CancelAsync));
            IsLoaded = true;
            OnPropertyChanged(nameof(HasReports));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(UsedToday));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read the reports: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>"3 of 250 asked for today."</summary>
    public string UsedToday
    {
        get
        {
            var since = _now().AddDays(-1);
            var used = Reports.Count(r => r.Report.Submitted >= since);
            return $"{used:N0} of {TraceReport.DailyLimit} asked for in the last day (Exchange's limit; cancelled ones count). Reports are kept 10 days.";
        }
    }

    async Task CancelAsync(TraceReportRow row)
    {
        if (_records is null)
            return;
        try
        {
            await _records.CancelTraceReportAsync(row.Report.JobId);
            await Refresh();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't cancel it: {Friendly.Message(e)}";
        }
    }
}
