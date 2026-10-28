using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;

namespace Tenantry.Graph;

/// <summary>
/// App registrations and enterprise apps (8c-2). A certificate is removed by sending the app's
/// other certificates back (removeKey needs a proof signed by one of the app's own keys, which an
/// admin doesn't hold); Graph returns each certificate's key when keyCredentials is selected.
/// </summary>
public sealed partial class GraphChangeExecutor
{
    const string DirectoryObject = "https://graph.microsoft.com/v1.0/directoryObjects/";

    Task<ApplyResult> ApplyAppAsync(AppChange change, CancellationToken cancellationToken) => change switch
    {
        RemoveAppCredential r => RemoveAppCredentialAsync(r, cancellationToken),
        AddAppSecret a => AddAppSecretAsync(a, cancellationToken),
        SetAppOwner o => AppStepAsync(ct => o.Add
            ? graph.PostAsync($"{AppPath(o)}/owners/$ref", new JsonObject { ["@odata.id"] = DirectoryObject + o.Owner.Id }, ct)
            : DeleteIfThereAsync($"{AppPath(o)}/owners/{o.Owner.Id}/$ref", ct), cancellationToken, tolerateExists: o.Add),
        SetEnterpriseAppSetting s => SetEnterpriseAppSettingAsync(s, cancellationToken),
        AddAppUserAssignment a => AppStepAsync(ct => graph.PostAsync($"servicePrincipals/{a.App.Id}/appRoleAssignedTo",
            new JsonObject { ["principalId"] = a.Principal.Id, ["resourceId"] = a.App.Id, ["appRoleId"] = a.RoleId }, ct), cancellationToken, tolerateExists: true),
        RemoveAppUserAssignment r => AppStepAsync(ct => DeleteIfThereAsync($"servicePrincipals/{r.App.Id}/appRoleAssignedTo/{r.AssignmentId}", ct), cancellationToken),
        RevokeAppGrant g => AppStepAsync(ct => g.IsApplicationPermission
            ? DeleteIfThereAsync($"servicePrincipals/{g.App.Id}/appRoleAssignments/{g.GrantId}", ct)
            : DeleteIfThereAsync($"oauth2PermissionGrants/{g.GrantId}", ct), cancellationToken),
        GrantAdminConsent c => GrantAdminConsentAsync(c, cancellationToken),
        DeleteApp d => AppStepAsync(ct => DeleteIfThereAsync(AppPath(d), ct), cancellationToken),
        RestoreApp r => AppStepAsync(ct => graph.PostAsync($"directory/deletedItems/{r.App.Id}/restore", new JsonObject(), ct), cancellationToken),
        PurgeApp p => AppStepAsync(ct => DeleteIfThereAsync($"directory/deletedItems/{p.App.Id}", ct), cancellationToken),
        CreateAppRegistration c => CreateAppRegistrationAsync(c, cancellationToken),
        SetAppRisk r => AppStepAsync(ct => graph.PostAsync($"identityProtection/riskyServicePrincipals/{(r.Compromised ? "confirmCompromised" : "dismiss")}",
            new JsonObject { ["servicePrincipalIds"] = new JsonArray(r.App.Id) }, ct), cancellationToken),
        _ => Task.FromResult(new ApplyResult(ChangeOutcome.Failed, "Tenantry can't apply this kind of change yet.")),
    };

    static string AppPath(AppChange change) => change.IsRegistration ? $"applications/{change.App.Id}" : $"servicePrincipals/{change.App.Id}";

