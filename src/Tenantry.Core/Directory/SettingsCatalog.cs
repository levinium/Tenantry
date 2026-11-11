using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tenantry.Core.Directory;

/// <summary>How a settings catalog setting is set.</summary>
public enum CatalogKind
{
    /// <summary>One option from a list; an option can bring settings of its own.</summary>
    Choice,

    /// <summary>A value: text or a whole number.</summary>
    Simple,

    /// <summary>A list of values.</summary>
    SimpleCollection,

    /// <summary>Several options at once.</summary>
    ChoiceCollection,

    /// <summary>A repeating group: each item has the same settings inside.</summary>
    GroupCollection,

    /// <summary>One group of settings.</summary>
    Group,

    /// <summary>Something Tenantry doesn't edit (shown and kept).</summary>
    Other,
}

/// <summary>An option of a choice setting, and the settings choosing it brings.</summary>
public sealed record CatalogOption(string ItemId, string Name, string? Description, IReadOnlyList<string> Children);

/// <summary>
/// A settings catalog definition (Graph's deviceManagementConfigurationSettingDefinition): what it
/// is called and does, how it's set, its options, limits and default, and the settings inside it.
/// </summary>
/// <param name="ValueType">"string" or "integer" for values.</param>
/// <param name="ChildIds">The settings inside a group (or a repeating group's items).</param>
public sealed record CatalogDefinition(string Id, CatalogKind Kind, string Name, string? Description, string? CategoryId, IReadOnlyList<CatalogOption> Options,
    string? DefaultOptionId, string ValueType, long? Min, long? Max, string? DefaultValue, IReadOnlyList<string> ChildIds, int? MaxCount, string? Keywords = null)
{
    public bool IsInteger => ValueType == "integer";

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    public static CatalogDefinition From(JsonElement d)
    {
        var type = Str(d, "@odata.type") ?? "";
        var kind = type switch
        {
            _ when type.EndsWith("ChoiceSettingCollectionDefinition", StringComparison.Ordinal) => CatalogKind.ChoiceCollection,
            _ when type.EndsWith("ChoiceSettingDefinition", StringComparison.Ordinal) => CatalogKind.Choice,
            _ when type.EndsWith("SimpleSettingCollectionDefinition", StringComparison.Ordinal) => CatalogKind.SimpleCollection,
            _ when type.EndsWith("SimpleSettingDefinition", StringComparison.Ordinal) => CatalogKind.Simple,
            _ when type.EndsWith("SettingGroupCollectionDefinition", StringComparison.Ordinal) => CatalogKind.GroupCollection,
            _ when type.EndsWith("SettingGroupDefinition", StringComparison.Ordinal) => CatalogKind.Group,
            _ => CatalogKind.Other,
        };
        var options = d.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Select(x => new CatalogOption(Str(x, "itemId") ?? "", Str(x, "displayName") ?? Str(x, "name") ?? Str(x, "itemId") ?? "", Str(x, "description"),
                x.TryGetProperty("dependedOnBy", out var dep) && dep.ValueKind == JsonValueKind.Array
                    ? [.. dep.EnumerateArray().Select(c => Str(c, "dependedOnBy")).OfType<string>()] : [])).ToArray()
            : [];
        var valueDef = d.TryGetProperty("valueDefinition", out var vd) && vd.ValueKind == JsonValueKind.Object ? vd : (JsonElement?)null;
        var valueType = valueDef is { } v && (Str(v, "@odata.type") ?? "").Contains("Integer", StringComparison.Ordinal) ? "integer" : "string";
        string? defaultValue = d.TryGetProperty("defaultValue", out var dv) && dv.ValueKind == JsonValueKind.Object && dv.TryGetProperty("value", out var dvv)
            ? dvv.ValueKind == JsonValueKind.String ? dvv.GetString() : dvv.ToString() : null;
        IReadOnlyList<string> children = d.TryGetProperty("childIds", out var c) && c.ValueKind == JsonValueKind.Array ? [.. c.EnumerateArray().Select(x => x.GetString()).OfType<string>()] : [];
        return new CatalogDefinition(Str(d, "id") ?? "", kind, Str(d, "displayName") ?? Str(d, "name") ?? Str(d, "id") ?? "",
            (Str(d, "description") ?? Str(d, "helpText")) is { Length: > 0 } desc ? desc.Trim() : null, Str(d, "categoryId"), options, Str(d, "defaultOptionId"), valueType,
            valueDef is { } m ? Long(m, "minimumValue") ?? Long(m, "minimumLength") : null, valueDef is { } x2 ? Long(x2, "maximumValue") ?? Long(x2, "maximumLength") : null,
            defaultValue, children, (int?)Long(d, "maximumCount"),
            d.TryGetProperty("keywords", out var k) && k.ValueKind == JsonValueKind.Array ? string.Join(' ', k.EnumerateArray().Select(x => x.GetString())) : null);
    }
}

