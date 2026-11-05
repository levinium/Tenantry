using Tenantry.Core.Directory;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>How a group's members read on its page: names from the local copy, labels only where they tell something.</summary>
public sealed class GroupMemberRowTests
{
    static readonly UserSummary Ada = new("u1", "Ada Lovelace", "ada@contoso.example", null, null, null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, []);

    static readonly DeviceSummary Kiosk = new("k1", "obj-k1", "dev-k1", null, "CONTOSO-KIOSK-01", "Android", "15", "Samsung", "Galaxy Tab A9", null,
        "Registered", true, true, true, null, null, null, null, null, null, null);

    sealed class Index : IDirectoryIndex
    {
        public UserSummary? User(string id) => id == Ada.Id ? Ada : null;
        public GroupSummary? Group(string id) => null;
        public IEnumerable<UserSummary> AllUsers => [Ada];
        public IEnumerable<GroupSummary> AllGroups => [];
        public DeviceSummary? Device(string id) => id == Kiosk.ObjectId ? Kiosk : null;
    }

    [Fact]
    public void A_device_member_is_named_from_the_local_copy_of_devices()
    {
        var row = GroupAccess.Describe(new GroupMember("obj-k1", "device"), new Index());

        Assert.Equal("CONTOSO-KIOSK-01", row.Name);
        Assert.Equal("Android 15 · Galaxy Tab A9", row.Detail);
        Assert.Equal("Device", row.TypeText);
    }

    [Fact]
    public void A_device_not_held_locally_still_shows_as_a_device()
    {
        var row = GroupAccess.Describe(new GroupMember("obj-gone", "device"), new Index());

        Assert.Equal("A device", row.Name);
    }

    [Fact]
    public void People_carry_no_type_label_and_others_do()
    {
        var index = new Index();
        var person = new PersonRow(GroupAccess.Describe(new GroupMember("u1", "user"), index), null, true, "", () => { }, null);
        var device = new PersonRow(GroupAccess.Describe(new GroupMember("obj-k1", "device"), index), null, true, "", () => { }, null);

        Assert.False(person.ShowType);
        Assert.True(device.ShowType);
    }
}
