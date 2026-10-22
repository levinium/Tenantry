namespace Tenantry.Core.Directory;

/// <summary>
/// One of a person's devices: its Entra object (registered or joined) and, when Intune manages it,
/// its Intune record, joined on the Entra device ID. Either side can be missing.
/// </summary>
/// <param name="ObjectId">The Entra device object's ID (enable, disable).</param>
/// <param name="DeviceId">The Entra device ID: what BitLocker keys and LAPS passwords are filed under.</param>
/// <param name="ManagedId">The Intune managed device's ID (sync, restart, wipe…).</param>
/// <param name="JoinType">"Entra joined", "Hybrid joined", "Registered" or "Intune only".</param>
/// <param name="Relation">How it's theirs: "Primary user", "Registered by them", "They own it".</param>
public sealed record UserDevice(
    string? ObjectId,
    string? DeviceId,
    string? ManagedId,
    string Name,
    string? Os,
    string? OsVersion,
    string? Manufacturer,
    string? Model,
    string? Serial,
    string JoinType,
    bool? Enabled,
    bool? Compliant,
    bool? Encrypted,
    DateTimeOffset? LastActivity,
    DateTimeOffset? Registered,
    string Relation,
    string? Ownership)
{
    public bool IsManaged => ManagedId is not null;

    public bool IsWindows => Os?.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>A BitLocker recovery key's metadata; the key itself is read only when asked for (Microsoft audits each read).</summary>
public sealed record RecoveryKeyInfo(string Id, DateTimeOffset? Created, string Volume);

/// <summary>A device's backed-up local admin password (Windows LAPS): when, and for which account. The password is read only when asked for.</summary>
public sealed record LapsInfo(string? Account, DateTimeOffset? BackedUp);

/// <summary>The Devices tab's reads, live from Graph.</summary>
public interface IDeviceRecords
{
    /// <summary>Their Entra devices and Intune devices, joined; or why they can't be read (no Intune role).</summary>
    Task<Available<IReadOnlyList<UserDevice>>> GetDevicesAsync(string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecoveryKeyInfo>> GetRecoveryKeysAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>One recovery key. Microsoft records who read it.</summary>
    Task<string> RevealRecoveryKeyAsync(string keyId, CancellationToken cancellationToken = default);

    /// <summary>Null when the device backs up no local admin password.</summary>
    Task<LapsInfo?> GetLapsAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>The latest local admin password and its account. Microsoft records who read it.</summary>
    Task<(string Account, string Password)> RevealLapsAsync(string deviceId, CancellationToken cancellationToken = default);
}
