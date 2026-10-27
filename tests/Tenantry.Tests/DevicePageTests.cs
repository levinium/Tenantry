using System.Net;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>The device page (6b): Graph's reads, Intune's codes in words, and the tabs.</summary>
public sealed class DevicePageTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static GraphUserRecords Records(FakeHttp http) =>
        new(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

    static DeviceSummary Laptop(string? managedId = "m1", string? userId = "u1") =>
        new("e1", "e1", "d1", managedId, "LAPTOP-1", "Windows", "10.0.26100", "Contoso", "Book", "SN1", "Entra joined", true, false, true,
            DateTimeOffset.UtcNow.AddHours(-2), null, DateTimeOffset.UtcNow.AddDays(-100), DeviceFields.Company, userId, "Ada", "ada@contoso.example");

    [Fact]
    public async Task Hardware_and_signed_in_users_come_from_Intunes_beta_read()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/beta/deviceManagement/managedDevices/m1?$select=", """
                {"id":"m1","physicalMemoryInBytes":17179869184,"processorArchitecture":"x64","jailBroken":"False",
                 "managementCertificateExpirationDate":"2027-09-24T00:00:00Z","complianceGracePeriodExpirationDateTime":"9999-12-31T23:59:59Z",
                 "deviceEnrollmentType":"windowsAzureADJoin","partnerReportedThreatState":"unknown","ethernetMacAddress":"",
                 "hardwareInformation":{"serialNumber":"SN1","totalStorageSpace":512000000000,"freeStorageSpace":100000000000,"tpmSpecificationVersion":"2.0",
                   "batteryHealthPercentage":0,"imei":"","osBuildNumber":"10.0.26100.1"},
                 "usersLoggedOn":[{"userId":"u2","lastLogOnDateTime":"2026-10-20T08:00:00Z"},{"userId":"u1","lastLogOnDateTime":"2026-10-26T08:00:00Z"}]}
                """)
            .On(HttpMethod.Get, "/devices/e1/registeredOwners", """{"value":[{"id":"u1","displayName":"Ada","userPrincipalName":"ada@contoso.example"}]}""")
            .On(HttpMethod.Get, "/devices/e1/registeredUsers", """{"value":[]}""");

        var live = await Records(http).GetDeviceAsync(Laptop(), Ct);

        var hw = live.Hardware!;
        Assert.Equal((17179869184L, "x64", "2.0", "SN1", false), (hw.MemoryBytes, hw.Processor, hw.Tpm, hw.Serial, hw.Jailbroken));
        // Zero battery, empty IMEI and MAC, "unknown" threat and Intune's "never" date all mean nothing reported.
        Assert.Equal((null, null, null, null, null), ((object?)hw.BatteryHealthPercent, hw.Imei, hw.EthernetMac, hw.ThreatState, (object?)hw.GraceExpires));
        Assert.NotNull(hw.CertificateExpires);
        Assert.Equal(["u1", "u2"], live.LoggedOn.Select(u => u.UserId));
        Assert.Equal("Ada", Assert.Single(live.Owners).Name);
    }

    [Fact]
    public async Task A_device_Intune_doesnt_let_us_read_still_shows_its_Entra_people()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/beta/deviceManagement/managedDevices/m1", """{"error":{"code":"Forbidden","message":"no"}}""", HttpStatusCode.Forbidden)
            .On(HttpMethod.Get, "/devices/e1/registeredOwners", """{"value":[{"id":"u1","displayName":"Ada"}]}""")
            .On(HttpMethod.Get, "/devices/e1/registeredUsers", """{"value":[]}""");

        var live = await Records(http).GetDeviceAsync(Laptop(), Ct);

        Assert.Null(live.Hardware);
        Assert.NotNull(live.IntuneProblem);
        Assert.Single(live.Owners);
    }

    [Fact]
    public async Task Policies_put_problems_first_and_settings_are_read_on_their_own()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/deviceCompliancePolicyStates/ps2/settingStates", """
                {"value":[{"settingName":"DefaultDeviceCompliancePolicy.RequireUserExistence","state":"compliant","errorDescription":"0"},
                          {"settingName":"DefaultDeviceCompliancePolicy.RequireRemainContact","state":"nonCompliant","errorDescription":"0","currentValue":"40"}]}
                """)
            .On(HttpMethod.Get, "/deviceCompliancePolicyStates", """
                {"value":[{"id":"ps1","displayName":"A Policy","state":"compliant","settingCount":2},
                          {"id":"ps2","displayName":"Default Device Compliance Policy","state":"nonCompliant","settingCount":2}]}
                """);
        var records = Records(http);

        var policies = (await records.GetCompliancePoliciesAsync("m1", Ct)).Value!;
        var settings = await records.GetPolicySettingsAsync("m1", "ps2", compliance: true, Ct);

        Assert.Equal([("ps1", false), ("ps2", true)], policies.Select(p => (p.Id, p.IsProblem)));
        Assert.Equal(["nonCompliant", "compliant"], settings.Select(s => s.State));
        var row = new SettingRow(settings[0]);
        Assert.Equal(("Has checked in with Intune recently", "Not Compliant", "Now: 40"), (row.Name, row.StateText, row.Detail));
        Assert.Null(new SettingRow(settings[1]).Detail);
    }

    [Fact]
    public async Task No_apps_assigned_and_no_Defender_report_read_as_explanations_not_errors()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/beta/users/u1/mobileAppIntentAndStates/m1", """{"error":{"code":"ResourceNotFound","message":"x"}}""", HttpStatusCode.NotFound)
            .On(HttpMethod.Get, "/managedDevices/m1/windowsProtectionState", """{"error":{"code":"ResourceNotFound","message":"x"}}""", HttpStatusCode.NotFound);
        var records = Records(http);

        var apps = await records.GetAppStatesAsync("u1", "m1", Ct);
        var defender = await records.GetProtectionAsync("m1", Ct);

        Assert.Equal((0, null), (apps.Value!.Count, apps.Unavailable));
        Assert.Null(defender.Value);
        Assert.Contains("Defender", defender.Unavailable, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groups_are_read_with_their_dynamic_flag()
    {
        var http = new FakeHttp().On(HttpMethod.Get, "/devices/e1/memberOf/microsoft.graph.group", """
            {"value":[{"id":"g2","displayName":"Zeta","groupTypes":["DynamicMembership"]},{"id":"g1","displayName":"Alpha","groupTypes":[]}]}
            """);

        var groups = (await Records(http).GetGroupsAsync("e1", Ct)).Value!;

        Assert.Equal([("Alpha", false), ("Zeta", true)], groups.Select(g => (g.Name, g.IsDynamic)));
    }

    [Theory]
    [InlineData("nonCompliant", "Non Compliant")]
    [InlineData("DefaultDeviceCompliancePolicy.SomethingNew", "Something New")]
    [InlineData("TPMVersion", "TPM Version")]
    public void Intune_codes_read_as_words(string code, string words) => Assert.Equal(words, DeviceText.Words(code));

    [Fact]
    public void Required_apps_that_failed_are_problems_whichever_required_intent_Intune_names()
    {
        Assert.True(new DeviceAppState("VPN", null, "requiredAndAvailableInstall", "failed").IsProblem);
        Assert.True(new DeviceAppState("VPN", null, "requiredInstall", "notInstalled").IsProblem);
        Assert.False(new DeviceAppState("Whiteboard", null, "availableInstall", "notInstalled").IsProblem);
    }

    sealed class Noop : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static async Task<(DeviceDetailViewModel Page, ChangePipeline Pipeline, DeviceSummary Device)> DemoPage(Func<DeviceSummary, bool> pick)
    {
        var directory = new DemoTenantDirectory(userCount: 40);
        var records = new DemoUserRecords(directory);
        var device = (await directory.GetDevicesAsync(Ct)).First(pick);
        var pipeline = new ChangePipeline(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var page = new DeviceDetailViewModel(device, pipeline, Availability.Yes, () => DateTimeOffset.UtcNow, records, records);
        return (page, pipeline, device);
    }

    [Fact]
    public async Task Someone_who_is_primary_user_signed_in_and_owner_is_one_row_with_three_roles()
    {
        var (page, _, _) = await DemoPage(d => d.Platform == "Windows" && d.IsManaged && d.UserId is not null);
        var users = page.Sections.OfType<DeviceUsersSection>().Single();

        await users.LoadAsync(Ct);

        var person = Assert.Single(users.People);
        Assert.Equal(["Primary User", "Signed In", "Registered Owner"], person.Roles);
    }

    [Fact]
    public async Task A_failing_policy_opens_to_its_settings_and_a_device_Intune_doesnt_manage_says_so()
    {
        var (page, _, _) = await DemoPage(d => d.Platform == "Windows" && d.IsManaged && d.Compliant == false);
        var compliance = page.Sections.OfType<DeviceComplianceSection>().Single();
        await compliance.LoadAsync(Ct);
        var failing = compliance.Policies.First();
        for (var i = 0; i < 20 && failing.Settings.Count == 0; i++)
            await Task.Delay(5, Ct);

        Assert.Equal(("Not Compliant", true), (failing.StateText, failing.IsExpanded));
        Assert.Contains(failing.Settings, s => s.StateText == "Not Compliant");

        var (entraOnly, _, _) = await DemoPage(d => !d.IsManaged);
        var apps = entraOnly.Sections.OfType<DeviceAppsSection>().Single();
        await apps.LoadAsync(Ct);
        Assert.Contains("doesn't manage", apps.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Joining_a_security_group_stages_a_device_membership_and_shows_at_once()
    {
        var directory = new DemoTenantDirectory(userCount: 40);
        var records = new DemoUserRecords(directory);
        var groups = await directory.GetGroupsAsync(Ct);
        var device = (await directory.GetDevicesAsync(Ct)).First(d => d.Platform == "Windows" && d.ObjectId is not null);
        var pipeline = new ChangePipeline(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var index = new ListIndex(groups);
        var page = new DeviceDetailViewModel(device, pipeline, Availability.Yes, () => DateTimeOffset.UtcNow, records, records, index);
        var section = page.Sections.OfType<DeviceGroupsSection>().Single();
        await section.LoadAsync(Ct);

        Assert.DoesNotContain(section.Picker.Results, o => groups.First(g => g.Id == o.Id).Kind != GroupKind.Security);
        section.Picker.Query = "SG Fin";
        section.Picker.PickCommand.Execute(section.Picker.Results.First());

        var join = Assert.IsType<SetGroupMembership>(Assert.Single(pipeline.Pending));
        Assert.Equal(("device", device.ObjectId, true), (join.PersonType, join.Person.Id, join.New));
        Assert.Contains(section.Groups, g => g.PendingText == "Joining");
    }

    sealed class ListIndex(IReadOnlyList<GroupSummary> groups) : IDirectoryIndex
    {
        public UserSummary? User(string id) => null;
        public GroupSummary? Group(string id) => groups.FirstOrDefault(g => g.Id == id);
        public IEnumerable<UserSummary> AllUsers => [];
        public IEnumerable<GroupSummary> AllGroups => groups;
    }
}
