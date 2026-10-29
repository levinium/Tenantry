using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Permissions;

namespace Tenantry.Graph;

public sealed record ProvisionResult(
    string ClientId,
    string TenantId,
    string TenantName,
    string PrimaryDomain,
    bool Reused,
    IReadOnlyList<string> Unresolved);

/// <summary>
/// Creates, or brings up to date, Tenantry's app registration in the signed-in admin's tenant,
/// and grants tenant-wide consent to its delegated permissions. Safe to run again: it finds the
/// existing registration by tag, updates its permissions and tops up the grants.
///
/// Needs a token carrying Application.ReadWrite.All, DelegatedPermissionGrant.ReadWrite.All and
/// AppRoleAssignment.ReadWrite.All (Cloud Application Administrator or higher). Grants made this way take effect immediately
/// and skip any admin-consent workflow; the wizard says so before running.
/// </summary>
public sealed class AppRegistrationProvisioner
{
    public const string AppDisplayName = "Tenantry";
    public const string AppTag = "TenantryAdminApp";
    public const string RedirectUri = "http://localhost";

    // A new application takes a few seconds to replicate before its service principal and
    // grants can reference it; Graph says 400 or 404 until then.
    const int ReplicationAttempts = 8;
    static readonly TimeSpan ReplicationDelay = TimeSpan.FromSeconds(5);

    readonly GraphClient _graph;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public AppRegistrationProvisioner(GraphClient graph, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _graph = graph;
        _delay = delay ?? Task.Delay;
    }

    sealed record ResolvedApi(ApiPermissions Api, string ServicePrincipalId, IReadOnlyList<(string Name, string Id)> Scopes);

    public async Task<ProvisionResult> ProvisionAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("Reading the tenant");
        var org = await _graph.GetFirstAsync("organization?$select=id,displayName,verifiedDomains", cancellationToken)
                  ?? throw new InvalidOperationException("The tenant returned no organization.");
        var tenantId = org.GetProperty("id").GetString()!;
        var tenantName = org.GetProperty("displayName").GetString() ?? tenantId;
        var primaryDomain = org.GetProperty("verifiedDomains").EnumerateArray()
            .Where(d => d.GetProperty("isDefault").GetBoolean())
            .Select(d => d.GetProperty("name").GetString())
            .FirstOrDefault() ?? "";

