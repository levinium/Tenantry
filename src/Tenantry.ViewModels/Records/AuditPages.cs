using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;
using F = Tenantry.Core.Directory.AuditFields;

namespace Tenantry.ViewModels.Records;

/// <summary>A search in the list: its name, dates, filters in words and where it's got to.</summary>
public sealed record AuditSearchRow(AuditSearch Search)
{
    public string Name => Search.Name;
    public string Range => $"{Day(Search.Start)} to {Day(Search.End)}";
    public string StatusText => Search.StatusText;
    public BadgeKind StatusKind => Search.Status switch { "succeeded" => BadgeKind.Success, "failed" or "cancelled" => BadgeKind.Danger, _ => BadgeKind.Info };
    public bool IsWaiting => !Search.IsFinished;
    public string? CountText => Search.IsDone ? Search.ApproximateCount is { } n ? $"{n:N0} records" + (Search.LimitExceeded ? " (more than Purview returns)" : "") : null : null;
    public bool HasCount => CountText is not null;

    public string Filters
    {
        get
        {
            var f = Search.Filters;
            var parts = new List<string>();
            if (f.Users.Count > 0)
                parts.Add($"People: {string.Join(", ", f.Users)}");
            if (f.Operations.Count > 0)
                parts.Add($"Activities: {string.Join(", ", f.Operations)}");
            if (f.RecordTypes.Count > 0)
                parts.Add($"Record types: {string.Join(", ", f.RecordTypes.Select(AuditRecordTypes.Label))}");
            if (!string.IsNullOrWhiteSpace(f.Keyword))
                parts.Add($"Keyword: {f.Keyword}");
            if (f.IpAddresses is { Count: > 0 } ips)
                parts.Add($"IP addresses: {string.Join(", ", ips)}");
            if (f.ObjectIds is { Count: > 0 } objects)
                parts.Add($"Objects: {string.Join(", ", objects)}");
            return parts.Count == 0 ? "Everything" : string.Join(" · ", parts);
        }
    }

    static string Day(DateTimeOffset? when) => when is { } w ? w.ToLocalTime().ToString("d MMM yyyy h:mm tt", CultureInfo.CurrentCulture) : "?";
}

/// <summary>An audit record in the preview pane: who, what, where, and the whole record.</summary>
public sealed class AuditRecordPreviewViewModel(AuditRecord record)
{
    public string Title => record.Operation;
    public string? Subtitle => record.User;

    public IReadOnlyList<SummaryLine> Facts =>
    [
        new("When", record.When is { } w ? w.ToLocalTime().ToString("d MMM yyyy h:mm:ss tt", CultureInfo.CurrentCulture) : "Unknown"),
        new("Who", record.User ?? "Unknown"),
        .. record.Service is { } s ? [new SummaryLine("Service", s)] : Array.Empty<SummaryLine>(),
        .. record.RecordType is { } t ? [new SummaryLine("Record Type", AuditRecordTypes.Label(t))] : Array.Empty<SummaryLine>(),
        .. record.ObjectId is { Length: > 0 } o ? [new SummaryLine("Object", o)] : Array.Empty<SummaryLine>(),
        .. record.ClientIp is { Length: > 0 } ip ? [new SummaryLine("IP Address", ip)] : Array.Empty<SummaryLine>(),
    ];

    /// <summary>The whole record, indented, to read or copy.</summary>
    public string Json
    {
        get
        {
            try
            {
                return JsonNode.Parse(record.Json)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? record.Json;
            }
            catch (JsonException)
            {
                return record.Json;
            }
        }
    }
}

/// <summary>
/// Audit Log (8e): search the unified audit log by dates, people, activities, record types, keyword,
/// addresses and objects. Purview runs each search in the background (it can wait queued for many
/// minutes), so searches are listed with their state and checked every so often; a finished one's
/// records open in a list with the usual filters, sorting and CSV export.
/// </summary>
public sealed partial class AuditLogViewModel : ObservableObject
{
    /// <summary>Records read from one search at most (Purview returns up to 50,000).</summary>
    public const int RecordLimit = 10_000;

    readonly IAuditRecords? _records;
    readonly IDirectoryIndex _index;
    readonly Func<DateTimeOffset> _now;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    IReadOnlyList<AuditRecord> _shown = [];
    bool _read;
    bool _polling;

    public static TimeSpan CheckEvery { get; set; } = TimeSpan.FromSeconds(20);

