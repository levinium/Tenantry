using Tenantry.Core.Capabilities;
using Tenantry.Core.Directory;
using Tenantry.ViewModels;

namespace Tenantry.Tests;

public class PaletteAndGroupsTests
{
    static async Task<MainViewModel> Loaded()
    {
        var directory = FakeDirectory.Standard();
        directory.Groups.Add(new GroupSummary("g1", "IT Admins", "itadmins@example.org", null, GroupKind.MailEnabledSecurity, false, false, null, null,
            [new GroupMember("2", "user"), new GroupMember("g2", "group"), new GroupMember("x", "device")]));
        directory.Groups.Add(new GroupSummary("g2", "Finance Team", null, null, GroupKind.Microsoft365, false, true, "Private", null, [new GroupMember("1", "user")]));
        var tenant = new ConnectedTenant("Contoso", "admin@contoso.example", directory,
            new TenantContext(new HashSet<string>([DirectoryRoles.GlobalAdministrator]), new HashSet<string>(), true));
        var vm = new MainViewModel(tenant);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        return vm;
    }

    [Fact]
    public async Task Names_that_start_with_the_query_come_first_and_every_word_must_match()
    {
        var vm = await Loaded();
        vm.OpenPaletteCommand.Execute(null);

        vm.Palette.Query = "ada";
        Assert.Equal("Ada Lovelace", vm.Palette.Results[0].Entry.Title);
        Assert.True(vm.Palette.Results[0].IsSelected);

        // Both words must appear; department counts.
        vm.Palette.Query = "it grace";
        Assert.Equal(["Grace Hopper"], vm.Palette.Results.Select(r => r.Entry.Title));

        vm.Palette.Query = "zzz";
        Assert.True(vm.Palette.ShowNoMatches);
    }

    [Fact]
    public async Task Enter_opens_the_highlighted_result_and_closes_the_palette()
    {
        var vm = await Loaded();
        vm.OpenPaletteCommand.Execute(null);
        vm.Palette.Query = "it admins";

        vm.Palette.OpenSelected();

        Assert.False(vm.IsPaletteOpen);
        var page = Assert.IsType<GroupDetailViewModel>(vm.SelectedTab);
        Assert.Equal("IT Admins", page.Group.DisplayName);
    }

    [Fact]
    public async Task A_view_result_switches_to_its_list_and_applies_the_view()
    {
        var vm = await Loaded();
        vm.OpenPaletteCommand.Execute(null);
        vm.Palette.Query = "distribution lists";

        var view = vm.Palette.Results.Single(r => r.Entry.Kind == "View");
        vm.Palette.OpenCommand.Execute(view);

        Assert.Same(vm.Groups, vm.SelectedTab);
        Assert.Equal("groups", vm.SelectedSection?.Key);
        Assert.Equal("Distribution lists", vm.Groups.CurrentViewName);
    }

    [Fact]
    public async Task Arrow_keys_wrap_around_the_results()
    {
        var vm = await Loaded();
        vm.OpenPaletteCommand.Execute(null);
        vm.Palette.Query = "example.org";
        var count = vm.Palette.Results.Count;
        Assert.True(count > 2);

        vm.Palette.Move(-1);

        Assert.True(vm.Palette.Results[count - 1].IsSelected);
        Assert.Single(vm.Palette.Results, r => r.IsSelected);
    }

    [Fact]
    public async Task A_group_page_names_its_members_and_opens_them()
    {
        var vm = await Loaded();
        var admins = vm.Groups.Rows.Cast<GroupSummary>().Single(g => g.Id == "g1");

        vm.OpenGroup(admins);
        var page = Assert.IsType<GroupDetailViewModel>(vm.SelectedTab);

        Assert.Equal(["Alan Turing", "Finance Team", "A device"], page.Members.Select(m => m.Name));
        Assert.Equal("3 direct members: 1 user, 1 group, 1 device", page.MembersSummary);
        page.OpenMemberCommand.Execute(page.Members.Single(m => m.Name == "Alan Turing"));
        Assert.Equal("Alan Turing", Assert.IsType<UserDetailViewModel>(vm.SelectedTab).User.DisplayName);
    }

    [Fact]
    public async Task The_preview_pane_stays_open_across_areas_and_is_remembered_next_launch()
    {
        var preferences = new Tenantry.Core.Tenancy.MemoryAppPreferencesStore();
        var tenant = Tenantry.Tests.Fakes.FakeConnector.Tenant();
        var vm = new MainViewModel(tenant, preferences: preferences);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);

        vm.Users.TogglePreview();
        vm.SelectedSection = vm.Sections.Single(s => s.Key == "groups");

        Assert.True(vm.Groups.IsPreviewOpen);
        Assert.True(preferences.Load().PreviewOpen);

        // Next launch starts with it open; closing it anywhere closes it everywhere.
        var next = new MainViewModel(tenant, preferences: preferences);
        Assert.True(next.Users.IsPreviewOpen);
        next.Groups.ClosePreview();
        Assert.False(next.Users.IsPreviewOpen);
        Assert.False(preferences.Load().PreviewOpen);
    }

    [Fact]
    public void A_built_in_view_added_after_pins_were_saved_gets_its_default_pin()
    {
        // Saved by an older version that had no "shared" view, with "security" unpinned.
        var store = new Tenantry.Core.Lists.MemoryListStateStore();
        store.Save("groups", new Tenantry.Core.Lists.ListState(Tenantry.Core.Lists.ListQuery.Empty, [], default, [], "all",
            PinnedViews: ["all", "m365", "distribution"], KnownViews: ["all", "m365", "security", "distribution", "teams"]));
        var tenant = Tenantry.Tests.Fakes.FakeConnector.Tenant() with { ListState = store };

        var vm = new MainViewModel(tenant);

        Assert.Equal(["all", "m365", "distribution", "shared"], vm.Groups.ChipViews.Select(v => v.View.Id));
    }

    [Fact]
    public async Task The_first_tab_is_the_sidebar_areas_list_and_records_stay_open()
    {
        var vm = await Loaded();
        Assert.Equal([vm.Users], vm.Tabs);
        Assert.False(vm.HasRecordTabs);

        vm.OpenUser(vm.Users.Rows.Cast<UserSummary>().First());
        Assert.True(vm.HasRecordTabs);

        // Another area replaces the list tab; the open record stays.
        vm.SelectedSection = vm.Sections.Single(s => s.Key == "groups");
        Assert.Same(vm.Groups, vm.SelectedTab);
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(vm.Groups, vm.Tabs[0]);
        Assert.IsType<UserDetailViewModel>(vm.Tabs[1]);
        Assert.DoesNotContain(vm.Users, vm.Tabs);
    }
}
