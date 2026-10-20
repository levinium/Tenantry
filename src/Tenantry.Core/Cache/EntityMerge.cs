using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tenantry.Core.Cache;

/// <summary>The one merge rule every store uses, so memory and disk behave identically.</summary>
public static class EntityMerge
{
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>
    /// Merges <paramref name="incoming"/> over <paramref name="existing"/> (top-level properties
    /// replace; absent ones are kept) and returns the JSON to store, or null when nothing changed.
    /// </summary>
    public static string? Merge(string? existing, JsonObject incoming)
    {
        var merged = existing is null ? new JsonObject() : JsonNode.Parse(existing)!.AsObject();
        foreach (var (name, value) in incoming)
        {
            // OData annotations ("@odata.type") describe the response, not the object.
            if (name.StartsWith('@'))
                continue;
            merged[name] = value?.DeepClone();
        }
        var json = merged.ToJsonString(Compact);
        return json == existing ? null : json;
    }
}
