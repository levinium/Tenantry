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
