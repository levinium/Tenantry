using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>The Devices list (6a): its views, bulk actions, deleting a device, and the demo's one set of devices.</summary>
public sealed class DeviceListTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DeviceSummary Device(string name, string? objectId = "e-", string? managedId = "m-", bool? enabled = true, DateTimeOffset? seen = null, string os = "Windows") =>
        new(objectId is null ? "intune:" + managedId + name : objectId + name, objectId is null ? null : objectId + name, "d-" + name,
            managedId is null ? null : managedId + name, name, os, "11", "Contoso", "Book", null, objectId is null ? "Intune only" : "Entra joined",
            objectId is null ? null : enabled, true, true, seen ?? Now.AddDays(-1), null, Now.AddDays(-400), DeviceFields.Company, null, null, null);

    static async Task<BulkPlan> Plan(string title, IReadOnlyList<DeviceSummary> devices, Func<BulkChoice, bool>? choose = null)
    {
        var action = BulkActions.ForDevices(() => devices, () => Now).Single(a => a.Title == title);
        if (choose is not null)
            action.Choice = action.Choices.Single(choose);
        return await action.PlanAsync(Ct);
    }

    [Fact]
    public async Task Intune_actions_skip_devices_Intune_doesnt_manage()
    {
        var plan = await Plan("Restart", [Device("A"), Device("B", managedId: null)]);

        var restart = Assert.IsType<RunDeviceAction>(Assert.Single(plan.Changes));
        Assert.Equal(("manageddevice", "m-A", DeviceCommand.Restart), (restart.Device.Kind, restart.Device.Id, restart.Command));
        Assert.Equal(["B: not managed by Intune"], plan.Skipped);
    }

    [Fact]
    public async Task Disabling_skips_devices_already_disabled_and_those_not_in_Entra()
    {
        var plan = await Plan("Disable or Enable", [Device("A"), Device("B", enabled: false), Device("C", objectId: null)], c => c.Label == "Disable");

        var disable = Assert.IsType<SetDeviceEnabled>(Assert.Single(plan.Changes));
        Assert.Equal(("device", "e-A", true, false), (disable.Device.Kind, disable.Device.Id, disable.Old, disable.New));
        Assert.Equal(["B: already disabled", "C: not in Entra"], plan.Skipped);
    }

    [Fact]
    public async Task A_device_never_used_is_stale_only_if_registered_before_the_cutoff()
    {
        var devices = new[]
        {
            Device("New") with { LastCheckIn = null, Registered = Now.AddDays(-2) },
            Device("Forgotten") with { LastCheckIn = null, Registered = Now.AddDays(-300) },
            Device("Unknown") with { LastCheckIn = null, Registered = null },
        };

        var plan = await Plan("Delete Stale Devices", devices, c => (int)c.Value == 90);

        Assert.Equal(["Forgotten"], plan.Changes.Select(c => c.Target.Name));
        Assert.StartsWith("New: never used yet, registered", plan.Skipped[0], StringComparison.Ordinal);
        Assert.Equal("Unknown: never used, and when it was registered isn't known", plan.Skipped[1]);
    }

    [Fact]
    public async Task Deleting_stale_devices_skips_any_seen_since_the_cutoff()
    {
        var devices = new[]
        {
            Device("Recent", seen: Now.AddDays(-30)),
            Device("Old", seen: Now.AddDays(-200)),
            Device("IntuneOnly", objectId: null, seen: Now.AddDays(-400)),
            Device("EntraOnly", managedId: null, seen: Now.AddDays(-120)),
        };

        var plan = await Plan("Delete Stale Devices", devices, c => (int)c.Value == 90);

        Assert.Equal(["Old", "IntuneOnly", "EntraOnly"], plan.Changes.Select(c => c.Target.Name));
        var deletes = plan.Changes.Cast<DeleteDevice>().ToArray();
        Assert.Equal(("device", "m-Old"), (deletes[0].Device.Kind, deletes[0].ManagedId));
        Assert.Equal(("manageddevice", null), (deletes[1].Device.Kind, deletes[1].ManagedId));
        Assert.Equal(("device", null), (deletes[2].Device.Kind, deletes[2].ManagedId));
        Assert.All(deletes, d => Assert.True(d.IsCaution));
        Assert.Single(plan.Skipped);
        Assert.StartsWith("Recent: seen", plan.Skipped[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_device_removes_Intune_first_then_Entra_and_tolerates_either_gone()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Delete, "/deviceManagement/managedDevices/m1", "", System.Net.HttpStatusCode.NotFound)
            .On(HttpMethod.Delete, "/devices/e1", "");
        var store = new MemoryEntityStore();
        store.Apply(DirectorySync.Devices, [new EntityChange("e1", JsonNode.Parse("""{"id":"e1","deviceId":"d1","displayName":"OLD"}""")!.AsObject())]);
        store.Apply(DirectorySync.ManagedDevices, [new EntityChange("m1", JsonNode.Parse("""{"id":"m1","azureADDeviceId":"d1"}""")!.AsObject())]);
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store, NoDelay.Instance);

        var result = await executor.ApplyAsync(new DeleteDevice(new ChangeTarget("device", "e1", "OLD", null), "m1", "Last seen long ago"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal(["/v1.0/deviceManagement/managedDevices/m1", "/v1.0/devices/e1"], http.Requests.Select(r => r.PathAndQuery));
        Assert.Empty(await new CachedTenantDirectory(store, "Contoso").GetDevicesAsync(Ct));
    }

    [Fact]
    public void A_delete_says_where_it_deletes_from()
    {
        Assert.Equal("Deleted from Intune and Entra", new DeleteDevice(new ChangeTarget("device", "e1", "A", null), "m1", "x").After);
        Assert.Equal("Deleted from Entra", new DeleteDevice(new ChangeTarget("device", "e1", "A", null), null, "x").After);
        Assert.Equal("Deleted from Intune", new DeleteDevice(new ChangeTarget("manageddevice", "m1", "A", null), null, "x").After);
    }

    [Fact]
    public void Views_find_stale_and_unhealthy_devices()
    {
        var devices = new[]
        {
            Device("Fine"),
            Device("Stale", seen: Now.AddDays(-120)),
            Device("Phone", os: "iOS") with { Compliant = false },
            Device("Unmanaged", managedId: null) with { Encrypted = false },
        };
        var engine = new ListEngine<DeviceSummary>(DeviceFields.All);
        var rows = engine.Prepare(devices);
        IReadOnlyList<string> In(string view) =>
            engine.Run(rows, DeviceList.Views.Single(v => v.Id == view).Query, Now).Select(d => d.Name).ToArray();

        Assert.Equal(["Stale"], In("stale"));
        Assert.Equal(["Phone"], In("not-compliant"));
        Assert.Equal(["Phone"], In("ios"));
        Assert.Equal(["Unmanaged"], In("not-managed"));
        Assert.Equal(["Unmanaged"], In("not-encrypted"));
    }

    [Fact]
    public async Task The_demo_list_and_each_persons_devices_tab_show_the_same_devices()
    {
        var directory = new DemoTenantDirectory(userCount: 40);
        var records = new DemoUserRecords(directory);
        var devices = await directory.GetDevicesAsync(Ct);
        var owner = devices.First(d => d.UserId is not null).UserId!;

        var tab = (await records.GetDevicesAsync(owner, Ct)).Value!;

        Assert.Equal(devices.Where(d => d.UserId == owner).Select(d => d.Name).Order(), tab.Select(d => d.Name).Order());
        // Disabling from the list shows on their tab.
        var target = devices.First(d => d.UserId == owner);
        Assert.True(records.ApplyDevice(new SetDeviceEnabled(BulkActions.EntraTarget(target), true, false)));
        Assert.False((await records.GetDevicesAsync(owner, Ct)).Value!.Single(d => d.ObjectId == target.ObjectId).Enabled);
        Assert.Contains(devices, d => d.UserId is null);
        Assert.Contains(devices, d => d.LastSeen is { } seen && seen < DateTimeOffset.UtcNow.AddDays(-90));
    }
}
