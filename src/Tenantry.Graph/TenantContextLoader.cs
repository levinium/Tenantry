using System.Text.Json;
using Tenantry.Core.Capabilities;

namespace Tenantry.Graph;

/// <summary>
/// Reads what the capability gates need: the signed-in admin's active directory roles
/// (including ones held through role-assignable groups and activated PIM roles) and the
/// service plans the tenant's subscriptions provide.
/// </summary>
public sealed class TenantContextLoader(GraphClient graph)
{
    public async Task<TenantContext> LoadAsync(CancellationToken cancellationToken = default)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var role in graph.GetAllAsync("me/transitiveMemberOf/microsoft.graph.directoryRole?$select=roleTemplateId", cancellationToken))
            if (role.TryGetProperty("roleTemplateId", out var id) && id.GetString() is { } templateId)
                roles.Add(templateId);

        var plans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var sku in graph.GetAllAsync("subscribedSkus?$select=capabilityStatus,servicePlans", cancellationToken))
        {
            // Suspended and deleted subscriptions no longer provide their features.
            var status = sku.TryGetProperty("capabilityStatus", out var s) ? s.GetString() : null;
            if (status is not ("Enabled" or "Warning"))
                continue;
            foreach (var plan in sku.GetProperty("servicePlans").EnumerateArray())
                if (plan.GetProperty("provisioningStatus").GetString() == "Success" && plan.GetProperty("servicePlanName").GetString() is { } name)
                    plans.Add(name);
        }

        return new TenantContext(roles, plans, OperatingSystem.IsWindows());
    }
}
