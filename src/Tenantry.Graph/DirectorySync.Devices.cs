using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// The Devices list's two sources: Entra's devices through <c>devices/delta</c> (changes only,
/// like users and groups), and Intune's managed devices read whole (Intune has no delta), at most
/// hourly or on Refresh. Without an Intune role or license the Intune part is skipped with a note.
/// </summary>
public sealed partial class DirectorySync
{
    /// <summary>Entra's devices, keyed by object ID.</summary>
    public const string Devices = "device";

    /// <summary>Intune's managed devices, keyed by Intune ID; joined to <see cref="Devices"/> on the Entra device ID.</summary>
    public const string ManagedDevices = "manageddevice";

    const string DeviceSelect = "id,deviceId,displayName,operatingSystem,operatingSystemVersion,manufacturer,model,trustType,accountEnabled,isCompliant,"
                                + "approximateLastSignInDateTime,registrationDateTime";

    internal const string ManagedDeviceSelect = "id,deviceName,operatingSystem,osVersion,manufacturer,model,serialNumber,azureADDeviceId,complianceState,"
                                                + "isEncrypted,lastSyncDateTime,enrolledDateTime,managedDeviceOwnerType,userId,userPrincipalName,"
                                                + "userDisplayName,totalStorageSpaceInBytes,freeStorageSpaceInBytes";

    /// <summary>The Devices list's Refresh: Entra's device changes and the whole Intune list, nothing else.</summary>
    public async Task<int> SyncDevicesAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            var notes = new List<string>();
            var changed = await SyncDevicesAsync(notes, SyncScope.Refresh, progress, cancellationToken)
                          + await SyncManagedDevicesAsync(notes, progress, cancellationToken)
                          + await SyncRecoveryKeysAsync(progress, cancellationToken);
            // Keep the other parts' notes; replace the devices' own.
            _notes = [.. _notes.Where(n => !n.StartsWith("Devices weren't", StringComparison.Ordinal) && !n.StartsWith("Intune devices", StringComparison.Ordinal)), .. notes];
            return changed;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Entra's devices, changes only. No permission to read them is a note, not a failed sync.</summary>
    async Task<int> SyncDevicesAsync(List<string> notes, SyncScope scope, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report("Checking devices for changes…");
            return await SyncDeltaAsync(Devices, Devices, $"devices/delta?$select={DeviceSelect}", "devices", scope,
                _ => item => new EntityChange(Id(item), ToObject(item)), progress, cancellationToken);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            notes.Add("Devices weren't read: reading Entra devices needs a role that can read the directory's devices.");
            return 0;
        }
    }

    /// <summary>Every device Intune manages, stored whole each time (devices it no longer manages drop out).</summary>
    async Task<int> SyncManagedDevicesAsync(List<string> notes, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report("Reading Intune devices…");
            var changes = new List<EntityChange>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var item in graph.GetAllAsync($"deviceManagement/managedDevices?$select={ManagedDeviceSelect}&$top=999", cancellationToken))
            {
                var id = Id(item);
                seen.Add(id);
                changes.Add(new EntityChange(id, ToObject(item)));
                if (seen.Count % 1000 == 0)
                    progress?.Report($"Reading Intune devices… {seen.Count:N0}");
            }
            var changed = store.Apply(ManagedDevices, changes) + store.RemoveExcept(ManagedDevices, seen);
            store.SetMark(ManagedDevices, new SyncMark(null, _now()));
            return changed;
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            // No Intune role, or no Intune in the tenant: devices show from Entra only.
            notes.Add("Intune devices weren't read: they need an Intune role (and Intune in the tenant).");
            store.SetMark(ManagedDevices, new SyncMark(null, _now()));
            return 0;
        }
    }

    /// <summary>
    /// Which devices have a BitLocker recovery key backed up: stored per Entra device ID. Intune's
    /// own "encrypted" flag depends on a compliance policy asking for it (measured live: half the
    /// laptops with a key backed up reported not encrypted), so a backed-up key counts as encrypted.
    /// Only the keys' IDs and devices are read, never a key.
    /// </summary>
    public const string RecoveryKeys = "recoverykey";

    async Task<int> SyncRecoveryKeysAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report("Reading which devices have BitLocker keys backed up…");
            var devices = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            await foreach (var key in graph.GetAllAsync("informationProtection/bitlocker/recoveryKeys?$select=id,deviceId", cancellationToken))
                if (key.TryGetProperty("deviceId", out var d) && d.GetString() is { Length: > 0 } deviceId)
                    devices[deviceId] = new JsonObject { ["id"] = deviceId };
            var changed = store.Apply(RecoveryKeys, devices.Select(d => new EntityChange(d.Key, d.Value)).ToList())
                          + store.RemoveExcept(RecoveryKeys, devices.Keys.ToHashSet(StringComparer.Ordinal));
            store.SetMark(RecoveryKeys, new SyncMark(null, _now()));
            return changed;
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            // No role that can read BitLocker keys: encryption is Intune's word alone.
            store.SetMark(RecoveryKeys, new SyncMark(null, _now()));
            return 0;
        }
    }

    /// <summary>Whether a stored Intune row has a real Entra device ID (Intune sends all zeros when it has none).</summary>
    internal static string? AzureDeviceId(JsonElement managed) =>
        managed.TryGetProperty("azureADDeviceId", out var a) && a.GetString() is { Length: > 0 } id && id != Guid.Empty.ToString() ? id : null;
}
