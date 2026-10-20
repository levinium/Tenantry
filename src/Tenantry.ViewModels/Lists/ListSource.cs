using Tenantry.Core.Lists;

namespace Tenantry.ViewModels.Lists;

/// <summary>A menu entry for a row's More button and right-click menu.</summary>
public sealed record RowMenuItem(string Label, string Icon, Action? Run = null, string? CopyText = null, bool IsSeparator = false)
{
    public static readonly RowMenuItem Separator = new("", "", IsSeparator: true);
}

/// <summary>The typed half of a list, hidden behind object so the view model and XAML stay non-generic.</summary>
internal interface IListSource
{
    string Key { get; }
    string Title { get; }
    string Noun { get; }
    string SearchHint { get; }
    IReadOnlyList<FieldOption> FieldOptions { get; }
    IReadOnlyList<SavedView> BuiltInViews { get; }
    bool HasPreview { get; }

    Task LoadAsync(CancellationToken cancellationToken);
    int Total { get; }
    IReadOnlyList<object> Run(ListQuery query, DateTimeOffset now);
    int Count(ListQuery query, DateTimeOffset now);
    IReadOnlyList<FacetValue> Facet(ListQuery query, string field, DateTimeOffset now);
    SearchParse ParseSearch(string text);
    string Describe(Condition condition);
    IReadOnlyList<ColumnViewModel> BuildColumns(DateTimeOffset now);
    string GroupKey(object row, string field);
    string Id(object row);
    object? Preview(object row);
    void Open(object row);
    IReadOnlyList<RowMenuItem> Actions(object row);
}

internal sealed class ListSource<T>(ListDefinition<T> definition, Func<CancellationToken, Task<IReadOnlyList<T>>> load, Action<T> open) : IListSource
    where T : notnull
{
    readonly ListEngine<T> _engine = new(definition.Fields);
    IReadOnlyList<Prepared<T>> _rows = [];

    public string Key => definition.Key;
    public string Title => definition.Title;
    public string Noun => definition.Noun;
    public string SearchHint => definition.SearchHint;
    public bool HasPreview => definition.Preview is not null;
    public IReadOnlyList<SavedView> BuiltInViews => definition.Views;

    public IReadOnlyList<FieldOption> FieldOptions { get; } =
        definition.Fields.Select(f => new FieldOption(f.Key, f.Label, f.Kind)).ToArray();

    public int Total => _rows.Count;

    public async Task LoadAsync(CancellationToken cancellationToken) =>
        _rows = _engine.Prepare(await load(cancellationToken));

    public IReadOnlyList<object> Run(ListQuery query, DateTimeOffset now) =>
        _engine.Run(_rows, query, now).Cast<object>().ToArray();

    public int Count(ListQuery query, DateTimeOffset now) => _engine.Run(_rows, query with { Sort = [], GroupBy = null }, now).Count;

    public IReadOnlyList<FacetValue> Facet(ListQuery query, string field, DateTimeOffset now) => _engine.Facet(_rows, query, field, now);

    public SearchParse ParseSearch(string text) => SearchSyntax.Parse(text, definition.Fields);

    public string Describe(Condition condition) => ConditionText.Describe(condition, _engine);

    public string GroupKey(object row, string field) =>
        _engine.Field(field) is { } f
            ? f.IsChoice ? f.ChoiceValues((T)row)[0] : f.Display((T)row) is { Length: > 0 } d ? d : f.EmptyLabel
            : "";

    public string Id(object row) => definition.Id((T)row);

    public object? Preview(object row) => definition.Preview?.Invoke((T)row);

    public void Open(object row) => open((T)row);

    public IReadOnlyList<RowMenuItem> Actions(object row) =>
        definition.Actions.Select(a => new RowMenuItem(a.Label, a.Icon, () => a.Run((T)row))).ToArray();

    public IReadOnlyList<ColumnViewModel> BuildColumns(DateTimeOffset now) => definition.Columns.Select(c =>
    {
        var field = c.Field ?? c.Key;
        var hasField = _engine.Field(field) is not null;
        return new ColumnViewModel
        {
            Key = c.Key,
            Header = c.Header,
            Cell = c.Cell,
            Field = hasField ? field : null,
            MinWidth = c.MinWidth,
            Width = c.DefaultWidth,
            IsVisible = c.DefaultVisible,
            EmptyText = c.EmptyText,
            Text = r => c.Text?.Invoke((T)r),
            Subtitle = r => c.Subtitle?.Invoke((T)r),
            Initials = r => c.Initials?.Invoke((T)r) ?? "",
            Badge = c.Badge is { } badge ? r => badge((T)r) : null,
            Badges = r => c.Badges?.Invoke((T)r) ?? [],
            DateCell = c.Date is { } date ? r =>
            {
                var value = date((T)r);
                var tone = c.AgeTone?.Invoke(value is { } v ? now - v : null);
                return (value is null ? c.EmptyText : Display.Relative(value, now), Display.Exact(value), tone);
            } : null,
            Plain = r => c.Cell switch
            {
                CellKind.Badge => c.Badge?.Invoke((T)r).Text ?? "",
                CellKind.Badges => string.Join("; ", c.Badges?.Invoke((T)r) ?? []),
                CellKind.Date => c.Date?.Invoke((T)r)?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "",
                CellKind.Number => c.Number?.Invoke((T)r)?.ToString() ?? "",
                _ => c.Text?.Invoke((T)r) ?? "",
            },
        };
    }).ToArray();
}
