using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's devices, seeded per person: a company laptop managed by Intune (BitLocker key, and a
/// backed-up local admin password), usually a phone, sometimes a personal tablet registered only.
/// Keys and passwords are made up.
/// </summary>
public sealed partial class DemoUserRecords : IDeviceRecords
{
    readonly Dictionary<string, List<UserDevice>> _userDevices = new(StringComparer.Ordinal);

    public async Task<Available<IReadOnlyList<UserDevice>>> GetDevicesAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!_userDevices.TryGetValue(userId, out var devices))
        {
            var person = await PersonAsync(userId, cancellationToken);
            var random = new Random(StableSeed(userId) + 30);
            var now = DateTimeOffset.UtcNow;
            var initials = new string(person.DisplayName.Split(' ').Select(w => w[0]).ToArray()).ToUpperInvariant();
            devices = person.Kind == UserKind.Guest ? [] :
            [
                new($"obj-l-{userId}", $"dev-l-{userId}", $"mdm-l-{userId}", $"NG-{initials}-{random.Next(100, 999)}", "Windows", "10.0.26100", "Lenovo", "ThinkPad T14",
                    $"PF{random.Next(1000000, 9999999)}", "Entra joined", true, random.NextDouble() > 0.1, true, now.AddHours(-random.Next(1, 48)),
                    now.AddDays(-random.Next(100, 700)), "Primary user", "Company-owned"),
            ];
            if (person.Kind != UserKind.Guest && random.NextDouble() < 0.7)
                devices.Add(new($"obj-p-{userId}", $"dev-p-{userId}", $"mdm-p-{userId}", $"{person.DisplayName.Split(' ')[0]}'s iPhone", "iOS", "19.1", "Apple", "iPhone 15",
                    null, "Registered", true, true, true, now.AddHours(-random.Next(1, 30)), now.AddDays(-random.Next(30, 400)), "Primary user", "Personal"));
            if (random.NextDouble() < 0.2)
                devices.Add(new($"obj-t-{userId}", $"dev-t-{userId}", null, "iPad", "iPadOS", "18.6", "Apple", "iPad Air", null, "Registered", true, null, null,
                    now.AddDays(-random.Next(5, 60)), now.AddDays(-random.Next(200, 900)), "Registered by them", null));
            _userDevices[userId] = devices;
        }
        return new(devices.ToArray());
    }

    public Task<IReadOnlyList<RecoveryKeyInfo>> GetRecoveryKeysAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RecoveryKeyInfo>>(deviceId.StartsWith("dev-l-", StringComparison.Ordinal)
            ? [new RecoveryKeyInfo($"key-{deviceId}", DateTimeOffset.UtcNow.AddDays(-120), "System drive")]
            : []);

    public Task<string> RevealRecoveryKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        var random = new Random(StableSeed(keyId));
        return Task.FromResult(string.Join('-', Enumerable.Range(0, 8).Select(_ => random.Next(0, 999999).ToString("D6", System.Globalization.CultureInfo.InvariantCulture))));
    }

    public Task<LapsInfo?> GetLapsAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(deviceId.StartsWith("dev-l-", StringComparison.Ordinal) ? new LapsInfo("LocalAdmin", DateTimeOffset.UtcNow.AddDays(-3)) : null);

    public Task<(string Account, string Password)> RevealLapsAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(("LocalAdmin", $"Demo-{new Random(StableSeed(deviceId)).Next(100000, 999999)}!x"));

    /// <summary>Applies a device change to the demo's devices; false when it isn't one.</summary>
    public bool ApplyDevice(ChangeOp change)
    {
        switch (change)
        {
            case SetDeviceEnabled e:
                foreach (var list in _userDevices.Values)
                    for (var i = 0; i < list.Count; i++)
                        if (list[i].ObjectId == e.Device.Id)
                            list[i] = list[i] with { Enabled = e.New };
                return true;
            case RunDeviceAction { Command: DeviceCommand.Retire or DeviceCommand.Wipe } a:
                foreach (var list in _userDevices.Values)
                    list.RemoveAll(d => d.ManagedId == a.Device.Id);
                return true;
            case RunDeviceAction:
                return true;
            default:
                return false;
        }
    }
}
