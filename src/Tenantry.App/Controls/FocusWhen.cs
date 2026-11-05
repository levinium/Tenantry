using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Tenantry.App.Controls;

/// <summary>
/// Puts the cursor in a box when a condition turns true: <c>c:FocusWhen.Value="{Binding IsAdding}"</c>,
/// so a form opened by an Add button is ready to type into.
/// </summary>
public static class FocusWhen
{
    public static readonly AttachedProperty<bool> ValueProperty =
        AvaloniaProperty.RegisterAttached<InputElement, bool>("Value", typeof(FocusWhen));

    static FocusWhen() => ValueProperty.Changed.AddClassHandler<InputElement>((element, e) =>
    {
        if (e.NewValue is true)
            // After the layout pass that makes the form visible; a hidden box can't take focus.
            Dispatcher.UIThread.Post(() => element.Focus(), DispatcherPriority.Input);
    });

    public static bool GetValue(InputElement element) => element.GetValue(ValueProperty);

    public static void SetValue(InputElement element, bool value) => element.SetValue(ValueProperty, value);
}
