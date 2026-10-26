using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.App.Views;

public partial class RuleValueEditorView : UserControl
{
    public RuleValueEditorView() => InitializeComponent();

    /// <summary>Enter adds the typed entry as a chip; Backspace in an empty box brings the last chip back to fix.</summary>
    void OnEntryKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not RuleValueEditor editor)
            return;
        if (e.Key is Key.Enter or Key.Tab && editor.NewEntry.Trim().Length > 0)
        {
            editor.AddEntry();
            e.Handled = e.Key == Key.Enter;
        }
        else if (e.Key == Key.Back && editor.NewEntry.Length == 0 && editor.HasChips)
        {
            editor.EditLastChip();
            EntryBox.CaretIndex = editor.NewEntry.Length;
            e.Handled = true;
        }
    }

    /// <summary>Leaving the box keeps what was typed: it becomes a chip.</summary>
    void OnEntryLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RuleValueEditor editor && editor.NewEntry.Trim().Length > 0)
            editor.AddEntry();
    }
}
