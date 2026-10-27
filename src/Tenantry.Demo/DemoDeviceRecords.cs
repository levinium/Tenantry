using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// A person's devices in the demo: theirs from the demo directory's one set of devices (see
/// <see cref="DemoTenantDirectory"/>). Keys and passwords are made up.
/// </summary>
public sealed partial class DemoUserRecords : IDeviceRecords
{
    public async Task<Available<IReadOnlyList<UserDevice>>> GetDevicesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var devices = (await directory.GetDevicesAsync(cancellationToken)).Where(d => d.UserId == userId);
        return new(devices
            .Select(d => new UserDevice(d.ObjectId, d.DeviceId, d.ManagedId, d.Name, d.Os, d.OsVersion, d.Manufacturer, d.Model, d.Serial, d.JoinType,
                d.Enabled, d.Compliant, d.Encrypted, d.LastSeen, d.Registered, d.IsManaged ? "Primary user" : "Registered by them", d.Ownership))
            .OrderByDescending(d => d.LastActivity ?? DateTimeOffset.MinValue)
            .ToArray());
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
                directory.UpdateDevice(e.Device.Id, d => d with { Enabled = e.New });
                return true;
            case RunDeviceAction { Command: DeviceCommand.Retire or DeviceCommand.Wipe } a:
                // Intune lets go of it; its Entra record stays, as it does for real.
                directory.UpdateDevice(a.Device.Id, d => d.ObjectId is null ? null
                    : d with { ManagedId = null, Compliant = null, Encrypted = null, LastCheckIn = null, Ownership = null });
                return true;
            case DeleteDevice delete:
                directory.UpdateDevice(delete.Device.Id, _ => null);
                return true;
            case RenameDevice rename:
                directory.UpdateDevice(rename.Device.Id, d => d with { Name = rename.New });
                return true;
            case SetPrimaryUser primary:
                directory.UpdateDevice(primary.Device.Id, d => d with { UserId = primary.NewUser.Id, UserName = primary.NewUser.Name, UserPrincipalName = primary.NewUser.Address });
                return true;
            case SetDeviceOwnership owner:
                directory.UpdateDevice(owner.Device.Id, d => d with { Ownership = owner.NewCompany ? DeviceFields.Company : DeviceFields.Personal });
                return true;
            case SetLostMode:
                return true;
            case SetAutopilotGroupTag or DeleteAutopilotDevice:
                return ApplyAutopilot(change);
            case RunDeviceAction:
                return true;
            default:
                return false;
        }
    }
}