    public AuditLogViewModel(IAuditRecords? records, IDirectoryIndex? index, IListStateStore? listState, Func<DateTimeOffset> now,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _records = records;
        _index = index ?? EmptyIndex.Instance;
        _now = now;
        _delay = delay ?? Task.Delay;
        People = new ChipPicker("Add a person: type a name", () => GroupAccess.People(_index), () => { });
        RecordTypes = [.. AuditRecordTypes.Common.Select(t => new CheckOption(t.Value, t.Label, () => { }))];
        Results = DataListViewModel.Create(Definition, _ => Task.FromResult(_shown), _ => { }, listState, now);
        Results.HasRecordPages = false;
        Results.EmptyText = "Choose a finished search to see its records.";
        SetRange(Ranges[1]);
    }

    static ListDefinition<AuditRecord> Definition => new()
    {
        Key = "auditrecords",
        Title = "Records",
        Noun = "records",
        Fields = F.All,
        Id = r => r.Id,
        Views = [new SavedView("all", "All Records", ListQuery.Empty with { Sort = [new SortLevel(F.When, Descending: true)] }, BuiltIn: true, Pinned: true)],
        SearchHint = "Search who, activity, object and address… or try activity:FileAccessed",
        Preview = r => new AuditRecordPreviewViewModel(r),
        Columns =
        [
            new() { Key = F.Operation, Header = "Activity", Cell = CellKind.Primary, MinWidth = 220, DefaultWidth = 260, Text = r => r.Operation, Subtitle = r => r.User ?? "",
                Initials = r => Display.Initials(r.User ?? r.Operation) },
            new() { Key = F.When, Header = "When", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = r => r.When },
            new() { Key = F.Service, Header = "Service", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 150, Text = r => r.Service ?? "" },
            new() { Key = F.Object, Header = "Object", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 260, Text = r => r.ObjectId ?? "" },
            new() { Key = F.Ip, Header = "IP Address", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 150, Text = r => r.ClientIp ?? "" },
            new() { Key = F.RecordType, Header = "Record Type", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 200, DefaultVisible = false,
                Text = r => AuditRecordTypes.Label(r.RecordType) },
        ],
    };

    // ---- The form ---------------------------------------------------------------------------------

    public static IReadOnlyList<EditorChoice> Ranges { get; } =
    [
        new("1", "Last 24 Hours", ""),
        new("7", "Last 7 Days", ""),
        new("30", "Last 30 Days", ""),
        new("90", "Last 90 Days", ""),
    ];

    [RelayCommand]
    void SetRange(EditorChoice range)
    {
        var end = _now().ToLocalTime();
        EndDate = end.Date;
        StartDate = end.Date.AddDays(1 - int.Parse(range.Value, CultureInfo.InvariantCulture));
    }

    [ObservableProperty]
    public partial DateTime? StartDate { get; set; }

    [ObservableProperty]
    public partial DateTime? EndDate { get; set; }

    public ChipPicker People { get; }

    [ObservableProperty]
    public partial string Activities { get; set; } = "";

    public IReadOnlyList<CheckOption> RecordTypes { get; }

    [ObservableProperty]
    public partial string Keyword { get; set; } = "";

    [ObservableProperty]
    public partial string IpAddresses { get; set; } = "";

    [ObservableProperty]
    public partial string ObjectIds { get; set; } = "";

    [ObservableProperty]
    public partial string SearchName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    public partial bool IsStarting { get; private set; }

