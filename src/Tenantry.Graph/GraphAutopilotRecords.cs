using System.Net;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Windows Autopilot devices (6c), from beta, which adds each device's profile assignment. Needs
/// DeviceManagementServiceConfig and an Intune role; without either it says so.
/// </summary>
public sealed partial class GraphUserRecords : IAutopilotRecords
{
    const string AutopilotPath = Beta + "deviceManagement/windowsAutopilotDeviceIdentities";

    public async Task<Available<IReadOnlyList<AutopilotDevice>>> GetAutopilotDevicesAsync(CancellationToken cancellationToken = default)
    {
        List<JsonElement> items;
        try
        {
            items = await Collect($"{AutopilotPath}?$expand=deploymentProfile", cancellationToken);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.BadRequest)
        {
            // The profile can't be expanded here: list without its name.
            items = await TryCollectAsync(AutopilotPath, cancellationToken) ?? [];
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return Available<IReadOnlyList<AutopilotDevice>>.Because(
                "Autopilot needs an Intune role, and Tenantry's permission to read Intune's service settings (grant it from the banner at the top).");
        }
        return new(items.Select(ToAutopilot).OrderBy(d => d.Serial, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    async Task<List<JsonElement>?> TryCollectAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await Collect(path, cancellationToken);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>"pendingReset" → "Pending Reset", for states Microsoft adds later.</summary>
    static string Spaced(string code)
    {
        var spaced = System.Text.RegularExpressions.Regex.Replace(code, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    internal static AutopilotDevice ToAutopilot(JsonElement d) => new(
        Id: Text(d, "id") ?? "",
        Serial: NonEmpty(d, "serialNumber") ?? "No serial number",
        Manufacturer: NonEmpty(d, "manufacturer"),
        Model: NonEmpty(d, "model"),
        GroupTag: NonEmpty(d, "groupTag"),
        ProfileName: d.TryGetProperty("deploymentProfile", out var p) && p.ValueKind == JsonValueKind.Object ? NonEmpty(p, "displayName") : null,
        ProfileStatus: NonEmpty(d, "deploymentProfileAssignmentStatus") switch
        {
            "assignedInSync" or "assignedOutOfSync" or "assignedUnkownSyncState" or "assignedUnknownSyncState" => "Assigned",
            "pending" => "Pending",
            "failed" => "Failed",
            "notAssigned" => "Not Assigned",
            null => "Unknown",
            var other => Spaced(other),
        },
        EnrollmentState: NonEmpty(d, "enrollmentState") switch
        {
            "enrolled" => "Enrolled",
            "notContacted" => "Not Contacted",
            "pendingReset" => "Pending Reset",
            "failed" => "Failed",
            "blocked" => "Blocked",
            null or "unknown" => "Unknown",
            var other => Spaced(other),
        },
        LastContacted: Real(When(d, "lastContactedDateTime")),
        ManagedDeviceId: NonEmpty(d, "managedDeviceId") is { } m && m != Guid.Empty.ToString() ? m : null,
        EntraDeviceId: NonEmpty(d, "azureActiveDirectoryDeviceId") is { } a && a != Guid.Empty.ToString() ? a : null,
        AssignedUser: NonEmpty(d, "userPrincipalName"),
        PurchaseOrder: NonEmpty(d, "purchaseOrderIdentifier"));
}
