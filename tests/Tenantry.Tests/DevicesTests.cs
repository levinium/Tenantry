using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>The Devices tab (4c-2): joining Entra and Intune, what each action sends, secrets shown on request and logged.</summary>
public sealed class DevicesTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static GraphUserRecords Records(FakeHttp http) =>
        new(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore());

    static GraphChangeExecutor Executor(FakeHttp http) =>
        new(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

    [Fact]
    public async Task Intune_and_Entra_devices_are_joined_on_the_Entra_device_id()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/users/u2/registeredDevices", """
                {"value":[
                  {"id":"o1","deviceId":"d1","displayName":"LAPTOP-1","trustType":"AzureAd","accountEnabled":true},
                  {"id":"o2","deviceId":"d2","displayName":"iPad","operatingSystem":"iPadOS","trustType":"Workplace","accountEnabled":false}]}
                """)
            .On(HttpMethod.Get, "/users/u2/ownedDevices", """{"value":[]}""")
            .On(HttpMethod.Get, "/users/u2/managedDevices", """
                {"value":[{"id":"m1","deviceName":"LAPTOP-1","operatingSystem":"Windows","azureADDeviceId":"d1","complianceState":"noncompliant","isEncrypted":true,"managedDeviceOwnerType":"company"}]}
                """);

        var devices = (await Records(http).GetDevicesAsync("u2", Ct)).Value!;

        Assert.Equal(2, devices.Count);
        var laptop = devices.Single(d => d.Name == "LAPTOP-1");
        Assert.Equal(("o1", "d1", "m1", "Entra joined", false, "Company-owned"), (laptop.ObjectId, laptop.DeviceId, laptop.ManagedId, laptop.JoinType, laptop.Compliant, laptop.Ownership));
        var ipad = devices.Single(d => d.Name == "iPad");
        Assert.Equal((false, "Registered", "Registered by them", false), (ipad.IsManaged, ipad.JoinType, ipad.Relation, ipad.Enabled));
    }

    [Fact]
    public async Task Someone_Intune_has_never_seen_has_no_managed_devices_rather_than_an_error()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/users/u2/registeredDevices", """{"value":[]}""")
            .On(HttpMethod.Get, "/users/u2/ownedDevices", """{"value":[]}""")
            .On(HttpMethod.Get, "/users/u2/managedDevices", """{"error":{"code":"ResourceNotFound","message":"An error has occurred"}}""", System.Net.HttpStatusCode.NotFound);

        var read = await Records(http).GetDevicesAsync("u2", Ct);

        Assert.Equal((0, null), (read.Value!.Count, read.Unavailable));
    }

    [Fact]
    public async Task The_newest_local_admin_password_is_decoded()
    {
        var http = new FakeHttp().On(HttpMethod.Get, "/directory/deviceLocalCredentials/d1", """
            {"credentials":[
              {"accountName":"admin","passwordBase64":"b2xk","backupDateTime":"2026-10-01T00:00:00Z"},
              {"accountName":"admin","passwordBase64":"bmV3LXBhc3M=","backupDateTime":"2026-10-20T00:00:00Z"}]}
            """);

        Assert.Equal(("admin", "new-pass"), await Records(http).RevealLapsAsync("d1", Ct));
    }

    [Fact]
    public async Task A_wipe_erases_everything_and_rotating_the_admin_password_uses_beta()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "/managedDevices/m1/wipe", "")
            .On(HttpMethod.Post, "/beta/deviceManagement/managedDevices/m1/rotateLocalAdminPassword", "");
        var device = new ChangeTarget("manageddevice", "m1", "zz-tenantry-test-laptop", null);
        var executor = Executor(http);

        var wipe = await executor.ApplyAsync(new RunDeviceAction(device, DeviceCommand.Wipe), Ct);
        var rotate = await executor.ApplyAsync(new RunDeviceAction(device, DeviceCommand.RotateLocalAdminPassword), Ct);

        Assert.Equal((ChangeOutcome.Applied, ChangeOutcome.Applied), (wipe.Outcome, rotate.Outcome));
        Assert.Equal("""{"keepEnrollmentData":false,"keepUserData":false}""", http.Requests[0].Body);
        Assert.Equal("zz-tenantry-test-laptop", new RunDeviceAction(device, DeviceCommand.Wipe).ConfirmPhrase);
        Assert.Null(new RunDeviceAction(device, DeviceCommand.Retire).ConfirmPhrase);
    }

    // ---- The tab ----------------------------------------------------------------------------

    sealed class Fake : IUserRecords, IDeviceRecords
    {
        public int Reveals { get; private set; }

        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailboxDelegate>>([]);
        public Task<MailDetails> GetMailAsync(string userId, string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Available<IReadOnlyList<UserDevice>>> GetDevicesAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Available<IReadOnlyList<UserDevice>>(
            [
                new("o1", "d1", "m1", "zz-tenantry-test-laptop", "Windows", "11", "Lenovo", "T14", "PF1", "Entra joined", true, true, true, null, null, "Primary user", "Company-owned"),
            ]));
        public Task<IReadOnlyList<RecoveryKeyInfo>> GetRecoveryKeysAsync(string deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RecoveryKeyInfo>>([new RecoveryKeyInfo("k1", null, "System drive")]);
        public Task<string> RevealRecoveryKeyAsync(string keyId, CancellationToken cancellationToken = default)
        {
            Reveals++;
            return Task.FromResult("111111-222222");
        }
        public Task<LapsInfo?> GetLapsAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult<LapsInfo?>(null);
        public Task<(string Account, string Password)> RevealLapsAsync(string deviceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    sealed class Noop : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static async Task<(DevicesSection Tab, ChangePipeline Pipeline, Fake Records)> Tab()
    {
        var pipeline = new ChangePipeline(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var records = new Fake();
        var person = new UserSummary("u2", "zz-tenantry-test-bo", "zz-tenantry-test-bo@contoso.example", null, null, null, true, UserKind.Member,
            DateTimeOffset.UnixEpoch, null, []);
        var page = new UserDetailViewModel(person, pipeline: pipeline, canEdit: Availability.Yes, records: records);
        var tab = page.Sections.OfType<DevicesSection>().Single();
        await tab.LoadAsync(Ct);
        return (tab, pipeline, records);
    }

    [Fact]
    public async Task A_key_is_read_only_on_show_logged_and_hidden_again()
    {
        SecretRow.ShowFor = TimeSpan.FromMilliseconds(50);
        var (tab, pipeline, records) = await Tab();
        var laptop = tab.Devices.Single();

        laptop.IsExpanded = true;
        for (var i = 0; i < 50 && laptop.Secrets.Count == 0; i++)
            await Task.Delay(10, Ct);
        var key = laptop.Secrets.Single();
        Assert.Equal((0, "••••••••••••"), (records.Reveals, key.Masked));

        var showing = key.ShowCommand.ExecuteAsync(null);
        for (var i = 0; i < 50 && !key.IsShown; i++)
            await Task.Delay(5, Ct);
        Assert.Equal("111111-222222", key.Masked);
        Assert.Contains(pipeline.Log.All(), e => e.Change is SecretViewed { What: "BitLocker recovery key (system drive)" });

        await showing;
        Assert.False(key.IsShown);
        Assert.Equal(1, records.Reveals);
    }

    [Fact]
    public async Task Actions_stage_and_retire_and_wipe_replace_each_other()
    {
        var (tab, pipeline, _) = await Tab();
        var laptop = tab.Devices.Single();

        laptop.RunCommand.Execute(DeviceCommand.Sync);
        laptop.RunCommand.Execute(DeviceCommand.Retire);
        laptop.RunCommand.Execute(DeviceCommand.Wipe);
        laptop.ToggleEnabledCommand.Execute(null);

        Assert.Equal(["Sync with Intune", "Wipe", "Device sign-in"], pipeline.Pending.Select(p => p.Label));
        Assert.Equal(("Undo", "Undo", "Retire"), (laptop.WipeText, laptop.EnableText, laptop.RetireText));

        laptop.RunCommand.Execute(DeviceCommand.Wipe);
        Assert.Equal(2, pipeline.Pending.Count);
    }
}
