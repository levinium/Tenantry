using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.App.Controls;

/// <summary>
/// A horizontal stacked bar: each segment's width is its share of <see cref="Maximum"/>, so bars
/// sharing a maximum compare by length. A segment is coloured by the theme brush "Chart" + its
/// kind (ChartGoodMail, ChartSpamDetections…), falling back to ChartOther; it repaints on theme change.
/// </summary>
public class SegmentBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<BarSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentBar, IReadOnlyList<BarSegment>?>(nameof(Segments));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<SegmentBar, double>(nameof(Maximum), 1);

    /// <summary>A single-colour bar: a fraction (0 to 1) of the width, in the brush named by <see cref="Kind"/>.</summary>
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<SegmentBar, double>(nameof(Fraction), double.NaN);

    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<SegmentBar, string?>(nameof(Kind));

    static SegmentBar()
    {
        AffectsRender<SegmentBar>(SegmentsProperty, MaximumProperty, FractionProperty, KindProperty);
    }

    public SegmentBar()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<BarSegment>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Fraction { get => GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 12 : Height);

    IBrush BrushFor(string? kind) =>
        (kind is not null && this.TryFindResource("Chart" + kind, ActualThemeVariant, out var b) && b is IBrush brush) ? brush
        : this.TryFindResource("ChartOther", ActualThemeVariant, out var o) && o is IBrush other ? other : Brushes.Gray;

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;
        if (this.TryFindResource("ChartTrack", ActualThemeVariant, out var t) && t is IBrush track)
            context.DrawRectangle(track, null, new Rect(0, 0, width, height), 3, 3);
        if (!double.IsNaN(Fraction))
        {
            var w = Math.Clamp(Fraction, 0, 1) * width;
            if (w > 0)
                context.DrawRectangle(BrushFor(Kind), null, new Rect(0, 0, Math.Max(2, w), height), 3, 3);
            return;
        }
        if (Segments is not { Count: > 0 } segments || Maximum <= 0)
            return;
        var x = 0.0;
        using (context.PushClip(new RoundedRect(new Rect(0, 0, width, height), 3)))
            foreach (var segment in segments)
            {
                if (segment.Value <= 0)
                    continue;
                var w = segment.Value / Maximum * width;
                context.DrawRectangle(BrushFor(segment.Kind), null, new Rect(x, 0, Math.Max(1, w), height));
                x += w;
            }
    }
}
