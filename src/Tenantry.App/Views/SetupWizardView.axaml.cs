using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Tenantry.ViewModels;

namespace Tenantry.App.Views;

public partial class SetupWizardView : UserControl
{
    public SetupWizardView() => InitializeComponent();

    async void OnCopyScript(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SetupWizardViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(vm.ManualScript);
        // Avalonia's Windows clipboard holds a live COM object until flushed, which blocks
        // other apps' copy and paste; flushing hands the data to the OS (see TrispotQR).
        await clipboard.FlushAsync();
    }
}
