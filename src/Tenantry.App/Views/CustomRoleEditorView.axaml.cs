using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tenantry.ViewModels.Records;

namespace Tenantry.App.Views;

public partial class CustomRoleEditorView : UserControl
{
    bool _shift;

    public CustomRoleEditorView()
    {
        InitializeComponent();
        // Click carries no keys: note Shift as the pointer goes down, before the box toggles.
        PermissionList.AddHandler(PointerPressedEvent, (_, e) => _shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel);
    }

    void OnPermissionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: PermissionChoice choice } && DataContext is CustomRoleEditorViewModel editor)
            editor.Clicked(choice, _shift);
        _shift = false;
    }
}
