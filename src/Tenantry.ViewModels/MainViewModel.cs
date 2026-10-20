using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    const string Later = "Coming in a later release";

    readonly ITenantDirectory _directory;

    public MainViewModel(ITenantDirectory directory)
    {
        _directory = directory;
        Users = new UserListViewModel(directory, OpenUser);
        Sections =
        [
            new NavSection("users", "Users"),
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

    public string TenantName => _directory.TenantName;

    public string WindowTitle => $"Tenantry · {TenantName}";

    public IReadOnlyList<NavSection> Sections { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public UserListViewModel Users { get; }

    [ObservableProperty]
    public partial TabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    public partial NavSection? SelectedSection { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Users.LoadAsync(cancellationToken);

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
}
