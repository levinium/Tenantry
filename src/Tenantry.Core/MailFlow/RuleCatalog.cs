using System.Text.Json;

namespace Tenantry.Core.MailFlow;

/// <summary>Where a part sits in a rule.</summary>
public enum RulePartKind
{
    Condition,
    Exception,
    Action,
}

/// <summary>A fixed choice for an enum value: what Exchange takes, and how it reads.</summary>
public sealed record RuleChoice(string Value, string Label);

/// <summary>A companion setting a part needs alongside its main value (a header's name beside its words).</summary>
public sealed record RuleExtra(string Parameter, string? ExceptParameter, string ValueType, IReadOnlyList<RuleChoice> Choices, bool Required, string? Default);

/// <summary>
/// One thing a rule can check or do, from the catalog (Microsoft's documented conditions and
/// actions). A condition is also an exception, through its "ExceptIf" parameter.
/// </summary>
/// <param name="Group">The admin center's heading ("The sender", "Any attachment").</param>
/// <param name="Label">How the admin center words it ("The sender is").</param>
/// <param name="Fixed">Parameters always sent with it (Append or Prepend for a disclaimer).</param>
/// <param name="Availability">"exo", "legacy", or why it can't be used in Exchange Online.</param>
public sealed record RulePartDef(
    RulePartKind Kind,
    string Group,
    string Label,
    string Parameter,
    string? ExceptParameter,
    string ValueType,
    IReadOnlyList<RuleChoice> Choices,
    IReadOnlyList<RuleExtra> Extras,
    IReadOnlyDictionary<string, string> Fixed,
    string Availability,
    bool Terminal,
    string? Notes,
    bool Advanced = false)
{
    /// <summary>Usable in Exchange Online (current or legacy).</summary>
    public bool IsAvailable => Availability is "exo" or "legacy";

    /// <summary>Why it can't be used here, from the catalog's availability notes; null when it can.</summary>
    public string? UnavailableReason => Availability switch
    {
        "exo" or "legacy" => null,
        "reserved" => "Reserved by Microsoft for its own use in Exchange Online",
        "dlpOnPremOnly" => "Only works with on-premises Exchange data loss prevention",
        "edgeOnly" => "Only works on an on-premises Edge Transport server",
        "deprecated" => "Retired by Microsoft",
        _ => "Not available in Exchange Online",
    };

    /// <summary>The parameter for this part where it's used: the ExceptIf one for exceptions.</summary>
    public string ParameterFor(RulePartKind kind) => kind == RulePartKind.Exception ? ExceptParameter ?? Parameter : Parameter;

    public static string ExtraParameterFor(RuleExtra extra, RulePartKind kind) => kind == RulePartKind.Exception ? extra.ExceptParameter ?? extra.Parameter : extra.Parameter;
}

/// <summary>A rule-level setting (mode, dates, stop processing…).</summary>
public sealed record RulePropertyDef(string Name, string? Label, string Parameter, string ValueType, IReadOnlyList<RuleChoice> Choices, string? Default,
    IReadOnlyList<string> Cmdlets, string Availability, string? Notes);

/// <summary>Every condition, exception and action Exchange Online mail flow rules support, loaded once from the embedded catalog.</summary>
public sealed class RuleCatalog
{
    static readonly Lazy<RuleCatalog> Loaded = new(Load);

    public static RuleCatalog Instance => Loaded.Value;

    public required IReadOnlyList<RulePartDef> Conditions { get; init; }
    public required IReadOnlyList<RulePartDef> Actions { get; init; }
    public required IReadOnlyList<RulePropertyDef> Properties { get; init; }

    /// <summary>What each kind of value means, from the catalog ("words": one or more words or phrases…).</summary>
    public required IReadOnlyDictionary<string, string> ValueTypes { get; init; }

    public IEnumerable<RulePartDef> For(RulePartKind kind) => kind == RulePartKind.Action ? Actions : Conditions.Where(c => kind == RulePartKind.Condition || c.ExceptParameter is not null);

    static RuleCatalog Load()
    {
        using var stream = typeof(RuleCatalog).Assembly.GetManifestResourceStream("Tenantry.Core.MailFlow.transport-rule-catalog.json")
                           ?? throw new InvalidOperationException("The mail flow rule catalog isn't embedded.");
        var root = JsonDocument.Parse(stream).RootElement;
        return new RuleCatalog
        {
            Conditions = [.. root.GetProperty("conditions").EnumerateArray().Select(e => Part(e, RulePartKind.Condition))],
            Actions = [.. root.GetProperty("actions").EnumerateArray().Select(e => Part(e, RulePartKind.Action))],
            Properties = [.. root.GetProperty("properties").EnumerateArray().Select(Property)],
            ValueTypes = root.GetProperty("valueTypes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal),
        };
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static IReadOnlyList<RuleChoice> Choices(JsonElement e) =>
        e.TryGetProperty("enumValues", out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String
                ? new RuleChoice(c.GetString()!, c.GetString()!)
                : new RuleChoice(Str(c, "value") ?? "", Str(c, "eac") ?? Str(c, "label") ?? Str(c, "value") ?? ""))]
            : [];

    static RulePartDef Part(JsonElement e, RulePartKind kind) => new(
        kind,
        Str(e, "group") ?? "",
        Str(e, "label") ?? Str(e, "eac") ?? Str(e, "parameter") ?? "",
        Str(e, "parameter") ?? "",
        Str(e, "exceptParameter"),
        Str(e, "valueType") ?? "string",
        Choices(e),
        e.TryGetProperty("extraParameters", out var extras) && extras.ValueKind == JsonValueKind.Array
            ? [.. extras.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => new RuleExtra(
                Str(x, "parameter") ?? "", Str(x, "exceptParameter"), Str(x, "valueType") ?? "string", Choices(x),
                x.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True,
                x.TryGetProperty("default", out var d) ? d.ValueKind == JsonValueKind.String ? d.GetString() : d.ValueKind == JsonValueKind.Null ? null : d.GetRawText() : null))]
            : [],
        e.TryGetProperty("fixedParameters", out var fixedParameters) && fixedParameters.ValueKind == JsonValueKind.Object
            ? fixedParameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Str(e, "availability") ?? "exo",
        e.TryGetProperty("terminal", out var t) && t.ValueKind == JsonValueKind.True,
        Str(e, "notes"),
        // Not offered in the admin center, or superseded: listed with the rest, marked Advanced.
        !(e.TryGetProperty("eacVisible", out var visible) && visible.ValueKind == JsonValueKind.True) || Str(e, "availability") == "legacy");

    static RulePropertyDef Property(JsonElement e) => new(
        Str(e, "name") ?? "",
        Str(e, "eac"),
        Str(e, "parameter") ?? Str(e, "name") ?? "",
        Str(e, "valueType") ?? "string",
        Choices(e),
        e.TryGetProperty("default", out var d) ? d.ValueKind == JsonValueKind.String ? d.GetString() : d.ValueKind == JsonValueKind.Null ? null : d.GetRawText() : null,
        e.TryGetProperty("cmdlets", out var c) && c.ValueKind == JsonValueKind.Array ? [.. c.EnumerateArray().Select(x => x.GetString() ?? "")] : [],
        Str(e, "availability") ?? "exo",
        Str(e, "notes"));
}
