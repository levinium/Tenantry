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

    /// <summary>Asks where to save a file (suggested name, type name, extension) and writes the bytes; false when cancelled.</summary>
    public static Func<string, string, string, byte[], Task<bool>> SaveFile { get; set; } = (_, _, _, _) => Task.FromResult(false);

    /// <summary>Asks for any file (with the picker's title) and reads it: its name and bytes, or null when cancelled.</summary>
    public static Func<string, Task<(string Name, byte[] Bytes)?>> OpenFile { get; set; } = _ => Task.FromResult<(string, byte[])?>(null);

    /// <summary>Asks for a CSV file and reads it: its name and text, or null when cancelled.</summary>
    public static Func<Task<(string Name, string Text)?>> OpenCsv { get; set; } = () => Task.FromResult<(string, string)?>(null);

    /// <summary>Asks for a folder on this computer: its path, or null when cancelled.</summary>
    public static Func<Task<string?>> PickFolder { get; set; } = () => Task.FromResult<string?>(null);

    /// <summary>Shows a folder on this computer in the system's file browser.</summary>
    public static Action<string> OpenFolder { get; set; } = _ => { };
}

/// <summary>
/// Brings the local copy of the directory up to date before it matters: changes since the last
/// check, usually a few seconds, skipped when the copy was checked within the age given. Set by
/// the open tenant; returns null when up to date, or a note saying why it couldn't be.
/// </summary>
public static class Freshness
{
    /// <summary>Exports, Import and Apply: Microsoft 365 as it is now (checked within the last few seconds).</summary>
    public static readonly TimeSpan ForAction = TimeSpan.FromSeconds(15);

    /// <summary>Opening a record or preview, or ticking rows.</summary>
    public static readonly TimeSpan ForRecord = TimeSpan.FromSeconds(30);

    /// <summary>Coming back to a list or report.</summary>
    public static readonly TimeSpan ForList = TimeSpan.FromMinutes(2);

    /// <summary>The open tenant's check, given the oldest copy that will do.</summary>
    public static Func<TimeSpan, Task<string?>>? BringUpToDate { get; set; }

    /// <summary>Checks when the copy is older than <paramref name="maxAge"/>; a note when it couldn't.</summary>
    public static async Task<string?> EnsureAsync(TimeSpan maxAge) =>
        BringUpToDate is { } check ? await check(maxAge) : null;

    public static Task<string?> BeforeExportAsync() => EnsureAsync(ForAction);
}
