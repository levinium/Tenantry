using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Directory;

namespace Tenantry.Core.MailFlow;

/// <summary>One line of a rule: a condition, exception or action from the catalog, with its value and companion values.</summary>
public sealed class RuleLine(RulePartDef def, RulePartKind kind)
{
    public RulePartDef Def { get; } = def;
    public RulePartKind Kind { get; } = kind;

    /// <summary>The main value: a string, a list of strings, a number or true; null until filled in.</summary>
    public JsonNode? Value { get; set; }

    /// <summary>Companion values, by the extra's base parameter name.</summary>
    public Dictionary<string, JsonNode?> Extras { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RuleLine Clone()
    {
        var copy = new RuleLine(Def, Kind) { Value = Value?.DeepClone() };
        foreach (var (k, v) in Extras)
            copy.Extras[k] = v?.DeepClone();
        return copy;
    }
}

/// <summary>A rule as the editor holds it: its lines, its settings, and anything Tenantry doesn't know (kept as it is).</summary>
public sealed class RuleDraft
{
    /// <summary>Exchange's identity; null for a new rule.</summary>
    public string? Identity { get; set; }

    public string Name { get; set; } = "";
    public RuleMode Mode { get; set; } = RuleMode.Test;
    public bool Enabled { get; set; } = true;

    public List<RuleLine> Conditions { get; } = [];
    public List<RuleLine> Exceptions { get; } = [];
    public List<RuleLine> Actions { get; } = [];

    /// <summary>Rule-level settings by parameter (comments, dates, stop processing, sender address location…).</summary>
    public Dictionary<string, JsonNode?> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Settings Tenantry doesn't know (a newer Microsoft option): shown read-only and never sent back changed.</summary>
    public Dictionary<string, JsonElement> Kept { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<RuleLine> AllLines => Conditions.Concat(Exceptions).Concat(Actions);

    public List<RuleLine> LinesFor(RulePartKind kind) => kind switch
    {
        RulePartKind.Condition => Conditions,
        RulePartKind.Exception => Exceptions,
        _ => Actions,
    };

    public RuleDraft Clone()
    {
        var copy = new RuleDraft { Identity = Identity, Name = Name, Mode = Mode, Enabled = Enabled };
        copy.Conditions.AddRange(Conditions.Select(l => l.Clone()));
        copy.Exceptions.AddRange(Exceptions.Select(l => l.Clone()));
        copy.Actions.AddRange(Actions.Select(l => l.Clone()));
        foreach (var (k, v) in Settings)
            copy.Settings[k] = v?.DeepClone();
        foreach (var (k, v) in Kept)
            copy.Kept[k] = v;
        return copy;
    }
}

/// <summary>
/// Reads a rule from Exchange (Get-TransportRule's output) into a <see cref="RuleDraft"/>, and turns a
/// draft back into New-/Set-TransportRule parameters: everything for a new rule, only what changed
/// (cleared parameters as null) for an edit.
/// </summary>
public static partial class RuleCodec
{
    /// <summary>Get-TransportRule fields that describe the rule rather than set it.</summary>
    static readonly HashSet<string> Metadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "Identity", "Guid", "Name", "Priority", "State", "Mode", "Description", "DistinguishedName", "Id", "ImmutableId", "IsValid", "ObjectState",
        "OrganizationId", "RuleVersion", "Size", "WhenChanged", "WhenChangedUTC", "WhenCreated", "WhenCreatedUTC", "CreatedBy", "LastModifiedBy",
        "ManuallyModified", "ExchangeVersion", "IsRuleConfigurationSupported", "RuleConfigurationUnsupportedReason", "DlpPolicy", "DlpPolicyId",
        "RegexSize", "Conditions", "Exceptions", "Actions", "OriginatingServer", "PSComputerName", "RunspaceId", "PSShowComputerName",
    };

    static readonly HashSet<string> NotSettings = new(StringComparer.OrdinalIgnoreCase) { "Name", "Identity", "Priority", "Mode", "Enabled", "State" };

    /// <summary>Whether a value read from Exchange means "set".</summary>
    public static bool IsSet(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.String => value.GetString()!.Length > 0,
        _ => true,
    };

    /// <summary>A value as the draft holds it. Sizes ("10 MB (10,485,760 bytes)") become their byte count.</summary>
    public static JsonNode? Normalize(JsonElement value, string valueType) => valueType switch
    {
        "size" => MailDetails.ParseBytes(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()) is { } bytes ? JsonValue.Create(bytes) : JsonNode.Parse(value.GetRawText()),
        _ => JsonNode.Parse(value.GetRawText()),
    };