    /// <param name="tolerateExists">An add Graph refuses because it's already there counts as done.</param>
    async Task<ApplyResult> AppStepAsync(Func<CancellationToken, Task> step, CancellationToken cancellationToken, bool tolerateExists = false)
    {
        try
        {
            await step(cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e) when (tolerateExists && e.Status == System.Net.HttpStatusCode.BadRequest
                                       && (e.Message.Contains("already exist", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("conflicting", StringComparison.OrdinalIgnoreCase)))
        {
            return new ApplyResult(ChangeOutcome.Applied, "It was already in place.");
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    async Task<ApplyResult> RemoveAppCredentialAsync(RemoveAppCredential change, CancellationToken cancellationToken)
    {
        try
        {
            if (change.IsSecret)
            {
                await graph.PostAsync($"applications/{change.App.Id}/removePassword", new JsonObject { ["keyId"] = change.KeyId }, cancellationToken);
                return new ApplyResult(ChangeOutcome.Applied);
            }
            var app = await graph.GetAsync($"applications/{change.App.Id}?$select=keyCredentials", cancellationToken);
            var keep = new JsonArray();
            var found = false;
            foreach (var key in app.GetProperty("keyCredentials").EnumerateArray())
            {
                if (string.Equals(key.GetProperty("keyId").GetString(), change.KeyId, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    continue;
                }
                // Send each remaining certificate back as Graph gave it, minus read-only fields.
                var node = JsonNode.Parse(key.GetRawText())!.AsObject();
                node.Remove("@odata.type");
                keep.Add(node);
            }
            if (!found)
                return new ApplyResult(ChangeOutcome.Applied, "It was already gone.");
            await graph.PatchAsync($"applications/{change.App.Id}", new JsonObject { ["keyCredentials"] = keep }, cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    /// <summary>The secret's value comes back once, in the answer; it goes to the admin (<see cref="ApplyResult.Reveal"/>) and nowhere else.</summary>
    async Task<ApplyResult> AddAppSecretAsync(AddAppSecret change, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await graph.PostAsync($"applications/{change.App.Id}/addPassword", new JsonObject
            {
                ["passwordCredential"] = new JsonObject
                {
                    ["displayName"] = change.Description,
                    ["endDateTime"] = DateTimeOffset.UtcNow.AddMonths(change.Months).ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                },
            }, cancellationToken);
            return answer.TryGetProperty("secretText", out var secret) && secret.GetString() is { Length: > 0 } value
                ? new ApplyResult(ChangeOutcome.Applied, "Copy it now: Entra won't show it again.", Reveal: value)
                : new ApplyResult(ChangeOutcome.Failed, "Entra made the secret but didn't send its value back; remove it and try again.");
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    async Task<ApplyResult> SetEnterpriseAppSettingAsync(SetEnterpriseAppSetting change, CancellationToken cancellationToken)
    {
        try
        {
            JsonObject body;
            if (change.Setting == "visible")
            {
                // Visibility is the HideApp tag: the other tags go back as they were.
                var sp = await graph.GetAsync($"servicePrincipals/{change.App.Id}?$select=tags", cancellationToken);
                var tags = sp.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString()).OfType<string>().Where(x => x != "HideApp").ToList()
                    : [];
                if (!change.New)
                    tags.Add("HideApp");
                body = new JsonObject { ["tags"] = new JsonArray([.. tags.Select(x => (JsonNode)x)]) };
            }
            else
                body = new JsonObject { [change.Setting == "enabled" ? "accountEnabled" : "appRoleAssignmentRequired"] = change.New };
            await graph.PatchAsync($"servicePrincipals/{change.App.Id}", body, cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    /// <summary>
    /// What the admin center's Grant admin consent does: the app's enterprise app (made if missing),
    /// one AllPrincipals grant per API with the delegated scopes added, and an app role assignment
    /// per application permission it doesn't hold yet.
    /// </summary>
    async Task<ApplyResult> GrantAdminConsentAsync(GrantAdminConsent change, CancellationToken cancellationToken)
    {
        try
        {
            var sp = await graph.GetFirstAsync($"servicePrincipals?$filter=appId eq '{change.AppId}'&$select=id", cancellationToken)
                     ?? await graph.PostAsync("servicePrincipals", new JsonObject { ["appId"] = change.AppId }, cancellationToken);
            var spId = sp.GetProperty("id").GetString()!;
            foreach (var api in change.Items.Where(i => !i.IsApplication).GroupBy(i => i.ResourceId))
            {
                var existing = await graph.GetFirstAsync(
                    $"oauth2PermissionGrants?$filter=clientId eq '{spId}' and consentType eq 'AllPrincipals' and resourceId eq '{api.Key}'", cancellationToken);
                if (existing is { } grant)
                {
                    var scopes = (grant.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    scopes.AddRange(api.Select(i => i.Name).Where(n => !scopes.Contains(n, StringComparer.OrdinalIgnoreCase)));
                    await graph.PatchAsync($"oauth2PermissionGrants/{grant.GetProperty("id").GetString()}", new JsonObject { ["scope"] = string.Join(' ', scopes) }, cancellationToken);
                }
                else
                    await graph.PostAsync("oauth2PermissionGrants", new JsonObject
                    {
                        ["clientId"] = spId,
                        ["consentType"] = "AllPrincipals",
                        ["resourceId"] = api.Key,
                        ["scope"] = string.Join(' ', api.Select(i => i.Name)),
                    }, cancellationToken);
            }
            var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await foreach (var r in graph.GetAllAsync($"servicePrincipals/{spId}/appRoleAssignments", cancellationToken))
                held.Add($"{r.GetProperty("resourceId").GetString()}/{r.GetProperty("appRoleId").GetString()}");
            foreach (var role in change.Items.Where(i => i.IsApplication && !held.Contains($"{i.ResourceId}/{i.PermissionId}")))
                await graph.PostAsync($"servicePrincipals/{spId}/appRoleAssignments",
                    new JsonObject { ["principalId"] = spId, ["resourceId"] = role.ResourceId, ["appRoleId"] = role.PermissionId }, cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    /// <summary>The application, then its enterprise app so people here can use it.</summary>
    async Task<ApplyResult> CreateAppRegistrationAsync(CreateAppRegistration change, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["displayName"] = change.App.Name, ["signInAudience"] = change.Audience };
        if (change.RedirectUri is { } uri)
            body[change.Platform switch { "Single-Page App" => "spa", "Mobile and Desktop" => "publicClient", _ => "web" }] =
                new JsonObject { ["redirectUris"] = new JsonArray(uri) };
        try
        {
            var app = await graph.PostAsync("applications", body, cancellationToken);
            var appId = app.GetProperty("appId").GetString()!;
            try
            {
                await graph.PostAsync("servicePrincipals", new JsonObject { ["appId"] = appId }, cancellationToken);
            }
            catch (GraphException e)
            {
                return new ApplyResult(ChangeOutcome.Applied, $"Made the app, but not its enterprise app: {Describe(e)}");
            }
            return new ApplyResult(ChangeOutcome.Applied, $"Application (client) ID {appId}");
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }
}
