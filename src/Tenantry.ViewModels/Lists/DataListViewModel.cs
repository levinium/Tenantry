using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Lists;

namespace Tenantry.ViewModels.Lists;

/// <summary>
/// The view model behind every list: search (with shortcuts), filter pills and editors, the
/// query builder, sorting, grouping, columns, density, views, preview, row menus, CSV export
/// and remembered state. Lists differ only in their <see cref="ListDefinition{T}"/>.
/// </summary>
public sealed partial class DataListViewModel : TabViewModel
{
    readonly IListSource _source;
    readonly IListStateStore _store;
    readonly Func<DateTimeOffset> _now;
    readonly List<Condition> _conditions = [];
    List<SortLevel> _sort = [];
    IReadOnlyList<Condition> _searchConditions = [];
    string _freeText = "";
    bool _restoring;

    DataListViewModel(IListSource source, IListStateStore store, Func<DateTimeOffset>? now, bool canClose)
    {
        // Nothing below may save or refresh until Restore has put the remembered state back.
        _restoring = true;
        _source = source;
        _store = store;
        _now = now ?? (() => DateTimeOffset.Now);
        _canClose = canClose;
        Rows = [];
        Columns = new ObservableCollection<ColumnViewModel>(source.BuildColumns(_now()));
        foreach (var c in Columns)
            c.PropertyChanged += OnColumnChanged;
        Columns.CollectionChanged += (_, e) =>
        {
            foreach (ColumnViewModel c in e.NewItems ?? Array.Empty<ColumnViewModel>())
            {
                c.PropertyChanged -= OnColumnChanged;
                c.PropertyChanged += OnColumnChanged;
            }
        };
        NewViewName = "";
        SearchText = "";
        GroupOptions = [new GroupOption(null, "No grouping"), .. source.FieldOptions.Where(f => f.Kind is FieldKind.Choice or FieldKind.MultiChoice).Select(f => new GroupOption(f.Key, f.Label))];
        SelectedGroup = GroupOptions[0];
        foreach (var v in source.BuiltInViews)
            Views.Add(new SavedViewItem(v));
        Restore();
    }

    public static DataListViewModel Create<T>(ListDefinition<T> definition, Func<CancellationToken, Task<IReadOnlyList<T>>> load,
        Action<T> open, IListStateStore? store = null, Func<DateTimeOffset>? now = null, bool canClose = false) where T : notnull =>
        new(new ListSource<T>(definition, load, open), store ?? new MemoryListStateStore(), now, canClose);

    readonly bool _canClose;
    public override string Title => _source.Title;

    /// <summary>The list's main action button ("New user"), shown disabled with its reason when unavailable.</summary>
    public ListPrimaryAction? PrimaryAction { get; set; }
    public override bool CanClose => _canClose;
    public string SearchHint => _source.SearchHint;
    public bool HasPreview => _source.HasPreview;

    // ---- Rows and counts ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsEmpty))]
    public partial IReadOnlyList<object> Rows { get; private set; }

