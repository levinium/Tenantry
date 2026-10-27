using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's devices, the one set behind both the Devices list and each person's Devices tab.
/// Seeded per person: a company laptop managed by Intune, usually a phone, sometimes a personal
/// tablet registered only. A few shared devices have no primary user, and some laptops haven't been
/// seen for months (for the stale views).
/// </summary>
public sealed partial class DemoTenantDirectory
{
    List<DeviceSummary>? _devices;

    public async Task<IReadOnlyList<DeviceSummary>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var users = await GetUsersAsync(cancellationToken);
        return (_devices ??= GenerateDevices(users)).ToArray();
    }

    /// <summary>Changes one device, found by its key, Entra object ID or Intune ID; false when there's none.</summary>
    public bool UpdateDevice(string id, Func<DeviceSummary, DeviceSummary?> change)
    {
        if (_devices is null)
            return false;
        var index = _devices.FindIndex(d => d.Key == id || d.ObjectId == id || d.ManagedId == id);
        if (index < 0)
            return false;
        if (change(_devices[index]) is { } changed)
            _devices[index] = changed;
        else
            _devices.RemoveAt(index);
        return true;
    }

    static List<DeviceSummary> GenerateDevices(IReadOnlyList<UserSummary> users)
    {
        var now = DateTimeOffset.UtcNow;
        var devices = new List<DeviceSummary>();
        foreach (var person in users.Where(u => u.Kind == UserKind.Member && u.Mailbox?.Type is null or "User"))
        {
            var id = person.Id;
            var random = new Random(DemoUserRecords.StableSeed(id) + 30);
            var initials = new string(person.DisplayName.Split(' ').Select(w => w[0]).ToArray()).ToUpperInvariant();
            var stale = random.NextDouble() < 0.06;
            var mac = random.NextDouble() < 0.15;
            devices.Add(new DeviceSummary($"obj-l-{id}", $"obj-l-{id}", $"dev-l-{id}", $"mdm-l-{id}",
                $"NG-{initials}-{random.Next(100, 999)}",
                mac ? "macOS" : "Windows", mac ? "15.6" : "10.0.26100", mac ? "Apple" : "Lenovo", mac ? "MacBook Air" : "ThinkPad T14",
                $"PF{random.Next(1000000, 9999999)}", mac ? "Registered" : "Entra joined", true, random.NextDouble() > 0.1, random.NextDouble() > 0.05,
                LastCheckIn: stale ? now.AddDays(-random.Next(95, 200)) : now.AddHours(-random.Next(1, 48)),
                LastSignIn: stale ? now.AddDays(-random.Next(95, 200)) : now.AddHours(-random.Next(1, 72)),
                Registered: now.AddDays(-random.Next(100, 700)), DeviceFields.Company, id, person.DisplayName, person.UserPrincipalName,
                StorageTotal: 512L << 30, StorageFree: (long)((512L << 30) * (0.05 + random.NextDouble() * 0.6))));
            if (random.NextDouble() < 0.7)
                devices.Add(new DeviceSummary($"obj-p-{id}", $"obj-p-{id}", $"dev-p-{id}", $"mdm-p-{id}", $"{person.DisplayName.Split(' ')[0]}'s iPhone",
                    "iOS", "19.1", "Apple", "iPhone 15", null, "Registered", true, true, true,
                    now.AddHours(-random.Next(1, 30)), now.AddHours(-random.Next(1, 30)), now.AddDays(-random.Next(30, 400)), DeviceFields.Personal,
                    id, person.DisplayName, person.UserPrincipalName, 128L << 30, (long)((128L << 30) * random.NextDouble() * 0.5)));
            if (random.NextDouble() < 0.2)
                devices.Add(new DeviceSummary($"obj-t-{id}", $"obj-t-{id}", $"dev-t-{id}", null, "iPad", "iPadOS", "18.6", "Apple", "iPad Air", null,
                    "Registered", true, null, null, null, now.AddDays(-random.Next(5, 60)), now.AddDays(-random.Next(200, 900)), null,
                    id, person.DisplayName, person.UserPrincipalName));
        }

        // Shared devices with no primary user: reception kiosks, a conference room PC, and an old
        // laptop Entra still lists though nobody has used it for a year.
        var shared = new Random(20261027);
        for (var i = 1; i <= 3; i++)
            devices.Add(new DeviceSummary($"obj-k-{i}", $"obj-k-{i}", $"dev-k-{i}", $"mdm-k-{i}", $"NG-KIOSK-{i:D2}", "Android", "15", "Samsung", "Galaxy Tab A9",
                $"R9{shared.Next(100000, 999999)}", "Registered", true, i != 3, true, now.AddHours(-shared.Next(1, 20)), null,
                now.AddDays(-shared.Next(100, 300)), DeviceFields.Company, null, null, null));
        devices.Add(new DeviceSummary("obj-room-1", "obj-room-1", "dev-room-1", "mdm-room-1", "NG-BOARDROOM", "Windows", "10.0.26100", "Dell", "OptiPlex 7010",
            $"CN{shared.Next(1000000, 9999999)}", "Entra joined", true, true, true, now.AddHours(-3), now.AddDays(-1), now.AddDays(-420), DeviceFields.Company,
            null, null, null, 256L << 30, 180L << 30));
        devices.Add(new DeviceSummary("obj-old-1", "obj-old-1", "dev-old-1", null, "NG-OLD-LAPTOP", "Windows", "10.0.19045", "HP", "EliteBook 840 G5",
            null, "Hybrid joined", false, null, null, null, now.AddDays(-380), now.AddDays(-1900), null, null, null, null));
        return devices;
    }
}
