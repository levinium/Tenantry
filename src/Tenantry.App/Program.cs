using Avalonia;
using Tenantry.Core.Tenancy;

namespace Tenantry.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Named exactly this because Avalonia's XAML previewer looks it up by convention.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Keeps the exception of a crash in logs/crash-*.txt under the app-data folder (never the
    /// repository), so a crash can be diagnosed afterwards. Stack traces only: no tenant data.
    /// </summary>
    static void WriteCrashLog(object exception)
    {
        try
        {
            var folder = Path.Combine(AppPaths.Root, "logs");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),
                $"Tenantry {typeof(Program).Assembly.GetName().Version} on {Environment.OSVersion}{Environment.NewLine}{exception}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Crashing anyway; nothing more to do.
        }
    }
}
