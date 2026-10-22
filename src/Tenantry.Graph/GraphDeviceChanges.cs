using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;

namespace Tenantry.Graph;

/// <summary>Device changes (4c-2): enabling or disabling in Entra, and Intune remote actions.</summary>
public sealed partial class GraphChangeExecutor
{
    Task<ApplyResult> SetDeviceEnabledAsync(SetDeviceEnabled change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.PatchAsync($"devices/{change.Device.Id}", new JsonObject { ["accountEnabled"] = change.New }, ct),
            ct => graph.GetAsync($"devices/{change.Device.Id}?$select=accountEnabled", ct),
            answer => answer is { } d && d.TryGetProperty("accountEnabled", out var on) && on.ValueKind == (change.New ? JsonValueKind.True : JsonValueKind.False),
            cancellationToken);

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
