using System.Net;
using System.Text;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>The Devices tab's reads (4c-2): Entra devices joined with Intune's, BitLocker keys and LAPS passwords.</summary>
public sealed partial class GraphUserRecords : IDeviceRecords
{
    const string EntraSelect = "id,deviceId,displayName,operatingSystem,operatingSystemVersion,manufacturer,model,trustType,accountEnabled,isCompliant,"
                               + "approximateLastSignInDateTime,registrationDateTime";

    const string IntuneSelect = "id,deviceName,operatingSystem,osVersion,manufacturer,model,serialNumber,azureADDeviceId,complianceState,isEncrypted,"
                                + "lastSyncDateTime,enrolledDateTime,managedDeviceOwnerType";

    public async Task<Available<IReadOnlyList<UserDevice>>> GetDevicesAsync(string userId, CancellationToken cancellationToken = default)
    {
        // Registered and owned overlap (a joined laptop is usually both); Intune's list is by primary user.
        var registered = Collect($"users/{userId}/registeredDevices?$select={EntraSelect}", cancellationToken);
        var owned = Collect($"users/{userId}/ownedDevices?$select={EntraSelect}", cancellationToken);
        Task<List<JsonElement>> managed;
        string? intuneProblem = null;
        try
        {
            managed = Collect($"users/{userId}/managedDevices?$select={IntuneSelect}", cancellationToken);
            await managed;
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            intuneProblem = "Intune devices need an Intune role (and an Intune license in the tenant).";
            managed = Task.FromResult(new List<JsonElement>());
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
        {
            // Intune answers "not found" for someone it has never managed a device for (seen live).
            managed = Task.FromResult(new List<JsonElement>());
        }

        var entra = new Dictionary<string, (JsonElement Device, string Relation)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in await owned)
            if (Text(d, "deviceId") is { } id)
                entra[id] = (d, "They own it");
        foreach (var d in await registered)
            if (Text(d, "deviceId") is { } id)
                entra.TryAdd(id, (d, "Registered by them"));

        var devices = new List<UserDevice>();
        var joined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in await managed)
        {
            var azureId = Text(m, "azureADDeviceId") is { } a && a != Guid.Empty.ToString() ? a : null;
            var hasEntra = azureId is not null && entra.TryGetValue(azureId, out var e);
            var entraDevice = hasEntra ? entra[azureId!].Device : default;
            if (hasEntra)
                joined.Add(azureId!);
            devices.Add(new UserDevice(
                ObjectId: hasEntra ? Text(entraDevice, "id") : null,
                DeviceId: azureId,
                ManagedId: Text(m, "id"),
                Name: Text(m, "deviceName") ?? "A device",
                Os: Text(m, "operatingSystem"),
                OsVersion: Text(m, "osVersion"),
                Manufacturer: Text(m, "manufacturer"),
                Model: Text(m, "model"),
                Serial: Text(m, "serialNumber") is { Length: > 0 } serial ? serial : null,
                JoinType: hasEntra ? Join(Text(entraDevice, "trustType")) : "Intune only",
                Enabled: hasEntra && entraDevice.TryGetProperty("accountEnabled", out var on) ? on.ValueKind == JsonValueKind.True : null,
                Compliant: Text(m, "complianceState") switch { "compliant" => true, "noncompliant" => false, _ => null },
                Encrypted: m.TryGetProperty("isEncrypted", out var enc) && enc.ValueKind is JsonValueKind.True or JsonValueKind.False ? enc.ValueKind == JsonValueKind.True : null,
                LastActivity: When(m, "lastSyncDateTime"),
                Registered: When(m, "enrolledDateTime"),
                Relation: "Primary user",
                Ownership: Text(m, "managedDeviceOwnerType") switch { "company" => "Company-owned", "personal" => "Personal", _ => null }));
        }
        foreach (var (id, (d, relation)) in entra)
        {
            if (joined.Contains(id))
                continue;
            devices.Add(new UserDevice(
                ObjectId: Text(d, "id"), DeviceId: id, ManagedId: null,
                Name: Text(d, "displayName") ?? "A device",
                Os: Text(d, "operatingSystem"), OsVersion: Text(d, "operatingSystemVersion"),
                Manufacturer: Text(d, "manufacturer"), Model: Text(d, "model"), Serial: null,
                JoinType: Join(Text(d, "trustType")),
                Enabled: d.TryGetProperty("accountEnabled", out var on) ? on.ValueKind == JsonValueKind.True : null,
                Compliant: d.TryGetProperty("isCompliant", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False ? c.ValueKind == JsonValueKind.True : null,
                Encrypted: null,
                LastActivity: When(d, "approximateLastSignInDateTime"),
                Registered: When(d, "registrationDateTime"),
                Relation: relation, Ownership: null));
        }
        var ordered = devices.OrderByDescending(d => d.LastActivity ?? DateTimeOffset.MinValue).ToArray();
        return intuneProblem is null || ordered.Length > 0 ? new(ordered) : Available<IReadOnlyList<UserDevice>>.Because(intuneProblem);
    }

    async Task<List<JsonElement>> Collect(string path, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        await foreach (var item in graph.GetAllAsync(path, cancellationToken))
            items.Add(item);
        return items;
    }

    internal static string Join(string? trustType) => trustType switch
    {
        "AzureAd" => "Entra joined",
        "ServerAd" => "Hybrid joined",
        "Workplace" => "Registered",
        _ => "Registered",
    };

    public async Task<IReadOnlyList<RecoveryKeyInfo>> GetRecoveryKeysAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var keys = new List<RecoveryKeyInfo>();
        await foreach (var k in graph.GetAllAsync($"informationProtection/bitlocker/recoveryKeys?$filter=deviceId eq '{deviceId}'", cancellationToken))
            keys.Add(new RecoveryKeyInfo(Text(k, "id") ?? "", When(k, "createdDateTime"), Text(k, "volumeType") switch
            {
                "operatingSystemVolume" => "System drive",
                "fixedDataVolume" => "Data drive",
                "removableDataVolume" => "Removable drive",
                _ => "Drive",
            }));
        return keys.OrderByDescending(k => k.Created ?? DateTimeOffset.MinValue).ToArray();
    }

    public async Task<string> RevealRecoveryKeyAsync(string keyId, CancellationToken cancellationToken = default) =>
        Text(await graph.GetAsync($"informationProtection/bitlocker/recoveryKeys/{keyId}?$select=key", cancellationToken), "key")
        ?? throw new InvalidOperationException("Microsoft 365 didn't return the key.");

    public async Task<LapsInfo?> GetLapsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await graph.GetAsync($"directory/deviceLocalCredentials/{deviceId}", cancellationToken);
            return new LapsInfo(null, When(info, "lastBackupDateTime"));
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>The newest backed-up credential; Graph sends the password base64-encoded.</summary>
    public async Task<(string Account, string Password)> RevealLapsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var info = await graph.GetAsync($"directory/deviceLocalCredentials/{deviceId}?$select=credentials", cancellationToken);
        var newest = info.TryGetProperty("credentials", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().OrderByDescending(c => When(c, "backupDateTime") ?? DateTimeOffset.MinValue).FirstOrDefault()
            : default;
        if (newest.ValueKind != JsonValueKind.Object || Text(newest, "passwordBase64") is not { } encoded)
            throw new InvalidOperationException("No local admin password is backed up for this device.");
        return (Text(newest, "accountName") ?? "Administrator", Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }
}
