using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>The extra device actions and Autopilot (6c): what each sends, which devices each skips, and the Autopilot page.</summary>
public sealed class DeviceActionsTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static GraphChangeExecutor Executor(FakeHttp http) =>
        new(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

    static readonly ChangeTarget Laptop = new("manageddevice", "m1", "zz-tenantry-test-laptop", null);

    static DeviceSummary Device(string name, string os = "Windows", string? managedId = "m-") =>
        new("e-" + name, "e-" + name, "d-" + name, managedId is null ? null : managedId + name, name, os, "11", "Contoso", "Book", null, "Entra joined",
            true, true, true, Now.AddDays(-1), null, Now.AddDays(-300), DeviceFields.Company, "u1", "Ada", "ada@contoso.example");

    static async Task<BulkPlan> Plan(string title, IReadOnlyList<DeviceSummary> devices, Action<BulkAction>? fill = null, bool one = false,
        IDirectoryIndex? index = null)
    {
        var action = BulkActions.ForDevices(() => devices, () => Now, one, index).Single(a => a.Title == title);
        fill?.Invoke(action);
        return await action.PlanAsync(Ct);
    }

    [Theory]
    [InlineData(DeviceCommand.QuickScan, "/v1.0/deviceManagement/managedDevices/m1/windowsDefenderScan", """{"quickScan":true}""")]
    [InlineData(DeviceCommand.CollectDiagnostics, "/beta/deviceManagement/managedDevices/m1/createDeviceLogCollectionRequest", """{"templateType":{"templateType":"predefined"}}""")]
    [InlineData(DeviceCommand.RotateBitLockerKeys, "/beta/deviceManagement/managedDevices/m1/rotateBitLockerKeys", "{}")]
    [InlineData(DeviceCommand.AutopilotReset, "/v1.0/deviceManagement/managedDevices/m1/wipe", """{"keepEnrollmentData":true,"keepUserData":false}""")]
    [InlineData(DeviceCommand.Locate, "/v1.0/deviceManagement/managedDevices/m1/locateDevice", "{}")]
    public async Task Each_command_goes_to_its_Intune_action(DeviceCommand command, string path, string body)
    {
        var http = new FakeHttp().On(HttpMethod.Post, path, "");

        var result = await Executor(http).ApplyAsync(new RunDeviceAction(Laptop, command), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal((path, body), (http.Requests[0].PathAndQuery, http.Requests[0].Body));
    }

    [Fact]
    public async Task Fresh_start_sends_whether_to_keep_their_files()
    {
        var http = new FakeHttp().On(HttpMethod.Post, "/cleanWindowsDevice", "");

        await Executor(http).ApplyAsync(new RunDeviceAction(Laptop, DeviceCommand.FreshStart, KeepUserData: true), Ct);

        Assert.Equal("""{"keepUserData":true}""", http.Requests[0].Body);
    }

    [Fact]
    public void Erasing_commands_share_the_remove_slot_and_need_the_name_typed()
    {
        foreach (var command in new[] { DeviceCommand.Wipe, DeviceCommand.FreshStart, DeviceCommand.AutopilotReset })
        {
            var change = new RunDeviceAction(Laptop, command);
            Assert.Equal(("remove", "zz-tenantry-test-laptop", true), (change.Key, change.ConfirmPhrase, change.IsCaution));
        }
        Assert.Equal(("remove", null), (new RunDeviceAction(Laptop, DeviceCommand.Retire).Key, new RunDeviceAction(Laptop, DeviceCommand.Retire).ConfirmPhrase));
        Assert.False(new RunDeviceAction(Laptop, DeviceCommand.UpdateDefinitions).IsCaution);
    }

    [Fact]
    public async Task Windows_only_actions_skip_other_devices_and_say_why()
    {
        var plan = await Plan("Quick Scan", [Device("PC"), Device("Phone", "iOS"), Device("Old", managedId: null)]);

        Assert.Equal(["PC"], plan.Changes.Select(c => c.Target.Name));
        Assert.Equal(["Phone: not Windows", "Old: not managed by Intune"], plan.Skipped);
    }

    [Fact]
    public async Task One_device_offers_rename_primary_user_and_lost_mode_and_checks_what_is_typed()
    {
        Assert.DoesNotContain(BulkActions.ForDevices(() => [], () => Now), a => a.Title is "Rename" or "Lost Mode");

        var badName = await Plan("Rename", [Device("PC")], a => a.Text = "has spaces in it", one: true);
        var goodName = await Plan("Rename", [Device("PC")], a => a.Text = "NG-NEW-01", one: true);
        var noMessage = await Plan("Lost Mode", [Device("Phone", "iOS")], a => a.Choice = a.Choices.First(), one: true);
        var lost = await Plan("Lost Mode", [Device("Phone", "iOS")], a => { a.Choice = a.Choices.First(); a.Text = "Please call the front desk"; }, one: true);

        Assert.Empty(badName.Changes);
        Assert.Contains("15 letters", Assert.Single(badName.Skipped), StringComparison.Ordinal);
        Assert.Equal(("PC", "NG-NEW-01"), (((RenameDevice)Assert.Single(goodName.Changes)).Old, ((RenameDevice)goodName.Changes[0]).New));
        Assert.NotNull(noMessage.Error);
        Assert.Equal((true, "Please call the front desk"), (((SetLostMode)Assert.Single(lost.Changes)).On, ((SetLostMode)lost.Changes[0]).Message));
    }

    [Fact]
    public async Task Ownership_skips_devices_already_that_way()
    {
        var plan = await Plan("Ownership", [Device("A"), Device("B") with { Ownership = DeviceFields.Personal }], a => a.Choice = a.Choices.Single(c => c.Label == "Personal"));

        var change = Assert.IsType<SetDeviceOwnership>(Assert.Single(plan.Changes));
        Assert.Equal(("A", true, false), (change.Target.Name, change.OldCompany, change.NewCompany));
        Assert.Equal(["B: already personal"], plan.Skipped);
    }

    [Fact]
    public async Task Changing_the_primary_user_adds_a_ref_and_reads_it_back()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "/beta/deviceManagement/managedDevices/m1/users/$ref", "")
            .On(HttpMethod.Get, "/beta/deviceManagement/managedDevices/m1?$select=userId", """{"userId":"u2"}""");

        var result = await Executor(http).ApplyAsync(new SetPrimaryUser(Laptop, "Ada", new ChangeTarget("user", "u2", "Bo", null)), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("""{"@odata.id":"https://graph.microsoft.com/beta/users/u2"}""", http.Requests[0].Body);
    }

    // ---- Autopilot ------------------------------------------------------------------------

    [Fact]
    public void Autopilot_rows_read_in_plain_words()
    {
        using var json = JsonDocument.Parse("""
            {"id":"a1","serialNumber":"SN1","manufacturer":"Contoso","model":"Book","groupTag":"",
             "deploymentProfileAssignmentStatus":"assignedUnkownSyncState","enrollmentState":"notContacted",
             "lastContactedDateTime":"0001-01-01T00:00:00Z","managedDeviceId":"00000000-0000-0000-0000-000000000000",
             "deploymentProfile":{"displayName":"Standard Laptop"}}
            """);

        var d = GraphUserRecords.ToAutopilot(json.RootElement);

        Assert.Equal(("SN1", null, "Standard Laptop", "Assigned", "Not Contacted"), (d.Serial, d.GroupTag, d.ProfileName, d.ProfileStatus, d.EnrollmentState));
        Assert.Equal((null, null), ((object?)d.LastContacted, d.ManagedDeviceId));
    }

    [Fact]
    public async Task Autopilot_profile_names_come_from_the_profiles_not_an_expand()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/windowsAutopilotDeploymentProfiles/p1/assignedDevices", """{"value":[{"id":"a1"}]}""")
            .On(HttpMethod.Get, "/windowsAutopilotDeploymentProfiles?$select", """{"value":[{"id":"p1","displayName":"Standard Laptop"}]}""")
            .On(HttpMethod.Get, "/windowsAutopilotDeviceIdentities", """
                {"value":[{"id":"a1","serialNumber":"SN1","enrollmentState":"enrolled"},{"id":"a2","serialNumber":"SN2","enrollmentState":"notContacted"}]}
                """);
        var records = new GraphUserRecords(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

        var devices = (await records.GetAutopilotDevicesAsync(Ct)).Value!;

        Assert.Equal([("SN1", "Standard Laptop"), ("SN2", null)], devices.Select(d => (d.Serial, d.ProfileName)));
        Assert.DoesNotContain(http.Requests, r => r.PathAndQuery.Contains("$expand", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Setting_a_group_tag_sends_it_and_reads_it_back()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "/windowsAutopilotDeviceIdentities/a1/updateDeviceProperties", "")
            .On(HttpMethod.Get, "/windowsAutopilotDeviceIdentities/a1?$select=groupTag", """{"groupTag":"Finance"}""");

        var result = await Executor(http).ApplyAsync(new SetAutopilotGroupTag(new ChangeTarget("autopilot", "a1", "SN1", null), null, "Finance"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("""{"groupTag":"Finance"}""", http.Requests[0].Body);
    }

    [Fact]
    public async Task Group_tag_skips_devices_that_already_have_it()
    {
        var a = new AutopilotDevice("a1", "SN1", null, null, "Finance", null, "Assigned", "Enrolled", null, null, null, null, null);
        var b = a with { Id = "a2", Serial = "SN2", GroupTag = null };
        var action = BulkActions.ForAutopilot(() => [a, b]).Single(x => x.Title == "Set Group Tag");
        action.Text = "Finance";

        var plan = await action.PlanAsync(Ct);

        Assert.Equal(["SN2"], plan.Changes.Select(c => c.Target.Name));
        Assert.Equal(["SN1: already Finance"], plan.Skipped);
    }

    sealed class Noop : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    [Fact]
    public async Task The_Autopilot_page_reads_when_first_shown()
    {
        var directory = new DemoTenantDirectory(userCount: 40);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var devices = ViewModels.Lists.DataListViewModel.Create(ViewModels.Lists.DeviceList.Definition(d => d), directory.GetDevicesAsync, _ => { }, null, () => Now);
        var area = new DevicesAreaViewModel(devices, records, pipeline, Availability.Yes, null, () => Now);
        Assert.Equal(0, area.Autopilot.Total);

        area.SelectedPage = DevicesAreaViewModel.Pages.Single(p => p.Key == "autopilot");
        for (var i = 0; i < 50 && area.Autopilot.Total == 0; i++)
            await Task.Delay(5, Ct);

        Assert.True(area.Autopilot.Total > 0);
        Assert.Null(area.AutopilotNote);
    }
}
