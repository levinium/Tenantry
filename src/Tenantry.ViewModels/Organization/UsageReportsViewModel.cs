using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Organization;

/// <summary>A usage report's row: the JSON Microsoft sent, with a key.</summary>
public sealed record UsageRow(string Id, JsonElement Raw);

/// <summary>
/// Usage Reports (15b): Microsoft 365's activity reports for a chosen period, each as a standard
/// list whose columns come from the report itself, so search, sort, filters and CSV export all work.
/// </summary>
public sealed partial class UsageReportsViewModel : ObservableObject
{
    readonly IUsageReportRecords? _records;
    readonly IListStateStore? _listState;
    readonly Func<DateTimeOffset> _now;

    public UsageReportsViewModel(IUsageReportRecords? records, IListStateStore? listState, Func<DateTimeOffset> now)
    {
        _records = records;
        _listState = listState;
        _now = now;
        Report = UsageReports.All[0];
        Period = UsageReports.Periods[1];
    }

    public static IReadOnlyList<UsageReport> Reports => UsageReports.All;
    public static IReadOnlyList<UsagePeriod> Periods => UsageReports.Periods;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(About))]
    public partial UsageReport Report { get; set; }

    [ObservableProperty]
    public partial UsagePeriod Period { get; set; }

    public string About => Report.About;

    partial void OnReportChanged(UsageReport value) => Reread();
    partial void OnPeriodChanged(UsagePeriod value) => Reread();

    void Reread()
    {
        if (IsLoaded)
            _ = Load(force: true);
    }

    [ObservableProperty]
    public partial DataListViewModel? List { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    [ObservableProperty]
    public partial bool IsReading { get; private set; }

    public bool IsLoaded { get; private set; }

    /// <summary>Report housekeeping, the same on every row.</summary>
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "reportRefreshDate", "reportPeriod", "@odata.type", "details" };

    static readonly string[] PrimaryNames = ["displayName", "userPrincipalName", "siteUrl", "groupDisplayName", "ownerDisplayName", "ownerPrincipalName"];

    public async Task Load(bool force = false)
    {
        if (IsLoaded && !force)
            return;
        IsLoaded = true;
        IsReading = true;
        try
        {
            var read = _records is null ? new UsageReportRead([], "Usage reports aren't available for this tenant.") : await _records.GetUsageReportAsync(Report, Period);
            Note = read.Note ?? (read.Rows.Any(Concealed) ? "Names are hidden behind codes. To see them, turn off Hide Names in Reports on Report Privacy (Settings)." : null);
            var rows = read.Rows.Select((r, i) => new UsageRow(i.ToString(CultureInfo.InvariantCulture), r)).ToArray();
            var list = DataListViewModel.Create(Definition(Report, read.Rows), _ => Task.FromResult<IReadOnlyList<UsageRow>>(rows), _ => { }, _listState, _now);
            list.HasRecordPages = false;
            list.PrimaryAction = new ListPrimaryAction("Refresh", "ArrowSync", true, "Read the report from Microsoft again", () => _ = Load(force: true));
            list.EmptyText = "Microsoft has nothing for this period.";
            List = list;
            await list.LoadAsync();
        }
        finally
        {
            IsReading = false;
        }
    }

    [RelayCommand]
    Task Refresh() => Load(force: true);

    /// <summary>A hidden name is 32 hex characters in the name column.</summary>
    static bool Concealed(JsonElement r) => r.TryGetProperty("userPrincipalName", out var u) && u.GetString() is { Length: 32 } s && s.All(char.IsAsciiHexDigit);

    static string? Cell(JsonElement r, string name) => r.TryGetProperty(name, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.ToString(),
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(e => e.ToString())),
        _ => null,
    } : null;

    static double? Number(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static DateTimeOffset? Date(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>The list for a report, its fields and columns from the properties its rows have.</summary>
    internal static ListDefinition<UsageRow> Definition(UsageReport report, IReadOnlyList<JsonElement> rows)
    {
        var sample = rows.FirstOrDefault();
        var names = sample.ValueKind == JsonValueKind.Object ? sample.EnumerateObject().Select(p => p.Name).Where(n => !Skipped.Contains(n)).ToList() : [];
        var primary = PrimaryNames.FirstOrDefault(names.Contains) ?? names.FirstOrDefault() ?? "id";
        var second = primary == "displayName" && names.Contains("userPrincipalName") ? "userPrincipalName" : null;
        FieldKind KindOf(string n) => n.EndsWith("Date", StringComparison.OrdinalIgnoreCase) || n.EndsWith("DateTime", StringComparison.OrdinalIgnoreCase) ? FieldKind.Date
            : sample.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? FieldKind.Number
            : sample.TryGetProperty(n, out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False ? FieldKind.Choice : FieldKind.Text;
        string Label(string n) => Display.TitleCase(SettingsCatalog.Words(n));
        var fields = names.Select(n => KindOf(n) switch
        {
            FieldKind.Date => new Field<UsageRow> { Key = n, Label = Label(n), Kind = FieldKind.Date, Date = r => Date(r.Raw, n) },
            FieldKind.Number => new Field<UsageRow> { Key = n, Label = Label(n), Kind = FieldKind.Number, Number = r => Number(r.Raw, n) },
            var k => new Field<UsageRow> { Key = n, Label = Label(n), Kind = k, Searchable = k == FieldKind.Text, Text = r => Cell(r.Raw, n) },
        }).ToList();
        if (fields.Count == 0)
            fields.Add(new Field<UsageRow> { Key = "id", Label = "Row", Kind = FieldKind.Text, Text = r => r.Id });
        var columns = names.Where(n => n != second).Select((n, i) => n == primary
            ? new ColumnDef<UsageRow> { Key = n, Header = Label(n), Cell = CellKind.Primary, MinWidth = 220, DefaultWidth = 280, Text = r => Cell(r.Raw, n),
                Subtitle = second is null ? null : r => Cell(r.Raw, second), Initials = r => Display.Initials(Cell(r.Raw, n) ?? "?") }
            : KindOf(n) switch
            {
                FieldKind.Date => new ColumnDef<UsageRow> { Key = n, Header = Label(n), Cell = CellKind.Date, MinWidth = 120, DefaultWidth = 140, Date = r => Date(r.Raw, n), DefaultVisible = i < 12 },
                FieldKind.Number => new ColumnDef<UsageRow> { Key = n, Header = Label(n), Cell = CellKind.Number, MinWidth = 100, DefaultWidth = 130, Number = r => Number(r.Raw, n), DefaultVisible = i < 12 },
                _ => new ColumnDef<UsageRow> { Key = n, Header = Label(n), Cell = CellKind.Text, MinWidth = 110, DefaultWidth = 160, Text = r => Cell(r.Raw, n), DefaultVisible = i < 12 },
            }).OrderBy(c => c.Cell == CellKind.Primary ? 0 : 1).ToList();
        if (columns.Count == 0)
            columns.Add(new ColumnDef<UsageRow> { Key = "id", Header = "Row", Cell = CellKind.Primary, Text = r => r.Id, Initials = _ => "#" });
        return new ListDefinition<UsageRow>
        {
            Key = "usage-" + report.Key,
            Title = report.Title,
            Noun = "rows",
            Fields = fields,
            Columns = columns,
            Id = r => r.Id,
            Views = [new SavedView("all", "All", ListQuery.Empty with { Sort = [new SortLevel(primary, Descending: false)] }, BuiltIn: true, Pinned: true)],
            SearchHint = "Search names, addresses, sites…",
        };
    }
}
