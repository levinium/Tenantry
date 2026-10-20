using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Tenantry.App.Services;
using Tenantry.ViewModels;

namespace Tenantry.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = new ShellViewModel(new TenantConnector()) };
            Platform.OpenUrl = url => _ = window.Launcher.LaunchUriAsync(new Uri(url));
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