        progress?.Report("Looking up the permissions Tenantry needs");
        var unresolved = new List<string>();
        var apis = new List<ResolvedApi>();
        foreach (var api in TenantryPermissions.All)
        {
            var sp = await _graph.GetFirstAsync(
                $"servicePrincipals?$filter=appId eq '{api.ResourceAppId}'&$select=id,appId,oauth2PermissionScopes", cancellationToken);
            if (sp is null && api.CreateIfMissing)
            {
                progress?.Report($"Adding {api.ResourceName} to this tenant");
                try
                {
                    await _graph.PostAsync("servicePrincipals", new JsonObject { ["appId"] = api.ResourceAppId }, cancellationToken);
                    sp = await _graph.GetFirstAsync(
                        $"servicePrincipals?$filter=appId eq '{api.ResourceAppId}'&$select=id,appId,oauth2PermissionScopes", cancellationToken);
                }
                catch (GraphException)
                {
                    // Reported below as not available.
                }
            }
            if (sp is null)
            {
                unresolved.Add($"{api.ResourceName} is not available in this tenant");
                continue;
            }

            // Real tenants publish some scope names twice (Microsoft Graph lists User.ReadBasic.All
            // more than once), so the first entry per name wins rather than assuming uniqueness.
            var published = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sp.Value.GetProperty("oauth2PermissionScopes").EnumerateArray())
                published.TryAdd(s.GetProperty("value").GetString()!, s.GetProperty("id").GetString()!);
            var scopes = new List<(string, string)>();
            foreach (var name in api.DelegatedScopes)
            {
                if (published.TryGetValue(name, out var id))
                    scopes.Add((name, id));
                else
                    unresolved.Add($"{api.ResourceName}: {name}");
            }
            apis.Add(new ResolvedApi(api, sp.Value.GetProperty("id").GetString()!, scopes));
        }

        var requiredResourceAccess = new JsonArray(apis.Select(a => (JsonNode)new JsonObject
        {
            ["resourceAppId"] = a.Api.ResourceAppId,
            ["resourceAccess"] = new JsonArray(a.Scopes.Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["type"] = "Scope" }).ToArray()),
        }).ToArray());

        var existing = await _graph.GetFirstAsync($"applications?$filter=tags/any(t:t eq '{AppTag}')&$select=id,appId", cancellationToken);
        string appId;
        if (existing is { } found)
        {
            progress?.Report("Updating the existing Tenantry app registration");
            appId = found.GetProperty("appId").GetString()!;
            await _graph.PatchAsync($"applications/{found.GetProperty("id").GetString()}", new JsonObject
            {
                ["isFallbackPublicClient"] = true,
                ["publicClient"] = new JsonObject { ["redirectUris"] = new JsonArray(RedirectUri) },
                ["requiredResourceAccess"] = requiredResourceAccess,
            }, cancellationToken);
        }
        else
        {
            progress?.Report("Creating the Tenantry app registration");
            var created = await _graph.PostAsync("applications", new JsonObject
            {
                ["displayName"] = AppDisplayName,
                ["signInAudience"] = "AzureADMyOrg",
                ["tags"] = new JsonArray(AppTag),
                ["notes"] = "Created by the Tenantry setup wizard. Delegated permissions only: what it can do is limited by the signed-in admin's own roles.",
                ["isFallbackPublicClient"] = true,
                ["publicClient"] = new JsonObject { ["redirectUris"] = new JsonArray(RedirectUri) },
                ["requiredResourceAccess"] = requiredResourceAccess,
            }, cancellationToken);
            appId = created.GetProperty("appId").GetString()!;
        }

        progress?.Report("Creating its service principal");
        var servicePrincipalId = await EnsureServicePrincipalAsync(appId, cancellationToken);

        // Tenant-wide consent would otherwise let every user in the tenant sign in to Tenantry.
        // Requiring assignment limits it to people (or groups) an admin assigns, starting with
        // whoever runs setup.
        progress?.Report("Restricting sign-in to assigned admins");
        await AssignSignedInAdminAsync(servicePrincipalId, cancellationToken);

        progress?.Report("Granting tenant-wide consent");
        var grants = new List<JsonElement>();
        await foreach (var g in _graph.GetAllAsync($"oauth2PermissionGrants?$filter=clientId eq '{servicePrincipalId}'", cancellationToken))
            grants.Add(g);

        foreach (var api in apis.Where(a => a.Scopes.Count > 0))
        {
            var scope = string.Join(' ', api.Scopes.Select(s => s.Name));
            var grant = grants.FirstOrDefault(g =>
                g.GetProperty("resourceId").GetString() == api.ServicePrincipalId
                && g.GetProperty("consentType").GetString() == "AllPrincipals");

            if (grant.ValueKind == JsonValueKind.Object)
            {
                await _graph.PatchAsync($"oauth2PermissionGrants/{grant.GetProperty("id").GetString()}",
                    new JsonObject { ["scope"] = scope }, cancellationToken);
            }
            else
            {
                await WhileReplicatingAsync(() => _graph.PostAsync("oauth2PermissionGrants", new JsonObject
                {
                    ["clientId"] = servicePrincipalId,
                    ["consentType"] = "AllPrincipals",
                    ["resourceId"] = api.ServicePrincipalId,
                    ["scope"] = scope,
                }, cancellationToken), cancellationToken);
            }
        }

        progress?.Report("Done");
        return new ProvisionResult(appId, tenantId, tenantName, primaryDomain, existing is not null, unresolved);
    }

    async Task<string> EnsureServicePrincipalAsync(string appId, CancellationToken cancellationToken)
    {
        var sp = await _graph.GetFirstAsync($"servicePrincipals?$filter=appId eq '{appId}'&$select=id,appRoleAssignmentRequired", cancellationToken);
        if (sp is { } found)
        {
            var id = found.GetProperty("id").GetString()!;
            if (!(found.TryGetProperty("appRoleAssignmentRequired", out var required) && required.ValueKind == JsonValueKind.True))
                await _graph.PatchAsync($"servicePrincipals/{id}", new JsonObject { ["appRoleAssignmentRequired"] = true }, cancellationToken);
            return id;
        }
        var created = await WhileReplicatingAsync(() =>
            _graph.PostAsync("servicePrincipals", new JsonObject { ["appId"] = appId, ["appRoleAssignmentRequired"] = true }, cancellationToken),
            cancellationToken);
        return created.GetProperty("id").GetString()!;
    }

    /// <summary>The app's default access role: an app with no roles of its own uses the empty GUID.</summary>
    public const string DefaultAccessRoleId = "00000000-0000-0000-0000-000000000000";

    async Task AssignSignedInAdminAsync(string servicePrincipalId, CancellationToken cancellationToken)
    {
        var me = (await _graph.GetAsync("me?$select=id", cancellationToken)).GetProperty("id").GetString()!;
        await foreach (var assignment in _graph.GetAllAsync($"servicePrincipals/{servicePrincipalId}/appRoleAssignedTo", cancellationToken))
            if (assignment.GetProperty("principalId").GetString() == me)
                return;

        await WhileReplicatingAsync(() => _graph.PostAsync($"servicePrincipals/{servicePrincipalId}/appRoleAssignedTo", new JsonObject
        {
            ["principalId"] = me,
            ["resourceId"] = servicePrincipalId,
            ["appRoleId"] = DefaultAccessRoleId,
        }, cancellationToken), cancellationToken);
    }

    async Task<JsonElement> WhileReplicatingAsync(Func<Task<JsonElement>> call, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (GraphException e) when (e.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound && attempt < ReplicationAttempts)
            {
                await _delay(ReplicationDelay, cancellationToken);
            }
        }
    }
}
