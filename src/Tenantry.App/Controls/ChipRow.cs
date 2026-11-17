using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Tenantry.App.Controls;

/// <summary>
/// Chips on one line, in order, as many as fit. The ones that don't fit are hidden (and reported
/// through <see cref="HiddenChanged"/>, so they can be offered elsewhere), rather than wrapping
/// one or two onto a line of their own. A child marked <see cref="KeepProperty"/> always shows,
/// in place of the last ones before it.
/// </summary>
public sealed class ChipRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<ChipRow, double>(nameof(Spacing), 8);

    public static readonly AttachedProperty<bool> KeepProperty = AvaloniaProperty.RegisterAttached<ChipRow, Control, bool>("Keep");

    public static bool GetKeep(Control control) => control.GetValue(KeepProperty);
    public static void SetKeep(Control control, bool value) => control.SetValue(KeepProperty, value);

    static ChipRow()
    {
        AffectsMeasure<ChipRow>(SpacingProperty);
        AffectsParentMeasure<ChipRow>(KeepProperty);
    }

    public ChipRow() => ClipToBounds = true;

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The data of each chip that doesn't fit, in order.</summary>
    public IReadOnlyList<object?> Hidden { get; private set; } = [];

    public event EventHandler? HiddenChanged;

    readonly HashSet<Control> _shown = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children.Where(c => c.IsVisible).ToList();
        foreach (var child in children)
            child.Measure(Size.Infinity);
        double Width(IEnumerable<Control> set) => set.Sum(c => c.DesiredSize.Width) + Math.Max(0, set.Count() - 1) * Spacing;

        var room = availableSize.Width;
        var shown = new List<Control>();
        foreach (var child in children)
        {
            if (Width(shown.Append(child)) > room)
                break;
            shown.Add(child);
        }
        // The one that must show (where you are) takes the place of the last ones that fit.
        foreach (var keep in children.Where(c => GetKeep(c) && !shown.Contains(c)))
        {
            shown.Add(keep);
            while (Width(shown) > room && shown.FindLastIndex(c => !GetKeep(c)) is var last and >= 0)
                shown.RemoveAt(last);
        }
        _shown.Clear();
        _shown.UnionWith(shown);

        var hidden = children.Where(c => !_shown.Contains(c)).Select(c => c.DataContext).ToArray();
        if (!hidden.SequenceEqual(Hidden))
        {
            Hidden = hidden;
            // After layout: whoever listens may change what's on screen.
            Dispatcher.UIThread.Post(() => HiddenChanged?.Invoke(this, EventArgs.Empty));
        }
        var height = shown.Count == 0 ? 0 : shown.Max(c => c.DesiredSize.Height);
        return new Size(Math.Min(Width(shown), double.IsInfinity(room) ? double.MaxValue : room), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (var child in Children)
        {
            if (_shown.Contains(child))
            {
                child.Arrange(new Rect(x, (finalSize.Height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
                x += child.DesiredSize.Width + Spacing;
            }
            else
                // Off the end, where the clip hides it.
                child.Arrange(new Rect(finalSize.Width + 1000, 0, child.DesiredSize.Width, child.DesiredSize.Height));
        }
        return finalSize;
    }
}
