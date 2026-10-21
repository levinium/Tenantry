using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Tenantry.ViewModels.Lists;

namespace Tenantry.App.Lists;

/// <summary>
/// One <see cref="DataListView"/> per list, kept for the list's lifetime. A plain DataTemplate
/// lets the tab control recycle one view for every list tab, swapping its data and rebuilding
/// its grid on each switch: slow, it loses scroll position, and it hit a DataGrid bug when the
/// previous list was grouped. Not an IRecyclingDataTemplate, on purpose.
/// </summary>
public sealed class ListViewCache : IDataTemplate
{
    readonly ConditionalWeakTable<DataListViewModel, DataListView> _views = [];

    public bool Match(object? data) => data is DataListViewModel;

    public Control? Build(object? param) =>
        param is DataListViewModel list ? _views.GetValue(list, l => new DataListView { DataContext = l }) : null;
}
