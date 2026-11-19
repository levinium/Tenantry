using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tenantry.ViewModels;

namespace Tenantry.App.Views;

public partial class FindFilesView : UserControl
{
    public FindFilesView()
    {
        InitializeComponent();
        // Enter in the extensions box makes a badge; it doesn't start the search (Find Files is the default button).
        ExtensionBox.AddHandler(KeyDownEvent, OnExtensionKey, RoutingStrategies.Tunnel);
        ExtensionBox.LostFocus += (_, _) => (DataContext as FindFilesViewModel)?.AddExtensionDraft();
        // A comma or space turns what's before it into a badge: keep typing at the end of what's left.
        ExtensionBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && ExtensionBox.Text is { } text && ExtensionBox.CaretIndex > text.Length)
                ExtensionBox.CaretIndex = text.Length;
        };
    }

    void OnExtensionKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FindFilesViewModel find)
            return;
        switch (e.Key)
        {
            case Key.Enter when find.ExtensionDraft.Trim().Length > 0:
                find.AddExtensionDraft();
                e.Handled = true;
                break;
            case Key.Back when find.ExtensionDraft.Length == 0:
                find.RemoveLastExtension();
                e.Handled = true;
                break;
        }
    }
}
