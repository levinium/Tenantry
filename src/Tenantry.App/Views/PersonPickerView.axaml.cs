using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tenantry.ViewModels.Editing;

namespace Tenantry.App.Views;

public partial class PersonPickerView : UserControl
{
    bool _shift;

    public PersonPickerView()
    {
        InitializeComponent();
        // Click carries no keys: note Shift as the pointer goes down, before the row's click.
        RowList.AddHandler(PointerPressedEvent, (_, e) => _shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel);
    }

    void OnRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PickRow row } && DataContext is PersonPicker picker)
            picker.Choose(row, _shift);
        _shift = false;
    }
}
