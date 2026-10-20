using System.Text.Json;

namespace Tenantry.Core.Licensing;

/// <summary>
/// The names admins know licenses by ("Microsoft 365 Business Premium"), where Graph only gives
/// an ID and a code ("SPB"). From Microsoft's published product-name table, embedded at build
/// time; refresh it with tools/update-license-names.py.
/// </summary>
public static class LicenseNames
{
    sealed record Table(Dictionary<string, string> ById, Dictionary<string, string> ByCode);

    static readonly Lazy<Table> Data = new(Load);

    static Table Load()
    {
        using var stream = typeof(LicenseNames).Assembly.GetManifestResourceStream("Tenantry.Core.Licensing.license-names.json");
        if (stream is null)
            return new Table([], []);
        using var doc = JsonDocument.Parse(stream);
        static Dictionary<string, string> Read(JsonElement e) =>
            e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
        return new Table(Read(doc.RootElement.GetProperty("byId")), Read(doc.RootElement.GetProperty("byCode")));
    }

    /// <summary>The product name for a license, falling back to its code, then its ID.</summary>
    public static string For(string skuId, string? code)
    {
        var table = Data.Value;
        if (table.ById.TryGetValue(skuId, out var name) && name.Length > 0)
            return name;
        if (code is { Length: > 0 } && table.ByCode.TryGetValue(code, out name) && name.Length > 0)
            return name;
        return code is { Length: > 0 } ? code : skuId;
    }

    public static int Count => Data.Value.ById.Count;
}
