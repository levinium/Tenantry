namespace Tenantry.PowerShell;

/// <summary>Finds PowerShell 7 (pwsh) on this computer: on the PATH, or where its installers put it.</summary>
public static class PwshLocator
{
    /// <summary>Points Tenantry at a particular pwsh (a portable copy, say).</summary>
    public const string OverrideVariable = "TENANTRY_PWSH";

    public const string InstallHelp = "https://learn.microsoft.com/powershell/scripting/install/installing-powershell";

    public static string? Find()
    {
        if (Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } chosen && File.Exists(chosen))
            return chosen;
        var exe = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var path = Path.Combine(folder.Trim('"'), exe);
                if (File.Exists(path))
                    return path;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return Known().FirstOrDefault(File.Exists);
    }

    static IEnumerable<string> Known()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (root.Length > 0)
                    yield return Path.Combine(root, "PowerShell", "7", "pwsh.exe");
            // The Microsoft Store and winget install an alias here.
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe");
        }
        else
        {
            yield return "/usr/local/bin/pwsh";
            yield return "/opt/homebrew/bin/pwsh";
            yield return "/usr/bin/pwsh";
            yield return "/opt/microsoft/powershell/7/pwsh";
            yield return "/snap/bin/pwsh";
        }
    }
}
