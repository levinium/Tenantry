using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>Configuration profiles (13b): the settings catalog (beta configurationPolicies) and older template profiles (deviceConfigurations).</summary>
public sealed partial class GraphUserRecords : IConfigurationProfileRecords
{
    public async Task<IReadOnlyList<ConfigurationProfile>> GetConfigurationProfilesAsync(CancellationToken cancellationToken = default)
    {
        var profiles = new List<ConfigurationProfile>();
        await foreach (var p in graph.GetAllAsync(SettingsCatalog.Beta +
                           "configurationPolicies?$expand=assignments&$select=id,name,description,platforms,technologies,templateReference,settingCount,lastModifiedDateTime,roleScopeTagIds",
                           cancellationToken))
            profiles.Add(ToProfile(p));
        return await NameAssignmentsAsync(profiles, p => p.Assignments, (p, a) => p with { Assignments = a }, cancellationToken);
    }

    internal static ConfigurationProfile ToProfile(JsonElement p)
    {
        var template = p.TryGetProperty("templateReference", out var t) && t.ValueKind == JsonValueKind.Object ? t : (JsonElement?)null;
        var header = new JsonObject
        {
            ["name"] = Text(p, "name"),
            ["description"] = Text(p, "description") ?? "",
            ["platforms"] = Text(p, "platforms"),
            ["technologies"] = Text(p, "technologies"),
            ["roleScopeTagIds"] = p.TryGetProperty("roleScopeTagIds", out var tags) && tags.ValueKind == JsonValueKind.Array ? JsonNode.Parse(tags.GetRawText()) : new JsonArray("0"),
        };
        if (template is { } tr && Text(tr, "templateId") is { Length: > 0 } templateId)
            header["templateReference"] = new JsonObject { ["templateId"] = templateId };
        return new ConfigurationProfile(Text(p, "id") ?? "", Text(p, "name") ?? "A profile", Text(p, "description") is { Length: > 0 } d ? d : null, Text(p, "platforms") ?? "",
            Text(p, "technologies") ?? "", template is { } f ? Text(f, "templateFamily") : null, template is { } n ? Text(n, "templateDisplayName") : null,
            p.TryGetProperty("settingCount", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0, When(p, "lastModifiedDateTime"),
            p.TryGetProperty("assignments", out var a) && a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray().Select(IntuneAssignment.From).OfType<IntuneAssignment>()] : [],
            JsonDocument.Parse(header.ToJsonString()).RootElement.Clone());
    }

