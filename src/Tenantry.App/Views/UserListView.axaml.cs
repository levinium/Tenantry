using Avalonia.Controls;
using Avalonia.Input;
using Tenantry.ViewModels;

namespace Tenantry.App.Views;

public partial class UserListView : UserControl
{
    public UserListView() => InitializeComponent();

    // Double-click opens the record. Enter does the same, for keyboard users.
    void OnRowDoubleTapped(object? sender, TappedEventArgs e) =>
        (DataContext as UserListViewModel)?.OpenSelectedCommand.Execute(null);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is UserListViewModel vm && vm.SelectedUser is not null)
        {
            vm.OpenSelectedCommand.Execute(null);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
