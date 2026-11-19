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
        // Leaving the box (Tab, a click elsewhere) makes the badge once focus has moved: changing the
        // badges while Avalonia moves focus drew the next control's focus outline twice and threw
        // "Border already has a visual parent AdornerLayer" (Mark, 2026-11-19).
        ExtensionBox.LostFocus += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => (DataContext as FindFilesViewModel)?.AddExtensionDraft());
        // A comma or space turns what's before it into a badge, once the box has finished taking the
        // keystroke (text changed from inside that update isn't shown); typing carries on after what's left.
        // Only what can be in an extension gets into the box: each keystroke is checked before the box
        // takes it, and anything pasted is cleaned up once it's in.
        ExtensionBox.AddHandler(TextInputEvent, OnExtensionText, RoutingStrategies.Tunnel);
        ExtensionBox.TextChanged += (_, _) =>
        {
            if (DataContext is FindFilesViewModel find && ExtensionBox.Text is { } pasted
                && pasted.Any(c => !FindFilesViewModel.IsExtensionCharacter(c) && !FindFilesViewModel.IsExtensionSeparator(c)))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    find.ExtensionDraft = find.KeepForExtensionBox(ExtensionBox.Text ?? "");
                    ExtensionBox.CaretIndex = ExtensionBox.Text?.Length ?? 0;
                });
                return;
            }
            if (ExtensionBox.Text?.IndexOfAny([',', ' ', ';']) is null or < 0)
                return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                (DataContext as FindFilesViewModel)?.TakeFinishedExtensions();
                ExtensionBox.CaretIndex = ExtensionBox.Text?.Length ?? 0;
            });
        };
    }

    void OnExtensionText(object? sender, TextInputEventArgs e)
    {
        if (DataContext is not FindFilesViewModel find || e.Text is not { Length: > 0 } typed)
            return;
        var kept = find.KeepForExtensionBox(typed);
        if (kept == typed)
            return;
        if (kept.Length == 0)
            e.Handled = true;
        else
            e.Text = kept;
    }

    void OnExtensionKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FindFilesViewModel find)
            return;
        switch (e.Key)
        {
            // Enter or Tab with something typed: it becomes a badge and you stay in the box for the next.
            // Tab in an empty box moves on as usual.
            case Key.Enter or Key.Tab when find.ExtensionDraft.Trim().Length > 0:
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
