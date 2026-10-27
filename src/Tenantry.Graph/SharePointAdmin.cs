using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using Tenantry.Core.Auth;
using Tenantry.Core.Directory;
using Tenantry.Core.Http;

namespace Tenantry.Graph;

public sealed class SharePointException(HttpStatusCode status, string message, string? errorType = null) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>CSOM's name for what went wrong ("Microsoft.Online.SharePoint.Common.SpoNoSiteException"), when it said.</summary>
    public string? ErrorType { get; } = errorType;

    /// <summary>SharePoint has no such site (or deleted site).</summary>
    public bool IsNoSite => ErrorType is { } t && (t.EndsWith("SpoNoSiteException", StringComparison.Ordinal) || t.EndsWith("FileNotFoundException", StringComparison.Ordinal));
}

/// <summary>
/// The SharePoint admin site (<c>{tenant}-admin.sharepoint.com</c>): what Graph can't do for
/// OneDrive. Who holds admin rights on a site comes from the admin REST API
/// (<c>SPO.Tenant/GetSiteAdministrators</c>); giving or taking rights goes through CSOM
/// (<c>client.svc/ProcessQuery</c>), the call Set-SPOUser makes. One token for the tenant's
/// SharePoint covers the admin site.
/// </summary>
/// <remarks>
/// Setting up a OneDrive (ProfileLoader.CreatePersonalSiteEnqueueBulk, Tenant.RequestPersonalSites)
/// is refused for this app even for a Global Administrator ("Attempted to perform an unauthorized
/// operation"), while Microsoft's own module (Request-SPOPersonalSite) succeeds for the same admin:
/// SharePoint keeps that action for Microsoft's tools. The OneDrive tab offers the command instead.
/// </remarks>
public sealed class SharePointAdmin(HttpClient http, ITokenSource tokens, GraphClient graph,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    const string TenantTypeId = "{268004ae-ef6b-4e9b-8425-127220d84719}";

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

    /// <summary>
    /// A GET on any site's REST API (<c>{siteUrl}/_api/…</c>), OneDrives included: the tenant's
    /// SharePoint token covers every host (checked live).
    /// </summary>
    public async Task<JsonElement> GetSiteAsync(string url, CancellationToken cancellationToken = default) =>
        JsonDocument.Parse(await SendAsync(HttpMethod.Get, url, null, "application/json;odata=nometadata", cancellationToken)).RootElement.Clone();

    /// <summary>The CSOM type of the filter Get-SPOSite passes (SPOSitePropertiesEnumerableFilter).</summary>
    const string SiteFilterTypeId = "{b92aeee2-c92c-4b67-abcc-024e471bc140}";

    /// <summary>
    /// Every site's properties, as Get-SPOSite reads them (Tenant.GetSitePropertiesFromSharePointByFilters):
    /// storage, owner, template, sharing, lock, archive, hub and Teams connection in one pass. Pages
    /// arrive as SharePoint sends them (measured live: 557 rows with OneDrives in one 3.6 s page).
    /// </summary>
    /// <param name="includePersonal">Include OneDrives.</param>
    public async IAsyncEnumerable<JsonElement> GetSitesAsync(bool includePersonal,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? start = null;
        for (var page = 0; page < 1000; page++)
        {
            var startIndex = start is null ? "<Property Name=\"StartIndex\" Type=\"Null\" />"
                : $"<Property Name=\"StartIndex\" Type=\"String\">{SecurityElement.Escape(start)}</Property>";
            var answer = await ProcessQueryAsync(
                "<ObjectPath Id=\"2\" ObjectPathId=\"1\" /><ObjectPath Id=\"4\" ObjectPathId=\"3\" />" +
                "<Query Id=\"5\" ObjectPathId=\"3\"><Query SelectAllProperties=\"true\"><Properties /></Query>" +
                "<ChildItemQuery SelectAllProperties=\"true\"><Properties /></ChildItemQuery></Query>",
                $"<Constructor Id=\"1\" TypeId=\"{TenantTypeId}\" />" +
                $"<Method Id=\"3\" ParentId=\"1\" Name=\"GetSitePropertiesFromSharePointByFilters\"><Parameters><Parameter TypeId=\"{SiteFilterTypeId}\">" +
                "<Property Name=\"Filter\" Type=\"String\"></Property><Property Name=\"IncludeDetail\" Type=\"Boolean\">true</Property>" +
                $"<Property Name=\"IncludePersonalSite\" Type=\"Enum\">{(includePersonal ? 1 : 2)}</Property>{startIndex}" +
                "<Property Name=\"Template\" Type=\"Null\" /></Parameter></Parameters></Method>",
                cancellationToken);
            if (ResultOf(answer, 5) is not { ValueKind: JsonValueKind.Object } result)
                yield break;
            if (result.TryGetProperty("_Child_Items_", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    yield return item;
            start = Str(result, "NextStartIndexFromSharePoint");
            if (string.IsNullOrEmpty(start))
                yield break;
        }
    }

    static string Text(string value) => $"<Parameter Type=\"String\">{SecurityElement.Escape(value)}</Parameter>";

    const string Tenant = $"<Constructor Id=\"1\" TypeId=\"{TenantTypeId}\" />";

    // ---- Site settings and lifecycle (7c) ----------------------------------------------------

    /// <summary>A site's properties as Get-SPOSite reads them (with detail), or null when SharePoint has no such site.</summary>
    public async Task<JsonElement?> GetSitePropertiesAsync(string siteUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var answer = await ProcessQueryAsync(
                "<ObjectPath Id=\"2\" ObjectPathId=\"1\" /><ObjectPath Id=\"4\" ObjectPathId=\"3\" />" +
                "<Query Id=\"5\" ObjectPathId=\"3\"><Query SelectAllProperties=\"true\"><Properties /></Query></Query>",
                Tenant + $"<Method Id=\"3\" ParentId=\"1\" Name=\"GetSitePropertiesByUrl\"><Parameters>{Text(siteUrl)}<Parameter Type=\"Boolean\">true</Parameter></Parameters></Method>",
                cancellationToken);
            return ResultOf(answer, 5);
        }
        catch (SharePointException e) when (e.IsNoSite)
        {
            return null;
        }
    }

    /// <summary>
    /// Sets site properties and saves them (SiteProperties.Update, as Set-SPOSite does). SharePoint
    /// may finish the change a little later; callers read it back.
    /// </summary>
    public Task SetSitePropertiesAsync(string siteUrl, IReadOnlyList<(string Name, SitePropertyType Type, string Value)> values,
        CancellationToken cancellationToken = default)
    {
        var sets = string.Concat(values.Select((v, i) =>
            $"<SetProperty Id=\"{10 + i}\" ObjectPathId=\"3\" Name=\"{v.Name}\"><Parameter Type=\"{v.Type}\">{SecurityElement.Escape(v.Value)}</Parameter></SetProperty>"));
        return ProcessQueryAsync(
            $"<ObjectPath Id=\"2\" ObjectPathId=\"1\" /><ObjectPath Id=\"4\" ObjectPathId=\"3\" />{sets}<Method Name=\"Update\" Id=\"9\" ObjectPathId=\"3\" />",
            Tenant + $"<Method Id=\"3\" ParentId=\"1\" Name=\"GetSitePropertiesByUrl\"><Parameters>{Text(siteUrl)}<Parameter Type=\"Boolean\">false</Parameter></Parameters></Method>",
            cancellationToken);
    }

    /// <summary>
    /// One of the tenant's site methods that takes a site's address: ArchiveSiteByUrl,
    /// UnarchiveSiteByUrl, RemoveSite (to the recycle bin) or RestoreDeletedSite (each checked live).
    /// </summary>
    public Task RunSiteMethodAsync(string method, string siteUrl, CancellationToken cancellationToken = default) =>
        ProcessQueryAsync(
            "<ObjectPath Id=\"2\" ObjectPathId=\"1\" /><ObjectPath Id=\"4\" ObjectPathId=\"3\" />",
            Tenant + $"<Method Id=\"3\" ParentId=\"1\" Name=\"{method}\"><Parameters>{Text(siteUrl)}</Parameters></Method>",
            cancellationToken);

    /// <summary>Sites in SharePoint's recycle bin (Get-SPODeletedSite), page by page.</summary>
    public async IAsyncEnumerable<JsonElement> GetDeletedSitesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? start = null;
        for (var page = 0; page < 1000; page++)
        {
            var answer = await ProcessQueryAsync(
                "<ObjectPath Id=\"2\" ObjectPathId=\"1\" /><ObjectPath Id=\"4\" ObjectPathId=\"3\" />" +
                "<Query Id=\"5\" ObjectPathId=\"3\"><Query SelectAllProperties=\"true\"><Properties /></Query>" +
                "<ChildItemQuery SelectAllProperties=\"true\"><Properties /></ChildItemQuery></Query>",
                Tenant + "<Method Id=\"3\" ParentId=\"1\" Name=\"GetDeletedSitePropertiesFromSharePoint\"><Parameters>" +
                (start is null ? "<Parameter Type=\"Null\" />" : Text(start)) + "</Parameters></Method>",
                cancellationToken);
            if (ResultOf(answer, 5) is not { ValueKind: JsonValueKind.Object } result)
                yield break;
            if (result.TryGetProperty("_Child_Items_", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    yield return item;
            start = Str(result, "NextStartIndexFromSharePoint");
            if (string.IsNullOrEmpty(start))
                yield break;
        }
    }

    /// <summary>The organization's external sharing level: no site can be more open than this.</summary>
    public async Task<int> GetTenantSharingAsync(CancellationToken cancellationToken = default)
    {
        var answer = await ProcessQueryAsync(
            "<ObjectPath Id=\"2\" ObjectPathId=\"1\" /><Query Id=\"3\" ObjectPathId=\"1\"><Query SelectAllProperties=\"false\"><Properties>" +
            "<Property Name=\"SharingCapability\" ScalarProperty=\"true\" /></Properties></Query></Query>",
            Tenant, cancellationToken);
        return ResultOf(answer, 3) is { } tenant && tenant.TryGetProperty("SharingCapability", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
    }

    /// <summary>Who is in one of a site's SharePoint groups, by sign-in name.</summary>
    public async Task<IReadOnlyList<string>> GetSiteGroupLoginsAsync(string siteUrl, int groupId, CancellationToken cancellationToken = default)
    {
        var answer = await GetSiteAsync($"{siteUrl}/_api/web/sitegroups/GetById({groupId})/users?$select=LoginName", cancellationToken);
        return answer.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(u => Str(u, "LoginName")).OfType<string>()]
            : [];
    }

    /// <summary>Adds someone to a site's SharePoint group (SharePoint makes them known to the site first), or takes them out.</summary>
    public async Task SetSiteGroupMemberAsync(string siteUrl, int groupId, string login, bool add, CancellationToken cancellationToken = default)
    {
        const string json = "application/json;odata=nometadata";
        if (add)
        {
            await SendAsync(HttpMethod.Post, $"{siteUrl}/_api/web/ensureuser", JsonSerializer.Serialize(new { logonName = login }), json, cancellationToken, json);
            await SendAsync(HttpMethod.Post, $"{siteUrl}/_api/web/sitegroups/GetById({groupId})/users", JsonSerializer.Serialize(new { LoginName = login }),
                json, cancellationToken, json);
        }
        else
            await SendAsync(HttpMethod.Post,
                $"{siteUrl}/_api/web/sitegroups/GetById({groupId})/users/removeByLoginName(@v)?@v='{Uri.EscapeDataString(login.Replace("'", "''", StringComparison.Ordinal))}'",
                "", json, cancellationToken, json);
    }

    /// <summary>The site's Owners group's ID, or null when it has none (a OneDrive).</summary>
    public async Task<int?> GetOwnerGroupIdAsync(string siteUrl, CancellationToken cancellationToken = default)
    {
        var web = await GetSiteAsync($"{siteUrl}/_api/web?$select=AssociatedOwnerGroup/Id&$expand=AssociatedOwnerGroup", cancellationToken);
        return web.TryGetProperty("AssociatedOwnerGroup", out var g) && g.ValueKind == JsonValueKind.Object && g.TryGetProperty("Id", out var id)
               && id.ValueKind == JsonValueKind.Number
            ? id.GetInt32()
            : null;
    }

    /// <summary>SharePoint's state for an address (SPSiteManager/status): 0 free, 1 being made, 2 ready, 3 failed.</summary>
    public async Task<int> GetSiteStatusAsync(string siteUrl, CancellationToken cancellationToken = default)
    {
        var host = await HostAsync(cancellationToken);
        var answer = await GetSiteAsync($"https://{host}/_api/SPSiteManager/status?url='{Uri.EscapeDataString(siteUrl)}'", cancellationToken);
        return answer.TryGetProperty("SiteStatus", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
    }

    /// <summary>
    /// Makes a communication site (SPSiteManager/create, the documented REST call) and waits briefly
    /// for SharePoint to finish it. Returns its state as <see cref="GetSiteStatusAsync"/> gives it.
    /// Whoever signs in becomes its owner (checked live); naming another owner fails outright when
    /// they have no SharePoint license, so other owners are added to its Owners group after.
    /// </summary>
    public async Task<int> CreateCommunicationSiteAsync(string siteUrl, string title, string? description,
        CancellationToken cancellationToken = default)
    {
        const string json = "application/json;odata=nometadata";
        var host = await HostAsync(cancellationToken);
        var body = JsonSerializer.Serialize(new
        {
            request = new
            {
                Title = title,
                Url = siteUrl,
                Lcid = 1033,
                ShareByEmailEnabled = false,
                Description = description ?? "",
                WebTemplate = "SITEPAGEPUBLISHING#0",
            },
        });
        var answer = JsonDocument.Parse(await SendAsync(HttpMethod.Post, $"https://{host}/_api/SPSiteManager/create", body, json, cancellationToken, json)).RootElement;
        var status = answer.TryGetProperty("SiteStatus", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 1;
        for (var i = 0; status == 1 && i < 10; i++)
        {
            await _delay(TimeSpan.FromSeconds(3), cancellationToken);
            status = await GetSiteStatusAsync(siteUrl, cancellationToken);
        }
        return status;
    }

    /// <summary>The tenant's SharePoint address (https://contoso.sharepoint.com).</summary>
    public async Task<string> RootUrlAsync(CancellationToken cancellationToken = default) => "https://" + await HostAsync(cancellationToken);

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
            throw new SharePointException(HttpStatusCode.BadRequest, Str(error, "ErrorMessage") ?? "SharePoint refused the request.", Str(error, "ErrorTypeName"));
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

    async Task<string> SendAsync(HttpMethod method, string url, string? body, string accept, CancellationToken cancellationToken, string contentType = "text/xml")
    {
        string[] scopes = [$"https://{await HostAsync(cancellationToken)}/.default"];
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(scopes, null, cancellationToken));
            request.Headers.Accept.ParseAdd(accept);
            if (body is not null)
            {
                // "application/json;odata=nometadata" carries a parameter StringContent won't take as a media type.
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                request.Content.Headers.ContentType.CharSet = "utf-8";
            }
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
