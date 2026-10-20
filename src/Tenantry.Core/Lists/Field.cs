namespace Tenantry.Core.Lists;

public enum FieldKind
{
    Text,
    Choice,
    MultiChoice,
    Date,
    Number,
}

/// <summary>
/// One attribute of a list row: how to read it, what it's called, and how it filters. Fill in
/// the accessor that matches the kind: <see cref="Text"/> for Text and Choice,
/// <see cref="Values"/> for MultiChoice, <see cref="Date"/>, or <see cref="Number"/>.
/// </summary>
public sealed class Field<T>
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required FieldKind Kind { get; init; }

    /// <summary>Other names accepted in search syntax, for example "dept" for department.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    public Func<T, string?>? Text { get; init; }
    public Func<T, IReadOnlyList<string>>? Values { get; init; }
    public Func<T, DateTimeOffset?>? Date { get; init; }
    public Func<T, double?>? Number { get; init; }

    /// <summary>How an empty value reads, and the value filters use for "has none".</summary>
    public string EmptyLabel { get; init; } = "None";

    /// <summary>Whether free-text search looks in this field.</summary>
    public bool Searchable { get; init; }

    /// <summary>A fixed order for choice values (Active before Blocked); otherwise by count.</summary>
    public IReadOnlyList<string>? ChoiceOrder { get; init; }

    public bool IsChoice => Kind is FieldKind.Choice or FieldKind.MultiChoice;

    public bool Matches(string name) =>
        string.Equals(name, Key, StringComparison.OrdinalIgnoreCase)
        || Aliases.Any(a => string.Equals(name, a, StringComparison.OrdinalIgnoreCase))
        || string.Equals(name, Label, StringComparison.OrdinalIgnoreCase);

    /// <summary>The row's choice values, with empty mapped to <see cref="EmptyLabel"/>.</summary>
    public IReadOnlyList<string> ChoiceValues(T row)
    {
        if (Kind == FieldKind.MultiChoice)
        {
            var values = Values?.Invoke(row) ?? [];
            return values.Count == 0 ? [EmptyLabel] : values;
        }
        var text = Text?.Invoke(row);
        return [string.IsNullOrEmpty(text) ? EmptyLabel : text];
    }

    /// <summary>The value as plain text, for search, CSV export and text conditions.</summary>
    public string Display(T row, IFormatProvider? format = null) => Kind switch
    {
        FieldKind.MultiChoice => string.Join(", ", Values?.Invoke(row) ?? []),
        FieldKind.Date => Date?.Invoke(row) is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm", format) : "",
        FieldKind.Number => Number?.Invoke(row) is { } n ? n.ToString(format) : "",
        _ => Text?.Invoke(row) ?? "",
    };
}
