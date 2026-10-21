using Tenantry.Core.Tenancy;

namespace Tenantry.Tests;

/// <summary>How big the main window opens on different screens, and when a saved placement comes back.</summary>
public sealed class WindowSizingTests
{
    [Theory]
    // 1080p at 100%, taskbar 48 px: 90% of the usable area, centred.
    [InlineData(1920, 1032, 1.0, 1728, 929)]
    // 1080p laptop at 150%: the same share; in logical pixels that's 1152 × 619.
    [InlineData(1920, 1032, 1.5, 1728, 929)]
    // 4K at 150%: 90% would be 3456 physical = 2304 logical, under the cap.
    [InlineData(3840, 2088, 1.5, 3456, 1879)]
    // Ultrawide at 100%: capped at 2400 × 1500 logical so it doesn't sprawl.
    [InlineData(5120, 1392, 1.0, 2400, 1253)]
    // Small laptop screen (1366 × 728 usable at 125%): still 90%, above the 800 × 500 minimum.
    [InlineData(1366, 728, 1.25, 1229, 655)]
    public void A_first_launch_takes_most_of_the_screen_within_limits(int screenWidth, int screenHeight, double scaling, int width, int height)
    {
        var placement = WindowSizing.FirstLaunch(new ScreenArea(0, 0, screenWidth, screenHeight, scaling));

        Assert.Equal((width, height), (placement.Width, placement.Height));
        Assert.Equal(((screenWidth - width) / 2, (screenHeight - height) / 2), (placement.X, placement.Y));
        Assert.False(placement.Maximized);
    }

    static readonly ScreenArea Main = new(0, 0, 1920, 1032, 1.0);
    static readonly ScreenArea Right = new(1920, 0, 2560, 1400, 1.0);

    [Fact]
    public void A_saved_placement_comes_back_where_it_was()
    {
        var saved = new WindowPlacement(2100, 80, 2000, 1200, Maximized: true);

        Assert.Equal(saved, WindowSizing.Restore(saved, [Main, Right]));
    }

    [Fact]
    public void A_window_left_on_an_unplugged_screen_starts_fresh()
    {
        var saved = new WindowPlacement(2100, 80, 2000, 1200, false);

        Assert.Null(WindowSizing.Restore(saved, [Main]));
    }

    [Fact]
    public void A_window_too_big_or_hanging_off_its_screen_is_brought_inside_it()
    {
        // Saved on a bigger screen at the same spot: shrunk to fit and moved back on.
        var saved = new WindowPlacement(400, 300, 2400, 1400, false);

        var restored = WindowSizing.Restore(saved, [Main])!;

        Assert.Equal(new WindowPlacement(0, 0, 1920, 1032, false), restored);
    }
}
