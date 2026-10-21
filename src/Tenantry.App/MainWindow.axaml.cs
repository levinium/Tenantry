using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Tenantry.Core.Tenancy;

namespace Tenantry.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    WindowPlacement? _normal;

    /// <summary>
    /// Opens where the person left the window (if that's still on a screen), else large on the
    /// primary screen; remembers the placement when the window closes.
    /// </summary>
    public void RememberPlacement(WindowPlacementStore store)
    {
        var screens = Screens.All
            .Select(s => new ScreenArea(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height, s.Scaling))
            .ToArray();
        var primary = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (primary is not null && screens.Length > 0)
        {
            var placement = (store.Load() is { } saved ? WindowSizing.Restore(saved, screens) : null)
                ?? WindowSizing.FirstLaunch(new ScreenArea(primary.WorkingArea.X, primary.WorkingArea.Y,
                    primary.WorkingArea.Width, primary.WorkingArea.Height, primary.Scaling));
            var scaling = screens.FirstOrDefault(s => Contains(s, placement.X, placement.Y)) is { Width: > 0 } on ? on.Scaling : primary.Scaling;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(placement.X, placement.Y);
            Width = placement.Width / scaling;
            Height = placement.Height / scaling;
            _normal = placement with { Maximized = false };
            if (placement.Maximized)
                WindowState = WindowState.Maximized;
        }

        // Only a normal window's bounds are kept, so un-maximizing next time returns there. Checked
        // once things settle: maximizing moves and resizes the window before its state says so.
        PositionChanged += (_, _) => Dispatcher.UIThread.Post(Track, DispatcherPriority.Background);
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(Track, DispatcherPriority.Background);
        Closing += (_, _) =>
        {
            if (_normal is { } normal && WindowState != WindowState.Minimized)
                store.Save(normal with { Maximized = WindowState is WindowState.Maximized or WindowState.FullScreen });
        };
    }

    void Track()
    {
        if (WindowState != WindowState.Normal || !IsVisible)
            return;
        var scaling = DesktopScaling;
        _normal = new WindowPlacement(Position.X, Position.Y, (int)Math.Round(Bounds.Width * scaling), (int)Math.Round(Bounds.Height * scaling), false);
    }

    static bool Contains(ScreenArea s, int x, int y) => x >= s.X && x < s.X + s.Width && y >= s.Y && y < s.Y + s.Height;
}
