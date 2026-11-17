using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.App.Views;

public partial class RuleValueEditorView : UserControl
{
    public RuleValueEditorView()
    {
        InitializeComponent();
        // On the way down: the box handles Backspace itself (even when empty), so a bubbling
        // handler never hears it.
        EntryBox.AddHandler(KeyDownEvent, OnEntryKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Enter adds the typed entry as a chip (so does a comma, in the view model); Backspace in an empty
    /// box brings the last chip back to fix. Tab or clicking away leaves the text in the box as typed.
    /// </summary>
    void OnEntryKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not RuleValueEditor editor)
            return;
        if (e.Key == Key.Enter && editor.NewEntry.Trim().Length > 0)
        {
            editor.AddEntry();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && editor.NewEntry.Length == 0 && editor.HasChips)
        {
            editor.EditLastChip();
            EntryBox.CaretIndex = editor.NewEntry.Length;
            e.Handled = true;
        }
    }
}