    public static RuleDraft Read(JsonElement rule, RuleCatalog? catalog = null)
    {
        catalog ??= RuleCatalog.Instance;
        var draft = new RuleDraft
        {
            Identity = Text(rule, "Guid") ?? Text(rule, "Identity"),
            Name = Text(rule, "Name") ?? "",
            Mode = MailFlowRule.ParseMode(Text(rule, "Mode")),
            Enabled = !string.Equals(Text(rule, "State"), "Disabled", StringComparison.OrdinalIgnoreCase),
        };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in new[] { RulePartKind.Condition, RulePartKind.Exception, RulePartKind.Action })
            foreach (var def in catalog.For(kind).Where(d => d.Parameter.Length > 0))
            {
                var parameter = def.ParameterFor(kind);
                if (used.Contains(parameter) || !rule.TryGetProperty(parameter, out var value) || !IsSet(value))
                    continue;
                // Parts sharing a parameter (append or prepend a disclaimer) are told apart by their fixed values.
                if (def.Fixed.Any(f => !rule.TryGetProperty(f.Key, out var fv) || !string.Equals(fv.ValueKind == JsonValueKind.String ? fv.GetString() : fv.GetRawText(), f.Value, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var line = new RuleLine(def, kind) { Value = def.ValueType == "none" ? JsonValue.Create(true) : Normalize(value, def.ValueType) };
                used.Add(parameter);
                foreach (var f in def.Fixed.Keys)
                    used.Add(f);
                foreach (var extra in def.Extras)
                {
                    var extraParameter = RulePartDef.ExtraParameterFor(extra, kind);
                    if (rule.TryGetProperty(extraParameter, out var ev) && IsSet(ev))
                        line.Extras[extra.Parameter] = Normalize(ev, extra.ValueType);
                    used.Add(extraParameter);
                }
                draft.LinesFor(kind).Add(line);
            }

        foreach (var property in catalog.Properties.Where(p => !NotSettings.Contains(p.Parameter)))
            if (rule.TryGetProperty(property.Parameter, out var value) && IsSet(value))
            {
                draft.Settings[property.Parameter] = Normalize(value, property.ValueType);
                used.Add(property.Parameter);
            }

        foreach (var p in rule.EnumerateObject())
            if (!p.Name.Contains('@') && !Metadata.Contains(p.Name) && !used.Contains(p.Name) && !NotSettings.Contains(p.Name) && IsSet(p.Value))
                draft.Kept[p.Name] = p.Value.Clone();
        return draft;
    }

    /// <summary>Every parameter the draft sets (New-TransportRule), without Name, Mode or Enabled.</summary>
    public static Dictionary<string, JsonNode?> Parameters(RuleDraft draft)
    {
        var parameters = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in draft.AllLines)
            foreach (var (k, v) in LineParameters(line))
                parameters[k] = v;
        foreach (var (k, v) in draft.Settings)
            if (v is not null)
                parameters[k] = v.DeepClone();
        return parameters;
    }

    /// <summary>One line's parameters: its main value, its fixed values and its companions.</summary>
    public static Dictionary<string, JsonNode?> LineParameters(RuleLine line)
    {
        var parameters = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            [line.Def.ParameterFor(line.Kind)] = line.Def.ValueType == "none" ? JsonValue.Create(true) : Send(line.Value, line.Def.ValueType),
        };
        foreach (var (k, v) in line.Def.Fixed)
            parameters[k] = JsonValue.Create(v);
        foreach (var extra in line.Def.Extras)
            if (line.Extras.TryGetValue(extra.Parameter, out var value) && value is not null)
                parameters[RulePartDef.ExtraParameterFor(extra, line.Kind)] = Send(value, extra.ValueType);
        return parameters;
    }

    /// <summary>A value as Exchange takes it: a size as its byte count in text (Exchange reads plain digits as bytes).</summary>
    static JsonNode? Send(JsonNode? value, string valueType) =>
        valueType == "size" && value is JsonValue v && v.TryGetValue<long>(out var bytes)
            ? JsonValue.Create(bytes.ToString(CultureInfo.InvariantCulture))
            : value?.DeepClone();

    /// <summary>
    /// The rule as Get-TransportRule would show it: its parameters, name, mode, state and the
    /// settings Tenantry keeps untouched. Reading it back gives the same draft.
    /// </summary>
    public static JsonObject ToExchange(RuleDraft draft)
    {
        var rule = new JsonObject();
        foreach (var (k, v) in draft.Kept)
            rule[k] = JsonNode.Parse(v.GetRawText());
        foreach (var (k, v) in Parameters(draft))
            rule[k] = v;
        rule["Name"] = draft.Name;
        rule["Mode"] = MailFlowRule.ExchangeMode(draft.Mode);
        rule["State"] = draft.Enabled ? "Enabled" : "Disabled";
        if (draft.Identity is { } identity)
            rule["Guid"] = identity;
        return rule;
    }

