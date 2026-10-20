using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Tenancy;

namespace Tenantry.ViewModels;

/// <summary>Start screen: saved tenants, add a tenant, or open the demo.</summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    readonly ITenantConnector _connector;
    readonly Action _addTenant;
    readonly Action<ConnectedTenant> _open;

    public WelcomeViewModel(ITenantConnector connector, Action addTenant, Action<ConnectedTenant> open)
    {
        _connector = connector;
        _addTenant = addTenant;
        _open = open;
        var now = DateTimeOffset.Now;
        Profiles = new ObservableCollection<TenantCard>(connector.LoadProfiles()
            .OrderByDescending(p => p.LastUsedAt ?? DateTimeOffset.MinValue)
            .Select(p => new TenantCard(p, now)));
    }

    public ObservableCollection<TenantCard> Profiles { get; }

    public bool HasProfiles => Profiles.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(AddTenantCommand), nameof(OpenDemoCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    bool NotBusy => !IsBusy;

    // A browser sign-in waits indefinitely if the tab is closed, so it must be cancellable.
    CancellationTokenSource? _signIn;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    async Task ConnectAsync(TenantCard card)
    {
        var profile = card.Profile;
        IsBusy = true;
        Error = null;
        Status = $"Signing in to {profile.DisplayName}… finish in your browser if it asks.";
        _signIn = new CancellationTokenSource();
        try
        {
            _open(await _connector.ConnectAsync(profile, _signIn.Token));
        }
        catch (Exception e)
        {
            Error = Friendly.Message(e);
        }
        finally
        {
            _signIn.Dispose();
            _signIn = null;
            IsBusy = false;
            Status = null;
        }
    }

    [RelayCommand]
    void CancelSignIn() => _signIn?.Cancel();

    [RelayCommand(CanExecute = nameof(NotBusy))]
    void AddTenant() => _addTenant();

    [RelayCommand(CanExecute = nameof(NotBusy))]
    void OpenDemo() => _open(_connector.ConnectDemo());

    [RelayCommand]
    void Remove(TenantCard card)
    {
        _connector.RemoveProfile(card.Profile.Id);
        Profiles.Remove(card);
        OnPropertyChanged(nameof(HasProfiles));
    }
}
