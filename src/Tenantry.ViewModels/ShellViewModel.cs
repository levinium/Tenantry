using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Tenancy;

namespace Tenantry.ViewModels;

/// <summary>
/// The window's root: shows the welcome screen, the setup wizard or a connected tenant, and
/// moves between them.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    readonly ITenantConnector _connector;
    readonly IAppPreferencesStore _preferences;

    /// <param name="preferences">Where app-wide choices are remembered; in memory when null (tests, snapshots).</param>
    public ShellViewModel(ITenantConnector connector, IAppPreferencesStore? preferences = null)
    {
        _connector = connector;
        _preferences = preferences ?? new MemoryAppPreferencesStore();
        CurrentPage = NewWelcome();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial object CurrentPage { get; private set; }

    public string Title => CurrentPage is MainViewModel main ? main.WindowTitle : "Tenantry";

    /// <summary>
    /// Called before the window first shows. When a tenant will open by itself (the default, or
    /// the only one), the window starts on an "Opening…" page rather than flashing the home page.
    /// </summary>
    public void PrepareStartup()
    {
        if (CurrentPage is WelcomeViewModel welcome && welcome.StartupTenant is { } tenant)
            CurrentPage = new StartingViewModel(welcome, tenant.DisplayName);
    }

    /// <summary>Called once at app start: opens the default tenant, or the only one.</summary>
    public async Task StartAsync()
    {
        var welcome = CurrentPage switch
        {
            StartingViewModel starting => starting.Welcome,
            WelcomeViewModel home => home,
            _ => null,
        };
        if (welcome is null)
            return;
        await welcome.OpenStartupTenantAsync();
        // Still waiting means the sign-in failed or was cancelled: the home page says which.
        if (CurrentPage is StartingViewModel)
            CurrentPage = welcome;
    }

    /// <summary>Something that went wrong unexpectedly, shown above the page until dismissed; the app keeps running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    /// <summary>An error no screen caught (the app's last-chance handler): said once, in plain words.</summary>
    public void ShowProblem(Exception exception) =>
        Problem = $"Something went wrong: {Friendly.Message(exception)} Tenantry kept running; if it happens again, try the last step once more.";

    [RelayCommand]
    void DismissProblem() => Problem = null;

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
        var main = new MainViewModel(tenant, ShowWelcome, syncInterval: MainViewModel.DefaultSyncInterval, preferences: _preferences);
        CurrentPage = main;
        _ = main.InitializeAsync();
    }
}
