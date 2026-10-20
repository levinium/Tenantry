namespace Tenantry.Core.Lists;

/// <summary>A row with its search text worked out once, so each keystroke is a cheap scan.</summary>
public sealed record Prepared<T>(T Item, string Haystack);

public sealed record FacetValue(string Value, int Count);

/// <summary>
/// Runs <see cref="ListQuery"/> over rows in memory. Conditions compile once per query into
/// delegates; a run is one pass to filter plus a sort. Unknown fields (a saved view that names
/// a column since removed) are ignored rather than failing the whole query.
/// </summary>
public sealed class ListEngine<T>
{
    readonly Dictionary<string, Field<T>> _fields;

    public ListEngine(IEnumerable<Field<T>> fields)
    {
        Fields = fields.ToArray();
        _fields = Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Field<T>> Fields { get; }

    public Field<T>? Field(string key) => _fields.GetValueOrDefault(key);

    public IReadOnlyList<Prepared<T>> Prepare(IEnumerable<T> rows)
    {
        var searchable = Fields.Where(f => f.Searchable).ToArray();
        return rows.Select(r => new Prepared<T>(r,
            string.Join('\n', searchable.Select(f => f.Display(r))).ToLowerInvariant())).ToArray();
    }

    public IReadOnlyList<T> Run(IReadOnlyList<Prepared<T>> rows, ListQuery query, DateTimeOffset now)
    {
        var match = Matcher(query.Search, query.Where, now);
        var result = new List<T>(rows.Count);
        foreach (var row in rows)
            if (match(row))
                result.Add(row.Item);

        var sort = query.GroupBy is { } group && Field(group) is not null
            ? query.Sort.Where(s => !s.Field.Equals(group, StringComparison.OrdinalIgnoreCase)).Prepend(new SortLevel(group)).ToArray()
            : query.Sort;
        if (sort.Count > 0)
            result.Sort(Comparer(sort));
        return result;
    }

    /// <summary>
    /// How many rows each value of a choice field would give, counted over the rows that match
    /// every OTHER condition. So the numbers answer "what would I get if I picked this?".
    /// </summary>
    public IReadOnlyList<FacetValue> Facet(IReadOnlyList<Prepared<T>> rows, ListQuery query, string fieldKey, DateTimeOffset now)
    {
        if (Field(fieldKey) is not { IsChoice: true } field)
            return [];

        var others = new ConditionGroup(query.Where.Op, query.Where.Op == GroupOp.And
            ? query.Where.Items.Where(c => !(c is ChoiceCondition cc && cc.Field.Equals(field.Key, StringComparison.OrdinalIgnoreCase))).ToArray()
            : query.Where.Items);
        var match = Matcher(query.Search, others, now);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!match(row))
                continue;
            foreach (var value in field.ChoiceValues(row.Item).Distinct(StringComparer.OrdinalIgnoreCase))
                counts[value] = counts.GetValueOrDefault(value) + 1;
        }

        IEnumerable<FacetValue> values = counts.Select(kv => new FacetValue(kv.Key, kv.Value));
        if (field.ChoiceOrder is { } order)
        {
            foreach (var fixedValue in order.Where(v => !counts.ContainsKey(v)))
                values = values.Append(new FacetValue(fixedValue, 0));
            return values.OrderBy(v => IndexIn(order, v.Value)).ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return values
            .OrderBy(v => string.Equals(v.Value, field.EmptyLabel, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(v => v.Count)
            .ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static int IndexIn(IReadOnlyList<string> order, string value)
    {
        for (var i = 0; i < order.Count; i++)
            if (string.Equals(order[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        return int.MaxValue;
    }

    Func<Prepared<T>, bool> Matcher(string search, ConditionGroup where, DateTimeOffset now)
    {
        var tokens = search.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var condition = Compile(where, now);
        return tokens.Length == 0
            ? row => condition(row.Item)
            : row => condition(row.Item) && tokens.All(t => row.Haystack.Contains(t, StringComparison.Ordinal));
    }

    public Func<T, bool> Compile(Condition condition, DateTimeOffset now)
    {
        switch (condition)
        {
            case ConditionGroup group:
                var parts = group.Items.Select(c => Compile(c, now)).ToArray();
                if (parts.Length == 0)
                    return _ => true;
                return group.Op == GroupOp.And
                    ? row => parts.All(p => p(row))
                    : row => parts.Any(p => p(row));

            case ChoiceCondition choice when Field(choice.Field) is { IsChoice: true } field:
                var include = new HashSet<string>(choice.Include, StringComparer.OrdinalIgnoreCase);
                var exclude = new HashSet<string>(choice.Exclude, StringComparer.OrdinalIgnoreCase);
                return row =>
                {
                    var values = field.ChoiceValues(row);
                    return (include.Count == 0 || values.Any(include.Contains)) && !values.Any(exclude.Contains);
                };

            case TextCondition text when Field(text.Field) is { } field:
                var needle = text.Value.Trim();
                return text.Op switch
                {
                    TextOp.Contains => row => field.Display(row).Contains(needle, StringComparison.OrdinalIgnoreCase),
                    TextOp.NotContains => row => !field.Display(row).Contains(needle, StringComparison.OrdinalIgnoreCase),
                    TextOp.Is => row => string.Equals(field.Display(row), needle, StringComparison.OrdinalIgnoreCase),
                    TextOp.StartsWith => row => field.Display(row).StartsWith(needle, StringComparison.OrdinalIgnoreCase),
                    TextOp.IsEmpty => row => field.Display(row).Length == 0,
                    TextOp.IsNotEmpty => row => field.Display(row).Length > 0,
                    _ => _ => true,
                };

            case DateCondition date when Field(date.Field) is { Kind: FieldKind.Date, Date: { } get }:
                return date.Op switch
                {
                    DateOp.WithinLastDays => row => get(row) is { } d && d >= now.AddDays(-date.Days),
                    DateOp.OlderThanDays => row => get(row) is { } d && d < now.AddDays(-date.Days),
                    DateOp.Between => row => get(row) is { } d && (date.From is null || d >= date.From) && (date.To is null || d <= date.To),
                    DateOp.Never => row => get(row) is null,
                    DateOp.Any => row => get(row) is not null,
                    _ => _ => true,
                };

            case NumberCondition number when Field(number.Field) is { Kind: FieldKind.Number, Number: { } get }:
                return row => get(row) is { } n && (number.Min is null || n >= number.Min) && (number.Max is null || n <= number.Max);

            default:
                return _ => true;
        }
    }

    public IComparer<T> Comparer(IReadOnlyList<SortLevel> levels)
    {
        var comparisons = levels
            .Select(l => (Field: Field(l.Field), l.Descending))
            .Where(l => l.Field is not null)
            .Select(l => (Compare: KeyComparison(l.Field!), l.Descending))
            .ToArray();
        return Comparer<T>.Create((a, b) =>
        {
            foreach (var (compare, descending) in comparisons)
            {
                var result = compare(a, b);
                if (result != 0)
                    return descending ? -result : result;
            }
            return 0;
        });
    }

    // Empty values sort first ascending: "never signed in" and "no department" lead an audit.
    static Comparison<T> KeyComparison(Field<T> field) => field.Kind switch
    {
        FieldKind.Date => (a, b) => Nullable.Compare(field.Date!(a), field.Date!(b)),
        FieldKind.Number => (a, b) => Nullable.Compare(field.Number!(a), field.Number!(b)),
        _ => (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(field.Display(a), field.Display(b)),
    };
}
