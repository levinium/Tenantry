using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>An enterprise app's SAML single sign-on: read from the enterprise app and its app registration.</summary>
public sealed partial class GraphUserRecords
{
    public async Task<SamlSetup?> GetSamlAsync(EnterpriseApp app, CancellationToken cancellationToken = default)
    {
        var sp = await graph.GetAsync($"servicePrincipals/{app.Id}?$select=appId,preferredSingleSignOnMode,loginUrl,samlSingleSignOnSettings,keyCredentials,preferredTokenSigningKeyThumbprint,appOwnerOrganizationId",
            cancellationToken);
        JsonElement? registration = null;
        try
        {
            registration = await graph.GetAsync($"applications(appId='{app.AppId}')?$select=id,identifierUris,web", cancellationToken);
        }
        catch (GraphException e) when (e.Status == System.Net.HttpStatusCode.NotFound)
        {
        }
        return ToSaml(sp, registration);
    }

    /// <summary>
    /// A signing certificate's thumbprint in hex. On an enterprise app, customKeyIdentifier holds the
    /// thumbprint's 20 bytes in base64 (seen live), unlike an app registration's, which is hex already.
    /// </summary>
    internal static string SigningThumbprint(string customKeyIdentifier)
    {
        var bytes = new byte[32];
        return Convert.TryFromBase64String(customKeyIdentifier, bytes, out var length) && length == 20
            ? Convert.ToHexString(bytes, 0, 20)
            : customKeyIdentifier.ToUpperInvariant();
    }

    internal static SamlSetup ToSaml(JsonElement sp, JsonElement? registration)
    {
        static string? Url(string? u) => string.IsNullOrWhiteSpace(u) ? null : u;
        var web = registration is { } reg && reg.TryGetProperty("web", out var w) && w.ValueKind == JsonValueKind.Object ? w : (JsonElement?)null;
        var settings = new SamlSettings(
            registration is { } r ? [.. Strings(r, "identifierUris")] : [],
            web is { } wb ? [.. Strings(wb, "redirectUris")] : [],
            Url(Text(sp, "loginUrl")),
            web is { } wl ? Url(Text(wl, "logoutUrl")) : null,
            sp.TryGetProperty("samlSingleSignOnSettings", out var saml) && saml.ValueKind == JsonValueKind.Object ? Url(Text(saml, "relayState")) : null);
        var active = Text(sp, "preferredTokenSigningKeyThumbprint");
        var certificates = new List<SamlCertificate>();
        if (sp.TryGetProperty("keyCredentials", out var keys) && keys.ValueKind == JsonValueKind.Array)
            foreach (var key in keys.EnumerateArray().Where(k => Text(k, "usage") == "Sign"))
            {
                var thumbprint = Text(key, "customKeyIdentifier") is { Length: > 0 } id ? SigningThumbprint(id) : null;
                if (thumbprint is null || certificates.Any(c => c.Thumbprint == thumbprint))
                    continue;
                certificates.Add(new SamlCertificate(thumbprint, When(key, "startDateTime"), When(key, "endDateTime"),
                    string.Equals(thumbprint, active, StringComparison.OrdinalIgnoreCase)));
            }
        return new SamlSetup(Text(sp, "preferredSingleSignOnMode") == "saml", registration is not null, registration is { } x ? Text(x, "id") : null,
            Text(sp, "appOwnerOrganizationId") ?? "", Text(sp, "appId") ?? "", settings, [.. certificates.OrderByDescending(c => c.Active).ThenByDescending(c => c.End)]);
    }
}

/// <summary>SAML changes: the enterprise app (mode, sign-on URL, relay state, signing certificate) and its app registration (identifiers, reply URLs, logout URL).</summary>
public sealed partial class GraphChangeExecutor
{
    internal static JsonObject SamlServicePrincipalBody(SamlSettings settings) => new()
    {
        ["preferredSingleSignOnMode"] = "saml",
        ["loginUrl"] = settings.SignOnUrl is { Length: > 0 } s ? s : null,
        ["samlSingleSignOnSettings"] = new JsonObject { ["relayState"] = settings.RelayState is { Length: > 0 } r ? r : null },
    };

    /// <summary>The registration's identifiers, and its web settings as read with the reply URLs and logout URL replaced.</summary>
    internal static JsonObject SamlRegistrationBody(JsonElement registration, SamlSettings settings)
    {
        var web = registration.TryGetProperty("web", out var w) && w.ValueKind == JsonValueKind.Object ? JsonNode.Parse(w.GetRawText())!.AsObject() : new JsonObject();
        web.Remove("@odata.type");
        web.Remove("redirectUriSettings");
        web["redirectUris"] = new JsonArray([.. settings.ReplyUrls.Select(u => (JsonNode)u)]);
        web["logoutUrl"] = settings.LogoutUrl is { Length: > 0 } l ? l : null;
        return new JsonObject { ["identifierUris"] = new JsonArray([.. settings.Identifiers.Select(i => (JsonNode)i)]), ["web"] = web };
    }

