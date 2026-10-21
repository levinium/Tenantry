using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tenantry.ViewModels;

namespace Tenantry.App.Views;

public partial class MainView : UserControl
{
    MainViewModel? _vm;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.PropertyChanged -= OnViewModelChanged;
            _vm = DataContext as MainViewModel;
            if (_vm is not null)
                _vm.PropertyChanged += OnViewModelChanged;
        };

        // The palette's keys: arrows move the highlight, Enter opens it, Esc closes. Tunnel, so
        // the TextBox doesn't swallow Up/Down/Enter first.
        PaletteBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_vm is null)
                return;
            switch (e.Key)
            {
                case Key.Down:
                    _vm.Palette.Move(1);
                    break;
                case Key.Up:
                    _vm.Palette.Move(-1);
                    break;
                case Key.Enter:
                    _vm.Palette.OpenSelected();
                    break;
                case Key.Escape:
                    _vm.IsPaletteOpen = false;
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        PaletteBackdrop.PointerPressed += (_, _) =>
        {
            if (_vm is not null)
                _vm.IsPaletteOpen = false;
        };

        // Popups close on a click outside or Esc.
        DialogBackdrop.PointerPressed += (_, _) => _vm?.CloseDialog();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _vm?.HasDialog == true && !_vm.IsPaletteOpen)
            {
                _vm.CloseDialog();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Typing starts the moment the palette appears.
        if (e.PropertyName == nameof(MainViewModel.IsPaletteOpen) && _vm?.IsPaletteOpen == true)
            Dispatcher.UIThread.Post(() => PaletteBox.Focus(), DispatcherPriority.Input);
    }
}
