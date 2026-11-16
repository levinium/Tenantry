using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using FluentIcons.Avalonia;
using Avalonia.VisualTree;
using FluentIcons.Common;

namespace Tenantry.App.Controls;

/// <summary>
/// Gives a search box an × at its right end, shown while something is typed. Clicking it empties the
/// box, pushes the empty text to its binding straight away (even one that updates when the box is
/// left) and puts the cursor back in the box. Anything already at the box's right end stays, after the ×.
/// </summary>
public static class SearchClear
{
    public static readonly AttachedProperty<bool> IsOnProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsOn", typeof(SearchClear));

    public static bool GetIsOn(TextBox box) => box.GetValue(IsOnProperty);
    public static void SetIsOn(TextBox box, bool value) => box.SetValue(IsOnProperty, value);

    static readonly AttachedProperty<Button?> ButtonProperty =
        AvaloniaProperty.RegisterAttached<TextBox, Button?>("Button", typeof(SearchClear));

    // Hooked when the box is first shown, so right-end content set in XAML after this property is kept.
    static SearchClear() => IsOnProperty.Changed.AddClassHandler<TextBox>((box, e) =>
    {
        if (e.NewValue is not true)
            return;
        if (box.IsAttachedToVisualTree())
            Hook(box);
        else
            box.AttachedToVisualTree += (_, _) => Hook(box);
    });

    static void Hook(TextBox box)
    {
        if (box.GetValue(ButtonProperty) is not null || !GetIsOn(box))
            return;
        var button = new Button
        {
            Classes = { "chipx", "searchclear" },
            Content = new FluentIcon { Icon = Icon.Dismiss, FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Focusable = false,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = !string.IsNullOrEmpty(box.Text),
        };
        AutomationProperties.SetName(button, "Clear Search");
        button.Click += (_, _) => Clear(box);
        box.SetValue(ButtonProperty, button);

        var existing = box.InnerRightContent;
        box.InnerRightContent = existing is null
            ? button
            : new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { button, AsControl(existing) } };

        box.GetObservable(TextBox.TextProperty).Subscribe(new Watch(text => button.IsVisible = !string.IsNullOrEmpty(text)));
    }

    static Control AsControl(object content) => content as Control ?? new ContentControl { Content = content };

    /// <summary>Empties the box as the × does.</summary>
    public static void Clear(TextBox box)
    {
        box.Text = "";
        BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
        box.Focus();
    }

    sealed class Watch(Action<string?> next) : IObserver<string?>
    {
        public void OnNext(string? value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
