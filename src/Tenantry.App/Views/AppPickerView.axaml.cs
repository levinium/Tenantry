using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tenantry.ViewModels.Editing;

namespace Tenantry.App.Views;

/// <summary>
/// Chosen apps as chips, then an Add button. Its popup puts the cursor in the search box and
/// closes once an app is chosen.
/// </summary>
public partial class AppPickerView : UserControl
{
    AppPicker? _picker;
    bool _shift;

    public AppPickerView()
    {
        InitializeComponent();
        var flyout = (Flyout)Open.Flyout!;
        CancelButton.Click += (_, _) => flyout.Hide();
        // Click carries no keys: note Shift as the pointer goes down, before the row's click.
        RowList.AddHandler(PointerPressedEvent, (_, e) => _shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel);
        flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            (flyout.Content as Control)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus(), DispatcherPriority.Input);
        flyout.Closed += (_, _) =>
        {
            if (_picker is not null)
            {
                _picker.ClearTicks();
                _picker.Query = "";
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_picker is not null)
            _picker.Picked -= OnPicked;
        _picker = DataContext as AppPicker;
        if (_picker is not null)
            _picker.Picked += OnPicked;
    }

    void OnPicked(object? sender, EventArgs e) => Open.Flyout?.Hide();

    void OnRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PickRow row })
            _picker?.Choose(row, _shift);
        _shift = false;
    }
}
