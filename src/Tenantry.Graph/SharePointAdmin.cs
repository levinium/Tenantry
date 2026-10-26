using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using Tenantry.Core.Auth;
using Tenantry.Core.Directory;
using Tenantry.Core.Http;

namespace Tenantry.Graph;

public sealed class SharePointException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The SharePoint admin site (<c>{tenant}-admin.sharepoint.com</c>): what Graph can't do for
/// OneDrive. Who holds admin rights on a site comes from the admin REST API
/// (<c>SPO.Tenant/GetSiteAdministrators</c>); giving or taking rights and setting up a OneDrive
/// go through CSOM (<c>client.svc/ProcessQuery</c>), the same calls Set-SPOUser and
/// Request-SPOPersonalSite make. One token for the tenant's SharePoint covers the admin site.
/// </summary>
public sealed class SharePointAdmin(HttpClient http, ITokenSource tokens, GraphClient graph,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    const string TenantTypeId = "{268004ae-ef6b-4e9b-8425-127220d84719}";
    const string ProfileLoaderTypeId = "{9c42543a-91b3-4902-b2fe-14ccdefb6e2b}";

    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    string? _host;

    /// <summary>The tenant's SharePoint host (contoso.sharepoint.com), read once from Graph.</summary>
    async Task<string> HostAsync(CancellationToken cancellationToken)
    {
        if (_host is null)
        {
            var root = await graph.GetAsync("sites/root?$select=siteCollection", cancellationToken);
            _host = root.GetProperty("siteCollection").GetProperty("hostname").GetString()
                    ?? throw new InvalidOperationException("Microsoft 365 didn't say where this tenant's SharePoint is.");
        }
        return _host;
    }

    /// <summary>contoso.sharepoint.com → contoso-admin.sharepoint.com.</summary>
    public static string AdminHost(string host)
    {
        var dot = host.IndexOf('.');
        return host[..dot] + "-admin" + host[dot..];
    }

    /// <summary>Their OneDrive's address, or null when it hasn't been set up (SharePoint answers with a profile page then).</summary>
    public async Task<string?> GetPersonalSiteUrlAsync(string userPrincipalName, CancellationToken cancellationToken = default)
    {
        var result = await ProcessQueryAsync(
            $"<ObjectPath Id=\"2\" ObjectPathId=\"1\" /><Method Name=\"GetPersonalSiteUrl\" Id=\"3\" ObjectPathId=\"1\"><Parameters>{Text(userPrincipalName)}</Parameters></Method>",
            $"<Constructor Id=\"1\" TypeId=\"{TenantTypeId}\" />", cancellationToken);
        var url = ResultOf(result, 3) is { ValueKind: JsonValueKind.String } u ? u.GetString() : null;
        return url is null || url.Contains("/Person.aspx", StringComparison.OrdinalIgnoreCase) ? null : url.TrimEnd('/');
    }

    /// <summary>Everyone with admin rights on a site (a OneDrive's owner, plus anyone given access).</summary>
    public async Task<IReadOnlyList<SiteAdmin>> GetSiteAdministratorsAsync(string siteId, CancellationToken cancellationToken = default)
    {
        var host = await HostAsync(cancellationToken);
        var answer = await SendAsync(HttpMethod.Post, $"https://{AdminHost(host)}/_api/SPO.Tenant/GetSiteAdministrators?siteId='{siteId}'",
            null, "application/json;odata=nometadata", cancellationToken);
        var rows = JsonDocument.Parse(answer).RootElement;
        var list = rows.ValueKind == JsonValueKind.Object && rows.TryGetProperty("value", out var v) ? v : rows;
        return list.ValueKind != JsonValueKind.Array ? [] :
        [
            .. list.EnumerateArray().Select(a => new SiteAdmin(
                Str(a, "loginName") ?? "",
                Str(a, "name") is { Length: > 0 } name ? name : Str(a, "email") ?? Str(a, "loginName") ?? "Someone",
                Str(a, "email") is { Length: > 0 } email ? email : null,
                Str(a, "userPrincipalName") is { Length: > 0 } upn ? upn : null)),
        ];
    }

    /// <summary>Gives someone admin rights on a site (full access to a OneDrive), or takes them away.</summary>
    /// <param name="loginName">SharePoint's sign-in name for them (as GetSiteAdministrators gives it).</param>
    public Task SetSiteAdminAsync(string siteUrl, string loginName, bool isAdmin, CancellationToken cancellationToken = default) =>
        ProcessQueryAsync(
            $"<ObjectPath Id=\"2\" ObjectPathId=\"1\" /><Method Name=\"SetSiteAdmin\" Id=\"3\" ObjectPathId=\"1\"><Parameters>{Text(siteUrl)}" +
            $"{Text(loginName)}<Parameter Type=\"Boolean\">{(isAdmin ? "true" : "false")}</Parameter></Parameters></Method>",
            $"<Constructor Id=\"1\" TypeId=\"{TenantTypeId}\" />", cancellationToken);

    /// <summary>Asks SharePoint to create their OneDrive now rather than on first sign-in. It's ready within minutes.</summary>
    public Task RequestOneDriveAsync(string userPrincipalName, CancellationToken cancellationToken = default) =>
        ProcessQueryAsync(
            "<ObjectPath Id=\"2\" ObjectPathId=\"1\" /><Method Name=\"CreatePersonalSiteEnqueueBulk\" Id=\"3\" ObjectPathId=\"1\"><Parameters>" +
            $"<Parameter Type=\"Array\"><Object Type=\"String\">{SecurityElement.Escape(userPrincipalName)}</Object></Parameter></Parameters></Method>",
            $"<StaticMethod Id=\"1\" Name=\"GetProfileLoader\" TypeId=\"{ProfileLoaderTypeId}\" />", cancellationToken);

    static string Text(string value) => $"<Parameter Type=\"String\">{SecurityElement.Escape(value)}</Parameter>";

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Runs one CSOM request on the admin site. The answer is a JSON array: a header (with
    /// ErrorInfo when SharePoint refused), then each action's id followed by its result.
    /// </summary>
    async Task<JsonElement> ProcessQueryAsync(string actions, string objectPaths, CancellationToken cancellationToken)
    {
        var host = await HostAsync(cancellationToken);
        var body = "<Request AddExpandoFieldTypeSuffix=\"true\" SchemaVersion=\"15.0.0.0\" LibraryVersion=\"16.0.0.0\" ApplicationName=\"Tenantry\" " +
                   "xmlns=\"http://schemas.microsoft.com/sharepoint/clientquery/2009\">" +
                   $"<Actions>{actions}</Actions><ObjectPaths>{objectPaths}</ObjectPaths></Request>";
        var text = await SendAsync(HttpMethod.Post, $"https://{AdminHost(host)}/_vti_bin/client.svc/ProcessQuery", body, "application/json", cancellationToken);
        var answer = JsonDocument.Parse(text).RootElement.Clone();
        if (answer.ValueKind == JsonValueKind.Array && answer.GetArrayLength() > 0
            && answer[0].TryGetProperty("ErrorInfo", out var error) && error.ValueKind == JsonValueKind.Object)
            throw new SharePointException(HttpStatusCode.BadRequest, Str(error, "ErrorMessage") ?? "SharePoint refused the request.");
        return answer;
    }

    /// <summary>The result that follows action <paramref name="id"/> in a CSOM answer.</summary>
    static JsonElement? ResultOf(JsonElement answer, int id)
    {
        for (var i = 1; i + 1 < answer.GetArrayLength(); i++)
            if (answer[i].ValueKind == JsonValueKind.Number && answer[i].GetInt32() == id)
                return answer[i + 1];
        return null;
    }

    async Task<string> SendAsync(HttpMethod method, string url, string? xml, string accept, CancellationToken cancellationToken)
    {
        string[] scopes = [$"https://{await HostAsync(cancellationToken)}/.default"];
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(scopes, null, cancellationToken));
            request.Headers.Accept.ParseAdd(accept);
            if (xml is not null)
                request.Content = new StringContent(xml, Encoding.UTF8, "text/xml");
            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
                return text;
            if (HttpRetry.IsTransient(response.StatusCode) && attempt < HttpRetry.MaxAttempts)
            {
                await _delay(HttpRetry.DelayFor(response, attempt), cancellationToken);
                continue;
            }
            throw new SharePointException(response.StatusCode, response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Managing OneDrive needs the SharePoint Administrator role.",
                _ => ErrorText(text) ?? $"SharePoint answered {(int)response.StatusCode}.",
            });
        }
    }

    static string? ErrorText(string text)
    {
        try
        {
            var root = JsonDocument.Parse(text).RootElement;
            var error = root.TryGetProperty("odata.error", out var e) ? e : root.TryGetProperty("error", out var e2) ? e2 : default;
            return error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                ? m.ValueKind == JsonValueKind.Object ? Str(m, "value") : m.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
