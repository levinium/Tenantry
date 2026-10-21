using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Tenantry.App.Controls;

/// <summary>
/// Edits that take effect without a button: on a text box, Enter (unless it takes new lines) or
/// leaving the box runs <c>Commit</c>, and Esc runs <c>Cancel</c>. With <c>EnterUpdates</c>, Enter
/// also pushes the text to a binding that otherwise updates only when the box loses focus.
/// </summary>
public static class EditCommit
{
    public static readonly AttachedProperty<ICommand?> CommitProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("Commit", typeof(EditCommit));

    public static readonly AttachedProperty<ICommand?> CancelProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("Cancel", typeof(EditCommit));

    public static readonly AttachedProperty<bool> EnterUpdatesProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("EnterUpdates", typeof(EditCommit));

    static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Hooked", typeof(EditCommit));

    public static ICommand? GetCommit(TextBox box) => box.GetValue(CommitProperty);
    public static void SetCommit(TextBox box, ICommand? value) => box.SetValue(CommitProperty, value);
    public static ICommand? GetCancel(TextBox box) => box.GetValue(CancelProperty);
    public static void SetCancel(TextBox box, ICommand? value) => box.SetValue(CancelProperty, value);
    public static bool GetEnterUpdates(TextBox box) => box.GetValue(EnterUpdatesProperty);
    public static void SetEnterUpdates(TextBox box, bool value) => box.SetValue(EnterUpdatesProperty, value);

    static EditCommit()
    {
        CommitProperty.Changed.AddClassHandler<TextBox>((box, _) => Hook(box));
        CancelProperty.Changed.AddClassHandler<TextBox>((box, _) => Hook(box));
        EnterUpdatesProperty.Changed.AddClassHandler<TextBox>((box, _) => Hook(box));
    }

    static void Hook(TextBox box)
    {
        if (box.GetValue(HookedProperty))
            return;
        box.SetValue(HookedProperty, true);
        box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) => Run(GetCommit(box));
    }

    static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        if (e.Key == Key.Enter && !box.AcceptsReturn)
        {
            if (GetEnterUpdates(box))
                BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            Run(GetCommit(box));
            e.Handled = GetCommit(box) is not null || GetEnterUpdates(box);
        }
        else if (e.Key == Key.Escape && GetCancel(box) is { } cancel)
        {
            Run(cancel);
            e.Handled = true;
        }
    }

    static void Run(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
            command.Execute(null);
    }
}
