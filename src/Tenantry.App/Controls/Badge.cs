using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Tenantry.ViewModels;

namespace Tenantry.App.Controls;

/// <summary>
/// A coloured status pill. The colours come from the theme tokens (BadgeSuccessBg/Fg and so
/// on) through live resource bindings, so a pill repaints when the theme changes.
/// </summary>
public class Badge : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Badge, string?>(nameof(Text));

    public static readonly StyledProperty<BadgeKind> KindProperty =
        AvaloniaProperty.Register<Badge, BadgeKind>(nameof(Kind));

    /// <summary>A small status dot before the text: for chips that say how something stands.</summary>
    public static readonly StyledProperty<bool> ShowDotProperty =
        AvaloniaProperty.Register<Badge, bool>(nameof(ShowDot));

    public static readonly StyledProperty<IBrush?> DotBrushProperty =
        AvaloniaProperty.Register<Badge, IBrush?>(nameof(DotBrush));

    IDisposable? _background;
    IDisposable? _foreground;
    IDisposable? _border;
    IDisposable? _dot;

    public Badge() => Apply(BadgeKind.Neutral);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool ShowDot
    {
        get => GetValue(ShowDotProperty);
        set => SetValue(ShowDotProperty, value);
    }

    public IBrush? DotBrush
    {
        get => GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    public BadgeKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty)
            Apply(change.GetNewValue<BadgeKind>());
    }

    void Apply(BadgeKind kind)
    {
        _background?.Dispose();
        _foreground?.Dispose();
        _border?.Dispose();
        _dot?.Dispose();
        _background = Bind(BackgroundProperty, this.GetResourceObservable($"Badge{kind}Bg"));
        _foreground = Bind(ForegroundProperty, this.GetResourceObservable($"Badge{kind}Fg"));
        _border = Bind(BorderBrushProperty, this.GetResourceObservable($"Badge{kind}Border"));
        _dot = Bind(DotBrushProperty, this.GetResourceObservable($"Badge{kind}Dot"));
    }
}