/// <summary>A configuration profile from the settings catalog (or an endpoint security template, which uses it too).</summary>
/// <param name="Header">What a PUT of the whole profile needs besides its settings (name, platforms, technologies, template).</param>
public sealed record ConfigurationProfile(string Id, string Name, string? Description, string Platforms, string Technologies, string? TemplateFamily, string? TemplateName,
    int SettingCount, DateTimeOffset? Modified, IReadOnlyList<IntuneAssignment> Assignments, JsonElement Header)
{
    public string PlatformText => SettingsCatalog.PlatformText(Platforms);
    public bool IsTemplate => TemplateFamily is { Length: > 0 } f && f != "none";
}

/// <summary>An older template profile (Graph's deviceConfigurations): one of many types, each with its own properties.</summary>
public sealed record TemplateProfile(string Id, string Name, string? Description, string ODataType, DateTimeOffset? Modified, IReadOnlyList<IntuneAssignment> Assignments, JsonElement Raw)
{
    /// <summary>"windows10GeneralConfiguration" → "Windows 10 General Configuration".</summary>
    public string TypeText => SettingsCatalog.Words(ODataType.Replace("#microsoft.graph.", "", StringComparison.Ordinal));
}

/// <summary>Words and Graph shapes for the settings catalog.</summary>
public static class SettingsCatalog
{
    public const string Beta = "https://graph.microsoft.com/beta/deviceManagement/";

    public static string PlatformText(string platforms) => string.Join(", ", platforms.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(p => p switch
    {
        "windows10" => "Windows",
        "macOS" => "macOS",
        "iOS" => "iOS/iPadOS",
        "android" => "Android",
        "androidEnterprise" => "Android Enterprise",
        "linux" => "Linux",
        "windows10X" => "Windows 10X",
        _ => p,
    }));

    /// <summary>The platforms a new profile can be for, with Graph's technologies.</summary>
    public static IReadOnlyList<(string Platforms, string Technologies, string Title)> NewProfilePlatforms { get; } =
    [
        ("windows10", "mdm", "Windows 10 and Later"),
        ("macOS", "mdm,appleRemoteManagement", "macOS"),
        ("iOS", "mdm,appleRemoteManagement", "iOS/iPadOS"),
        ("linux", "linuxMdm", "Linux"),
        ("androidEnterprise", "mdm", "Android Enterprise"),
    ];

