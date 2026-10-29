using Tenantry.Core.Directory;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;

namespace Tenantry.Tests;

public class MainViewModelTests
{
    static async Task<MainViewModel> Loaded()
    {
        var vm = new MainViewModel(FakeConnector.Tenant());
        await vm.InitializeAsync();
        return vm;
    }

    [Fact]
    public async Task Opening_a_user_twice_reuses_one_tab()
    {
        var vm = await Loaded();
        var ada = ((UserSummary)vm.Users.Rows[0]);

        vm.OpenUser(ada);
        vm.SelectedTab = vm.Users;
        vm.OpenUser(ada);

        Assert.Equal(2, vm.Tabs.Count);
        var tab = Assert.IsType<UserDetailViewModel>(vm.SelectedTab);
        Assert.Same(ada, tab.User);
    }

    [Fact]
    public async Task Closing_a_record_selects_its_neighbour()
    {
        var vm = await Loaded();
        vm.OpenUser(((UserSummary)vm.Users.Rows[0]));
        vm.OpenUser(((UserSummary)vm.Users.Rows[1]));
        var last = vm.SelectedTab!;

        vm.CloseTabCommand.Execute(last);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.DoesNotContain(last, vm.Tabs);
        Assert.Same(vm.Tabs[1], vm.SelectedTab);
    }

    [Fact]
    public async Task The_users_list_cannot_be_closed()
    {
        var vm = await Loaded();

        vm.CloseTabCommand.Execute(vm.Users);

        Assert.Contains(vm.Users, vm.Tabs);
    }

    [Fact]
    public async Task Choosing_users_in_the_sidebar_returns_to_the_list()
    {
        var vm = await Loaded();
        vm.OpenUser(((UserSummary)vm.Users.Rows[0]));
        vm.SelectedSection = null;

        vm.SelectedSection = vm.Sections.Single(s => s.Key == "users");

        Assert.Same(vm.Users, vm.SelectedTab);
    }

    [Fact]
    public void Unbuilt_sections_are_disabled_with_a_reason()
    {
        var vm = new MainViewModel(FakeConnector.Tenant());

        Assert.All(vm.Sections.Where(s => s.Key is not ("users" or "groups" or "devices" or "sharepoint" or "security" or "reports")), s =>
        {
            Assert.False(s.IsAvailable);
            Assert.False(string.IsNullOrWhiteSpace(s.UnavailableReason));
        });
    }
}
