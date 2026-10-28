using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// App registrations and enterprise apps (8c), read live. The lists are two paged reads
/// (applications with their owners; service principals). A page names permissions and roles by
/// reading the APIs they belong to (their appRoles and oauth2PermissionScopes).
/// </summary>
public sealed partial class GraphUserRecords : IAppRecords
{
    const string AppSelect = "id,appId,displayName,signInAudience,passwordCredentials,keyCredentials,requiredResourceAccess,createdDateTime,web,spa,publicClient,notes,publisherDomain";

    const string SpSelect = "id,appId,displayName,servicePrincipalType,accountEnabled,appRoleAssignmentRequired,tags,publisherName,verifiedPublisher,homepage,preferredSingleSignOnMode,notes,createdDateTime";

    public async Task<AppDirectory> GetAppsAsync(CancellationToken cancellationToken = default)
    {
        var registrations = new List<AppRegistration>();
        await foreach (var a in graph.GetAllAsync($"applications?$select={AppSelect}&$expand=owners($select=id,displayName,userPrincipalName)&$top=100", cancellationToken))
            registrations.Add(Registration(a));
        var ours = registrations.Select(r => r.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var enterprise = new List<EnterpriseApp>();
        await foreach (var s in graph.GetAllAsync($"servicePrincipals?$select={SpSelect},appOwnerOrganizationId&$top=999", cancellationToken))
        {
            var appId = Text(s, "appId") ?? "";
            var tags = s.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray() : [];
            var owner = Text(s, "appOwnerOrganizationId");
            var source = ours.Contains(appId) ? AppSource.ThisOrganization
                : owner is not null && EnterpriseApp.MicrosoftTenants.Contains(owner) ? AppSource.Microsoft
                : AppSource.ThirdParty;
            var verified = s.TryGetProperty("verifiedPublisher", out var v) && v.ValueKind == JsonValueKind.Object ? Text(v, "displayName") : null;
            enterprise.Add(new EnterpriseApp(Text(s, "id") ?? "", appId, Text(s, "displayName") ?? "An app", verified ?? Text(s, "publisherName"), verified is not null,
                source, TypeName(Text(s, "servicePrincipalType")), !s.TryGetProperty("accountEnabled", out var on) || on.ValueKind != JsonValueKind.False,
                s.TryGetProperty("appRoleAssignmentRequired", out var req) && req.ValueKind == JsonValueKind.True, !tags.Contains("HideApp"),
                tags.Contains("WindowsAzureActiveDirectoryIntegratedApp"), When(s, "createdDateTime"), Text(s, "preferredSingleSignOnMode"), Text(s, "homepage"), Text(s, "notes")));
        }
        return new AppDirectory(registrations, enterprise);
    }

    static string TypeName(string? type) => type switch
    {
        "ManagedIdentity" => "Managed Identity",
        "SocialIdp" => "Social Identity Provider",
        null => "Application",
        var t => t,
    };

    static AppRegistration Registration(JsonElement a)
    {
        IEnumerable<AppCredential> Creds(string name, bool secret) =>
            a.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(c => new AppCredential(Text(c, "keyId") ?? "", secret, Text(c, "displayName"),
                    secret ? Text(c, "hint") : Thumbprint(Text(c, "customKeyIdentifier")), When(c, "startDateTime"), When(c, "endDateTime")))
                : [];
        var requested = new List<RequestedPermission>();
        if (a.TryGetProperty("requiredResourceAccess", out var rra) && rra.ValueKind == JsonValueKind.Array)
            foreach (var resource in rra.EnumerateArray())
                if (resource.TryGetProperty("resourceAccess", out var access) && access.ValueKind == JsonValueKind.Array)
                    foreach (var p in access.EnumerateArray())
                        requested.Add(new RequestedPermission(Text(resource, "resourceAppId") ?? "", Text(p, "id") ?? "", Text(p, "type") == "Role"));
        var redirects = new List<(string, string)>();
        foreach (var (key, platform) in new[] { ("web", "Web"), ("spa", "Single-Page App"), ("publicClient", "Mobile and Desktop") })
            if (a.TryGetProperty(key, out var section) && section.ValueKind == JsonValueKind.Object && section.TryGetProperty("redirectUris", out var uris) && uris.ValueKind == JsonValueKind.Array)
                redirects.AddRange(uris.EnumerateArray().Select(u => u.GetString()).OfType<string>().Select(u => (platform, u)));
        var owners = a.TryGetProperty("owners", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Select(x => new AppOwner(Text(x, "id") ?? "", Text(x, "displayName") ?? "Someone", Text(x, "userPrincipalName"))).ToArray()
            : [];
        return new AppRegistration(Text(a, "id") ?? "", Text(a, "appId") ?? "", Text(a, "displayName") ?? "An app", Text(a, "signInAudience") ?? "AzureADMyOrg",
            When(a, "createdDateTime"), [.. Creds("passwordCredentials", true), .. Creds("keyCredentials", false)], requested, redirects, owners,
            Text(a, "notes"), Text(a, "publisherDomain"));
    }

    /// <summary>A certificate's customKeyIdentifier: its SHA-1 thumbprint, which Graph sends as 40 hex characters.</summary>
    static string? Thumbprint(string? id) => string.IsNullOrEmpty(id) ? null : id.ToUpperInvariant();

    /// <summary>An API's name with its delegated scopes and app roles, by ID.</summary>
    sealed record Api(string Id, string Name, Dictionary<string, (string Name, string? Description)> Scopes, Dictionary<string, (string Name, string? Description)> Roles);

    async Task<Api?> ApiAsync(string filter, Dictionary<string, Api?> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(filter, out var known))
            return known;
        JsonElement? sp = null;
        try
        {
            sp = filter.StartsWith("appId:", StringComparison.Ordinal)
                ? await graph.GetFirstAsync($"servicePrincipals?$filter=appId eq '{filter[6..]}'&$select=id,displayName,appRoles,oauth2PermissionScopes", cancellationToken)
                : await graph.GetAsync($"servicePrincipals/{filter}?$select=id,displayName,appRoles,oauth2PermissionScopes", cancellationToken);
        }
        catch (GraphException)
        {
        }
        Api? api = null;
        if (sp is { } s)
        {
            Dictionary<string, (string, string?)> Read(string name, string label, string description)
            {
                var map = new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase);
                if (s.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var x in list.EnumerateArray())
                        map[Text(x, "id") ?? ""] = (Text(x, label) ?? Text(x, "displayName") ?? "A permission", Text(x, description));
                return map;
            }
            api = new Api(Text(s, "id") ?? "", Text(s, "displayName") ?? "An API", Read("oauth2PermissionScopes", "value", "adminConsentDisplayName"), Read("appRoles", "value", "displayName"));
        }
        cache[filter] = api;
        if (api is not null)
            cache[api.Id] = api;
        return api;
    }