    static IReadOnlyList<string> Split(string text) =>
        [.. text.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The form as a search, with what Purview would refuse.</summary>
    public (AuditSearchRequest Request, IReadOnlyList<string> Problems) Request()
    {
        var problems = new List<string>();
        if (StartDate is not { } start || EndDate is not { } end)
        {
            problems.Add("Choose the dates to search.");
            return (null!, problems);
        }
        // The end day is included: the search runs to the end of it (or to now, today).
        var from = new DateTimeOffset(start.Date);
        var to = new DateTimeOffset(end.Date.AddDays(1));
        if (to > _now())
            to = _now();
        if (from >= to)
            problems.Add("The start must be before the end.");
        if (from < _now().AddDays(-180))
            problems.Add("The audit log keeps 180 days (longer only with audit retention policies): start within the last 180 days.");
        var users = People.Items.Select(p => p.Address ?? p.Name).ToArray();
        var name = SearchName.Trim().Length > 0 ? SearchName.Trim()
            : $"{(users.Length == 1 ? People.Items[0].Name : users.Length > 1 ? $"{users.Length} people" : "Everyone")}, {start:d MMM} to {end:d MMM yyyy}";
        return (new AuditSearchRequest(name, from, to, users, Split(Activities), [.. RecordTypes.Where(r => r.IsChecked).Select(r => r.Value)],
            Keyword.Trim().Length > 0 ? Keyword.Trim() : null, Split(IpAddresses), Split(ObjectIds)), problems);
    }

    [RelayCommand]
    async Task Search()
    {
        if (_records is null)
            return;
        var (request, problems) = Request();
        Problems = problems;
        if (problems.Count > 0)
            return;
        IsStarting = true;
        try
        {
            var search = await _records.StartSearchAsync(request);
            var row = new AuditSearchRow(search);
            Searches.Insert(0, row);
            SelectedSearch = row;
            SearchName = "";
            Poll();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Problems = [$"Purview didn't start the search: {Friendly.Message(e)}"];
        }
        finally
        {
            IsStarting = false;
        }
    }

    // ---- Searches and results ------------------------------------------------------------------

    public ObservableCollection<AuditSearchRow> Searches { get; } = [];

    public bool HasNoSearches => Searches.Count == 0 && _read;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedWaiting))]
    public partial AuditSearchRow? SelectedSearch { get; set; }

    public bool HasSelection => SelectedSearch is not null;

    /// <summary>The chosen search is still queued or running.</summary>
    public bool SelectedWaiting => SelectedSearch?.IsWaiting == true;

    partial void OnSelectedSearchChanged(AuditSearchRow? value) => _ = ShowAsync(value);

    public DataListViewModel Results { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultsNote))]
    public partial string? ResultsNote { get; private set; }

    public bool HasResultsNote => ResultsNote is not null;

    public void Shown()
    {
        if (!_read)
            _ = ReadAsync();
    }

    [RelayCommand]
    async Task ReadAsync()
    {
        _read = true;
        if (_records is null)
        {
            Note = "The audit log can't be searched for this tenant.";
            return;
        }
        IsLoading = true;
        Note = null;
        try
        {
            var searches = await _records.GetSearchesAsync();
            Searches.Clear();
            foreach (var s in searches)
                Searches.Add(new AuditSearchRow(s));
            OnPropertyChanged(nameof(HasNoSearches));
            Poll();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note = $"Couldn't read the audit log searches: {Friendly.Message(e)} Searching needs the Audit Logs role in Purview (or Compliance Administrator).";
        }
        finally
        {
            IsLoading = false;
        }
    }

    async Task ShowAsync(AuditSearchRow? row)
    {
        _shown = [];
        ResultsNote = row switch
        {
            null => null,
            { Search.IsDone: true } => null,
            { IsWaiting: true } => "Purview is still running this search. It can take several minutes; it's checked every so often, so you can leave this page open.",
            _ => "This search didn't finish. Run it again.",
        };
        if (row is { Search.IsDone: true } && _records is not null)
        {
            try
            {
                _shown = await _records.GetRecordsAsync(row.Search.Id, RecordLimit);
                if (_shown.Count >= RecordLimit)
                    ResultsNote = $"Showing the first {RecordLimit:N0} records: narrow the search to see the rest.";
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ResultsNote = $"Couldn't read its records: {Friendly.Message(e)}";
            }
        }
        await Results.ReloadAsync(busyText: "Reading the records…");
    }

    /// <summary>Checks unfinished searches every so often until they finish; one loop at a time.</summary>
    void Poll()
    {
        if (_polling || _records is null || Searches.All(s => !s.IsWaiting))
            return;
        _polling = true;
        _ = PollAsync();
    }

    async Task PollAsync()
    {
        try
        {
            while (Searches.Any(s => s.IsWaiting))
            {
                await _delay(CheckEvery, CancellationToken.None);
                foreach (var row in Searches.Where(s => s.IsWaiting).ToArray())
                {
                    AuditSearch fresh;
                    try
                    {
                        fresh = await _records!.GetSearchAsync(row.Search.Id);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        continue;
                    }
                    if (fresh.Status == row.Search.Status)
                        continue;
                    var updated = new AuditSearchRow(fresh);
                    var at = Searches.IndexOf(row);
                    var wasSelected = ReferenceEquals(SelectedSearch, row);
                    Searches[at] = updated;
                    if (wasSelected)
                        SelectedSearch = updated;
                }
            }
        }
        finally
        {
            _polling = false;
        }
    }
}