    /// <summary>A draft with Set-TransportRule <paramref name="changes"/> applied (null or false clears).</summary>
    public static RuleDraft Apply(RuleDraft draft, IEnumerable<KeyValuePair<string, JsonNode?>> changes)
    {
        var rule = ToExchange(draft);
        foreach (var (k, v) in changes)
        {
            if (v is null || v is JsonValue flag && flag.GetValueKind() == JsonValueKind.False)
                rule.Remove(k);
            else
                rule[k] = v.DeepClone();
        }
        using var document = JsonDocument.Parse(rule.ToJsonString());
        return Read(document.RootElement.Clone());
    }

    /// <summary>
    /// What Set-TransportRule needs to turn <paramref name="original"/> into <paramref name="edited"/>:
    /// changed parameters with their new values, removed ones as null. Kept (unknown) settings are never sent.
    /// </summary>
    public static Dictionary<string, JsonNode?> Changes(RuleDraft original, RuleDraft edited)
    {
        var before = Parameters(original);
        var after = Parameters(edited);
        var changes = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in after)
            if (!before.TryGetValue(k, out var old) || !JsonNode.DeepEquals(old, v))
                changes[k] = v?.DeepClone();
        // Switches (stop processing, "has no classification") clear with false; everything else with null.
        foreach (var (k, old) in before)
            if (!after.ContainsKey(k))
                changes[k] = old is JsonValue v && v.GetValueKind() == JsonValueKind.True ? JsonValue.Create(false) : null;
        // Exchange wants a line's values together (a header's value needs its name), so a changed line is sent whole.
        foreach (var line in edited.AllLines)
        {
            var parts = LineParameters(line);
            if (parts.Keys.Any(changes.ContainsKey))
                foreach (var (k, v) in parts)
                    changes[k] = v?.DeepClone();
        }
        if (!string.Equals(original.Name, edited.Name, StringComparison.Ordinal))
            changes["Name"] = JsonValue.Create(edited.Name);
        if (original.Mode != edited.Mode)
            changes["Mode"] = JsonValue.Create(MailFlowRule.ExchangeMode(edited.Mode));
        return changes;
    }

    /// <summary>The parameters for New-TransportRule: everything the draft sets, plus its name, mode and on/off.</summary>
    public static JsonObject NewRuleParameters(RuleDraft draft)
    {
        var parameters = new JsonObject();
        foreach (var (k, v) in Parameters(draft))
            parameters[k] = v;
        parameters["Mode"] = MailFlowRule.ExchangeMode(draft.Mode);
        if (!draft.Enabled)
            parameters["Enabled"] = false;
        return parameters;
    }

    /// <summary>
    /// Which of <paramref name="expected"/> a rule read back from Exchange doesn't show. Lenient where Exchange
    /// rewrites what it was given (case, a single value as a list, addresses resolved to names): cleared ones must
    /// be unset, set ones must be set with the same number of values, and single values must match.
    /// </summary>
    public static List<string> Mismatches(JsonElement rule, IEnumerable<KeyValuePair<string, JsonNode?>> expected)
    {
        var wrong = new List<string>();
        foreach (var (name, value) in expected)
        {
            var has = rule.TryGetProperty(name, out var actual) && IsSet(actual);
            var cleared = value is null || value is JsonValue v && v.GetValueKind() == JsonValueKind.False;
            if (cleared ? has : !has)
                wrong.Add(name);
            else if (!cleared && !SameShape(actual, value!))
                wrong.Add(name);
        }
        return wrong;
    }

    static bool SameShape(JsonElement actual, JsonNode expected)
    {
        if (expected is JsonArray list)
            return actual.ValueKind == JsonValueKind.Array ? actual.GetArrayLength() == list.Count : list.Count == 1;
        var text = actual.ValueKind switch
        {
            JsonValueKind.String => actual.GetString(),
            JsonValueKind.Array when actual.GetArrayLength() == 1 => actual[0].ToString(),
            _ => actual.GetRawText(),
        };
        var want = expected.GetValueKind() == JsonValueKind.String ? expected.GetValue<string>() : expected.ToJsonString();
        // Dates go out as a day's start ("2026-12-01T00:00:00-05:00") and come back as Exchange writes them.
        if (SameDate(want, text))
            return true;
        // Sizes go out as bytes ("10485760") and come back as "10 MB (10,485,760 bytes)".
        return string.Equals(text, want, StringComparison.OrdinalIgnoreCase)
               || want.Length > 0 && want.All(char.IsAsciiDigit) && MailDetails.ParseBytes(text) is { } bytes
                  && bytes.ToString(CultureInfo.InvariantCulture) == want;
    }

