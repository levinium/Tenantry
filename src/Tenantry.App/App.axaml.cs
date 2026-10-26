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
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Every pop-up closes when a button inside it is clicked (see FlyoutDismiss).
        Controls.FlyoutDismiss.Register();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new ShellViewModel(new TenantConnector(), new JsonAppPreferencesStore());
            // Before the window shows, so an auto-opening tenant never flashes the home page.
            shell.PrepareStartup();
            var window = new MainWindow { DataContext = shell };
            window.RememberPlacement(new WindowPlacementStore());
            Platform.OpenUrl = url => _ = window.Launcher.LaunchUriAsync(new Uri(url));
            Platform.CopyText = text => _ = CopyAsync(window, text);
            Platform.SaveCsv = (name, csv) => SaveCsvAsync(window, name, csv);
            desktop.MainWindow = window;
            // Once the window is up (so a browser sign-in has something to return to).
            window.Opened += async (_, _) => await shell.StartAsync();
            UpdateChecker.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    static async Task<bool> SaveCsvAsync(MainWindow window, string suggestedName, string csv)
    {
        var file = await window.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save as a CSV file",
            SuggestedFileName = suggestedName,
            DefaultExtension = "csv",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("CSV file") { Patterns = ["*.csv"] }],
        });
        if (file is null)
            return false;
        await using var stream = await file.OpenWriteAsync();
        // UTF-8 with a byte-order mark, so Excel reads names with accents correctly.
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteAsync(csv);
        return true;
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
