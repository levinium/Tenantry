using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;

namespace Tenantry.Graph;

/// <summary>Device changes (4c-2): enabling or disabling in Entra, and Intune remote actions.</summary>
public sealed partial class GraphChangeExecutor
{
    async Task<ApplyResult> SetDeviceEnabledAsync(SetDeviceEnabled change, CancellationToken cancellationToken)
    {
        var result = await GraphCheckedAsync(
            ct => graph.PatchAsync($"devices/{change.Device.Id}", new JsonObject { ["accountEnabled"] = change.New }, ct),
            ct => graph.GetAsync($"devices/{change.Device.Id}?$select=accountEnabled", ct),
            answer => answer is { } d && d.TryGetProperty("accountEnabled", out var on) && on.ValueKind == (change.New ? JsonValueKind.True : JsonValueKind.False),
            cancellationToken);
        // The Devices list shows it at once.
        if (result.Outcome == ChangeOutcome.Applied)
            store.Apply(DirectorySync.Devices, [new EntityChange(change.Device.Id, new JsonObject { ["accountEnabled"] = change.New }, OnlyIfExists: true)]);
        return result;
    }

    /// <summary>
    /// Deletes the Intune record first (so Intune doesn't recreate the Entra one), then Entra's.
    /// Either already gone counts as deleted. The local copy drops both at once.
    /// </summary>
    async Task<ApplyResult> DeleteDeviceAsync(DeleteDevice change, CancellationToken cancellationToken)
    {
        var managedId = change.Device.Kind == "manageddevice" ? change.Device.Id : change.ManagedId;
        try
        {
            if (managedId is not null)
            {
                await DeleteIfThereAsync($"deviceManagement/managedDevices/{managedId}", cancellationToken);
                store.Apply(DirectorySync.ManagedDevices, [EntityChange.Removed(managedId)]);
            }
            if (change.Device.Kind == "device")
            {
                await DeleteIfThereAsync($"devices/{change.Device.Id}", cancellationToken);
                store.Apply(DirectorySync.Devices, [EntityChange.Removed(change.Device.Id)]);
            }
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    async Task DeleteIfThereAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await graph.DeleteAsync(path, cancellationToken);
        }
        catch (GraphException e) when (e.Status == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

    /// <summary>
    /// Intune sends the action to the device, which carries it out when it next checks in: there's
    /// nothing to read back yet. Rotating the local admin password is a beta-only action.
    /// </summary>
    async Task<ApplyResult> RunDeviceActionAsync(RunDeviceAction change, CancellationToken cancellationToken)
    {
        var (path, body) = change.Command switch
        {
            DeviceCommand.Sync => ("syncDevice", new JsonObject()),
            DeviceCommand.Restart => ("rebootNow", new JsonObject()),
            DeviceCommand.Lock => ("remoteLock", new JsonObject()),
            DeviceCommand.RotateLocalAdminPassword => ("rotateLocalAdminPassword", new JsonObject()),
            DeviceCommand.Retire => ("retire", new JsonObject()),
            _ => ("wipe", new JsonObject { ["keepEnrollmentData"] = false, ["keepUserData"] = false }),
        };
        var root = change.Command == DeviceCommand.RotateLocalAdminPassword ? "https://graph.microsoft.com/beta/" : "";
        var result = await GraphCheckedAsync(
            ct => graph.PostAsync($"{root}deviceManagement/managedDevices/{change.Device.Id}/{path}", body, ct),
            null, _ => true, cancellationToken);
        return result.Succeeded
            ? new ApplyResult(ChangeOutcome.Applied, change.Command == DeviceCommand.Sync ? null : "Sent; the device carries it out when it next checks in.")
            : result;
    }
}
