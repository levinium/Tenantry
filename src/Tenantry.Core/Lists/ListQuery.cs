using System.Text.Json.Serialization;

namespace Tenantry.Core.Lists;

/// <summary>
/// A filter rule. Plain records so a query can be saved as a view, sent to the cache's SQL
/// engine, or (later) produced by an assistant from a question.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChoiceCondition), "choice")]
[JsonDerivedType(typeof(TextCondition), "text")]
[JsonDerivedType(typeof(DateCondition), "date")]
[JsonDerivedType(typeof(NumberCondition), "number")]
[JsonDerivedType(typeof(ConditionGroup), "group")]
public abstract record Condition;

/// <summary>
/// Rows whose value is one of <paramref name="Include"/> (when any are given) and none of
/// <paramref name="Exclude"/>. For multi-choice fields a row matches an included value if any
/// of its values is that value, and is excluded if any of its values is excluded.
/// </summary>
public sealed record ChoiceCondition(string Field, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude) : Condition;

public enum TextOp
{
    Contains,
    NotContains,
    Is,
    StartsWith,
    IsEmpty,
    IsNotEmpty,
}

public sealed record TextCondition(string Field, TextOp Op, string Value = "") : Condition;

public enum DateOp
{
    /// <summary>Within the last <c>Days</c> days.</summary>
    WithinLastDays,

    /// <summary>More than <c>Days</c> days ago. Empty dates do NOT match; combine with Never for "inactive".</summary>
    OlderThanDays,

    /// <summary>From <c>From</c> to <c>To</c> inclusive; either may be open.</summary>
    Between,

    /// <summary>No date at all ("never signed in").</summary>
    Never,

    /// <summary>Any date at all.</summary>
    Any,
}

public sealed record DateCondition(string Field, DateOp Op, int Days = 0, DateTimeOffset? From = null, DateTimeOffset? To = null) : Condition;

/// <summary>Between <paramref name="Min"/> and <paramref name="Max"/> inclusive; either may be open.</summary>
public sealed record NumberCondition(string Field, double? Min, double? Max) : Condition;

public enum GroupOp
{
    And,
    Or,
}

public sealed record ConditionGroup(GroupOp Op, IReadOnlyList<Condition> Items) : Condition
{
    public static ConditionGroup All(params Condition[] items) => new(GroupOp.And, items);
    public static ConditionGroup Any(params Condition[] items) => new(GroupOp.Or, items);
    public static readonly ConditionGroup Empty = new(GroupOp.And, []);
}

public sealed record SortLevel(string Field, bool Descending = false);

/// <summary>Everything that decides which rows a list shows and in what order.</summary>
public sealed record ListQuery(string Search, ConditionGroup Where, IReadOnlyList<SortLevel> Sort, string? GroupBy = null)
{
    public static readonly ListQuery Empty = new("", ConditionGroup.Empty, []);
}
