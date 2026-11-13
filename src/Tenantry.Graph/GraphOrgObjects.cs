using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>Where organization kinds live (15c-15f): Graph, or the Teams admin API.</summary>
static class OrgAddress
{
    public const string TeamsRoot = "https://api.interfaces.records.teams.microsoft.com/";
    const string GraphBeta = "https://graph.microsoft.com/beta/";

    /// <summary>A kind's path as a URL its client takes: "beta/…" becomes Graph beta; Teams paths go to the Teams host.</summary>
    public static string Url(OrgApi api, string path) =>
        path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path
        : api == OrgApi.Teams ? TeamsRoot + path
        : path.StartsWith("beta/", StringComparison.Ordinal) ? GraphBeta + path[5..] : path;

    /// <summary>The full address, for the PowerShell shown.</summary>
    public static string Full(OrgApi api, string path) => Url(api, path) is var url && url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : GraphClient.V1 + url;
}

/// <summary>Organization kinds (15c-15f) read from Graph and the Teams admin API.</summary>
public sealed partial class GraphUserRecords : IOrgObjectRecords, IUsageReportRecords
{
    static string Friendly(Exception e) => e.Message.Split('\n')[0] is var line && line.Length > 160 ? line[..160] + "…" : e.Message.Split('\n')[0];