    [ObservableProperty]
    public partial object? SelectedRow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    public int Total => _source.Total;
    public bool IsEmpty => !IsLoading && Rows.Count == 0;
    public string CountText => Rows.Count == Total ? $"{Total:N0} {_source.Noun}" : $"{Rows.Count:N0} of {Total:N0} {_source.Noun}";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            await _source.LoadAsync(cancellationToken);
            Columns.Clear();
            foreach (var c in _source.BuildColumns(_now()))
                Columns.Add(c);
            ApplyColumnLayout(_store.Load(_source.Key)?.Columns);
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(Total));
            CountViews();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = Friendly.Message(e);
        }
        finally
        {
            IsLoading = false;
        }
        Refresh();
    }

    // ---- Search ---------------------------------------------------------------------------

    [ObservableProperty]
    public partial string SearchText { get; set; }

    /// <summary>What the search box's shortcuts turned into, so typing them is never a guess.</summary>
    [ObservableProperty]
    public partial string? SearchUnderstood { get; private set; }

    [ObservableProperty]
    public partial string? SearchProblem { get; private set; }

    partial void OnSearchTextChanged(string value)
    {
        var parse = _source.ParseSearch(value);
        _freeText = parse.FreeText;
        _searchConditions = parse.Conditions;
        SearchUnderstood = parse.Conditions.Count == 0 ? null : "Filtering: " + string.Join(" · ", parse.Conditions.Select(_source.Describe));
        SearchProblem = parse.Problems.Count == 0 ? null : string.Join("; ", parse.Problems);
        if (!_restoring)
            Refresh(save: false);
    }

    public static string SearchHelp =>
        "Type words to search, or shortcuts: field:value (dept:finance), -field:value to exclude (-status:blocked), " +
        "license:none, signin:>90d (more than 90 days ago), signin:<7d, signin:never, lc:>=2. Quote values with spaces.";

    // ---- Panels ---------------------------------------------------------------------------
    // One panel at a time opens below the toolbar: a filter editor, the query builder, sort,
    // columns, or views. Opening one closes the others, so the screen never stacks dialogs.

    [ObservableProperty]
    public partial bool IsColumnsPanelOpen { get; set; }

    [ObservableProperty]
    public partial bool IsViewsPanelOpen { get; set; }

    [RelayCommand]
    public void ClosePanels()
    {
        Editor = null;
        Builder = null;
        IsSortPanelOpen = false;
        IsColumnsPanelOpen = false;
        IsViewsPanelOpen = false;
    }

    [RelayCommand]
    public void OpenColumnsPanel()
    {
        var open = !IsColumnsPanelOpen;
        ClosePanels();
        IsColumnsPanelOpen = open;
    }

    [RelayCommand]
    public void OpenViewsPanel()
    {
        var open = !IsViewsPanelOpen;
        ClosePanels();
        IsViewsPanelOpen = open;
    }

    // ---- Filters --------------------------------------------------------------------------

    public ObservableCollection<FilterPill> Pills { get; } = [];
    public bool HasFilters => Pills.Count > 0;
    public int FilterCount => Pills.Count;
    public IReadOnlyList<FieldOption> FilterFields => _source.FieldOptions;

    [ObservableProperty]
    public partial FilterEditorViewModel? Editor { get; private set; }

    [RelayCommand]
    public void EditFilter(FieldOption field)
    {
        ClosePanels();
        var existing = _conditions.FirstOrDefault(c => FieldOf(c)?.Equals(field.Key, StringComparison.OrdinalIgnoreCase) == true);
        Editor = field.Kind switch
        {
            FieldKind.Choice or FieldKind.MultiChoice => new ChoiceFilterEditor(field, _source.Facet(CurrentQuery(), field.Key, _now()), existing as ChoiceCondition),
            FieldKind.Date => new DateFilterEditor(field, existing as DateCondition),
            FieldKind.Number => new NumberFilterEditor(field, existing as NumberCondition),
            _ => new TextFilterEditor(field, existing as TextCondition),
        };
    }

    [RelayCommand]
    public void ApplyFilter()
    {
        if (Editor is null)
            return;
        var key = Editor.Field.Key;
        var index = _conditions.FindIndex(c => FieldOf(c)?.Equals(key, StringComparison.OrdinalIgnoreCase) == true);
        var condition = Editor.Build();
        if (index >= 0)
        {
            if (condition is null)
                _conditions.RemoveAt(index);
            else
                _conditions[index] = condition;
        }
        else if (condition is not null)
            _conditions.Add(condition);
        Editor = null;
        Changed();
    }

    [RelayCommand]
    public void ClearEditorFilter()
    {
        if (Editor is null)
            return;
        var key = Editor.Field.Key;
        _conditions.RemoveAll(c => FieldOf(c)?.Equals(key, StringComparison.OrdinalIgnoreCase) == true);
        Editor = null;
        Changed();
    }

    [RelayCommand]
    public void CancelFilter() => Editor = null;

    [RelayCommand]
    public void ClearFilters()
    {
        _conditions.Clear();
        SearchText = "";
        Changed();
    }

    void EditPill(FilterPill pill)
    {
        if (pill.IsAdvanced)
            OpenBuilder();
        else if (FieldOf(pill.Condition) is { } key && FilterFields.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } field)
            EditFilter(field);
    }

    void RemovePill(FilterPill pill)
    {
        _conditions.Remove(pill.Condition);
        Changed();
    }

    static string? FieldOf(Condition c) => c switch
    {
        ChoiceCondition x => x.Field,
        TextCondition x => x.Field,
        DateCondition x => x.Field,
        NumberCondition x => x.Field,
        _ => null,
    };

    // ---- Query builder --------------------------------------------------------------------

    [ObservableProperty]
    public partial QueryGroupViewModel? Builder { get; private set; }

    /// <summary>Header menu: filter by this column's field.</summary>
    [RelayCommand]
    public void FilterByColumn(ColumnViewModel column)
    {
        if (column.Field is { } key && FilterFields.FirstOrDefault(f => f.Key == key) is { } field)
            EditFilter(field);
    }

    [RelayCommand]
    public void OpenBuilder()
    {
        ClosePanels();
        var builder = new QueryGroupViewModel(FilterFields, key => _source.Facet(ListQuery.Empty, key, _now()).Select(f => f.Value).ToArray(), isRoot: true);
        if (_conditions.OfType<ConditionGroup>().FirstOrDefault() is { } advanced)
            builder.Load(advanced);
        if (builder.Rules.Count == 0 && builder.Groups.Count == 0)
            builder.AddRule();
        Builder = builder;
    }

    [RelayCommand]
    public void ApplyBuilder()
    {
        if (Builder is null)
            return;
        var group = Builder.ToCondition();
        _conditions.RemoveAll(c => c is ConditionGroup);
        if (group.Items.Count > 0)
            _conditions.Add(group);
        Builder = null;
        Changed();
    }

    [RelayCommand]
    public void CancelBuilder() => Builder = null;

    // ---- Sorting --------------------------------------------------------------------------

    /// <summary>The Sort panel's working copy; applied with <see cref="ApplySortCommand"/>.</summary>
    public ObservableCollection<SortLevelViewModel> SortLevels { get; } = [];

    [ObservableProperty]
    public partial bool IsSortPanelOpen { get; set; }

    public IReadOnlyList<FieldOption> SortFields => _source.FieldOptions;

    /// <summary>Header click: sort by this column alone, or reverse it if it already leads.</summary>
    [RelayCommand]
    public void SortByColumn(ColumnViewModel column)
    {
        if (column.Field is not { } field)
            return;
        var descending = _sort.Count == 1 && _sort[0].Field == field && !_sort[0].Descending;
        _sort = [new SortLevel(field, descending)];
        Changed();
    }

    [RelayCommand]
    public void SortColumnAscending(ColumnViewModel column) => SortOnly(column, false);

    [RelayCommand]
    public void SortColumnDescending(ColumnViewModel column) => SortOnly(column, true);

    void SortOnly(ColumnViewModel column, bool descending)
    {
        if (column.Field is not { } field)
            return;
        _sort = [new SortLevel(field, descending)];
        Changed();
    }

    /// <summary>Header menu: group by this column's field, when it's a choice field.</summary>
    [RelayCommand]
    public void GroupByColumn(ColumnViewModel column)
    {
        if (GroupOptions.FirstOrDefault(g => g.Key is not null && g.Key == column.Field) is { } option)
            SelectedGroup = option;
    }

    public bool CanGroupBy(ColumnViewModel column) => GroupOptions.Any(g => g.Key is not null && g.Key == column.Field);

    /// <summary>Header menu: add this column as the next sort level (or flip it if present).</summary>
    [RelayCommand]
    public void AddSortByColumn(ColumnViewModel column)
    {
        if (column.Field is not { } field)
            return;
        var index = _sort.FindIndex(s => s.Field == field);
        if (index >= 0)
            _sort[index] = _sort[index] with { Descending = !_sort[index].Descending };
        else
            _sort.Add(new SortLevel(field));
        Changed();
    }

    [RelayCommand]
    public void OpenSortPanel()
    {
        if (IsSortPanelOpen)
        {
            IsSortPanelOpen = false;
            return;
        }
        ClosePanels();
        SortLevels.Clear();
        foreach (var level in _sort)
            AddLevel(SortFields.FirstOrDefault(f => f.Key == level.Field), level.Descending);
        if (SortLevels.Count == 0)
            AddLevel(SortFields.FirstOrDefault(), false);
        IsSortPanelOpen = true;
    }

    [RelayCommand]
    public void AddSortLevel() => AddLevel(SortFields.FirstOrDefault(f => SortLevels.All(l => l.Field != f)) ?? SortFields[0], false);

    void AddLevel(FieldOption? field, bool descending)
    {
        SortLevels.Add(new SortLevelViewModel(SortFields) { Field = field, Descending = descending, Index = SortLevels.Count });
    }

    [RelayCommand]
    public void RemoveSortLevel(SortLevelViewModel level)
    {
        SortLevels.Remove(level);
        Reindex();
    }

    [RelayCommand]
    public void MoveSortLevelUp(SortLevelViewModel level)
    {
        var i = SortLevels.IndexOf(level);
        if (i > 0)
            SortLevels.Move(i, i - 1);
        Reindex();
    }

    [RelayCommand]
    public void MoveSortLevelDown(SortLevelViewModel level)
    {
        var i = SortLevels.IndexOf(level);
        if (i >= 0 && i < SortLevels.Count - 1)
            SortLevels.Move(i, i + 1);
        Reindex();
    }

    void Reindex()
    {
        for (var i = 0; i < SortLevels.Count; i++)
            SortLevels[i].Index = i;
    }

    [RelayCommand]
    public void ApplySort()
    {
        _sort = SortLevels.Where(l => l.Field is not null)
            .Select(l => new SortLevel(l.Field!.Key, l.Descending))
            .DistinctBy(l => l.Field)
            .ToList();
        IsSortPanelOpen = false;
        Changed();
    }

    [RelayCommand]
    public void ClearSort()
    {
        _sort = [];
        SortLevels.Clear();
        IsSortPanelOpen = false;
        Changed();
    }

    public string SortSummary => _sort.Count switch
    {
        0 => "Sort",
        1 => $"Sort: {LabelOf(_sort[0].Field)}",
        _ => $"Sort: {_sort.Count} levels",
    };

    string LabelOf(string field) => SortFields.FirstOrDefault(f => f.Key == field)?.Label ?? field;

    void UpdateSortIndicators()
    {
        foreach (var column in Columns)
        {
            var index = column.Field is null ? -1 : _sort.FindIndex(s => s.Field == column.Field);
            column.Sort = index < 0 ? SortState.None : _sort[index].Descending ? SortState.Descending : SortState.Ascending;
            column.SortPriority = index + 1;
            column.ShowPriority = index >= 0 && _sort.Count > 1;
        }
        OnPropertyChanged(nameof(SortSummary));
    }

    // ---- Grouping -------------------------------------------------------------------------

    public IReadOnlyList<GroupOption> GroupOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGrouped))]
    public partial GroupOption SelectedGroup { get; set; }

    public bool IsGrouped => SelectedGroup.Key is not null;

    public string GroupSummary => IsGrouped ? $"Group: {SelectedGroup.Label}" : "Group";

    partial void OnSelectedGroupChanged(GroupOption value)
    {
        OnPropertyChanged(nameof(GroupSummary));
        Changed();
    }

    [RelayCommand]
    public void SelectGroup(GroupOption option) => SelectedGroup = option;

    public string GroupKeyOf(object row) => SelectedGroup.Key is { } key ? _source.GroupKey(row, key) : "";

    // ---- Columns and density --------------------------------------------------------------

    public ObservableCollection<ColumnViewModel> Columns { get; }

    /// <summary>Raised when columns change visibility or order, so the view rebuilds the grid.</summary>
    public event EventHandler? ColumnsChanged;

    bool _layoutChanging;

    [RelayCommand]
    public void ToggleColumn(ColumnViewModel column)
    {
        if (column.CanHide)
            column.IsVisible = !column.IsVisible;
    }

    // Visibility changes from anywhere (the chooser's checkboxes bind straight to IsVisible)
    // rebuild the grid and are remembered. Bulk layout changes suspend this and report once.
    void OnColumnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ColumnViewModel.IsVisible) || _layoutChanging || sender is not ColumnViewModel column)
            return;
        if (!column.CanHide && !column.IsVisible)
        {
            column.IsVisible = true;
            return;
        }
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
        Save();
    }

    [RelayCommand]
    public void MoveColumnUp(ColumnViewModel column) => MoveColumn(column, -1);

    [RelayCommand]
    public void MoveColumnDown(ColumnViewModel column) => MoveColumn(column, +1);

    void MoveColumn(ColumnViewModel column, int by)
    {
        var i = Columns.IndexOf(column);
        var j = i + by;
        // The primary column stays first.
        if (i < 0 || j < 1 || j >= Columns.Count || column.Cell == CellKind.Primary)
            return;
        Columns.Move(i, j);
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
        Save();
    }

    /// <summary>Called by the view after the user drags a column to a new place.</summary>
    public void ColumnMovedTo(string key, int visibleIndex)
    {
        var visible = Columns.Where(c => c.IsVisible).ToList();
        var column = Columns.FirstOrDefault(c => c.Key == key);
        if (column is null || column.Cell == CellKind.Primary)
            return;
        visible.Remove(column);
        visible.Insert(Math.Clamp(visibleIndex, 1, visible.Count), column);
        var hidden = Columns.Where(c => !c.IsVisible).ToList();
        Columns.Clear();
        foreach (var c in visible.Concat(hidden))
            Columns.Add(c);
        Save();
    }

    /// <summary>Called by the view when the user resizes a column.</summary>
    public void ColumnResized(string key, double width)
    {
        if (Columns.FirstOrDefault(c => c.Key == key) is { } column && column.Width != width)
        {
            column.Width = width;
            Save();
        }
    }

    [RelayCommand]
    public void ResetColumns()
    {
        _layoutChanging = true;
        Columns.Clear();
        foreach (var c in _source.BuildColumns(_now()))
            Columns.Add(c);
        _layoutChanging = false;
        UpdateSortIndicators();
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
        Save();
    }

    void ApplyColumnLayout(IReadOnlyList<ColumnLayout>? layout)
    {
        if (layout is null || layout.Count == 0)
            return;
        _layoutChanging = true;
        try
        {
            ApplyColumnLayoutCore(layout);
        }
        finally
        {
            _layoutChanging = false;
        }
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    void ApplyColumnLayoutCore(IReadOnlyList<ColumnLayout> layout)
    {
        var byKey = Columns.ToDictionary(c => c.Key);
        var ordered = layout.Where(l => byKey.ContainsKey(l.Key)).Select(l =>
        {
            var c = byKey[l.Key];
            c.IsVisible = c.Cell == CellKind.Primary || l.Visible;
            c.Width = l.Width ?? c.Width;
            return c;
        }).ToList();
        // Columns added since the layout was saved go at the end, as defined.
        ordered.AddRange(Columns.Where(c => layout.All(l => l.Key != c.Key)));
        ordered = [.. ordered.Where(c => c.Cell == CellKind.Primary), .. ordered.Where(c => c.Cell != CellKind.Primary)];
        Columns.Clear();
        foreach (var c in ordered)
            Columns.Add(c);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompact))]
    public partial ListDensity Density { get; set; }

    public bool IsCompact => Density == ListDensity.Compact;

    partial void OnDensityChanged(ListDensity value) => Save();

    [RelayCommand]
    public void ToggleDensity() => Density = Density == ListDensity.Compact ? ListDensity.Comfortable : ListDensity.Compact;

    // ---- Views ----------------------------------------------------------------------------

    public ObservableCollection<SavedViewItem> Views { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentViewName))]
    public partial SavedViewItem? CurrentView { get; private set; }

    [ObservableProperty]
    public partial bool IsModified { get; private set; }

    public string CurrentViewName => CurrentView is null ? "Custom view" : IsModified ? $"{CurrentView.Name} (changed)" : CurrentView.Name;

    [ObservableProperty]
    public partial string NewViewName { get; set; }

    [RelayCommand]
    public void ApplyView(SavedViewItem view)
    {
        ClosePanels();
        _restoring = true;
        _conditions.Clear();
        _conditions.AddRange(view.View.Query.Where.Items);
        _sort = [.. view.View.Query.Sort];
        SelectedGroup = GroupOptions.FirstOrDefault(g => g.Key == view.View.Query.GroupBy) ?? GroupOptions[0];
        _restoring = false;
        if (view.View.Columns is { Count: > 0 } columns)
        {
            ApplyColumnLayout(columns);
            UpdateSortIndicators();
        }
        SetCurrentView(view);
        SearchText = "";
        Changed();
    }

    [RelayCommand]
    public void SaveViewAs()
    {
        var name = NewViewName.Trim();
        if (name.Length == 0)
            return;
        var existing = Views.FirstOrDefault(v => !v.IsBuiltIn && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var view = new SavedViewItem(new SavedView(existing?.View.Id ?? Guid.NewGuid().ToString("N"), name, StoredQuery(), CurrentLayout()));
        if (existing is not null)
            Views[Views.IndexOf(existing)] = view;
        else
            Views.Add(view);
        NewViewName = "";
        SetCurrentView(view);
        CountViews();
        Changed();
    }

    [RelayCommand]
    public void DeleteView(SavedViewItem view)
    {
        if (view.IsBuiltIn)
            return;
        Views.Remove(view);
        if (CurrentView == view)
            SetCurrentView(null);
        Save();
    }

    void SetCurrentView(SavedViewItem? view)
    {
        foreach (var v in Views)
            v.IsCurrent = v == view;
        CurrentView = view;
    }

    void CountViews()
    {
        var now = _now();
        foreach (var v in Views)
            v.Count = _source.Total == 0 ? null : _source.Count(v.View.Query, now);
    }

    // ---- Preview, open, menus, export ----------------------------------------------------

    // The preview pane is a toggle on the toolbar. While it's open it shows whichever row is
    // selected, so moving through the list previews each row in turn.

    [ObservableProperty]
    public partial object? PreviewModel { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewToggleText))]
    public partial bool IsPreviewOpen { get; set; }

    /// <summary>Stays "Preview": the button's highlighted state says whether the pane is open.</summary>
    public string PreviewToggleText => "Preview";

    public bool HasPreviewContent => PreviewModel is not null;

    partial void OnIsPreviewOpenChanged(bool value) => UpdatePreview();

    partial void OnSelectedRowChanged(object? value) => UpdatePreview();

    void UpdatePreview()
    {
        PreviewModel = IsPreviewOpen && SelectedRow is { } row ? _source.Preview(row) : null;
        OnPropertyChanged(nameof(HasPreviewContent));
    }

    [RelayCommand]
    public void TogglePreview() => IsPreviewOpen = !IsPreviewOpen && HasPreview;

    /// <summary>Selects the row and opens the preview on it (the row menu's "Show in preview").</summary>
    [RelayCommand]
    public void PreviewRow(object row)
    {
        SelectedRow = row;
        IsPreviewOpen = true;
    }

    [RelayCommand]
    public void ClosePreview() => IsPreviewOpen = false;

    [RelayCommand]
    public void ExpandPreview()
    {
        if (SelectedRow is { } row)
            _source.Open(row);
    }

    [RelayCommand]
    public void OpenRow(object row) => _source.Open(row);

    [RelayCommand]
    public void OpenSelected()
    {
        if (SelectedRow is { } row)
            _source.Open(row);
    }

    /// <summary>Everything the row's More button and right-click menu offer.</summary>
    public IReadOnlyList<RowMenuItem> MenuFor(object row)
    {
        var items = new List<RowMenuItem>();
        items.Add(new RowMenuItem("Open", "Open", () => _source.Open(row)));
        if (HasPreview)
            items.Add(new RowMenuItem("Show in preview", "PanelRight", () => PreviewRow(row)));
        var actions = _source.Actions(row);
        if (actions.Count > 0)
        {
            items.Add(RowMenuItem.Separator);
            items.AddRange(actions);
        }
        items.Add(RowMenuItem.Separator);
        items.Add(new RowMenuItem("Copy row", "Copy", CopyText: RowText(row)));
        foreach (var c in Columns.Where(c => c.IsVisible))
        {
            var value = c.Cell == CellKind.Primary ? c.Text(row) : c.Plain(row);
            if (!string.IsNullOrWhiteSpace(value))
                items.Add(new RowMenuItem($"Copy {c.Header.ToLowerInvariant()}", "Copy", CopyText: value));
            if (c.Cell == CellKind.Primary && c.Subtitle(row) is { Length: > 0 } sub)
                items.Add(new RowMenuItem("Copy address", "Copy", CopyText: sub));
        }
        return items;
    }

    string RowText(object row) => string.Join('\t', Columns.Where(c => c.IsVisible).Select(c => c.Cell == CellKind.Primary ? c.Text(row) ?? "" : c.Plain(row)));

    /// <summary>The rows on screen, visible columns in order, as CSV (RFC 4180 quoting).</summary>
    public void ExportCsv(TextWriter writer)
    {
        var columns = Columns.Where(c => c.IsVisible).ToArray();
        var headers = new List<string>();
        foreach (var c in columns)
        {
            headers.Add(c.Header);
            if (c.Cell == CellKind.Primary)
                headers.Add(c.Header + " (detail)");
        }
        writer.WriteLine(string.Join(',', headers.Select(Csv)));
        foreach (var row in Rows)
        {
            var cells = new List<string>();
            foreach (var c in columns)
            {
                if (c.Cell == CellKind.Primary)
                {
                    cells.Add(c.Text(row) ?? "");
                    cells.Add(c.Subtitle(row) ?? "");
                }
                else
                    cells.Add(c.Plain(row));
            }
            writer.WriteLine(string.Join(',', cells.Select(Csv)));
        }
    }

    public string SuggestedExportName => $"{_source.Key}-{_now():yyyy-MM-dd}.csv";

    static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    // ---- Running the query, and remembering it --------------------------------------------

    ListQuery StoredQuery() => new("", new ConditionGroup(GroupOp.And, _conditions.ToArray()), _sort.ToArray(), SelectedGroup.Key);

    ListQuery CurrentQuery() => new(_freeText,
        new ConditionGroup(GroupOp.And, _conditions.Concat(_searchConditions).ToArray()),
        _sort.ToArray(), SelectedGroup.Key);

    IReadOnlyList<ColumnLayout> CurrentLayout() => Columns.Select(c => new ColumnLayout(c.Key, c.IsVisible, c.Width)).ToArray();

    void Changed()
    {
        if (_restoring)
            return;
        Refresh();
    }

    void Refresh(bool save = true)
    {
        Pills.Clear();
        foreach (var c in _conditions)
            Pills.Add(new FilterPill(c is ConditionGroup g ? $"Advanced: {CountRules(g)} rule{(CountRules(g) == 1 ? "" : "s")}" : _source.Describe(c),
                c, c is ConditionGroup, EditPill, RemovePill));
        OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(FilterCount));

        var selectedId = SelectedRow is { } s ? _source.Id(s) : null;
        Rows = _source.Run(CurrentQuery(), _now());
        SelectedRow = selectedId is null ? null : Rows.FirstOrDefault(r => _source.Id(r) == selectedId);
        UpdateSortIndicators();

        var stored = JsonSerializer.Serialize(StoredQuery());
        IsModified = CurrentView is not null && stored != JsonSerializer.Serialize(CurrentView.View.Query with { Search = "" });
        OnPropertyChanged(nameof(CurrentViewName));
        if (save)
            Save();
    }

    static int CountRules(ConditionGroup g) => g.Items.Sum(i => i is ConditionGroup sub ? CountRules(sub) : 1);

    void Save()
    {
        if (_restoring)
            return;
        _store.Save(_source.Key, new ListState(StoredQuery(), CurrentLayout(), Density,
            Views.Where(v => !v.IsBuiltIn).Select(v => v.View).ToArray(), CurrentView?.View.Id));
    }

    void Restore()
    {
        var state = _store.Load(_source.Key);
        _restoring = true;
        if (state is null)
        {
            if (Views.FirstOrDefault() is { } first)
            {
                _conditions.AddRange(first.View.Query.Where.Items);
                _sort = [.. first.View.Query.Sort];
                SetCurrentView(first);
            }
        }
        else
        {
            foreach (var v in state.Views)
                Views.Add(new SavedViewItem(v));
            _conditions.AddRange(state.Query.Where.Items);
            _sort = [.. state.Query.Sort];
            SelectedGroup = GroupOptions.FirstOrDefault(g => g.Key == state.Query.GroupBy) ?? GroupOptions[0];
            Density = state.Density;
            ApplyColumnLayout(state.Columns);
            SetCurrentView(Views.FirstOrDefault(v => v.View.Id == state.CurrentViewId));
        }
        _restoring = false;
        Refresh(save: false);
    }
}
