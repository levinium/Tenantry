using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Tenantry.Demo;
using Tenantry.ViewModels;

namespace Tenantry.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Demo tenant only until sign-in exists (Phase 1).
            var main = new MainViewModel(new DemoTenantDirectory());
            desktop.MainWindow = new MainWindow { DataContext = main };
            _ = main.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
