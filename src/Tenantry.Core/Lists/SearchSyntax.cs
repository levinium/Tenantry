using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tenantry.Core.Lists;

public sealed record SearchParse(string FreeText, IReadOnlyList<Condition> Conditions, IReadOnlyList<string> Problems);

/// <summary>
/// Turns shortcuts typed in a list's search box into conditions, leaving the rest as free text:
/// <list type="bullet">
/// <item><c>dept:finance</c>, <c>-status:blocked</c>, <c>license:none</c> (choice fields)</item>
/// <item><c>title:director</c>, <c>-title:intern</c>, <c>title:none</c> (text fields: contains)</item>
/// <item><c>signin:&lt;7d</c>, <c>signin:&gt;90d</c>, <c>signin:never</c>, <c>signin:any</c>; units d, w, m (30 days), y</item>
/// <item><c>licenses:&gt;=2</c>, <c>licenses:1..3</c>, <c>licenses:2</c> (numbers)</item>
/// </list>
/// Values with spaces go in quotes: <c>dept:"human resources"</c>. A key that names no field
/// is ordinary search text, so "re:" in a subject line still searches as typed.
/// </summary>
public static partial class SearchSyntax
{
    public static SearchParse Parse<T>(string input, IReadOnlyList<Field<T>> fields)
    {
        var free = new List<string>();
        var conditions = new List<Condition>();
        var problems = new List<string>();

        foreach (var token in Tokenize(input))
        {
            var match = Shortcut().Match(token);
            var field = match.Success ? fields.FirstOrDefault(f => f.Matches(match.Groups["key"].Value)) : null;
            if (field is null)
            {
                free.Add(token);
                continue;
            }

            var negate = match.Groups["neg"].Success;
            var value = match.Groups["value"].Value.Trim('"');
            var condition = ToCondition(field, value, negate);
            if (condition is null)
                problems.Add($"Didn't understand \"{token}\"");
            else
                conditions.Add(condition);
        }

        return new SearchParse(string.Join(' ', free), conditions, problems);
    }

    static Condition? ToCondition<T>(Field<T> field, string value, bool negate)
    {
        var isNone = value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Equals("empty", StringComparison.OrdinalIgnoreCase);
        switch (field.Kind)
        {
            case FieldKind.Choice or FieldKind.MultiChoice:
                if (value.Length == 0)
                    return null;
                var chosen = isNone ? field.EmptyLabel : value;
                return negate ? new ChoiceCondition(field.Key, [], [chosen]) : new ChoiceCondition(field.Key, [chosen], []);

            case FieldKind.Text:
                if (isNone)
                    return new TextCondition(field.Key, negate ? TextOp.IsNotEmpty : TextOp.IsEmpty);
                return value.Length == 0 ? null : new TextCondition(field.Key, negate ? TextOp.NotContains : TextOp.Contains, value);

            case FieldKind.Date:
                if (value.Equals("never", StringComparison.OrdinalIgnoreCase) || isNone)
                    return new DateCondition(field.Key, negate ? DateOp.Any : DateOp.Never);
                if (value.Equals("any", StringComparison.OrdinalIgnoreCase))
                    return new DateCondition(field.Key, negate ? DateOp.Never : DateOp.Any);
                var age = DateAge().Match(value);
                if (!age.Success || negate)
                    return null;
                var days = int.Parse(age.Groups["n"].Value, CultureInfo.InvariantCulture) * (age.Groups["unit"].Value.ToLowerInvariant() switch
                {
                    "w" => 7,
                    "m" => 30,
                    "y" => 365,
                    _ => 1,
                });
                return new DateCondition(field.Key, age.Groups["op"].Value == ">" ? DateOp.OlderThanDays : DateOp.WithinLastDays, days);

            case FieldKind.Number:
                if (negate)
                    return null;
                var range = NumberRange().Match(value);
                if (range.Success)
                    return new NumberCondition(field.Key, Num(range.Groups["min"].Value), Num(range.Groups["max"].Value));
                var compare = NumberCompare().Match(value);
                if (!compare.Success)
                    return null;
                var n = Num(compare.Groups["n"].Value)!.Value;
                return compare.Groups["op"].Value switch
                {
                    ">=" => new NumberCondition(field.Key, n, null),
                    ">" => new NumberCondition(field.Key, n + Increment(n), null),
                    "<=" => new NumberCondition(field.Key, null, n),
                    "<" => new NumberCondition(field.Key, null, n - Increment(n)),
                    _ => new NumberCondition(field.Key, n, n),
                };

            default:
                return null;
        }
    }

    // Counts are whole numbers, so "> 2" means "at least 3"; fractional values use a tiny step.
    static double Increment(double n) => n == Math.Floor(n) ? 1 : 1e-9;

    static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Splits on spaces, keeping quoted runs (including key:"two words") together.</summary>
    static IEnumerable<string> Tokenize(string input)
    {
        var current = new StringBuilder();
        var quoted = false;
        foreach (var ch in input)
        {
            if (ch == '"')
                quoted = !quoted;
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                    yield return current.ToString();
                current.Clear();
            }
            else
                current.Append(ch);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    [GeneratedRegex(@"^(?<neg>-)?(?<key>[A-Za-z][\w-]*):(?<value>.*)$")]
    private static partial Regex Shortcut();

    [GeneratedRegex(@"^(?<op>[<>])(?<n>\d+)(?<unit>[dwmyDWMY]?)$")]
    private static partial Regex DateAge();

    [GeneratedRegex(@"^(?<min>\d+(\.\d+)?)?\.\.(?<max>\d+(\.\d+)?)?$")]
    private static partial Regex NumberRange();

    [GeneratedRegex(@"^(?<op>>=|<=|>|<|=)?(?<n>\d+(\.\d+)?)$")]
    private static partial Regex NumberCompare();
}
