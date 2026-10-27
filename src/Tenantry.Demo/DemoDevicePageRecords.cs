using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's device pages: hardware, compliance, apps and Defender made up from each device's own
/// seed, so a page looks the same every time it opens. Group memberships live here too.
/// </summary>
public sealed partial class DemoUserRecords : IDevicePageRecords
{
    readonly Dictionary<string, List<DeviceGroup>> _deviceGroups = new(StringComparer.Ordinal);

    async Task<DeviceSummary?> DeviceAsync(string id, CancellationToken cancellationToken) =>
        (await directory.GetDevicesAsync(cancellationToken)).FirstOrDefault(d => d.ManagedId == id || d.ObjectId == id || d.Key == id);

    public Task<DeviceLive> GetDeviceAsync(DeviceSummary device, CancellationToken cancellationToken = default)
    {
        var random = new Random(StableSeed(device.Key) + 61);
        var phone = device.Platform is "iOS/iPadOS" or "Android";
        DeviceHardware? hardware = device.IsManaged ? new DeviceHardware
        {
            Serial = device.Serial,
            MemoryBytes = phone ? 6L << 30 : (random.NextDouble() < 0.5 ? 16L : 32L) << 30,
            StorageTotal = device.StorageTotal,
            StorageFree = device.StorageFree,
            Processor = phone ? "arm64" : "x64",
            BatteryHealthPercent = random.Next(72, 100),
            BatteryCycles = random.Next(40, 600),
            Tpm = phone ? null : "2.0",
            OsEdition = device.Platform == "Windows" ? "Enterprise" : null,
            OsBuild = device.OsVersion,
            OsLanguage = "en-US",
            WifiMac = string.Join(':', Enumerable.Range(0, 6).Select(_ => random.Next(0, 256).ToString("X2", System.Globalization.CultureInfo.InvariantCulture))),
            IpAddress = $"10.20.{random.Next(1, 30)}.{random.Next(2, 250)}",
            Imei = phone ? $"35{random.Next(10000000, 99999999)}{random.Next(10000, 99999)}" : null,
            EnrollmentType = device.Platform == "Windows" ? "windowsAzureADJoin" : phone ? "userEnrollment" : "appleUserEnrollment",
            Autopilot = device.Platform == "Windows" && random.NextDouble() < 0.6,
            CertificateExpires = DateTimeOffset.UtcNow.AddDays(random.Next(60, 365)),
            GraceExpires = device.Compliant == false ? DateTimeOffset.UtcNow.AddDays(random.Next(1, 5)) : null,
            ThreatState = device.Platform == "Windows" ? "secured" : null,
            ActiveMalware = 0,
        } : null;
        IReadOnlyList<LoggedOnUser> loggedOn = device.Platform == "Windows" && device.UserId is { } user
            ? [new LoggedOnUser(user, device.LastSignIn ?? device.LastCheckIn)]
            : [];
        IReadOnlyList<DevicePerson> people = device.UserId is { } owner && device.ObjectId is not null
            ? [new DevicePerson(owner, device.UserName ?? owner, device.UserPrincipalName)]
            : [];
        return Task.FromResult(new DeviceLive(hardware, loggedOn, people, people));
    }

    public async Task<Available<IReadOnlyList<PolicyState>>> GetCompliancePoliciesAsync(string managedId, CancellationToken cancellationToken = default)
    {
        var device = await DeviceAsync(managedId, cancellationToken);
        var failing = device?.Compliant == false;
        var platform = device?.Platform ?? "Windows";
        return new(
        [
            new PolicyState("cp-default", "Default Device Compliance Policy", "compliant", 3),
            new PolicyState("cp-baseline", $"{platform} Baseline", failing ? "nonCompliant" : "compliant", 4),
        ]);
    }

    public Task<Available<IReadOnlyList<PolicyState>>> GetConfigurationProfilesAsync(string managedId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Available<IReadOnlyList<PolicyState>>(
        [
            new PolicyState("cf-wifi", "Office Wi-Fi", "compliant", 2),
            new PolicyState("cf-updates", "Update Rings", "compliant", 5),
            new PolicyState("cf-browser", "Browser Settings", StableSeed(managedId) % 5 == 0 ? "error" : "compliant", 3),
        ]));

    public async Task<IReadOnlyList<PolicySetting>> GetPolicySettingsAsync(string managedId, string policyStateId, bool compliance, CancellationToken cancellationToken = default)
    {
        var device = await DeviceAsync(managedId, cancellationToken);
        return policyStateId switch
        {
            "cp-default" =>
            [
                new("DefaultDeviceCompliancePolicy.RequireRemainContact", "compliant", "0", null),
                new("DefaultDeviceCompliancePolicy.RequireDeviceCompliancePolicyAssigned", "compliant", "0", null),
                new("DefaultDeviceCompliancePolicy.RequireUserExistence", "compliant", "0", null),
            ],
            "cp-baseline" =>
            [
                new("Windows10CompliancePolicy.BitLockerEnabled", device?.Encrypted == false ? "nonCompliant" : "compliant", "0", null),
                new("Windows10CompliancePolicy.SecureBootEnabled", "compliant", "0", null),
                new("Windows10CompliancePolicy.OsMinimumVersion", device?.Compliant == false ? "nonCompliant" : "compliant", "0", device?.OsVersion),
                new("Windows10CompliancePolicy.FirewallEnabled", "compliant", "0", null),
            ],
            "cf-browser" when StableSeed(managedId) % 5 == 0 =>
            [
                new("BrowserSettings.HomePage", "error", "The setting couldn't be applied (0x87d1fde8)", null),
                new("BrowserSettings.BlockPopups", "compliant", "0", null),
                new("BrowserSettings.SmartScreen", "compliant", "0", null),
            ],
            _ => Enumerable.Range(1, 3).Select(i => new PolicySetting($"Profile.Setting{i}", "compliant", "0", null)).ToArray(),
        };
    }

