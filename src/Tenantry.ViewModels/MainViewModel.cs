using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    const string Later = "Coming in a later release";

    readonly ConnectedTenant _tenant;
    readonly Action _switchTenant;

    public MainViewModel(ConnectedTenant tenant, Action? switchTenant = null)
    {
        _tenant = tenant;
        _switchTenant = switchTenant ?? (() => { });
        Users = new UserListViewModel(tenant.Directory, OpenUser, Evaluate(Capabilities.EditUsers));

        var readUsers = Evaluate(Capabilities.ReadUsers);
        Sections =
        [
            new NavSection("users", "Users", readUsers.Reason),
            new NavSection("groups", "Groups & teams", Later),
            new NavSection("mail", "Mail & security", Later),
            new NavSection("sharepoint", "SharePoint & OneDrive", Later),
            new NavSection("devices", "Devices", Later),
            new NavSection("security", "Security & compliance", Later),
        ];
        Tabs.Add(Users);
        SelectedTab = Users;
        SelectedSection = Sections[0];
    }

    public string TenantName => _tenant.TenantName;

    public string Account => _tenant.Account;

    public string TenantInitials => Display.Initials(_tenant.TenantName);

    public bool IsDemo => _tenant.IsDemo;

    public string WindowTitle => $"Tenantry · {TenantName}";

    public IReadOnlyList<NavSection> Sections { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public UserListViewModel Users { get; }

    /// <summary>Every gated feature and whether this admin can use it here, for the "Your access" panel.</summary>
    public IReadOnlyList<CapabilityRow> Access => Capabilities.All
        .Select(c => new CapabilityRow(c.Title, Evaluate(c)))
        .ToArray();

    [ObservableProperty]
    public partial TabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    public partial NavSection? SelectedSection { get; set; }

    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    public Availability Evaluate(Capability capability) => CapabilityEvaluator.Evaluate(capability, _tenant.Context);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Users.LoadAsync(cancellationToken);
        }
        catch (Exception e)
        {
            LoadError = Friendly.Message(e);
        }
    }

    partial void OnSelectedSectionChanged(NavSection? value)
    {
        if (value?.Key == "users")
            SelectedTab = Users;
    }

    /// <summary>Opens a user's record page, or switches to it if it is already open.</summary>
    public void OpenUser(UserSummary user)
    {
        var existing = Tabs.OfType<UserDetailViewModel>().FirstOrDefault(t => t.User.Id == user.Id);
        if (existing is null)
        {
            existing = new UserDetailViewModel(user);
            Tabs.Add(existing);
        }
        SelectedTab = existing;
    }

    [RelayCommand]
    void CloseTab(TabViewModel tab)
    {
        if (!tab.CanClose)
            return;
        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;
        Tabs.RemoveAt(index);
        if (SelectedTab == tab || SelectedTab is null)
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    [RelayCommand]
    void SwitchTenant() => _switchTenant();
}

public sealed record CapabilityRow(string Title, Availability Availability)
{
    public string State => Availability.IsAvailable ? "Available" : Availability.Reason ?? "Unavailable";
    public string BadgeText => Availability.IsAvailable ? "Available" : "Unavailable";
    public BadgeKind BadgeKind => Availability.IsAvailable ? BadgeKind.Success : BadgeKind.Neutral;
    public string? Reason => Availability.Reason;
}
