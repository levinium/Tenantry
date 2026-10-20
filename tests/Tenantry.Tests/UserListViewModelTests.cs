using Tenantry.Core.Directory;
using Tenantry.ViewModels;

namespace Tenantry.Tests;

public class UserListViewModelTests
{
    static async Task<UserListViewModel> Loaded(Action<UserSummary>? open = null)
    {
        var vm = new UserListViewModel(FakeDirectory.Standard(), open ?? (_ => { }));
        await vm.LoadAsync();
        return vm;
    }

    static string[] Names(UserListViewModel vm) => vm.VisibleUsers.Select(u => u.DisplayName).ToArray();

    [Fact]
    public async Task Shows_everyone_with_no_filter()
    {
        var vm = await Loaded();

        Assert.Equal(4, vm.VisibleUsers.Count);
        Assert.Equal("4 users", vm.CountText);
    }

    [Fact]
    public async Task Every_word_must_match_in_any_order_and_any_case()
    {
        var vm = await Loaded();

        vm.FilterText = "it HOPPER";

        Assert.Equal(["Grace Hopper"], Names(vm));
        Assert.Equal("1 of 4 users", vm.CountText);
    }

    [Theory]
    [InlineData("guest", "Guest Person")]
    [InlineData("disabled", "Grace Hopper")]
    [InlineData("e5", "Alan Turing")]
    [InlineData("controller", "Ada Lovelace")]
    public async Task Matches_status_type_license_and_title_words(string filter, string expected)
    {
        var vm = await Loaded();

        vm.FilterText = filter;

        Assert.Equal([expected], Names(vm));
    }

    [Fact]
    public async Task No_match_gives_an_empty_list_and_clearing_restores_it()
    {
        var vm = await Loaded();

        vm.FilterText = "nobody-by-this-name";
        Assert.Empty(vm.VisibleUsers);

        vm.FilterText = "   ";
        Assert.Equal(4, vm.VisibleUsers.Count);
    }

    [Fact]
    public async Task Open_selected_passes_the_selected_user_on()
    {
        UserSummary? opened = null;
        var vm = await Loaded(u => opened = u);

        vm.OpenSelectedCommand.Execute(null);
        Assert.Null(opened);

        vm.SelectedUser = vm.VisibleUsers[1];
        vm.OpenSelectedCommand.Execute(null);
        Assert.Same(vm.VisibleUsers[1], opened);
    }
}