    public Task<Available<IReadOnlyList<DeviceAppState>>> GetAppStatesAsync(string userId, string managedId, CancellationToken cancellationToken = default)
    {
        var failed = StableSeed(managedId) % 4 == 0;
        return Task.FromResult(new Available<IReadOnlyList<DeviceAppState>>(
        [
            new DeviceAppState("Microsoft 365 Apps", "16.0", "requiredInstall", "installed"),
            new DeviceAppState("Company Portal", "11.2", "requiredInstall", "installed"),
            new DeviceAppState("VPN Client", "5.4", "requiredInstall", failed ? "failed" : "installed"),
            new DeviceAppState("Whiteboard", null, "availableInstall", "notInstalled"),
        ]));
    }

    static readonly (string Name, string Publisher)[] CommonApps =
    [
        ("Microsoft Edge", "Microsoft Corporation"), ("Microsoft Teams", "Microsoft Corporation"), ("Microsoft OneDrive", "Microsoft Corporation"),
        ("Microsoft 365 Apps for enterprise", "Microsoft Corporation"), ("Google Chrome", "Google LLC"), ("Zoom Workplace", "Zoom Video Communications"),
        ("Adobe Acrobat Reader", "Adobe Inc."), ("7-Zip", "Igor Pavlov"), ("Notepad++", "Notepad++ Team"), ("VLC media player", "VideoLAN"),
        ("Company Portal", "Microsoft Corporation"), ("Windows App", "Microsoft Corporation"), ("Visual C++ Redistributable", "Microsoft Corporation"),
        ("Mozilla Firefox", "Mozilla"), ("Slack", "Slack Technologies"), ("Printer Driver Utility", "Contoso Print"),
    ];

    public Task<Available<IReadOnlyList<DetectedApp>>> GetDetectedAppsAsync(string managedId, CancellationToken cancellationToken = default)
    {
        var random = new Random(StableSeed(managedId) + 62);
        var apps = CommonApps.Where(_ => random.NextDouble() < 0.8)
            .Select(a => new DetectedApp(a.Name, $"{random.Next(1, 130)}.{random.Next(0, 10)}.{random.Next(0, 9999)}", a.Publisher, (long)random.Next(5, 900) << 20))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return Task.FromResult(new Available<IReadOnlyList<DetectedApp>>(apps));
    }

    public async Task<Available<DeviceProtection>> GetProtectionAsync(string managedId, CancellationToken cancellationToken = default)
    {
        var device = await DeviceAsync(managedId, cancellationToken);
        if (device?.Platform != "Windows")
            return Available<DeviceProtection>.Because("This device doesn't report Microsoft Defender's state (only Windows devices do).");
        var random = new Random(StableSeed(managedId) + 63);
        var found = random.NextDouble() < 0.15;
        var now = DateTimeOffset.UtcNow;
        return new(new DeviceProtection(
            new ProtectionState(true, true, true, false, false, random.NextDouble() < 0.1, found, false, found ? "pendingFullScan" : "clean", "noStatus",
                $"1.421.{random.Next(100, 999)}.0", now.AddHours(-random.Next(2, 48)), now.AddDays(-random.Next(3, 20)), now.AddHours(-random.Next(1, 12))),
            found ? [new MalwareFound("Trojan:Win32/Example.A", "high", "quarantined", "trojan", now.AddDays(-2))] : []));
    }

    public async Task<Available<IReadOnlyList<DeviceGroup>>> GetGroupsAsync(string objectId, CancellationToken cancellationToken = default)
    {
        if (!_deviceGroups.TryGetValue(objectId, out var list))
        {
            var device = await DeviceAsync(objectId, cancellationToken);
            var groups = await directory.GetGroupsAsync(cancellationToken);
            list = [];
            if (device?.Platform == "Windows" && groups.FirstOrDefault(g => g.DisplayName == "SG IT") is { } it)
                list.Add(new DeviceGroup(it.Id, it.DisplayName, false));
            if (device?.UserId is null && groups.FirstOrDefault(g => g.DisplayName == "Front Desk Devices") is { } desk)
                list.Add(new DeviceGroup(desk.Id, desk.DisplayName, false));
            _deviceGroups[objectId] = list;
        }
        return new(list.ToArray());
    }

    /// <summary>A device joining or leaving a group (written by the demo's change executor).</summary>
    internal void SetDeviceGroup(string objectId, string groupId, string groupName, bool member)
    {
        if (!_deviceGroups.TryGetValue(objectId, out var list))
            _deviceGroups[objectId] = list = [];
        list.RemoveAll(g => g.Id == groupId);
        if (member)
            list.Add(new DeviceGroup(groupId, groupName, false));
    }
}
