using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>The Conditional Access editor's reads (8b-2): Microsoft's templates, terms of use, apps by name.</summary>
public sealed partial class GraphUserRecords : ICaEditingRecords
{
    IReadOnlyList<CaTemplate>? _templates;

    public async Task<IReadOnlyList<CaTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        if (_templates is not null)
            return _templates;
        var templates = new List<CaTemplate>();
        await foreach (var t in graph.GetAllAsync($"{Ca}templates", cancellationToken))
            templates.Add(new CaTemplate(Text(t, "id") ?? "", Text(t, "name") ?? "A template", Text(t, "description") ?? "",
                (Text(t, "scenarios") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                t.TryGetProperty("details", out var d) ? d.GetRawText() : "{}"));
        return _templates = [.. templates.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<Available<IReadOnlyList<TermsOfUse>>> GetTermsOfUseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var terms = new List<TermsOfUse>();
            await foreach (var a in graph.GetAllAsync("identityGovernance/termsOfUse/agreements?$select=id,displayName", cancellationToken))
                terms.Add(new TermsOfUse(Text(a, "id") ?? "", Text(a, "displayName") ?? "Terms of use"));
            return new(terms);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return Available<IReadOnlyList<TermsOfUse>>.Because("Requiring terms of use needs Tenantry's Agreement.Read.All permission; grant it in Settings.");
        }
    }

    public async Task<IReadOnlyList<(string AppId, string Name)>> SearchAppsAsync(string text, CancellationToken cancellationToken = default)
    {
        var term = text.Trim().Replace("'", "''", StringComparison.Ordinal);
        if (term.Length == 0)
            return [];
        var apps = new List<(string, string)>();
        var answer = await graph.GetAsync($"servicePrincipals?$filter=startswith(displayName,'{Uri.EscapeDataString(term)}')&$select=appId,displayName&$top=20", cancellationToken);
        foreach (var sp in answer.GetProperty("value").EnumerateArray())
            if (Text(sp, "appId") is { } appId)
                apps.Add((appId, Text(sp, "displayName") ?? appId));
        return apps;
    }
}

/// <summary>Conditional Access policies made or changed in the editor (8b-2).</summary>
public sealed partial class GraphChangeExecutor
{
    async Task<ApplyResult> CreateCaPolicyAsync(CreateCaPolicy change, CancellationToken cancellationToken)
    {
        string id;
        try
        {
            var made = await graph.PostAsync("identity/conditionalAccess/policies", CaJson.ForWrite(JsonNode.Parse(change.Json)!.AsObject()), cancellationToken);
            id = made.GetProperty("id").GetString()!;
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
        // A new policy shows in reads after a few seconds (seen live).
        foreach (var wait in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) })
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            try
            {
                await graph.GetAsync($"identity/conditionalAccess/policies/{id}?$select=id", cancellationToken);
                return new ApplyResult(ChangeOutcome.Applied);
            }
            catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
            {
            }
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "Entra made it; it shows in the list within a minute.");
    }

    Task<ApplyResult> UpdateCaPolicyAsync(UpdateCaPolicy change, CancellationToken cancellationToken)
    {
        var body = CaJson.ForWrite(JsonNode.Parse(change.Json)!.AsObject());
        var name = body["displayName"]?.GetValue<string>();
        var state = body["state"]?.GetValue<string>();
        return GraphCheckedAsync(
            ct => graph.PatchAsync($"identity/conditionalAccess/policies/{change.Policy.Id}", body, ct),
            ct => graph.GetAsync($"identity/conditionalAccess/policies/{change.Policy.Id}?$select=displayName,state", ct),
            answer => answer is { } p && (name is null || p.GetProperty("displayName").GetString() == name)
                      && (state is null || (p.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == state)),
            cancellationToken);
    }
}

/// <summary>Named locations (8b-3).</summary>
public sealed partial class GraphChangeExecutor
{
    const string Locations = "identity/conditionalAccess/namedLocations";

    async Task<ApplyResult> CreateNamedLocationAsync(CreateNamedLocation change, CancellationToken cancellationToken)
    {
        try
        {
            await graph.PostAsync(Locations, JsonNode.Parse(change.Json)!.AsObject(), cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    /// <summary>Graph needs the location's @odata.type on a change, as on create.</summary>
    Task<ApplyResult> UpdateNamedLocationAsync(UpdateNamedLocation change, CancellationToken cancellationToken)
    {
        var body = JsonNode.Parse(change.Json)!.AsObject();
        var name = body["displayName"]?.GetValue<string>();
        return GraphCheckedAsync(
            ct => graph.PatchAsync($"{Locations}/{change.Location.Id}", body, ct),
            ct => graph.GetAsync($"{Locations}/{change.Location.Id}", ct),
            answer => answer is { } l && l.GetProperty("displayName").GetString() == name,
            cancellationToken);
    }

    async Task<ApplyResult> DeleteNamedLocationAsync(DeleteNamedLocation change, CancellationToken cancellationToken)
    {
        try
        {
            await DeleteIfThereAsync($"{Locations}/{change.Location.Id}", cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }
}