    /// <summary>
    /// Whether two texts are the same date: the same moment, or the same day however it's written
    /// (Exchange may give the moment in UTC, or the day without an offset).
    /// </summary>
    static bool SameDate(string want, string? actual)
    {
        if (actual is null || !IsoDate().IsMatch(want) || !IsoDate().IsMatch(actual)
            || !DateTimeOffset.TryParse(want, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var w)
            || !DateTimeOffset.TryParse(actual, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var a))
            return false;
        return w == a || w.DateTime.Date == a.DateTime.Date || w.ToLocalTime().Date == a.ToLocalTime().Date;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")]
    private static partial System.Text.RegularExpressions.Regex IsoDate();

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A value as a person reads it: lists joined with "or", sizes in KB/MB, true as "yes".</summary>
    public static string Describe(JsonNode? value, string valueType)
    {
        switch (value)
        {
            case null:
                return "(not set)";
            case JsonArray list:
                var items = list.Select(i => i?.ToString() ?? "").Where(s => s.Length > 0).Select(s => $"'{s}'").ToArray();
                return items.Length <= 1 ? string.Concat(items) : string.Join(", ", items[..^1]) + " or " + items[^1];
            case JsonValue v when valueType == "size" && v.TryGetValue<long>(out var bytes):
                return MailDetails.Size(bytes);
            case JsonValue v when v.TryGetValue<bool>(out var flag):
                return flag ? "yes" : "no";
            default:
                return $"'{value}'";
        }
    }

    /// <summary>A value with fixed choices reads as the choice's label ("Outside the organization").</summary>
    static string Describe(JsonNode? value, string valueType, IReadOnlyList<RuleChoice> choices)
    {
        if (choices.Count > 0 && value is JsonValue v)
        {
            var raw = v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
            if (choices.FirstOrDefault(c => string.Equals(c.Value, raw, StringComparison.OrdinalIgnoreCase)) is { } choice)
                return $"'{choice.Label}'";
        }
        // A disclaimer or notification reads as its text, without the HTML.
        if (valueType == "html" && value is JsonValue html && html.GetValueKind() == JsonValueKind.String)
            return $"'{PlainText.FromHtml(html.GetValue<string>()).Trim()}'";
        return Describe(value, valueType);
    }

    /// <summary>One line in plain words: "The sender is 'pat@contoso.com'".</summary>
    public static string Describe(RuleLine line)
    {
        var text = line.Def.ValueType == "none" ? line.Def.Label : $"{line.Def.Label} {Describe(line.Value, line.Def.ValueType, line.Def.Choices)}";
        foreach (var extra in line.Def.Extras)
            if (line.Extras.TryGetValue(extra.Parameter, out var v) && v is not null)
                text += $" ({Describe(v, extra.ValueType, extra.Choices)})";
        return text;
    }

    /// <summary>The whole rule in a sentence, for Pending Changes and the editor's summary.</summary>
    public static string Summary(RuleDraft draft)
    {
        var when = draft.Conditions.Count == 0 ? "every message" : string.Join(" and ", draft.Conditions.Select(l => Lower(Describe(l))));
        var except = draft.Exceptions.Count == 0 ? "" : $", except when {string.Join(" or ", draft.Exceptions.Select(l => Lower(Describe(l))))}";
        var then = draft.Actions.Count == 0 ? "do nothing" : string.Join("; ", draft.Actions.Select(l => Lower(Describe(l))));
        return string.Create(CultureInfo.InvariantCulture, $"When {when}{except}: {then}.");
    }

    /// <summary>
    /// Before and after for an edit: the rule's sentence, followed by its name and each setting the
    /// edit changes (notes, dates, stop processing…), so a rename or a new note doesn't read as no change.
    /// </summary>
    public static (string Before, string After) EditSummaries(RuleDraft original, RuleDraft edited, RuleCatalog? catalog = null)
    {
        catalog ??= RuleCatalog.Instance;
        var before = new List<string> { Summary(original) };
        var after = new List<string> { Summary(edited) };
        if (!string.Equals(original.Name, edited.Name, StringComparison.Ordinal))
        {
            before.Add($"Name '{original.Name}'.");
            after.Add($"Name '{edited.Name}'.");
        }
        var properties = catalog.Properties.ToDictionary(p => p.Parameter, StringComparer.OrdinalIgnoreCase);
        foreach (var key in original.Settings.Keys.Union(edited.Settings.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var old = original.Settings.GetValueOrDefault(key);
            var now = edited.Settings.GetValueOrDefault(key);
            if (JsonNode.DeepEquals(old, now))
                continue;
            var property = properties.GetValueOrDefault(key);
            var label = property?.Label ?? key;
            var type = property?.ValueType ?? "text";
            before.Add($"{label}: {Describe(old, type)}.");
            after.Add($"{label}: {Describe(now, type)}.");
        }
        return (string.Join(" ", before), string.Join(" ", after));
    }

    /// <summary>Mid-sentence case: "The sender is" → "the sender is"; "SCL…" stays.</summary>
    public static string Lower(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && char.IsLower(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;
}
