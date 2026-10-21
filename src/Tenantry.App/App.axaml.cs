using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Tenantry.App.Services;
using Tenantry.Core.Tenancy;
using Tenantry.ViewModels;

namespace Tenantry.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new ShellViewModel(new TenantConnector(), new JsonAppPreferencesStore());
            // Before the window shows, so an auto-opening tenant never flashes the home page.
            shell.PrepareStartup();
            var window = new MainWindow { DataContext = shell };
            Platform.OpenUrl = url => _ = window.Launcher.LaunchUriAsync(new Uri(url));
            Platform.CopyText = text => _ = CopyAsync(window, text);
            desktop.MainWindow = window;
            // Once the window is up (so a browser sign-in has something to return to).
            window.Opened += async (_, _) => await shell.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    static async Task CopyAsync(MainWindow window, string text)
    {
        if (window.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(text);
        // Hand it to the OS now: an unflushed clipboard on Windows blocks other apps' paste.
        await clipboard.FlushAsync();
    }
}