    public async Task<AppRegistrationDetail> GetRegistrationDetailAsync(AppRegistration app, CancellationToken cancellationToken = default)
    {
        var cache = new Dictionary<string, Api?>(StringComparer.OrdinalIgnoreCase);
        var own = await graph.GetFirstAsync($"servicePrincipals?$filter=appId eq '{app.AppId}'&$select=id", cancellationToken);
        var spId = own is { } o ? Text(o, "id") : null;
        var adminScopes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var granted = new HashSet<(string, string)>();
        if (spId is not null)
        {
            await foreach (var g in graph.GetAllAsync($"oauth2PermissionGrants?$filter=clientId eq '{spId}'", cancellationToken))
                if (Text(g, "consentType") == "AllPrincipals")
                {
                    var set = adminScopes.TryGetValue(Text(g, "resourceId") ?? "", out var s) ? s : adminScopes[Text(g, "resourceId") ?? ""] = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var scope in (Text(g, "scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        set.Add(scope);
                }
            await foreach (var r in graph.GetAllAsync($"servicePrincipals/{spId}/appRoleAssignments", cancellationToken))
                granted.Add(((Text(r, "resourceId") ?? "").ToLowerInvariant(), (Text(r, "appRoleId") ?? "").ToLowerInvariant()));
        }
        var named = new List<NamedPermission>();
        foreach (var p in app.Requested)
        {
            var api = await ApiAsync($"appId:{p.ResourceAppId}", cache, cancellationToken);
            var (name, description) = api is null ? (p.PermissionId, null)
                : (p.IsApplication ? api.Roles : api.Scopes).TryGetValue(p.PermissionId, out var found) ? found : (p.PermissionId, null);
            bool? consented = spId is null || api is null ? (spId is null ? false : null)
                : p.IsApplication ? granted.Contains((api.Id.ToLowerInvariant(), p.PermissionId.ToLowerInvariant()))
                : adminScopes.TryGetValue(api.Id, out var scopes) && scopes.Contains(name);
            named.Add(new NamedPermission(api?.Id ?? p.ResourceAppId, api?.Name ?? p.ResourceAppId, p.PermissionId, name, p.IsApplication, description, consented));
        }
        return new AppRegistrationDetail(named);
    }

    public async Task<EnterpriseAppDetail> GetEnterpriseDetailAsync(EnterpriseApp app, CancellationToken cancellationToken = default)
    {
        // The reads don't depend on each other, so they run together.
        async Task<List<JsonElement>> All(string path)
        {
            var rows = new List<JsonElement>();
            await foreach (var row in graph.GetAllAsync(path, cancellationToken))
                rows.Add(row);
            return rows;
        }
        var assignedRead = All($"servicePrincipals/{app.Id}/appRoleAssignedTo?$top=999");
        var grantsRead = All($"oauth2PermissionGrants?$filter=clientId eq '{app.Id}'");
        var heldRead = All($"servicePrincipals/{app.Id}/appRoleAssignments");
        var ownersRead = All($"servicePrincipals/{app.Id}/owners?$select=id,displayName,userPrincipalName");
        await Task.WhenAll(assignedRead, grantsRead, heldRead, ownersRead);

        var cache = new Dictionary<string, Api?>(StringComparer.OrdinalIgnoreCase);
        var self = await ApiAsync(app.Id, cache, cancellationToken);
        var roles = new List<(string, string)> { (DefaultAccess, "Default Access") };
        if (self is not null)
            roles.AddRange(self.Roles.Select(r => (r.Key, r.Value.Name)));
        string? RoleName(string id) => roles.FirstOrDefault(r => string.Equals(r.Item1, id, StringComparison.OrdinalIgnoreCase)).Item2;

        var assignments = assignedRead.Result.Select(a => new AppUserAssignment(Text(a, "id") ?? "", Text(a, "principalId") ?? "", Text(a, "principalDisplayName") ?? "Someone",
            Text(a, "principalType") ?? "User", Text(a, "appRoleId") ?? DefaultAccess, RoleName(Text(a, "appRoleId") ?? DefaultAccess), When(a, "createdDateTime"))).ToList();

        var grants = grantsRead.Result.Select(g => (Id: Text(g, "id") ?? "", ResourceId: Text(g, "resourceId") ?? "",
            PrincipalId: Text(g, "consentType") == "AllPrincipals" ? null : Text(g, "principalId"), Scopes: (Text(g, "scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToList();
        var people = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in grants.Select(g => g.PrincipalId).OfType<string>().Distinct().Chunk(1000))
        {
            var answer = await graph.PostAsync("directoryObjects/getByIds",
                new JsonObject { ["ids"] = new JsonArray([.. chunk.Select(i => (JsonNode)i)]), ["types"] = new JsonArray("user") }, cancellationToken);
            foreach (var p in answer.GetProperty("value").EnumerateArray())
                people[Text(p, "id") ?? ""] = Text(p, "displayName") ?? "Someone";
        }
        var delegated = new List<DelegatedGrant>();
        foreach (var g in grants)
        {
            var api = await ApiAsync(g.ResourceId, cache, cancellationToken);
            delegated.Add(new DelegatedGrant(g.Id, g.ResourceId, api?.Name ?? "An API", g.PrincipalId,
                g.PrincipalId is { } pid ? people.GetValueOrDefault(pid, "Someone no longer here") : null, g.Scopes));
        }

        var appPermissions = new List<AppPermissionGrant>();
        foreach (var r in heldRead.Result)
        {
            var api = await ApiAsync(Text(r, "resourceId") ?? "", cache, cancellationToken);
            var roleId = Text(r, "appRoleId") ?? "";
            appPermissions.Add(new AppPermissionGrant(Text(r, "id") ?? "", Text(r, "resourceId") ?? "", api?.Name ?? Text(r, "resourceDisplayName") ?? "An API", roleId,
                api is not null && api.Roles.TryGetValue(roleId, out var role) ? role.Name : null, When(r, "createdDateTime")));
        }

        var owners = ownersRead.Result.Select(o => new AppOwner(Text(o, "id") ?? "", Text(o, "displayName") ?? "Someone", Text(o, "userPrincipalName"))).ToList();
        return new EnterpriseAppDetail(assignments, delegated, appPermissions, owners, roles);
    }

    /// <summary>The beta sign-in activity report (needs P1); it takes seconds, so the page asks for it separately. Capped at 30 s.</summary>
    public async Task<DateTimeOffset?> GetLastSignInAsync(EnterpriseApp app, CancellationToken cancellationToken = default)
    {
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cap.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (await graph.GetFirstAsync($"{Beta}reports/servicePrincipalSignInActivities?$filter=appId eq '{app.AppId}'", cap.Token) is { } activity
                && activity.TryGetProperty("lastSignInActivity", out var last) && last.ValueKind == JsonValueKind.Object)
                return When(last, "lastSignInDateTime");
        }
        catch (Exception e) when (e is GraphException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
        }
        return null;
    }

    const string DefaultAccess = AppUserAssignment.DefaultAccess;
}
