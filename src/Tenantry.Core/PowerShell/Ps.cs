using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tenantry.Core.PowerShell;

/// <summary>
/// Writes PowerShell text: quoted values, cmdlet calls and Graph requests. Everything Tenantry
/// shows as "the PowerShell for this change" goes through here, so quoting is done one way.
/// </summary>
public static partial class Ps
{
    public const string GraphRoot = "https://graph.microsoft.com/v1.0/";

    /// <summary>Lines longer than this are split one parameter per line.</summary>
    const int LineLimit = 110;

    // Relaxed escaping keeps <, > and accents as they are: this text is read by people, and
    // PowerShell reads it as a string either way.
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// A single-quoted string. PowerShell also ends a single-quoted string at the typographic
    /// quotes (‘ ’ ‚ ‛), so those are doubled too.
    /// </summary>
    public static string Quote(string? text)
    {
        if (text is null)
            return "$null";
        var b = new StringBuilder(text.Length + 2).Append('\'');
        foreach (var ch in text)
        {
            if (ch is '\'' or '‘' or '’' or '‚' or '‛')
                b.Append(ch);
            b.Append(ch);
        }
        return b.Append('\'').ToString();
    }

    /// <summary>A value as PowerShell writes it: $true, 42, 'text', @('a', 'b'), @{ Add = @('x') }.</summary>
    public static string Value(object? value) => value switch
    {
        null => "$null",
        bool b => b ? "$true" : "$false",
        string s => Quote(s),
        Enum e => Quote(e.ToString()),
        int or long or short or byte or uint or ulong => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        double or float or decimal => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        DateTimeOffset d => Quote(d.ToString("o", CultureInfo.InvariantCulture)),
        DateTime d => Quote(d.ToString("o", CultureInfo.InvariantCulture)),
        Guid g => Quote(g.ToString()),
        JsonElement j => Value(JsonNode.Parse(j.GetRawText())),
        JsonValue j => JsonValueText(j),
        JsonArray a => Array(a.Select(n => (object?)n)),
        JsonObject o => Table(o.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value))),
        IDictionary<string, object?> d => Table(d),
        IReadOnlyDictionary<string, object?> d => Table(d),
        IDictionary d => Table(d.Keys.Cast<object>().Select(k => new KeyValuePair<string, object?>(k.ToString()!, d[k]))),
        IEnumerable items => Array(items.Cast<object?>()),
        _ => Quote(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    static string JsonValueText(JsonValue value) => value.GetValueKind() switch
    {
        JsonValueKind.True => "$true",
        JsonValueKind.False => "$false",
        JsonValueKind.Number => value.ToJsonString(),
        JsonValueKind.Null => "$null",
        _ => Quote(value.ToString()),
    };

    static string Array(IEnumerable<object?> items) => "@(" + string.Join(", ", items.Select(Value)) + ")";

    /// <summary>
    /// A hashtable. Exchange's "#Exchange.GenericHashTable" type tag (how Tenantry adds to and
    /// removes from a list over REST) is left out: in PowerShell a plain hashtable does that.
    /// </summary>
    static string Table(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        var parts = pairs.Where(p => p.Key != "@odata.type")
            .Select(p => $"{Key(p.Key)} = {(p.Key is "Add" or "Remove" && p.Value is string s ? Array([s]) : Value(p.Value))}")
            .ToArray();
        return parts.Length == 0 ? "@{}" : "@{ " + string.Join("; ", parts) + " }";
    }

    static string Key(string key) => BareKey().IsMatch(key) ? key : Quote(key);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex BareKey();

    /// <summary>
    /// A cmdlet call. Booleans are written -Name:$true, which suits both switches and true/false
    /// parameters (Exchange's REST endpoint takes both as booleans, as Tenantry sends them).
    /// </summary>
    public static string Cmdlet(string name, IEnumerable<KeyValuePair<string, object?>>? parameters = null)
    {
        var args = (parameters ?? []).Select(p => p.Value is bool b ? $"-{p.Key}:{(b ? "$true" : "$false")}" : $"-{p.Key} {Value(p.Value)}").ToArray();
        var line = string.Join(' ', [name, .. args]);
        return line.Length <= LineLimit || args.Length < 2 ? line : name + " `\n    " + string.Join(" `\n    ", args);
    }

    /// <summary>A Graph request through the Microsoft Graph PowerShell module, exactly as Tenantry sends it.</summary>
    /// <param name="path">Relative to Graph v1.0 ("users/{id}"), or a full URL (beta).</param>
    /// <param name="assignTo">A variable to keep the answer in ("user" gives $user = …).</param>
    public static string Graph(string method, string path, JsonNode? body = null, string? assignTo = null)
    {
        var uri = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : GraphRoot + path;
        var call = $"Invoke-MgGraphRequest -Method {method.ToUpperInvariant()} -Uri {(Refers(uri) ? Expandable(uri) : Quote(uri))}";
        var prefix = assignTo is null ? "" : $"${assignTo} = ";
        if (body is null)
            return prefix + call;
        // The writer escapes Ref's private-use marks; put them back. (Typed text can't forge one
        // into code: a mark only ever becomes a variable read, and only around a variable path.)
        var json = body.ToJsonString(Indented).Replace("\\uE000", RefStart.ToString()).Replace("\\uE001", RefEnd.ToString());
        return $"$body = {(Refers(json) ? ExpandableHereString(json) : HereString(json))}\n{prefix}{call} -Body $body";
    }

    // A reference to an earlier step's answer is marked with two private-use characters that
    // only Ref writes, never by "$(" in the text: a value someone typed (a department called
    // "$(…)") must come out as text, not as code that runs.
    const char RefStart = '';
    const char RefEnd = '';

    /// <summary>Something an earlier step returned (<c>Ref("user", "id")</c> becomes $($user.id)), for a path or body.</summary>
    public static string Ref(string variable, string property) => $"{RefStart}{variable}.{property}{RefEnd}";

    static bool Refers(string text) => text.Contains(RefStart);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex VariablePath();

    /// <summary>
    /// A double-quoted string where only <see cref="Ref"/> marks expand; every other $ ($ref,
    /// $select, or typed text), backtick and double quote is escaped.
    /// </summary>
    public static string Expandable(string text) => "\"" + EscapeExpandable(text, inline: true) + "\"";

    /// <summary>A double-quoted here-string where only <see cref="Ref"/> marks expand.</summary>
    static string ExpandableHereString(string text)
    {
        var escaped = EscapeExpandable(text.Replace("\r\n", "\n"), inline: false);
        // A line starting "@ would end it early.
        return escaped.Split('\n').Any(l => l.StartsWith("\"@", StringComparison.Ordinal)) ? Expandable(text) : $"@\"\n{escaped}\n\"@";
    }

    static string EscapeExpandable(string text, bool inline)
    {
        var b = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == RefStart && text.IndexOf(RefEnd, i + 1) is var end and > 0 && VariablePath().IsMatch(text[(i + 1)..end]))
            {
                b.Append("$($").Append(text[(i + 1)..end]).Append(')');
                i = end;
                continue;
            }
            if (ch is RefStart or RefEnd)
                continue;
            // PowerShell also ends a double-quoted string at the typographic double quotes.
            if (ch is '`' or '$' || (inline && ch is '"' or '“' or '”' or '„'))
                b.Append('`');
            b.Append(ch);
        }
        return b.ToString();
    }

    /// <summary>
    /// Literal text as a single-quoted here-string. Nothing inside one is special except a line
    /// that starts with '@, which would end it early; JSON never has one, but text can.
    /// </summary>
    public static string HereString(string text)
    {
        text = text.Replace("\r\n", "\n");
        return text.Split('\n').Any(l => l.StartsWith("'@", StringComparison.Ordinal)) ? Quote(text) : $"@'\n{text}\n'@";
    }

    /// <summary>A comment line (or lines); newlines in the text become new comment lines.</summary>
    public static string Comment(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(l => ("# " + l).TrimEnd()));

    /// <summary>Statements one per line, skipping empty ones.</summary>
    public static string Lines(params IEnumerable<string?> statements) =>
        string.Join('\n', statements.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>A Graph path piece (an ID or a name) escaped for a URL.</summary>
    public static string Segment(string text) => Uri.EscapeDataString(text);
}
