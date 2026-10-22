using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Tenantry.ViewModels.Records;

namespace Tenantry.App.Controls;

/// <summary>
/// Tints a value for a moment when a record check finds it changed in Microsoft 365: a soft
/// accent wash that fades over a couple of seconds, with a tooltip saying why. Put it on the
/// Border around a value: <c>c:FreshHighlight.Source="{Binding Freshness}" c:FreshHighlight.Keys="title,department"</c>
/// (the list fields' keys the value shows).
/// </summary>
public static class FreshHighlight
{
    public static readonly AttachedProperty<RecordFreshness?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Border, RecordFreshness?>("Source", typeof(FreshHighlight));

    public static readonly AttachedProperty<string?> KeysProperty =
        AvaloniaProperty.RegisterAttached<Border, string?>("Keys", typeof(FreshHighlight));

    static readonly AttachedProperty<EventHandler<IReadOnlySet<string>>?> HandlerProperty =
        AvaloniaProperty.RegisterAttached<Border, EventHandler<IReadOnlySet<string>>?>("Handler", typeof(FreshHighlight));

    public static RecordFreshness? GetSource(Border border) => border.GetValue(SourceProperty);
    public static void SetSource(Border border, RecordFreshness? value) => border.SetValue(SourceProperty, value);
    public static string? GetKeys(Border border) => border.GetValue(KeysProperty);
    public static void SetKeys(Border border, string? value) => border.SetValue(KeysProperty, value);

    /// <summary>How long the tint takes to fade away.</summary>
    static readonly TimeSpan Fade = TimeSpan.FromSeconds(2.5);

    /// <summary>How long the "updated" tooltip stays on the value.</summary>
    static readonly TimeSpan TipFor = TimeSpan.FromSeconds(15);

    const string Tip = "Updated from Microsoft 365 just now: this changed since the last sync.";

    static FreshHighlight()
    {
        SourceProperty.Changed.AddClassHandler<Border>((border, e) =>
        {
            if (e.OldValue is RecordFreshness old && border.GetValue(HandlerProperty) is { } previous)
                old.Highlight -= previous;
            if (e.NewValue is not RecordFreshness source)
                return;
            EventHandler<IReadOnlySet<string>> handler = (_, keys) => Dispatcher.UIThread.Post(() => OnHighlight(border, keys));
            border.SetValue(HandlerProperty, handler);
            source.Highlight += handler;
        });
    }

    static void OnHighlight(Border border, IReadOnlySet<string> changed)
    {
        // A page closed since: nothing to tint.
        if (!border.IsAttachedToVisualTree() || GetKeys(border) is not { Length: > 0 } keys)
            return;
        if (!keys.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(changed.Contains))
            return;
        if (!border.TryFindResource("FreshBg", border.ActualThemeVariant, out var found) || found is not ISolidColorBrush tint)
            return;
        // Straight to the tint, then fade back to the same colour, fully transparent (never through grey).
        border.Transitions = null;
        border.Background = new SolidColorBrush(tint.Color);
        Dispatcher.UIThread.Post(() =>
        {
            border.Transitions = [new BrushTransition { Property = Border.BackgroundProperty, Duration = Fade, Easing = new CubicEaseIn() }];
            border.Background = new SolidColorBrush(Color.FromArgb(0, tint.Color.R, tint.Color.G, tint.Color.B));
        }, DispatcherPriority.Background);

        var earlierTip = ToolTip.GetTip(border);
        ToolTip.SetTip(border, Tip);
        DispatcherTimer.RunOnce(() =>
        {
            if (Equals(ToolTip.GetTip(border), Tip))
                ToolTip.SetTip(border, earlierTip);
        }, TipFor);
    }

    static bool IsAttachedToVisualTree(this Visual visual) => TopLevel.GetTopLevel(visual) is not null;
}
