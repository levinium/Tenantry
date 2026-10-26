using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace Tenantry.App.Controls;

/// <summary>
/// One rule for every pop-up in the app: clicking a button inside a flyout closes that flyout, so
/// an action never leaves its pop-up sitting over what it did (a dialog it opened, a change it
/// made). Menus (MenuFlyout) already close on their own. Exceptions: check boxes and toggles, a
/// button that opens a flyout of its own, and one marked <c>Classes="staysopen"</c>, where the
/// pop-up shows the result ("12 checked; 2 not found") or asks for more (a name to save under).
/// </summary>
public static class FlyoutDismiss
{
    public const string StaysOpen = "staysopen";

    /// <summary>Registers the rule once, for every button in the app.</summary>
    public static void Register() =>
        Button.ClickEvent.AddClassHandler<Button>(OnClick, RoutingStrategies.Bubble, handledEventsToo: true);

    static void OnClick(Button button, RoutedEventArgs e)
    {
        if (button is ToggleButton || button.Flyout is not null || button.Classes.Contains(StaysOpen))
            return;
        // After the click has run: closing now would detach the button from its data before its
        // command executes (seen in Contactry: the menu closed and Settings never opened).
        if (OwningPopup(button) is { IsOpen: true } popup)
            Dispatcher.UIThread.Post(() => popup.IsOpen = false, DispatcherPriority.Background);
    }

    /// <summary>The popup a control sits in, if any: through the logical tree, or the popup root's parent.</summary>
    internal static Popup? OwningPopup(Control control) =>
        control.FindLogicalAncestorOfType<Popup>()
        ?? (TopLevel.GetTopLevel(control) as PopupRoot)?.Parent as Popup;
}
