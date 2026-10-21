using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using Tenantry.App.Controls;
using Tenantry.App.Converters;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Lists;

namespace Tenantry.App.Lists;

/// <summary>
/// The one list UI. Grid columns are built here from the view model's column definitions,
/// because they differ per list and change when the user shows, hides or reorders them.
/// </summary>
public partial class DataListView : UserControl
{
    DataListViewModel? _vm;
    readonly Dictionary<DataGridColumn, string> _columnKeys = [];
    bool _syncingSelection;

    public DataListView()
    {
        InitializeComponent();
        // Rows open from their Open button (or Enter); no double-click, so a stray double-click
        // never opens a tab, and nothing depends on knowing a gesture.
        Grid.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _vm?.SelectedRow is not null)
            {
                _vm.OpenSelected();
                e.Handled = true;
            }
        };
        Grid.SelectionChanged += (_, _) =>
        {
            UpdateSelectAll();
            if (_vm is null || _syncingSelection || !_onScreen)
                return;
            // The view model updates the preview pane from the selection, and the selection bar
            // from every checked row.
            _syncingSelection = true;
            _vm.SelectedRow = Grid.SelectedItem;
            _vm.SelectedRows = Grid.SelectedItems.Cast<object>().ToArray();
            _syncingSelection = false;
        };
        Grid.ContextRequested += OnContextRequested;
        // On a narrow toolbar the display tools drop their labels; on a very narrow one every tool
        // does. Tooltips still name each one, and the left group wraps rather than overflowing.
        Toolbar.SizeChanged += (_, e) =>
        {
            Toolbar.Classes.Set("narrow", e.NewSize.Width < 1000);
            Toolbar.Classes.Set("tiny", e.NewSize.Width < 700);
        };
        // Freezing the name column only helps when there's room to scroll beside it. In a narrow
        // table it would eat most of the width and leave the scrollbar a stub, so only the row
        // actions stay frozen then.
        Grid.SizeChanged += (_, e) => Grid.FrozenColumnCount = e.NewSize.Width < 720 ? 2 : 3;
        // The preview pane takes about a third of the space, so the list is never squeezed out;
        // panels take at most about half the height, so the table keeps some room and a
        // panel's buttons never fall off the bottom.
        SizeChanged += (_, e) =>
        {
            PreviewPane.Width = Math.Clamp(e.NewSize.Width * 0.34, 300, 440);
            Resources["PanelMaxHeight"] = Math.Max(240, e.NewSize.Height * 0.55);
        };

        DragReorder.Attach(ColumnsList, (item, index) =>
        {
            if (item is ColumnViewModel column)
                _vm?.MoveColumnTo(column, index);
        }, fixedLeading: 1);
        DragReorder.Attach(SortList, (item, index) =>
        {
            if (item is SortLevelViewModel level)
                _vm?.MoveSortLevelTo(level, index);
        });
        Grid.ColumnReordered += (_, e) =>
        {
            if (_vm is not null && _columnKeys.TryGetValue(e.Column, out var key))
                _vm.ColumnMovedTo(key, e.Column.DisplayIndex - LeadingColumns);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.ColumnsChanged -= OnColumnsChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
        }
        _vm = DataContext as DataListViewModel;
        if (_vm is null)
            return;
        _vm.ColumnsChanged += OnColumnsChanged;
        _vm.PropertyChanged += OnViewModelChanged;
        RebuildColumns();
        UpdateItems();
        Grid.Classes.Set("compact", _vm.IsCompact);
    }

    void OnColumnsChanged(object? sender, EventArgs e) => RebuildColumns();

    /// <summary>
    /// False while the list is off screen (another tab is showing) and until it has settled
    /// back. The grid drops part of its selection while being detached and re-attached, and that
    /// churn must not uncheck rows: the view model's selection is put back once it settles.
    /// </summary>
    bool _onScreen;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_vm is null)
                return;
            _syncingSelection = true;
            Grid.SelectedItem = _vm.SelectedRow;
            _syncingSelection = false;
            ShowChecked();
            _onScreen = true;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _onScreen = false;
        base.OnDetachedFromVisualTree(e);
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DataListViewModel.Rows):
            case nameof(DataListViewModel.SelectedGroup):
                UpdateItems();
                break;
            case nameof(DataListViewModel.IsCompact):
                Grid.Classes.Set("compact", _vm!.IsCompact);
                break;
            case nameof(DataListViewModel.SelectedRow) when !_syncingSelection:
                _syncingSelection = true;
                Grid.SelectedItem = _vm!.SelectedRow;
                _syncingSelection = false;
                break;
            case nameof(DataListViewModel.SelectedRows) when !_syncingSelection:
                ShowChecked();
                break;
        }
    }

    /// <summary>Puts the view model's checked rows into the grid (after Clear selection or a reload).</summary>
    void ShowChecked()
    {
        if (_vm is null)
            return;
        _syncingSelection = true;
        Grid.SelectedItems.Clear();
        foreach (var row in _vm.SelectedRows)
            Grid.SelectedItems.Add(row);
        if (_vm.SelectedRows.Count == 0 && _vm.SelectedRow is { } single)
            Grid.SelectedItem = single;
        _syncingSelection = false;
        UpdateSelectAll();
    }

    // ---- Checkboxes (bulk edit) -------------------------------------------------------------

    /// <summary>Columns before the list's own: the checkbox and the row actions.</summary>
    const int LeadingColumns = 2;

    CheckBox? _selectAll;

    /// <summary>
    /// A checkbox per row, so picking several rows needs no hidden gesture (Ctrl- and
    /// Shift-click work too). The header box checks or clears every row in the list.
    /// </summary>
    DataGridTemplateColumn CheckColumn()
    {
        _selectAll = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 0, Padding = new Thickness(0) };
        _selectAll.Bind(ToolTip.TipProperty, new Binding(nameof(DataListViewModel.SelectAllTip)) { Source = _vm });
        _selectAll.Click += (_, e) =>
        {
            if (Grid.SelectedItems.Count > 0 && Grid.SelectedItems.Count >= (_vm?.Rows.Count ?? 0))
                Grid.SelectedItems.Clear();
            else
                Grid.SelectAll();
            e.Handled = true;
        };
        return new DataGridTemplateColumn
        {
            Header = _selectAll,
            Width = new DataGridLength(44),
            CanUserResize = false,
            CanUserReorder = false,
            CanUserSort = false,
            CellTemplate = new FuncDataTemplate<object>((_, _) =>
            {
                var check = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Padding = new Thickness(0) };
                check.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(DataGridRow.IsSelected))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(DataGridRow) },
                    Mode = BindingMode.OneWay,
                });
                ToolTip.SetTip(check, "Check to include this row when editing several at once");
                check.Click += (_, e) =>
                {
                    if (check.DataContext is { } row)
                    {
                        if (Grid.SelectedItems.Contains(row))
                            Grid.SelectedItems.Remove(row);
                        else
                            Grid.SelectedItems.Add(row);
                    }
                    e.Handled = true;
                };
                return check;
            }, supportsRecycling: true),
        };
    }

    void UpdateSelectAll()
    {
        if (_selectAll is null)
            return;
        var count = Grid.SelectedItems.Count;
        var total = _vm?.Rows.Count ?? 0;
        // Checked when every row is, cleared when none is, and in between when only some are.
        _selectAll.IsChecked = count == 0 ? false : count >= total ? true : null;
    }

    void UpdateItems()
    {
        if (_vm is null)
            return;
        // New rows make the grid drop and re-raise its selection part-way through; none of that
        // may reach the view model, or checked rows would be lost. The view model's selection is
        // put back afterwards.
        _syncingSelection = true;
        if (_vm.IsGrouped)
        {
            var view = new DataGridCollectionView(_vm.Rows);
            view.GroupDescriptions.Add(new KeyGroup(_vm));
            Grid.ItemsSource = view;
        }
        else
            Grid.ItemsSource = _vm.Rows;
        Grid.SelectedItem = _vm.SelectedRow;
        _syncingSelection = false;
        ShowChecked();
    }

    /// <summary>Groups rows by the view model's group key for the chosen field.</summary>
    sealed class KeyGroup(DataListViewModel vm) : DataGridGroupDescription
    {
        public override object GroupKeyFromItem(object item, int level, CultureInfo culture) => vm.GroupKeyOf(item);
    }

    // ---- Columns ----------------------------------------------------------------------------

    void RebuildColumns()
    {
        if (_vm is null)
            return;
        // Not Columns.Clear(): with grouped rows the grid keeps a hidden spacer column, and
        // Clear() drops it while leaving it flagged as present, so the next Add inserts one past
        // the end and throws (DataGridColumnCollection.ClearItems vs InsertItem, DataGrid 12.1).
        // Removing one at a time goes through the path that accounts for the spacer.
        for (var i = Grid.Columns.Count - 1; i >= 0; i--)
            Grid.Columns.RemoveAt(i);
        _columnKeys.Clear();
        Grid.Columns.Add(CheckColumn());
        Grid.Columns.Add(ActionsColumn());
        foreach (var c in _vm.Columns.Where(c => c.IsVisible))
        {
            var column = new DataGridTemplateColumn
            {
                Header = HeaderFor(c),
                CellTemplate = new FuncDataTemplate<object>((_, _) => CellFor(c), supportsRecycling: true),
                MinWidth = c.MinWidth,
                Width = new DataGridLength(Math.Max(c.Width ?? c.MinWidth, c.MinWidth)),
                CanUserSort = false,
                CanUserReorder = c.CanHide,
            };
            column.PropertyChanged += (_, e) =>
            {
                if (e.Property == DataGridColumn.WidthProperty && e.NewValue is DataGridLength { IsAbsolute: true } width)
                    _vm?.ColumnResized(c.Key, Math.Round(width.Value));
            };
            _columnKeys[column] = c.Key;
            Grid.Columns.Add(column);
        }
    }

    DataGridTemplateColumn ActionsColumn() => new()
    {
        Header = "",
        Width = new DataGridLength(48),
        CanUserResize = false,
        CanUserReorder = false,
        CanUserSort = false,
        CellTemplate = new FuncDataTemplate<object>((_, _) =>
        {
            // Opening is the name link; this column carries everything else for the row.
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
            Button? more = null;
            more = RowButton("MoreHorizontal", "More actions", row => BuildMenu(row).ShowAt(more!));
            panel.Children.Add(more);
            return panel;
        }, supportsRecycling: true),
    };

    static Button RowButton(string icon, string tip, Action<object> run)
    {
        var button = new Button { Content = new FluentIcon { Icon = IconName.Parse(icon), FontSize = 16 } };
        button.Classes.Add("rowaction");
        ToolTip.SetTip(button, tip);
        button.Click += (_, e) =>
        {
            if (button.DataContext is { } row)
                run(row);
            e.Handled = true;
        };
        return button;
    }

    Control HeaderFor(ColumnViewModel c)
    {
        var label = new TextBlock { Text = c.Header, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        var arrow = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = Avalonia.Media.FontWeight.Bold };
        arrow.Bind(TextBlock.TextProperty, new Binding(nameof(ColumnViewModel.SortGlyph)));
        arrow.Bind(IsVisibleProperty, new Binding(nameof(ColumnViewModel.IsSorted)));
        arrow.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Accent"));

        // Sized and coloured here rather than by a style class: inside the header presenter the
        // class style lost its size, leaving an unreadable sliver.
        var priorityText = new TextBlock
        {
            FontSize = 11,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Foreground = Avalonia.Media.Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        priorityText.Bind(TextBlock.TextProperty, new Binding(nameof(ColumnViewModel.PriorityText)));
        var priority = new Border
        {
            Child = priorityText,
            MinWidth = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        priority.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccentFill"));
        priority.Bind(IsVisibleProperty, new Binding(nameof(ColumnViewModel.ShowPriority)));
        ToolTip.SetTip(priority, "Sort level");

        // Indicators dock right and the name fills what's left, so a narrow column trims its name
        // ("Departm…") and never its sort arrow or level number.
        arrow.Margin = new Thickness(4, 0, 0, 0);
        priority.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(priority, Dock.Right);
        DockPanel.SetDock(arrow, Dock.Right);
        var title = new DockPanel { Children = { priority, arrow, label } };
        var sortButton = new Button { Content = title, IsEnabled = c.CanSort, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        sortButton.Classes.Add("header");
        sortButton.Bind(ToolTip.TipProperty, new Binding(nameof(ColumnViewModel.SortTip)));
        sortButton.Click += (_, _) => _vm?.SortByColumn(c);

        var menuButton = new Button { Content = new FluentIcon { Icon = FluentIcons.Common.Icon.ChevronDown, FontSize = 12 }, Padding = new Thickness(4) };
        menuButton.Classes.Add("rowaction");
        ToolTip.SetTip(menuButton, $"{c.Header} options: sort, filter, group, hide");
        menuButton.Click += (_, _) => ColumnMenu(c).ShowAt(menuButton);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), DataContext = c };
        grid.Children.Add(sortButton);
        Avalonia.Controls.Grid.SetColumn(menuButton, 1);
        grid.Children.Add(menuButton);
        return grid;
    }

    MenuFlyout ColumnMenu(ColumnViewModel c)
    {
        var menu = new MenuFlyout();
        if (c.CanSort)
        {
            menu.Items.Add(Item("Sort A → Z, by this column only", "TextSortAscending", () => _vm?.SortColumnAscending(c)));
            menu.Items.Add(Item("Sort Z → A, by this column only", "TextSortDescending", () => _vm?.SortColumnDescending(c)));
            menu.Items.Add(Item("Add as the next sort level", "ArrowSort", () => _vm?.AddSortByColumn(c)));
            menu.Items.Add(Item("Open the sort panel…", "ArrowSort", () => _vm?.OpenSortPanel()));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Filter by {c.Header.ToLowerInvariant()}…", "Filter", () => _vm?.FilterByColumn(c)));
            if (_vm?.CanGroupBy(c) == true)
                menu.Items.Add(Item($"Group by {c.Header.ToLowerInvariant()}", "GroupList", () => _vm?.GroupByColumn(c)));
        }
        if (c.CanHide)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Hide this column", "EyeOff", () => _vm?.ToggleColumn(c)));
        }
        return menu;
    }

    static MenuItem Item(string header, string icon, Action run)
    {
        var item = new MenuItem { Header = header, Icon = new FluentIcon { Icon = IconName.Parse(icon), FontSize = 14 } };
        item.Click += (_, _) => run();
        return item;
    }

    // ---- Cells ------------------------------------------------------------------------------

    static Binding Row<T>(Func<object, T> get) =>
        new(".") { Converter = new FuncValueConverter<object?, T?>(r => r is null ? default : get(r)) };

    Control CellFor(ColumnViewModel c)
    {
        switch (c.Cell)
        {
            case CellKind.Primary:
            {
                var initials = new TextBlock();
                initials.Bind(TextBlock.TextProperty, Row(c.Initials));
                var avatar = new Border { Child = initials, VerticalAlignment = VerticalAlignment.Center };
                avatar.Classes.Add("avatar");
                avatar.Classes.Add("small");
                // The name is the way into the record: one click, styled as a link.
                var nameText = new TextBlock { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
                nameText.Bind(TextBlock.TextProperty, Row(c.Text));
                var name = new Button { Content = nameText };
                name.Classes.Add("namelink");
                name.Bind(ToolTip.TipProperty, Row(r => $"Open {c.Text(r)}"));
                name.Click += (_, e) =>
                {
                    if (name.DataContext is { } row)
                        _vm?.OpenRow(row);
                    e.Handled = true;
                };
                var sub = new TextBlock { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
                sub.Classes.Add("caption");
                sub.Classes.Add("subtitle");
                sub.Bind(TextBlock.TextProperty, Row(c.Subtitle));
                sub.Bind(ToolTip.TipProperty, Row(c.Subtitle));
                var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { name, sub } };
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(12, 4) };
                grid.Classes.Add("primarycell");
                grid.Children.Add(avatar);
                Avalonia.Controls.Grid.SetColumn(text, 1);
                grid.Children.Add(text);
                return grid;
            }
            case CellKind.Badge:
            {
                var badge = new Badge { Margin = new Thickness(12, 0) };
                badge.Bind(Badge.TextProperty, Row(r => c.Badge!(r).Text));
                badge.Bind(Badge.KindProperty, Row(r => c.Badge!(r).Kind));
                return badge;
            }
            case CellKind.Badges:
            {
                // The first value, then "+N" for the rest: long product names never get cut
                // off mid-pill, and the tooltip lists them all.
                var first = new Badge { Kind = BadgeKind.Info, MaxWidth = 210 };
                first.Bind(Badge.TextProperty, Row(r => c.Badges(r).FirstOrDefault()));
                first.Bind(IsVisibleProperty, Row(r => c.Badges(r).Count > 0));
                var more = new Badge { Kind = BadgeKind.Neutral };
                more.Bind(Badge.TextProperty, Row(r => $"+{c.Badges(r).Count - 1}"));
                more.Bind(IsVisibleProperty, Row(r => c.Badges(r).Count > 1));
                var empty = new TextBlock { Text = c.EmptyText, VerticalAlignment = VerticalAlignment.Center };
                empty.Classes.Add("caption");
                empty.Bind(IsVisibleProperty, Row(r => c.Badges(r).Count == 0));
                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Margin = new Thickness(12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ClipToBounds = true,
                    Children = { first, more, empty },
                };
                panel.Bind(ToolTip.TipProperty, Row(r => c.Badges(r).Count == 0 ? null : string.Join("\n", c.Badges(r))));
                return panel;
            }
            case CellKind.Date when c.DateCell is { } date:
            {
                var plain = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                plain.Bind(TextBlock.TextProperty, Row(r => date(r).Relative));
                plain.Bind(IsVisibleProperty, Row(r => date(r).Tone is null));
                var toned = new Badge();
                toned.Bind(Badge.TextProperty, Row(r => date(r).Relative));
                toned.Bind(Badge.KindProperty, Row(r => date(r).Tone ?? BadgeKind.Neutral));
                toned.Bind(IsVisibleProperty, Row(r => date(r).Tone is not null));
                var panel = new Panel { Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center, Children = { plain, toned } };
                panel.Bind(ToolTip.TipProperty, Row(r => date(r).Exact));
                return panel;
            }
            case CellKind.Number:
            {
                var text = new TextBlock { Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                text.Bind(TextBlock.TextProperty, Row(c.Plain));
                return text;
            }
            default:
            {
                var text = new TextBlock { Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
                text.Bind(TextBlock.TextProperty, Row(c.Text));
                text.Bind(ToolTip.TipProperty, Row(c.Text));
                return text;
            }
        }
    }

    // ---- Menus, clipboard, export -----------------------------------------------------------

    static object? RowOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext;

    void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null || RowOf(e.Source) is not { } row)
            return;
        _vm.SelectedRow = row;
        BuildMenu(row).ShowAt(Grid, showAtPointer: true);
        e.Handled = true;
    }

    MenuFlyout BuildMenu(object row)
    {
        var menu = new MenuFlyout();
        foreach (var item in _vm!.MenuFor(row))
        {
            if (item.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            menu.Items.Add(Item(item.Label, item.Icon, () =>
            {
                if (item.CopyText is { } text)
                    _ = CopyAsync(text);
                else
                    item.Run?.Invoke();
            }));
        }
        return menu;
    }

    async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(text);
        // Hand the data to the OS now; an unflushed Avalonia clipboard on Windows blocks other
        // apps' copy and paste until this app exits (found in TrispotQR).
        await clipboard.FlushAsync();
    }

    // Menu rows run their action and close their menu.

    void OnFilterFieldClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FilterMenuItem item)
        {
            FilterButton.Flyout?.Hide();
            _vm?.EditFilter(item.Field);
            if (_vm is not null)
                _vm.FilterFieldSearch = "";
        }
    }

    void OnGroupClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GroupMenuItem item)
        {
            GroupButton.Flyout?.Hide();
            _vm?.SelectGroup(item.Option);
        }
    }

    void OnMoreViewClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SavedViewItem view)
        {
            MoreViewsButton.Flyout?.Hide();
            _vm?.ApplyView(view);
        }
    }

    void OnSaveView(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || string.IsNullOrWhiteSpace(_vm.NewViewName))
        {
            NewViewNameBox.Focus();
            return;
        }
        _vm.SaveViewAsCommand.Execute(null);
        SaveViewButton.Flyout?.Hide();
    }

    void OnManageViews(object? sender, RoutedEventArgs e)
    {
        SaveViewButton.Flyout?.Hide();
        _vm?.OpenViewsPanelCommand.Execute(null);
    }

    async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to CSV",
            SuggestedFileName = _vm.SuggestedExportName,
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV file") { Patterns = ["*.csv"] }],
        });
        if (file is null)
            return;
        await using var stream = await file.OpenWriteAsync();
        // UTF-8 with a byte-order mark, so Excel reads names with accents correctly.
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _vm.ExportCsv(writer);
    }
}