    internal static JsonObject SamlCertificateBody(AddSamlCertificate change) => new()
    {
        ["displayName"] = "CN=Microsoft Azure Federated SSO Certificate",
        // A day short: Entra refuses exactly the most years it allows, since its start is a moment later.
        ["endDateTime"] = DateTimeOffset.UtcNow.AddYears(change.Years).AddDays(-1).ToString("o", System.Globalization.CultureInfo.InvariantCulture),
    };

    async Task<ApplyResult> SamlAsync(AppChange change, CancellationToken cancellationToken)
    {
        var sp = $"servicePrincipals/{change.App.Id}";
        try
        {
            switch (change)
            {
                case SetSaml s:
                    var registration = await graph.GetAsync($"applications/{s.RegistrationId}?$select=web", cancellationToken);
                    // The registration first: Entra refuses SAML mode on an app with no identifier yet.
                    await graph.PatchAsync($"applications/{s.RegistrationId}", SamlRegistrationBody(registration, s.New), cancellationToken);
                    await graph.PatchAsync(sp, SamlServicePrincipalBody(s.New), cancellationToken);
                    break;
                case AddSamlCertificate a:
                    var made = await graph.PostAsync($"{sp}/addTokenSigningCertificate", SamlCertificateBody(a), cancellationToken);
                    var thumbprint = made.TryGetProperty("thumbprint", out var t) ? t.GetString() : null;
                    if (a.MakeActive && thumbprint is { Length: > 0 })
                        await graph.PatchAsync(sp, new JsonObject { ["preferredTokenSigningKeyThumbprint"] = thumbprint }, cancellationToken);
                    return new ApplyResult(ChangeOutcome.Applied, thumbprint is null ? null : $"Thumbprint {thumbprint}");
                case SetSamlActiveCertificate c:
                    await graph.PatchAsync(sp, new JsonObject { ["preferredTokenSigningKeyThumbprint"] = c.NewThumbprint }, cancellationToken);
                    break;
                case CreateSamlApp c:
                    var created = await graph.PostAsync($"applicationTemplates/{CreateSamlApp.TemplateId}/instantiate", new JsonObject { ["displayName"] = c.App.Name }, cancellationToken);
                    var appId = created.TryGetProperty("application", out var made2) && made2.TryGetProperty("appId", out var id) ? id.GetString() : null;
                    return new ApplyResult(ChangeOutcome.Applied, appId is null ? null : $"Application (client) ID {appId}. Set up SAML on its Single Sign-On tab.");
                case StopSaml:
                    await graph.PatchAsync(sp, new JsonObject { ["preferredSingleSignOnMode"] = null }, cancellationToken);
                    break;
            }
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    internal static PsScript? SamlScript(ChangeOp change)
    {
        string Patch(string path, string body) => $"Invoke-MgGraphRequest -Method PATCH -Uri {Ps.Quote(Ps.GraphRoot + path)} -Body {body}";
        return change switch
        {
            SetSaml s => PsScript.Graph(Ps.Lines(
                    Ps.Graph("GET", $"applications/{s.RegistrationId}?$select=web", assignTo: "app"),
                    "$web = $app.web; $web.Remove('redirectUriSettings')",
                    "$web.redirectUris = @(" + string.Join(", ", s.New.ReplyUrls.Select(u => Ps.Quote(u))) + ")",
                    "$web.logoutUrl = " + (s.New.LogoutUrl is { Length: > 0 } l ? Ps.Quote(l) : "$null"),
                    Patch($"applications/{s.RegistrationId}", "@{ identifierUris = @(" + string.Join(", ", s.New.Identifiers.Select(i => Ps.Quote(i))) + "); web = $web }"),
                    Ps.Graph("PATCH", $"servicePrincipals/{s.App.Id}", SamlServicePrincipalBody(s.New))),
                "Identifiers, reply URLs and the logout URL are on the app registration; the rest is on the enterprise app."),
            AddSamlCertificate a => PsScript.Graph(Ps.Lines(
                    Ps.Graph("POST", $"servicePrincipals/{a.App.Id}/addTokenSigningCertificate", SamlCertificateBody(a), assignTo: "cert"),
                    a.MakeActive ? Patch($"servicePrincipals/{a.App.Id}", "@{ preferredTokenSigningKeyThumbprint = $cert.thumbprint }") : null),
                "Entra makes the certificate and keeps its private key."),
            SetSamlActiveCertificate c => PsScript.Graph(Ps.Graph("PATCH", $"servicePrincipals/{c.App.Id}", new JsonObject { ["preferredTokenSigningKeyThumbprint"] = c.NewThumbprint })),
            CreateSamlApp c => PsScript.Graph(Ps.Graph("POST", $"applicationTemplates/{CreateSamlApp.TemplateId}/instantiate", new JsonObject { ["displayName"] = c.App.Name }),
                "Entra's non-gallery app template: makes the app registration and enterprise app together."),
            StopSaml p => PsScript.Graph(Ps.Graph("PATCH", $"servicePrincipals/{p.App.Id}", new JsonObject { ["preferredSingleSignOnMode"] = null })),
            _ => null,
        };
    }
}
