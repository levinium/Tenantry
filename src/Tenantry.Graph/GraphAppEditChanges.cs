using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>
/// App registration edits (8c-3): name and notes, redirect URIs, API permissions asked for, and a
/// new certificate. Lists are read, changed and sent back whole, keeping whatever Tenantry doesn't touch.
/// </summary>
public sealed partial class GraphChangeExecutor
{
    static JsonObject AppPropertyBody(SetAppProperty change) =>
        new() { [change.Property] = change.New.Length == 0 && change.Property == "notes" ? null : change.New };

    /// <summary>The platform's settings as read, with its redirect URIs replaced (and Graph's read-only details left out).</summary>
    internal static JsonObject RedirectBody(JsonElement app, SetRedirectUris change)
    {
        var property = SetRedirectUris.GraphProperty(change.Platform);
        var platform = app.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Object ? JsonNode.Parse(p.GetRawText())!.AsObject() : new JsonObject();
        platform.Remove("@odata.type");
        platform.Remove("redirectUriSettings");
        platform["redirectUris"] = new JsonArray([.. change.New.Select(u => (JsonNode)u)]);
        return new JsonObject { [property] = platform };
    }

    /// <summary>The app's requiredResourceAccess with one permission added or taken out (an API left with none goes).</summary>
    internal static JsonObject PermissionBody(JsonElement app, SetApiPermission change)
    {
        var apis = app.TryGetProperty("requiredResourceAccess", out var r) && r.ValueKind == JsonValueKind.Array ? JsonNode.Parse(r.GetRawText())!.AsArray() : new JsonArray();
        var type = change.IsApplication ? "Role" : "Scope";
        var api = apis.OfType<JsonObject>().FirstOrDefault(a => string.Equals((string?)a["resourceAppId"], change.ResourceAppId, StringComparison.OrdinalIgnoreCase));
        if (api is null && change.Add)
            apis.Add(api = new JsonObject { ["resourceAppId"] = change.ResourceAppId, ["resourceAccess"] = new JsonArray() });
        if (api is not null)
        {
            if (api["resourceAccess"] is not JsonArray access)
                api["resourceAccess"] = access = [];
            var existing = access.OfType<JsonObject>().FirstOrDefault(x => string.Equals((string?)x["id"], change.PermissionId, StringComparison.OrdinalIgnoreCase) && (string?)x["type"] == type);
            if (change.Add && existing is null)
                access.Add(new JsonObject { ["id"] = change.PermissionId, ["type"] = type });
            if (!change.Add && existing is not null)
                access.Remove(existing);
            if (access.Count == 0)
                apis.Remove(api);
        }
        return new JsonObject { ["requiredResourceAccess"] = apis };
    }

    /// <summary>The app's certificates as Graph gave them (keys included), plus the new one.</summary>
    internal static JsonObject CertificateBody(JsonElement app, AddAppCertificate change)
    {
        var keys = new JsonArray();
        if (app.TryGetProperty("keyCredentials", out var k) && k.ValueKind == JsonValueKind.Array)
            foreach (var key in k.EnumerateArray())
            {
                var node = JsonNode.Parse(key.GetRawText())!.AsObject();
                node.Remove("@odata.type");
                keys.Add(node);
            }
        keys.Add(new JsonObject
        {
            ["type"] = "AsymmetricX509Cert",
            ["usage"] = "Verify",
            ["key"] = change.Certificate,
            ["displayName"] = change.Subject,
        });
        return new JsonObject { ["keyCredentials"] = keys };
    }

    async Task<ApplyResult> AppEditAsync(AppChange change, CancellationToken cancellationToken)
    {
        try
        {
            var path = $"applications/{change.App.Id}";
            JsonObject body = change switch
            {
                SetAppProperty p => AppPropertyBody(p),
                SetRedirectUris r => RedirectBody(await graph.GetAsync($"{path}?$select=web,spa,publicClient", cancellationToken), r),
                SetApiPermission a => PermissionBody(await graph.GetAsync($"{path}?$select=requiredResourceAccess", cancellationToken), a),
                AddAppCertificate c => CertificateBody(await graph.GetAsync($"{path}?$select=keyCredentials", cancellationToken), c),
                _ => throw new InvalidOperationException("Not an app edit."),
            };
            await graph.PatchAsync(path, body, cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    /// <summary>Show PowerShell for an app edit: the same read, change and PATCH.</summary>
    internal static PsScript? AppEditScript(ChangeOp change)
    {
        string Patch(string id, string body) => $"Invoke-MgGraphRequest -Method PATCH -Uri {Ps.Quote(Ps.GraphRoot + $"applications/{id}")} -Body {body}";
        switch (change)
        {
            case SetAppProperty p:
                return PsScript.Graph(Ps.Graph("PATCH", $"applications/{p.App.Id}", AppPropertyBody(p)));
            case SetRedirectUris r:
                var property = SetRedirectUris.GraphProperty(r.Platform);
                return PsScript.Graph(Ps.Lines(
                        Ps.Graph("GET", $"applications/{r.App.Id}?$select={property}", assignTo: "app"),
                        $"$platform = $app.{property}; $platform.Remove('redirectUriSettings')",
                        "$platform.redirectUris = @(" + string.Join(", ", r.New.Select(u => Ps.Quote(u))) + ")",
                        Patch(r.App.Id, "@{ " + property + " = $platform }")),
                    "The platform's other settings are sent back as they were.");
            case SetApiPermission a:
                var type = Ps.Quote(a.IsApplication ? "Role" : "Scope");
                var resource = Ps.Quote(a.ResourceAppId);
                var permission = Ps.Quote(a.PermissionId);
                var step = a.Add
                    ? "$api = $apis | Where-Object { $_.resourceAppId -eq " + resource + " }" + Environment.NewLine
                      + "if (-not $api) { $api = @{ resourceAppId = " + resource + "; resourceAccess = @() }; $apis += $api }" + Environment.NewLine
                      + "$api.resourceAccess = @($api.resourceAccess) + @{ id = " + permission + "; type = " + type + " }"
                    : "foreach ($api in $apis) { $api.resourceAccess = @($api.resourceAccess | Where-Object { -not ($_.id -eq " + permission + " -and $_.type -eq " + type + ") }) }"
                      + Environment.NewLine + "$apis = @($apis | Where-Object { $_.resourceAccess.Count -gt 0 })";
                return PsScript.Graph(Ps.Lines(
                        Ps.Graph("GET", $"applications/{a.App.Id}?$select=requiredResourceAccess", assignTo: "app"),
                        "$apis = @($app.requiredResourceAccess)",
                        step,
                        Patch(a.App.Id, "@{ requiredResourceAccess = $apis }")),
                    a.Add ? "Asking for a permission doesn't give it: grant admin consent afterwards." : "Consent already given stays until it's revoked on the enterprise app.");
            case AddAppCertificate c:
                return PsScript.Graph(Ps.Lines(
                        Ps.Graph("GET", $"applications/{c.App.Id}?$select=keyCredentials", assignTo: "app"),
                        "$keys = @($app.keyCredentials); foreach ($key in $keys) { $key.Remove('@odata.type') }",
                        "$keys += @{ type = 'AsymmetricX509Cert'; usage = 'Verify'; displayName = " + Ps.Quote(c.Subject) + "; key = " + Ps.Quote(c.Certificate) + " }",
                        Patch(c.App.Id, "@{ keyCredentials = $keys }")),
                    "A certificate is added by sending back the app's others with the new one.");
            default:
                return null;
        }
    }
}