    /// <summary>"windows10EndpointProtectionConfiguration" → "Windows 10 Endpoint Protection Configuration".</summary>
    public static string Words(string name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var split = i > 0 && (char.IsUpper(c) && !char.IsUpper(name[i - 1]) || char.IsDigit(c) && !char.IsDigit(name[i - 1]) && name[i - 1] != 'v');
            if (split && current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
            current.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        if (current.Length > 0)
            words.Add(current.ToString());
        return string.Join(' ', words.Select(w => w.Length > 1 && w.All(char.IsUpper) ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    public static string TemplateText(string? family) => family switch
    {
        null or "" or "none" => "Settings Catalog",
        "endpointSecurityAntivirus" => "Endpoint Security: Antivirus",
        "endpointSecurityDiskEncryption" => "Endpoint Security: Disk Encryption",
        "endpointSecurityFirewall" => "Endpoint Security: Firewall",
        "endpointSecurityEndpointDetectionAndResponse" => "Endpoint Security: Detection and Response",
        "endpointSecurityAttackSurfaceReduction" => "Endpoint Security: Attack Surface Reduction",
        "endpointSecurityAccountProtection" => "Endpoint Security: Account Protection",
        "endpointSecurityApplicationControl" => "Endpoint Security: App Control",
        "baseline" => "Security Baseline",
        _ => Words(family),
    };

    const string Ns = "#microsoft.graph.deviceManagementConfiguration";

    /// <summary>The settingInstance's kind, from its Graph type.</summary>
    public static CatalogKind KindOf(JsonObject instance) => ((string?)instance["@odata.type"] ?? "") switch
    {
        var t when t.EndsWith("ChoiceSettingCollectionInstance", StringComparison.Ordinal) => CatalogKind.ChoiceCollection,
        var t when t.EndsWith("ChoiceSettingInstance", StringComparison.Ordinal) => CatalogKind.Choice,
        var t when t.EndsWith("SimpleSettingCollectionInstance", StringComparison.Ordinal) => CatalogKind.SimpleCollection,
        var t when t.EndsWith("SimpleSettingInstance", StringComparison.Ordinal) => CatalogKind.Simple,
        var t when t.EndsWith("GroupSettingCollectionInstance", StringComparison.Ordinal) => CatalogKind.GroupCollection,
        var t when t.EndsWith("GroupSettingInstance", StringComparison.Ordinal) => CatalogKind.Group,
        _ => CatalogKind.Other,
    };

    public static JsonObject SimpleValue(CatalogDefinition def, string? text) => def.IsInteger
        ? new JsonObject { ["@odata.type"] = Ns + "IntegerSettingValue", ["value"] = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : def.Min ?? 0 }
        : new JsonObject { ["@odata.type"] = Ns + "StringSettingValue", ["value"] = text ?? "" };

    /// <summary>
    /// A new setting as Intune would make it with its defaults: a choice's default option (or its first)
    /// with that option's settings, a value's default, an empty list, one group item. Definitions not in
    /// <paramref name="defs"/> are skipped (the caller reads them first).
    /// </summary>
    public static JsonObject Default(CatalogDefinition def, IReadOnlyDictionary<string, CatalogDefinition> defs)
    {
        JsonArray Children(IEnumerable<string> ids) => new([.. ids.Select(defs.GetValueOrDefault).OfType<CatalogDefinition>().Select(d => (JsonNode)Default(d, defs))]);
        var instance = new JsonObject { ["settingDefinitionId"] = def.Id };
        switch (def.Kind)
        {
            case CatalogKind.Choice:
                var option = def.Options.FirstOrDefault(o => o.ItemId == def.DefaultOptionId) ?? def.Options.FirstOrDefault();
                instance["@odata.type"] = Ns + "ChoiceSettingInstance";
                instance["choiceSettingValue"] = new JsonObject { ["@odata.type"] = Ns + "ChoiceSettingValue", ["value"] = option?.ItemId ?? "", ["children"] = Children(option?.Children ?? []) };
                break;
            case CatalogKind.Simple:
                instance["@odata.type"] = Ns + "SimpleSettingInstance";
                instance["simpleSettingValue"] = SimpleValue(def, def.DefaultValue);
                break;
            case CatalogKind.SimpleCollection:
                instance["@odata.type"] = Ns + "SimpleSettingCollectionInstance";
                instance["simpleSettingCollectionValue"] = new JsonArray();
                break;
            case CatalogKind.ChoiceCollection:
                instance["@odata.type"] = Ns + "ChoiceSettingCollectionInstance";
                instance["choiceSettingCollectionValue"] = new JsonArray();
                break;
            case CatalogKind.GroupCollection:
                instance["@odata.type"] = Ns + "GroupSettingCollectionInstance";
                instance["groupSettingCollectionValue"] = new JsonArray(GroupItem(def, defs));
                break;
            case CatalogKind.Group:
                instance["@odata.type"] = Ns + "GroupSettingInstance";
                instance["groupSettingValue"] = new JsonObject { ["children"] = Children(def.ChildIds) };
                break;
        }
        return instance;
    }

    /// <summary>One item of a repeating group, its settings at their defaults.</summary>
    public static JsonObject GroupItem(CatalogDefinition def, IReadOnlyDictionary<string, CatalogDefinition> defs) =>
        new() { ["children"] = new JsonArray([.. def.ChildIds.Select(defs.GetValueOrDefault).OfType<CatalogDefinition>().Select(d => (JsonNode)Default(d, defs))]) };

    /// <summary>Every definition a new setting needs, besides itself: its options' settings and its groups' settings, all the way down.</summary>
    public static IEnumerable<string> Needs(CatalogDefinition def) => def.Options.SelectMany(o => o.Children).Concat(def.ChildIds);

    /// <summary>A setting's value in words ("XTS-AES 256-bit", "3 entries", "Yes"), from its definition.</summary>
    public static string Words(JsonObject instance, IReadOnlyDictionary<string, CatalogDefinition> defs)
    {
        var def = defs.GetValueOrDefault((string?)instance["settingDefinitionId"] ?? "");
        string Option(string? id) => def?.Options.FirstOrDefault(o => o.ItemId == id)?.Name ?? id ?? "";
        return KindOf(instance) switch
        {
            // A choice's inner settings say what they're set to, so a change inside shows.
            CatalogKind.Choice => instance["choiceSettingValue"]?["children"] is JsonArray { Count: > 0 } inner
                ? $"{Option((string?)instance["choiceSettingValue"]?["value"])} ({string.Join("; ", inner.OfType<JsonObject>().Select(c => $"{defs.GetValueOrDefault((string?)c["settingDefinitionId"] ?? "")?.Name ?? (string?)c["settingDefinitionId"]}: {Words(c, defs)}"))})"
                : Option((string?)instance["choiceSettingValue"]?["value"]),
            CatalogKind.Simple => instance["simpleSettingValue"]?["value"]?.ToString() ?? "",
            CatalogKind.SimpleCollection => instance["simpleSettingCollectionValue"] is JsonArray { Count: > 0 } a
                ? a.Count <= 3 ? string.Join(", ", a.Select(x => x?["value"]?.ToString())) : $"{a.Count} entries" : "None",
            CatalogKind.ChoiceCollection => instance["choiceSettingCollectionValue"] is JsonArray { Count: > 0 } c ? string.Join(", ", c.Select(x => Option((string?)x?["value"]))) : "None",
            CatalogKind.GroupCollection => instance["groupSettingCollectionValue"] is JsonArray g ? $"{g.Count} {(g.Count == 1 ? "item" : "items")}" : "None",
            _ => "Set",
        };
    }

    /// <summary>What changed between two settings lists, by the root settings' names, for Pending Changes.</summary>
    public static (string Before, string After) Describe(JsonArray old, JsonArray @new, IReadOnlyDictionary<string, CatalogDefinition> defs)
    {
        static string Id(JsonNode? n) => (string?)n?["settingDefinitionId"] ?? "";
        var before = old.ToDictionary(Id, n => n!, StringComparer.Ordinal);
        var after = @new.ToDictionary(Id, n => n!, StringComparer.Ordinal);
        string Name(string id) => defs.GetValueOrDefault(id)?.Name ?? id;
        var changed = after.Keys.Where(k => before.TryGetValue(k, out var b) && !JsonNode.DeepEquals(b, after[k])).ToArray();
        var added = after.Keys.Where(k => !before.ContainsKey(k)).ToArray();
        var removed = before.Keys.Where(k => !after.ContainsKey(k)).ToArray();
        var b2 = string.Join("; ", changed.Concat(removed).Select(k => $"{Name(k)}: {Words(before[k].AsObject(), defs)}"));
        var a2 = string.Join("; ", new[]
        {
            changed.Length == 0 ? null : string.Join("; ", changed.Select(k => $"{Name(k)}: {Words(after[k].AsObject(), defs)}")),
            added.Length == 0 ? null : "added " + string.Join("; ", added.Select(k => $"{Name(k)}: {Words(after[k].AsObject(), defs)}")),
            removed.Length == 0 ? null : "removed " + string.Join(", ", removed.Select(Name)),
        }.OfType<string>());
        return (b2.Length == 0 ? $"{old.Count} settings" : b2, a2.Length == 0 ? $"{@new.Count} settings" : a2);
    }
}

/// <summary>Configuration profiles (13b): the settings catalog, its definitions, and older template profiles.</summary>
public interface IConfigurationProfileRecords
{
    Task<IReadOnlyList<ConfigurationProfile>> GetConfigurationProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>A profile's settings (each a settingInstance) and the definitions they use.</summary>
    Task<(JsonArray Settings, IReadOnlyDictionary<string, CatalogDefinition> Definitions)> GetProfileSettingsAsync(string profileId, CancellationToken cancellationToken = default);

    /// <summary>Settings the catalog offers for a platform and technologies that match the words (Microsoft's search).</summary>
    Task<IReadOnlyList<CatalogDefinition>> SearchSettingsAsync(string platforms, string technologies, string words, CancellationToken cancellationToken = default);

    /// <summary>Definitions by ID (for a new setting's inner settings).</summary>
    Task<IReadOnlyList<CatalogDefinition>> GetDefinitionsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TemplateProfile>> GetTemplateProfilesAsync(CancellationToken cancellationToken = default);
}
