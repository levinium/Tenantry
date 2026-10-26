namespace Tenantry.ViewModels;

/// <summary>
/// The few platform services view models need, set once by the app at startup. Tests leave
/// the defaults, which do nothing.
/// </summary>
public static class Platform
{
    /// <summary>Opens a web address in the user's browser.</summary>
    public static Action<string> OpenUrl { get; set; } = _ => { };

    /// <summary>Puts text on the clipboard.</summary>
    public static Action<string> CopyText { get; set; } = _ => { };

    /// <summary>Asks where to save a CSV file (suggesting a name) and writes it; false when cancelled.</summary>
    public static Func<string, string, Task<bool>> SaveCsv { get; set; } = (_, _) => Task.FromResult(false);
}
