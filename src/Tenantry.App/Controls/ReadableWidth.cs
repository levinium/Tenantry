using Avalonia;
using Avalonia.Controls;

namespace Tenantry.App.Controls;

/// <summary>
/// Holds a list at a readable width on a wide screen: as wide as there's room for, up to
/// <see cref="Max"/>, at the left of its card under its heading (centred, it left a wide gap on the
/// left that lined up with nothing). Rows inside still fill that width, so a row's buttons sit near
/// its name instead of at the far side of the window.
/// </summary>
public sealed class ReadableWidth : Decorator
{
    public static readonly StyledProperty<double> MaxProperty = AvaloniaProperty.Register<ReadableWidth, double>(nameof(Max), 720);

    public double Max
    {
        get => GetValue(MaxProperty);
        set => SetValue(MaxProperty, value);
    }

    static ReadableWidth() => AffectsMeasure<ReadableWidth>(MaxProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null)
            return default;
        var width = Math.Min(availableSize.Width, Max);
        Child.Measure(availableSize.WithWidth(width));
        return new Size(double.IsInfinity(availableSize.Width) ? Child.DesiredSize.Width : availableSize.Width, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, Math.Min(finalSize.Width, Max), finalSize.Height));
        return finalSize;
    }
}
