using Avalonia.Platform.Storage;
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
            Platform.OpenCsv = () => OpenCsvAsync(window);
            Platform.PickFolder = () => PickFolderAsync(window);
            Platform.OpenFolder = path => _ = window.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
            desktop.MainWindow = window;
            // Last chance for an error no screen caught (a button's command, a key handler): it's
            // written to the crash log and said above the page, and the app keeps running.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Program.WriteCrashLog(e.Exception);
                shell.ShowProblem(e.Exception);
                e.Handled = true;
            };
            // Once the window is up (so a browser sign-in has something to return to).
            window.Opened += async (_, _) => await shell.StartAsync();
            UpdateChecker.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    static async Task<(string Name, string Text)?> OpenCsvAsync(MainWindow window)
    {
        var files = await window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose a CSV File",
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("CSV File") { Patterns = ["*.csv"] }],
        });
        if (files.Count == 0)
            return null;
        await using var stream = await files[0].OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return (files[0].Name, DecodeCsv(buffer.ToArray()));
    }

    /// <summary>
    /// Excel saves "CSV UTF-8" with a byte-order mark, and plain "CSV" in the Windows code page
    /// (Zoë's ë is one byte there, not valid UTF-8). A mark decides; otherwise strict UTF-8, and
    /// Windows-1252 when that fails.
    /// </summary>
    internal static string DecodeCsv(byte[] bytes)
    {
        using (var marked = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            marked.Peek();
            if (marked.CurrentEncoding is not System.Text.UTF8Encoding || bytes is [0xEF, 0xBB, 0xBF, ..])
                return marked.ReadToEnd();
        }
        try
        {
            return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(bytes);
        }
    }

    static async Task<string?> PickFolderAsync(MainWindow window)
    {
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Choose a Folder",
            AllowMultiple = false,
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    static async Task<bool> SaveCsvAsync(MainWindow window, string suggestedName, string csv)
    {
        var file = await window.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save as a CSV File",
            SuggestedFileName = suggestedName,
            DefaultExtension = "csv",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("CSV File") { Patterns = ["*.csv"] }],
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
