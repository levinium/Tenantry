namespace Tenantry.Core.Directory;

/// <summary>
/// A managed device's hardware and health as Intune last heard it (6b). Every value is optional:
/// what a device reports depends on its platform (a phone has an IMEI and a battery, a desktop
/// neither) and on how it enrolled.
/// </summary>
public sealed record DeviceHardware
{
    public string? Serial { get; init; }
    public string? Imei { get; init; }
    public string? Meid { get; init; }
    public string? PhoneNumber { get; init; }
    public string? Carrier { get; init; }
    public string? WifiMac { get; init; }
    public string? EthernetMac { get; init; }
    public string? IpAddress { get; init; }
    public long? MemoryBytes { get; init; }
    public long? StorageTotal { get; init; }
    public long? StorageFree { get; init; }
    public string? Processor { get; init; }
    public int? BatteryHealthPercent { get; init; }
    public int? BatteryCycles { get; init; }
    public string? Tpm { get; init; }
    public string? OsEdition { get; init; }
    public string? OsBuild { get; init; }
    public string? OsLanguage { get; init; }
    public string? Bios { get; init; }
    public bool? Supervised { get; init; }
    public bool? Jailbroken { get; init; }
    public bool? SharedDevice { get; init; }
    public string? EnrollmentType { get; init; }
    public string? EnrollmentProfile { get; init; }
    public bool? Autopilot { get; init; }
    public DateTimeOffset? CertificateExpires { get; init; }

    /// <summary>When a device out of compliance loses access, while it's in its grace period.</summary>
    public DateTimeOffset? GraceExpires { get; init; }

    /// <summary>What a threat-defence partner (Microsoft Defender) reports: "Secured", "High", …</summary>
    public string? ThreatState { get; init; }
    public int? ActiveMalware { get; init; }
    public int? RemediatedMalware { get; init; }
}

/// <summary>Someone who signed in to a Windows device, and when last.</summary>
public sealed record LoggedOnUser(string UserId, DateTimeOffset? LastLogOn);

/// <summary>A person attached to a device in Entra (its registered owner or users).</summary>
public sealed record DevicePerson(string Id, string Name, string? UserPrincipalName);

/// <summary>What a device page reads about the device itself, live.</summary>
/// <param name="IntuneProblem">Why Intune's part couldn't be read (no role); Entra's part still shows.</param>
public sealed record DeviceLive(
    DeviceHardware? Hardware,
    IReadOnlyList<LoggedOnUser> LoggedOn,
    IReadOnlyList<DevicePerson> Owners,
    IReadOnlyList<DevicePerson> Users,
    string? IntuneProblem = null);

/// <summary>A compliance policy or configuration profile as it applies to one device.</summary>
/// <param name="State">Intune's state: "compliant", "nonCompliant", "error", "conflict", "notApplicable", …</param>
public sealed record PolicyState(string Id, string Name, string State, int SettingCount)
{
    /// <summary>Something the admin should look at.</summary>
    public bool IsProblem => State is "nonCompliant" or "error" or "conflict";
}

/// <summary>One setting of a policy on a device: what it checks and how the device does.</summary>
public sealed record PolicySetting(string Name, string State, string? Error, string? Current);

/// <summary>An app Intune assigns to the device's user, with its install state on this device.</summary>
public sealed record DeviceAppState(string Name, string? Version, string Intent, string State)
{
    /// <summary>A required app that failed or isn't there ("requiredInstall" or "requiredAndAvailableInstall", seen live).</summary>
    public bool IsProblem => State is "failed" or "notInstalled" && Intent.StartsWith("required", StringComparison.Ordinal);
}

/// <summary>An app the device reports as installed (Intune's discovered apps).</summary>
public sealed record DetectedApp(string Name, string? Version, string? Publisher, long? SizeBytes);

/// <summary>Microsoft Defender on a Windows device, as it last reported.</summary>
public sealed record ProtectionState(
    bool? MalwareProtection,
    bool? RealTime,
    bool? Tamper,
    bool QuickScanOverdue,
    bool FullScanOverdue,
    bool SignatureOverdue,
    bool RebootRequired,
    bool FullScanRequired,
    string? DeviceState,
    string? ProductStatus,
    string? SignatureVersion,
    DateTimeOffset? LastQuickScan,
    DateTimeOffset? LastFullScan,
    DateTimeOffset? LastReported);

/// <summary>Malware Defender found on the device.</summary>
public sealed record MalwareFound(string Name, string? Severity, string? State, string? Category, DateTimeOffset? Detected);

/// <summary>Defender's state and what it has found; State is null when the device doesn't report one.</summary>
public sealed record DeviceProtection(ProtectionState? State, IReadOnlyList<MalwareFound> Malware);

/// <summary>An Entra group the device is a direct member of.</summary>
public sealed record DeviceGroup(string Id, string Name, bool IsDynamic);

/// <summary>The device page's reads, live from Graph (Intune's beta where v1.0 lacks them).</summary>
public interface IDevicePageRecords
{
    /// <summary>Hardware and health, who signs in to it, and its Entra owner and users.</summary>
    Task<DeviceLive> GetDeviceAsync(DeviceSummary device, CancellationToken cancellationToken = default);

    Task<Available<IReadOnlyList<PolicyState>>> GetCompliancePoliciesAsync(string managedId, CancellationToken cancellationToken = default);

    Task<Available<IReadOnlyList<PolicyState>>> GetConfigurationProfilesAsync(string managedId, CancellationToken cancellationToken = default);

    /// <param name="compliance">A compliance policy's settings; otherwise a configuration profile's.</param>
    Task<IReadOnlyList<PolicySetting>> GetPolicySettingsAsync(string managedId, string policyStateId, bool compliance, CancellationToken cancellationToken = default);

    /// <summary>The apps Intune assigns to its primary user, and how each is doing on this device.</summary>
    Task<Available<IReadOnlyList<DeviceAppState>>> GetAppStatesAsync(string userId, string managedId, CancellationToken cancellationToken = default);

    Task<Available<IReadOnlyList<DetectedApp>>> GetDetectedAppsAsync(string managedId, CancellationToken cancellationToken = default);

    Task<Available<DeviceProtection>> GetProtectionAsync(string managedId, CancellationToken cancellationToken = default);

    Task<Available<IReadOnlyList<DeviceGroup>>> GetGroupsAsync(string objectId, CancellationToken cancellationToken = default);
}