    /// <summary>A usage report, as JSON (the beta reports take $format; v1.0 answers only CSV).</summary>
    public async Task<UsageReportRead> GetUsageReportAsync(UsageReport report, UsagePeriod period, CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = new List<JsonElement>();
            await foreach (var r in graph.GetAllAsync($"https://graph.microsoft.com/beta/reports/{report.Function}(period='{period.Value}')?$format=application/json", cancellationToken))
            {
                rows.Add(r.Clone());
                if (rows.Count >= MaxReportRows)
                    break;
            }
            return new UsageReportRead(rows, rows.Count >= MaxReportRows
                ? $"Showing the first {MaxReportRows:N0} rows; Microsoft has more. Download the report from the admin center for all of them." : null);
        }
        catch (GraphException e)
        {
            return new UsageReportRead([], e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                ? "Reading usage reports needs the Reports.Read.All permission and a role such as Reports Reader or Global Reader."
                : $"Couldn't read the report: {e.Message}");
        }
    }

    /// <summary>The most rows a usage report or an organization list shows; past this a note says so.</summary>
    const int MaxReportRows = 20000;

    const int MaxOrgRows = 20000;

    Task<string>? _orgId;

    /// <summary>The Teams admin API's client; set when the app registration has its permission.</summary>
    public GraphClient? TeamsAdmin { get; init; }

    async Task<string> OrgIdAsync(CancellationToken cancellationToken) =>
        await (_orgId ??= Task.Run(async () => (await graph.GetAsync("organization?$select=id", cancellationToken)).GetProperty("value")[0].GetProperty("id").GetString() ?? "", cancellationToken));

    async Task<string> ResolveAsync(string path, string? parentUrl, CancellationToken cancellationToken)
    {
        if (path.Contains("{org}", StringComparison.Ordinal))
            path = path.Replace("{org}", await OrgIdAsync(cancellationToken), StringComparison.Ordinal);
        if (parentUrl is not null)
            path = path.Replace("{parent}", parentUrl, StringComparison.Ordinal);
        return path;
    }

    static string? Plain(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

    public async Task<OrgRead> GetOrgObjectsAsync(OrgKind kind, string? parentUrl = null, CancellationToken cancellationToken = default)
    {
        var client = kind.Api == OrgApi.Teams ? TeamsAdmin : graph;
        if (client is null)
            return new OrgRead([], $"Reading {kind.Title} needs {kind.Needs ?? "a permission Tenantry doesn't have yet."}");
        try
        {
            var list = await ResolveAsync(kind.ListUrl, parentUrl, cancellationToken);
            var collection = list.Split('?')[0];
            var root = await client.GetAsync(OrgAddress.Url(kind.Api, list), cancellationToken);
            var rows = new List<JsonElement>();
            var isCollection = root.ValueKind == JsonValueKind.Array || root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var v0) && v0.ValueKind == JsonValueKind.Array;
            if (root.ValueKind == JsonValueKind.Array)
                rows.AddRange(root.EnumerateArray());
            else if (isCollection)
            {
                var page = root;
                while (true)
                {
                    rows.AddRange(page.GetProperty("value").EnumerateArray());
                    if (rows.Count >= MaxOrgRows || !page.TryGetProperty("@odata.nextLink", out var next) || next.GetString() is not { } link)
                        break;
                    page = await client.GetAsync(link, cancellationToken);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
                rows.Add(root);
            if (kind.Where is { } where)
                rows = [.. rows.Where(where)];
            var items = rows.Select(r =>
            {
                var id = Plain(r, kind.IdProperty) ?? "";
                var url = isCollection ? kind.AddressOf(id, collection) : collection;
                var name = kind.NameOf?.Invoke(r) ?? Plain(r, kind.NameProperty) ?? (id.Length > 0 ? id : kind.Title);
                return new OrgObject(id, name, url, r.Clone());
            }).ToList();
            if (kind.ReadEach)
            {
                // The list leaves out each item's settings (teams): read each, a few at a time.
                using var gate = new SemaphoreSlim(6);
                var whole = await Task.WhenAll(items.Select(async item =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        return item with { Raw = (await client.GetAsync(OrgAddress.Url(kind.Api, item.Url), cancellationToken)).Clone() };
                    }
                    catch (GraphException)
                    {
                        return item;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
                items = [.. whole];
            }
            return new OrgRead([.. items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)],
                rows.Count >= MaxOrgRows ? $"Showing the first {MaxOrgRows:N0}; Microsoft has more." : null);
        }
        catch (GraphException e)
        {
            return new OrgRead([], e.Status switch
            {
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => $"Microsoft refused: reading {kind.Title} needs {kind.Needs ?? "a role that can see them, and Tenantry's newer permissions (grant them from Settings)."}",
                HttpStatusCode.NotFound => kind.Single ? $"{kind.Title} isn't set up for this organization yet." : null,
                _ => $"Couldn't read {kind.Title}: {e.Message}",
            });
        }
        catch (Exception e) when (e is not OperationCanceledException && kind.Api == OrgApi.Teams)
        {
            // No token for the Teams admin API: the app registration doesn't ask for it yet (AADSTS650057).
            return new OrgRead([], $"Tenantry can't reach the Teams admin API yet: grant its new permissions from Settings. ({Friendly(e)})");
        }
    }

    public async Task<IReadOnlyList<string>> GetOrgExtraAsync(OrgKind kind, OrgObject item, OrgExtra extra, CancellationToken cancellationToken = default)
    {
        var client = kind.Api == OrgApi.Teams ? TeamsAdmin : graph;
        if (client is null)
            return [];
        try
        {
            var lines = new List<string>();
            await foreach (var r in client.GetAllAsync(OrgAddress.Url(kind.Api, $"{item.Url}/{extra.Path}"), cancellationToken))
                if (extra.Line(r) is { Length: > 0 } line)
                    lines.Add(line);
            return lines;
        }
        catch (GraphException e)
        {
            return [$"Couldn't read them: {e.Message}"];
        }
    }
}

/// <summary>Organization changes (15c-15f).</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>The Teams admin API's client, for Teams policy changes.</summary>
    public GraphClient? TeamsAdmin { get; init; }

    /// <summary>The body a setting change sends, from the object as it is now.</summary>
    internal static JsonObject SettingBody(SetOrgSetting change, JsonElement current)
    {
        var now = current.ValueKind == JsonValueKind.Object ? JsonNode.Parse(current.GetRawText())!.AsObject() : [];
        switch (change.Write)
        {
            case OrgWrite.Put:
                foreach (var name in now.Select(p => p.Key).Where(k => k.StartsWith('@') || k.Contains("@odata", StringComparison.Ordinal) || k is "id" or "version").ToArray())
                    now.Remove(name);
                OrgPath.Set(now, change.Path, change.Value);
                return now;
            case OrgWrite.MergePut:
                var merge = new JsonObject();
                if (now["Key"] is { } key)
                    merge["Key"] = key.DeepClone();
                OrgPath.Set(merge, change.Path, change.Value);
                return merge;
            default:
                // The whole top-level property, as it is now, with the change: Graph replaces a
                // complex property whole, so sending only part of it would clear the rest.
                var top = OrgPath.Top(change.Path);
                var patch = new JsonObject { [top] = now[top]?.DeepClone() };
                OrgPath.Set(patch, change.Path, change.Value);
                WholeAccess(patch, top);
                return patch;
        }
    }

    async Task<ApplyResult> ApplyOrgAsync(OrgChange change, CancellationToken cancellationToken)
    {
        var client = change.Api == OrgApi.Teams ? TeamsAdmin : graph;
        if (client is null)
            return new ApplyResult(ChangeOutcome.Failed, "Tenantry can't reach the Teams admin API yet: grant its new permissions from Settings.");
        var url = OrgAddress.Url(change.Api, change.Url);
        try
        {
            switch (change)
            {
                case SetOrgSetting s:
                    var body = SettingBody(s, await client.GetAsync(url, cancellationToken));
                    if (s.Write is OrgWrite.Put or OrgWrite.MergePut)
                        await client.PutAsync(url, body, cancellationToken);
                    else
                        await client.PatchAsync(url, body, cancellationToken);
                    break;
                case CreateOrgObject c:
                    if (c.Put)
                        await client.PutAsync(url, c.Body, cancellationToken);
                    else
                        await client.PostAsync(url, c.Body, cancellationToken);
                    break;
                case DeleteOrgObject:
                    await client.DeleteAsync(url, cancellationToken);
                    break;
                case RunOrgAction a when a.Method == "DELETE":
                    await client.DeleteAsync(url, cancellationToken);
                    break;
                case RunOrgAction a:
                    await client.PostAsync(url, a.Body ?? [], cancellationToken);
                    break;
            }
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
        catch (Exception e) when (e is not OperationCanceledException && change.Api == OrgApi.Teams)
        {
            return new ApplyResult(ChangeOutcome.Failed, $"Tenantry can't reach the Teams admin API yet: grant its new permissions from Settings. ({e.Message.Split('\n')[0]})");
        }
    }

    internal static PsScript? OrgScript(ChangeOp change)
    {
        if (change is not OrgChange o)
            return null;
        var uri = OrgAddress.Full(o.Api, o.Url);
        string Call(string method, JsonNode? body) => o.Api == OrgApi.Teams
            ? $"Invoke-RestMethod -Method {method} -Uri {Ps.Quote(uri)} -Headers @{{ Authorization = \"Bearer $teamsToken\" }} -ContentType 'application/json'" +
              (body is null ? "" : $" -Body '{body.ToJsonString().Replace("'", "''", StringComparison.Ordinal)}'")
            : Ps.Graph(method, uri, body);
        var note = o.Api == OrgApi.Teams ? "The Teams admin API needs a token of its own ($teamsToken, for 48ac35b8-9aa8-4d74-927d-1f4a14a0b239)." : null;
        return o switch
        {
            SetOrgSetting s when s.Write is OrgWrite.Put or OrgWrite.MergePut => PsScript.Graph(Call("PUT", SettingPreview(s)), Note(note, "Tenantry reads the object first and sends it back with the change.")),
            SetOrgSetting s => PsScript.Graph(Call("PATCH", SettingPreview(s)), Note(note, "Tenantry reads the object first, so other settings in the same group are sent as they are.")),
            CreateOrgObject c => PsScript.Graph(Call(c.Put ? "PUT" : "POST", c.Body), note),
            DeleteOrgObject => PsScript.Graph(Call("DELETE", null), note),
            RunOrgAction a => PsScript.Graph(Call(a.Method, a.Method == "DELETE" ? null : a.Body ?? []), note),
            _ => null,
        };
    }

    /// <summary>
    /// A cross-tenant partner leaves unset what it takes from the defaults; setting one access type
    /// there needs whole settings, so who and which apps it covers start as everyone and everything.
    /// </summary>
    static void WholeAccess(JsonObject patch, string top)
    {
        if (!top.StartsWith("b2b", StringComparison.Ordinal) || patch[top] is not JsonObject access)
            return;
        var type = (access["usersAndGroups"]?["accessType"] ?? access["applications"]?["accessType"])?.GetValue<string>();
        foreach (var (part, target, targetType) in new[] { ("usersAndGroups", "AllUsers", "user"), ("applications", "AllApplications", "application") })
        {
            if (access[part] is not JsonObject one)
            {
                if (type is null)
                    continue;
                access[part] = one = new JsonObject { ["accessType"] = type };
            }
            if (one["targets"] is not JsonArray)
                one["targets"] = new JsonArray(new JsonObject { ["target"] = target, ["targetType"] = targetType });
        }
    }

    static string? Note(string? first, string second) => first is null ? second : $"{first} {second}";

    /// <summary>The change alone, for the PowerShell shown (the real request carries the rest of the object as read).</summary>
    static JsonObject SettingPreview(SetOrgSetting s)
    {
        var body = new JsonObject();
        OrgPath.Set(body, s.Path, s.Value);
        return body;
    }
}
