using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tenantry.Core.Directory;

namespace Tenantry.Core.MailFlow;

/// <summary>How a value is entered.</summary>
public enum RuleInput
{
    /// <summary>Nothing to enter: the part is on or off by being there.</summary>
    None,
    Text,
    LongText,

    /// <summary>One entry per line (addresses, words, patterns…).</summary>
    Lines,
    Choice,
    Choices,
    Number,
    Size,
    Date,
    Switch,
}

/// <summary>What each kind of catalog value takes, how it's entered and when it's wrong.</summary>
public static partial class RuleValues
{
    /// <summary>Exchange's limit on one text pattern.</summary>
    public const int MaxPatternLength = 128;

    static readonly HashSet<string> ListTypes = new(StringComparer.Ordinal)
    {
        "addresses", "groups", "words", "patterns", "domains", "ipRanges", "betweenGroups", "headerWords", "headerPatterns",
        "adAttributeWords", "adAttributePatterns", "documentPropertyWords", "sensitiveInfoTypes",
    };

    static readonly HashSet<string> PatternTypes = new(StringComparer.Ordinal) { "patterns", "headerPatterns", "adAttributePatterns" };

    /// <summary>Types written as "Name:value1,value2", one per line.</summary>
    static readonly HashSet<string> PropertyTypes = new(StringComparer.Ordinal) { "adAttributeWords", "adAttributePatterns", "documentPropertyWords" };

    public static RuleInput InputFor(string valueType, IReadOnlyList<RuleChoice> choices) => valueType switch
    {
        "none" => RuleInput.None,
        "bool" => RuleInput.Switch,
        "html" => RuleInput.LongText,
        "size" => RuleInput.Size,
        "int" or "smtpStatusCode" => RuleInput.Number,
        "dateTime" => RuleInput.Date,
        "enumMulti" or "characterSets" => RuleInput.Choices,
        _ when PropertyTypes.Contains(valueType) => RuleInput.Lines,
        _ when ListTypes.Contains(valueType) => RuleInput.Lines,
        _ when choices.Count > 0 => RuleInput.Choice,
        _ => RuleInput.Text,
    };

    /// <summary>The values in a list, one per line. Patterns keep commas (they're part of a pattern); other lists also split on commas and semicolons.</summary>
    public static string[] SplitLines(string text, string valueType)
    {
        var separators = PatternTypes.Contains(valueType) || PropertyTypes.Contains(valueType) ? new[] { '\n' } : ['\n', ',', ';'];
        return text.Split(separators).Select(s => s.Trim().TrimEnd('\r')).Where(s => s.Length > 0).ToArray();
    }

    /// <summary>A value as it sits in an editor box: one line per entry for a list.</summary>
    public static string ToText(JsonNode? value) => value switch
    {
        null => "",
        JsonArray list => string.Join("\n", list.Select(i => i?.ToString() ?? "")),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => value.ToJsonString(),
    };

    /// <summary>The parts of a "Name:value1,value2" entry.</summary>
    public static (string Name, string Values) SplitProperty(string line)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? (line.Trim(), "") : (line[..colon].Trim(), line[(colon + 1)..].Trim());
    }

    /// <summary>What's wrong with a value, in plain words; null when it's fine.</summary>
    public static string? Problem(string valueType, JsonNode? value, bool required, IReadOnlyList<RuleChoice> choices)
    {
        if (valueType == "none")
            return null;
        if (IsEmpty(value))
            return required ? (ListTypes.Contains(valueType) ? "Add at least one" : "Fill this in") : null;
        if (value is JsonArray list)
        {
            var items = list.Select(i => i?.ToString() ?? "").ToArray();
            for (var i = 0; i < items.Length; i++)
                if (ItemProblem(valueType, items[i], choices) is { } problem)
                    return items.Length == 1 ? problem : $"Line {i + 1}: {problem}";
            return null;
        }
        var text = value is JsonValue s && s.GetValueKind() == JsonValueKind.String ? s.GetValue<string>() : value!.ToJsonString();
        return valueType switch
        {
            "dsnCode" when !DsnCode().IsMatch(text) => "Use 5.7.1, or a code from 5.7.900 to 5.7.999",
            "smtpStatusCode" when !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) || code is < 400 or > 500 => "Use a code from 400 to 500",
            "size" when MailDetails.ParseBytes(text) is not > 0 => "Enter a size above zero",
            "int" when !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => "Enter a whole number",
            "headerName" when text.Any(c => char.IsWhiteSpace(c) || c == ':') => "A header name has no spaces or colons",
            _ => null,
        };
    }

    static bool IsEmpty(JsonNode? value) => value switch
    {
        null => true,
        JsonArray list => list.Count == 0,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetValue<string>()),
        _ => false,
    };

    static string? ItemProblem(string valueType, string item, IReadOnlyList<RuleChoice> choices)
    {
        if (PatternTypes.Contains(valueType))
        {
            var pattern = valueType == "adAttributePatterns" ? SplitProperty(item).Values : item;
            if (PropertyProblem(valueType, item, choices) is { } p)
                return p;
            foreach (var one in valueType == "adAttributePatterns" ? pattern.Split(',') : [pattern])
            {
                if (one.Length > MaxPatternLength)
                    return $"a pattern can be at most {MaxPatternLength} characters";
                try
                {
                    _ = new Regex(one, RegexOptions.None, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException e)
                {
                    return $"not a valid pattern ({e.Message.Split(" - ").Last().TrimEnd('.')})";
                }
            }
            return null;
        }
        return valueType switch
        {
            "domains" when item.Contains('@') => $"'{item}' is an address; enter just the domain after the @",
            "domains" when !item.Contains('.') || item.Any(char.IsWhiteSpace) => $"'{item}' isn't a domain like contoso.com",
            "ipRanges" when !IsIpRange(item) => $"'{item}' isn't an IPv4 address, range (a-b) or CIDR block",
            "adAttributeWords" or "documentPropertyWords" => PropertyProblem(valueType, item, choices),
            _ => null,
        };
    }

    static string? PropertyProblem(string valueType, string item, IReadOnlyList<RuleChoice> choices)
    {
        if (!PropertyTypes.Contains(valueType))
            return null;
        var (name, values) = SplitProperty(item);
        if (values.Length == 0)
            return "write it as Property:word1,word2";
        if (choices.Count > 0 && !choices.Any(c => string.Equals(c.Value, name, StringComparison.OrdinalIgnoreCase)))
            return $"'{name}' isn't a property Exchange knows";
        return null;
    }

    static bool IsIpRange(string item)
    {
        if (item.Contains('/'))
        {
            var parts = item.Split('/');
            return parts.Length == 2 && IPAddress.TryParse(parts[0], out _) && int.TryParse(parts[1], out var bits) && bits is >= 0 and <= 32;
        }
        if (item.Contains('-'))
        {
            var parts = item.Split('-');
            return parts.Length == 2 && IPAddress.TryParse(parts[0].Trim(), out _) && IPAddress.TryParse(parts[1].Trim(), out _);
        }
        return IPAddress.TryParse(item, out _);
    }

    [GeneratedRegex(@"^5\.7\.(1|9\d\d)$")]
    private static partial Regex DsnCode();
}
