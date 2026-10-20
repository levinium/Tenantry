using System.Globalization;

namespace Tenantry.Core.Lists;

/// <summary>Plain-English descriptions of conditions, for filter pills and saved views.</summary>
public static class ConditionText
{
    public static string Describe<T>(Condition condition, ListEngine<T> engine)
    {
        string Label(string key) => engine.Field(key)?.Label ?? key;

        return condition switch
        {
            ChoiceCondition c when c.Include.Count > 0 && c.Exclude.Count > 0 =>
                $"{Label(c.Field)}: {Join(c.Include)}, not {Join(c.Exclude)}",
            ChoiceCondition c when c.Include.Count > 0 => $"{Label(c.Field)}: {Join(c.Include)}",
            ChoiceCondition c => $"{Label(c.Field)}: not {Join(c.Exclude)}",

            TextCondition t => t.Op switch
            {
                TextOp.Contains => $"{Label(t.Field)} contains \"{t.Value}\"",
                TextOp.NotContains => $"{Label(t.Field)} doesn't contain \"{t.Value}\"",
                TextOp.Is => $"{Label(t.Field)} is \"{t.Value}\"",
                TextOp.StartsWith => $"{Label(t.Field)} starts with \"{t.Value}\"",
                TextOp.IsEmpty => $"{Label(t.Field)} is empty",
                _ => $"{Label(t.Field)} isn't empty",
            },

            DateCondition d => d.Op switch
            {
                DateOp.WithinLastDays => $"{Label(d.Field)}: last {Days(d.Days)}",
                DateOp.OlderThanDays => $"{Label(d.Field)}: over {Days(d.Days)} ago",
                DateOp.Never => $"{Label(d.Field)}: never",
                DateOp.Any => $"{Label(d.Field)}: any date",
                _ => $"{Label(d.Field)}: {Date(d.From) ?? "…"} to {Date(d.To) ?? "…"}",
            },

            NumberCondition n when n.Min is { } min && n.Max is { } max =>
                min == max ? $"{Label(n.Field)} = {min}" : $"{Label(n.Field)} {min} to {max}",
            NumberCondition n when n.Min is { } min => $"{Label(n.Field)} ≥ {min}",
            NumberCondition n when n.Max is { } max => $"{Label(n.Field)} ≤ {max}",
            NumberCondition n => Label(n.Field),

            ConditionGroup g when g.Items.Count == 1 => Describe(g.Items[0], engine),
            ConditionGroup g => $"{(g.Op == GroupOp.Or ? "Any of" : "All of")} {g.Items.Count} rules",
            _ => "Filter",
        };
    }

    static string Join(IReadOnlyList<string> values) =>
        values.Count <= 3 ? string.Join(", ", values) : $"{string.Join(", ", values.Take(2))} +{values.Count - 2}";

    static string Days(int days) => days switch
    {
        1 => "1 day",
        _ when days % 365 == 0 => days == 365 ? "1 year" : $"{days / 365} years",
        _ => $"{days} days",
    };

    static string? Date(DateTimeOffset? d) => d?.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
}
