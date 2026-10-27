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

    const string BetaDevices = "https://graph.microsoft.com/beta/deviceManagement/managedDevices/";

    /// <summary>Intune renames the device when it next checks in; there's nothing to read back yet.</summary>
    async Task<ApplyResult> RenameDeviceAsync(RenameDevice change, CancellationToken cancellationToken)
    {
        var result = await GraphCheckedAsync(
            ct => graph.PostAsync($"{BetaDevices}{change.Device.Id}/setDeviceName", new JsonObject { ["deviceName"] = change.New }, ct),
            null, _ => true, cancellationToken);
        return result.Succeeded ? new ApplyResult(ChangeOutcome.Applied, "Sent; it takes the new name when it next checks in (Windows after a restart).") : result;
    }

    Task<ApplyResult> SetPrimaryUserAsync(SetPrimaryUser change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.PostAsync($"{BetaDevices}{change.Device.Id}/users/$ref",
                new JsonObject { ["@odata.id"] = $"https://graph.microsoft.com/beta/users/{change.NewUser.Id}" }, ct),
            ct => graph.GetAsync($"{BetaDevices}{change.Device.Id}?$select=userId", ct),
            answer => answer is { } d && d.TryGetProperty("userId", out var u) && u.GetString() == change.NewUser.Id,
            cancellationToken);

    Task<ApplyResult> SetDeviceOwnershipAsync(SetDeviceOwnership change, CancellationToken cancellationToken)
    {
        var value = change.NewCompany ? "company" : "personal";
        return GraphCheckedAsync(
            ct => graph.PatchAsync($"deviceManagement/managedDevices/{change.Device.Id}", new JsonObject { ["managedDeviceOwnerType"] = value }, ct),
            ct => graph.GetAsync($"deviceManagement/managedDevices/{change.Device.Id}?$select=managedDeviceOwnerType", ct),
            answer => answer is { } d && d.TryGetProperty("managedDeviceOwnerType", out var o) && o.GetString() == value,
            cancellationToken);
    }

    async Task<ApplyResult> SetLostModeAsync(SetLostMode change, CancellationToken cancellationToken)
    {
        var result = change.On
            ? await GraphCheckedAsync(ct => graph.PostAsync($"{BetaDevices}{change.Device.Id}/enableLostMode", new JsonObject
            {
                ["message"] = change.Message ?? "",
                ["phoneNumber"] = change.Phone ?? "",
                ["footer"] = "",
            }, ct), null, _ => true, cancellationToken)
            : await GraphCheckedAsync(ct => graph.PostAsync($"deviceManagement/managedDevices/{change.Device.Id}/disableLostMode", new JsonObject(), ct),
                null, _ => true, cancellationToken);
        return result.Succeeded ? new ApplyResult(ChangeOutcome.Applied, "Sent; the device carries it out when it next checks in.") : result;
    }

    Task<ApplyResult> SetAutopilotGroupTagAsync(SetAutopilotGroupTag change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.PostAsync($"deviceManagement/windowsAutopilotDeviceIdentities/{change.Entry.Id}/updateDeviceProperties",
                new JsonObject { ["groupTag"] = change.New ?? "" }, ct),
            ct => graph.GetAsync($"deviceManagement/windowsAutopilotDeviceIdentities/{change.Entry.Id}?$select=groupTag", ct),
            answer => answer is { } d && (d.TryGetProperty("groupTag", out var t) ? t.GetString() ?? "" : "") == (change.New ?? ""),
            cancellationToken);

    async Task<ApplyResult> DeleteAutopilotDeviceAsync(DeleteAutopilotDevice change, CancellationToken cancellationToken)
    {
        try
        {
            await DeleteIfThereAsync($"deviceManagement/windowsAutopilotDeviceIdentities/{change.Entry.Id}", cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied, "Removed; Autopilot can take a few minutes to stop listing it.");
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
            DeviceCommand.QuickScan => ("windowsDefenderScan", new JsonObject { ["quickScan"] = true }),
            DeviceCommand.FullScan => ("windowsDefenderScan", new JsonObject { ["quickScan"] = false }),
            DeviceCommand.UpdateDefinitions => ("windowsDefenderUpdateSignatures", new JsonObject()),
            DeviceCommand.CollectDiagnostics => ("createDeviceLogCollectionRequest", new JsonObject { ["templateType"] = new JsonObject { ["templateType"] = "predefined" } }),
            DeviceCommand.Locate => ("locateDevice", new JsonObject()),
            DeviceCommand.FreshStart => ("cleanWindowsDevice", new JsonObject { ["keepUserData"] = change.KeepUserData }),
            DeviceCommand.RotateBitLockerKeys => ("rotateBitLockerKeys", new JsonObject()),
            // Autopilot reset is a wipe that keeps the device's enrollment (as the admin center sends it).
            DeviceCommand.AutopilotReset => ("wipe", new JsonObject { ["keepEnrollmentData"] = true, ["keepUserData"] = false }),
            _ => ("wipe", new JsonObject { ["keepEnrollmentData"] = false, ["keepUserData"] = false }),
        };
        // Beta-only actions.
        var root = change.Command is DeviceCommand.RotateLocalAdminPassword or DeviceCommand.CollectDiagnostics or DeviceCommand.RotateBitLockerKeys
            ? "https://graph.microsoft.com/beta/" : "";
        var result = await GraphCheckedAsync(
            ct => graph.PostAsync($"{root}deviceManagement/managedDevices/{change.Device.Id}/{path}", body, ct),
            null, _ => true, cancellationToken);
        return result.Succeeded
            ? new ApplyResult(ChangeOutcome.Applied, change.Command == DeviceCommand.Sync ? null : "Sent; the device carries it out when it next checks in.")
            : result;
    }
}
