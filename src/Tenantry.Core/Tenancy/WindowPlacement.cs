using System.Text.Json;

namespace Tenantry.Core.Tenancy;

/// <summary>
/// Where the main window was and how big, in physical (device) pixels, plus whether it was
/// maximized. The rectangle is the normal (not maximized) one, so un-maximizing returns there.
/// </summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>A screen's usable area (without the taskbar or dock) in physical pixels, and its scaling (1.25 = 125%).</summary>
public readonly record struct ScreenArea(int X, int Y, int Width, int Height, double Scaling);

/// <summary>
/// How big the main window opens. Tenantry shows a lot at once, so the first launch fills most of
/// the screen it opens on, whatever its resolution or scaling; after that the window comes back
/// where the person left it, as long as that's still on a connected screen.
/// </summary>
public static class WindowSizing
{
    /// <summary>The share of the screen's usable area a first launch takes.</summary>
    public const double ScreenShare = 0.9;

    /// <summary>Beyond this (in logical pixels) a bigger window only spreads things out.</summary>
    public const double MaxWidth = 2400, MaxHeight = 1500;

    /// <summary>The window's own minimum, as in MainWindow.axaml.</summary>
    public const double MinWidth = 800, MinHeight = 500;

    /// <summary>How much of a saved window must be on a screen to bring it back there.</summary>
    const int MinVisible = 120;

    public static WindowPlacement FirstLaunch(ScreenArea screen)
    {
        var width = Fit(screen.Width, MinWidth, MaxWidth, screen.Scaling);
        var height = Fit(screen.Height, MinHeight, MaxHeight, screen.Scaling);
        return new WindowPlacement(screen.X + (screen.Width - width) / 2, screen.Y + (screen.Height - height) / 2, width, height, false);
    }

    /// <summary>
    /// The saved placement, shrunk and moved onto the screen it's mostly on; null when it's no
    /// longer on any screen (a monitor was unplugged), so the caller falls back to a first launch.
    /// </summary>
    public static WindowPlacement? Restore(WindowPlacement saved, IReadOnlyList<ScreenArea> screens)
    {
        if (saved.Width <= 0 || saved.Height <= 0)
            return null;
        var best = screens
            .Select(s => (Screen: s, Overlap: Overlap(saved, s)))
            .Where(o => o.Overlap.Width >= MinVisible && o.Overlap.Height >= MinVisible)
            .OrderByDescending(o => (long)o.Overlap.Width * o.Overlap.Height)
            .Select(o => (ScreenArea?)o.Screen)
            .FirstOrDefault();
        if (best is not { } screen)
            return null;
        var width = Math.Min(saved.Width, screen.Width);
        var height = Math.Min(saved.Height, screen.Height);
        var x = Math.Clamp(saved.X, screen.X, screen.X + screen.Width - width);
        var y = Math.Clamp(saved.Y, screen.Y, screen.Y + screen.Height - height);
        return saved with { X = x, Y = y, Width = width, Height = height };
    }

    static int Fit(int available, double min, double max, double scaling)
    {
        var wanted = Math.Min(available * ScreenShare, max * scaling);
        return (int)Math.Round(Math.Min(available, Math.Max(wanted, min * scaling)));
    }

    static (int Width, int Height) Overlap(WindowPlacement w, ScreenArea s) =>
        (Math.Min(w.X + w.Width, s.X + s.Width) - Math.Max(w.X, s.X),
         Math.Min(w.Y + w.Height, s.Y + s.Height) - Math.Max(w.Y, s.Y));
}

/// <summary>The main window's placement, kept in window.json beside the tenant profiles. Unreadable means none.</summary>
public sealed class WindowPlacementStore(string? path = null)
{
    readonly string _path = path ?? Path.Combine(AppPaths.Root, "window.json");

    public WindowPlacement? Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(_path)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(WindowPlacement placement)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(placement));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the placement only means the next launch opens at the default size.
        }
    }
}
