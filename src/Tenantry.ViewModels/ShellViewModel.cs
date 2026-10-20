using CommunityToolkit.Mvvm.ComponentModel;

namespace Tenantry.ViewModels;

/// <summary>
/// The window's root: shows the welcome screen, the setup wizard or a connected tenant, and
/// moves between them.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    readonly ITenantConnector _connector;

    public ShellViewModel(ITenantConnector connector)
    {
        _connector = connector;
        CurrentPage = NewWelcome();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial object CurrentPage { get; private set; }

    public string Title => CurrentPage is MainViewModel main ? main.WindowTitle : "Tenantry";

    /// <summary>Called once at app start: opens the default tenant, or the only one.</summary>
    public Task StartAsync() => CurrentPage is WelcomeViewModel welcome ? welcome.OpenStartupTenantAsync() : Task.CompletedTask;

    /// <summary>Shows a page directly (snapshots and tests drive pages this way).</summary>
    public void ShowPage(object page) => CurrentPage = page;

    WelcomeViewModel NewWelcome() => new(_connector, ShowWizard, Open);

    void ShowWelcome()
    {
        // Leaving a tenant stops its periodic sync and closes its local database.
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = NewWelcome();
    }

    void ShowWizard() => CurrentPage = new SetupWizardViewModel(_connector, ShowWelcome, ShowWelcome);

    void Open(ConnectedTenant tenant)
    {
        var main = new MainViewModel(tenant, ShowWelcome, syncInterval: MainViewModel.DefaultSyncInterval);
        CurrentPage = main;
        _ = main.InitializeAsync();
    }
}