    public async Task<(JsonArray Settings, IReadOnlyDictionary<string, CatalogDefinition> Definitions)> GetProfileSettingsAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var settings = new JsonArray();
        var defs = new Dictionary<string, CatalogDefinition>(StringComparer.OrdinalIgnoreCase);
        await foreach (var s in graph.GetAllAsync($"{SettingsCatalog.Beta}configurationPolicies/{profileId}/settings?$expand=settingDefinitions", cancellationToken))
        {
            if (s.TryGetProperty("settingInstance", out var instance) && instance.ValueKind == JsonValueKind.Object)
                settings.Add(JsonNode.Parse(instance.GetRawText()));
            if (s.TryGetProperty("settingDefinitions", out var d) && d.ValueKind == JsonValueKind.Array)
                foreach (var def in d.EnumerateArray().Select(CatalogDefinition.From))
                    defs[def.Id] = def;
        }
        return (settings, defs);
    }

    public async Task<IReadOnlyList<CatalogDefinition>> SearchSettingsAsync(string platforms, string technologies, string words, CancellationToken cancellationToken = default)
    {
        var platform = platforms.Split(',')[0].Trim();
        var technology = technologies.Split(',')[0].Trim();
        var search = words.Replace("\"", "", StringComparison.Ordinal).Trim();
        if (search.Length == 0)
            return [];
        var answer = await graph.GetAsync($"{SettingsCatalog.Beta}configurationSettings?$search=\"{Uri.EscapeDataString(search)}\"" +
            $"&$filter=applicability/platform has '{platform}' and applicability/technologies has '{technology}'&$top=60", cancellationToken);
        return [.. answer.GetProperty("value").EnumerateArray().Select(CatalogDefinition.From).Where(d => d.Kind != CatalogKind.Other)
            // Inner settings appear in a search too; only those that stand on their own can be added at the top.
            .Where(d => !IsChildOnly(answer, d.Id))];
    }

    static bool IsChildOnly(JsonElement answer, string id) =>
        answer.GetProperty("value").EnumerateArray().FirstOrDefault(d => Text(d, "id") == id) is { ValueKind: JsonValueKind.Object } d
        && Text(d, "rootDefinitionId") is { Length: > 0 } root && !string.Equals(root, id, StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<CatalogDefinition>> GetDefinitionsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var reads = ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(async id =>
        {
            try
            {
                return CatalogDefinition.From(await graph.GetAsync($"{SettingsCatalog.Beta}configurationSettings('{Uri.EscapeDataString(id)}')", cancellationToken));
            }
            catch (GraphException)
            {
                return null;
            }
        });
        return [.. (await Task.WhenAll(reads)).OfType<CatalogDefinition>()];
    }

    public async Task<IReadOnlyList<TemplateProfile>> GetTemplateProfilesAsync(CancellationToken cancellationToken = default)
    {
        var profiles = new List<TemplateProfile>();
        await foreach (var p in graph.GetAllAsync("deviceManagement/deviceConfigurations?$expand=assignments", cancellationToken))
            profiles.Add(new TemplateProfile(Text(p, "id") ?? "", Text(p, "displayName") ?? "A profile", Text(p, "description") is { Length: > 0 } d ? d : null, Text(p, "@odata.type") ?? "",
                When(p, "lastModifiedDateTime"),
                p.TryGetProperty("assignments", out var a) && a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray().Select(IntuneAssignment.From).OfType<IntuneAssignment>()] : [],
                p.Clone()));
        return await NameAssignmentsAsync(profiles, p => p.Assignments, (p, a) => p with { Assignments = a }, cancellationToken);
    }
}

/// <summary>Settings catalog changes: the whole profile PUT with its settings, and a new profile.</summary>
public sealed partial class GraphChangeExecutor
{
    internal static JsonObject CatalogBody(SetCatalogSettings change)
    {
        var body = change.Header.DeepClone().AsObject();
        body["settings"] = new JsonArray([.. change.New.Select(i => (JsonNode)new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.deviceManagementConfigurationSetting",
            ["settingInstance"] = i!.DeepClone(),
        })]);
        return body;
    }

    internal static JsonObject NewProfileBody(CreateConfigurationProfile change) => new()
    {
        ["name"] = change.Policy.Name,
        ["description"] = change.Description ?? "",
        ["platforms"] = change.Platforms,
        ["technologies"] = change.Technologies,
        ["roleScopeTagIds"] = new JsonArray("0"),
        ["settings"] = new JsonArray([.. change.Settings.Select(i => (JsonNode)new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.deviceManagementConfigurationSetting",
            ["settingInstance"] = i!.DeepClone(),
        })]),
    };

    async Task<ApplyResult> ApplyCatalogAsync(IntuneChange change, CancellationToken cancellationToken)
    {
        try
        {
            switch (change)
            {
                case SetCatalogSettings s:
                    // The name and description as they are now: a rename applied just before isn't undone.
                    var now = await graph.GetAsync($"{s.Path}?$select=name,description", cancellationToken);
                    var body = CatalogBody(s);
                    body["name"] = GraphUserRecords.StrOf(now, "name") ?? (string?)body["name"];
                    body["description"] = GraphUserRecords.StrOf(now, "description") ?? "";
                    await graph.PutAsync(s.Path, body, cancellationToken);
                    break;
                case CreateConfigurationProfile c:
                    await graph.PostAsync(SettingsCatalog.Beta + "configurationPolicies", NewProfileBody(c), cancellationToken);
                    break;
            }
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    internal static PsScript? CatalogScript(ChangeOp change) => change switch
    {
        SetCatalogSettings s => PsScript.Graph(Ps.Graph("PUT", s.Path, CatalogBody(s)), "The profile is sent whole with every setting, as Intune's portal does."),
        CreateConfigurationProfile c => PsScript.Graph(Ps.Graph("POST", SettingsCatalog.Beta + "configurationPolicies", NewProfileBody(c))),
        _ => null,
    };
}
