using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    IDisposable? _background;
    IDisposable? _foreground;

    public Badge() => Apply(BadgeKind.Neutral);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
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
        _background = Bind(BackgroundProperty, this.GetResourceObservable($"Badge{kind}Bg"));
        _foreground = Bind(ForegroundProperty, this.GetResourceObservable($"Badge{kind}Fg"));
    }
}
