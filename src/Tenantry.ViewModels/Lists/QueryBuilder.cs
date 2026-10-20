using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Lists;

namespace Tenantry.ViewModels.Lists;

/// <summary>An operator a rule offers for its field's kind.</summary>
public sealed record RuleOp(string Key, string Label, bool NeedsValue = true)
{
    public override string ToString() => Label;
}

/// <summary>One rule: field, operator, value. Becomes one condition.</summary>
public sealed partial class QueryRuleViewModel : ObservableObject
{
    readonly Func<string, IReadOnlyList<string>> _choices;

    public QueryRuleViewModel(IReadOnlyList<FieldOption> fields, Func<string, IReadOnlyList<string>> choices, Action<QueryRuleViewModel> remove)
    {
        _choices = choices;
        Fields = fields;
        RemoveCommand = new RelayCommand(() => remove(this));
        Value = "";
        Field = fields[0];
    }

    public IReadOnlyList<FieldOption> Fields { get; }
    public IRelayCommand RemoveCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Ops), nameof(ValueChoices), nameof(IsChoice))]
    public partial FieldOption Field { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsValue), nameof(ValueHint))]
    public partial RuleOp? Op { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; }

    public bool IsChoice => Field.Kind is FieldKind.Choice or FieldKind.MultiChoice;
    public bool NeedsValue => Op?.NeedsValue ?? true;
    public IReadOnlyList<string> ValueChoices => IsChoice ? _choices(Field.Key) : [];

    public string ValueHint => Field.Kind switch
    {
        FieldKind.Date => "days",
        FieldKind.Number => "number",
        _ => "value",
    };

    public IReadOnlyList<RuleOp> Ops => OpsFor(Field.Kind);

    partial void OnFieldChanged(FieldOption value)
    {
        Op = OpsFor(value.Kind)[0];
        Value = "";
    }

    public static IReadOnlyList<RuleOp> OpsFor(FieldKind kind) => kind switch
    {
        FieldKind.Choice or FieldKind.MultiChoice => [new("is", "is"), new("isnot", "is not")],
        FieldKind.Date =>
        [
            new("within", "in the last … days"), new("older", "more than … days ago"),
            new("never", "is never / empty", false), new("any", "has a date", false),
        ],
        FieldKind.Number => [new("eq", "="), new("ge", "≥"), new("le", "≤")],
        _ =>
        [
            new("contains", "contains"), new("notcontains", "doesn't contain"), new("is", "is exactly"),
            new("starts", "starts with"), new("empty", "is empty", false), new("notempty", "isn't empty", false),
        ],
    };

    public Condition? ToCondition()
    {
        var op = Op?.Key ?? "";
        var value = Value.Trim();
        if (NeedsValue && value.Length == 0)
            return null;
        int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var days);
        double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var number);
        return (Field.Kind, op) switch
        {
            (FieldKind.Choice or FieldKind.MultiChoice, "is") => new ChoiceCondition(Field.Key, [value], []),
            (FieldKind.Choice or FieldKind.MultiChoice, _) => new ChoiceCondition(Field.Key, [], [value]),
            (FieldKind.Date, "within") when days > 0 => new DateCondition(Field.Key, DateOp.WithinLastDays, days),
            (FieldKind.Date, "older") when days > 0 => new DateCondition(Field.Key, DateOp.OlderThanDays, days),
            (FieldKind.Date, "never") => new DateCondition(Field.Key, DateOp.Never),
            (FieldKind.Date, "any") => new DateCondition(Field.Key, DateOp.Any),
            (FieldKind.Date, _) => null,
            (FieldKind.Number, "ge") => new NumberCondition(Field.Key, number, null),
            (FieldKind.Number, "le") => new NumberCondition(Field.Key, null, number),
            (FieldKind.Number, _) => new NumberCondition(Field.Key, number, number),
            (_, "notcontains") => new TextCondition(Field.Key, TextOp.NotContains, value),
            (_, "is") => new TextCondition(Field.Key, TextOp.Is, value),
            (_, "starts") => new TextCondition(Field.Key, TextOp.StartsWith, value),
            (_, "empty") => new TextCondition(Field.Key, TextOp.IsEmpty),
            (_, "notempty") => new TextCondition(Field.Key, TextOp.IsNotEmpty),
            _ => new TextCondition(Field.Key, TextOp.Contains, value),
        };
    }

    /// <summary>Loads a rule from a condition the builder made; false if it can't be shown as one rule.</summary>
    public bool Load(Condition condition)
    {
        var field = Fields.FirstOrDefault(f => f.Key.Equals(FieldOf(condition), StringComparison.OrdinalIgnoreCase));
        if (field is null)
            return false;
        Field = field;
        (string op, string value) = condition switch
        {
            ChoiceCondition { Include.Count: 1, Exclude.Count: 0 } c => ("is", c.Include[0]),
            ChoiceCondition { Include.Count: 0, Exclude.Count: 1 } c => ("isnot", c.Exclude[0]),
            DateCondition { Op: DateOp.WithinLastDays } d => ("within", d.Days.ToString(CultureInfo.CurrentCulture)),
            DateCondition { Op: DateOp.OlderThanDays } d => ("older", d.Days.ToString(CultureInfo.CurrentCulture)),
            DateCondition { Op: DateOp.Never } => ("never", ""),
            DateCondition { Op: DateOp.Any } => ("any", ""),
            NumberCondition { Min: { } a, Max: { } b } when a == b => ("eq", a.ToString(CultureInfo.CurrentCulture)),
            NumberCondition { Min: { } a, Max: null } => ("ge", a.ToString(CultureInfo.CurrentCulture)),
            NumberCondition { Min: null, Max: { } b } => ("le", b.ToString(CultureInfo.CurrentCulture)),
            TextCondition t => (t.Op switch
            {
                TextOp.NotContains => "notcontains",
                TextOp.Is => "is",
                TextOp.StartsWith => "starts",
                TextOp.IsEmpty => "empty",
                TextOp.IsNotEmpty => "notempty",
                _ => "contains",
            }, t.Value),
            _ => ("", ""),
        };
        Op = Ops.FirstOrDefault(o => o.Key == op);
        Value = value;
        return Op is not null;
    }

    static string FieldOf(Condition c) => c switch
    {
        ChoiceCondition x => x.Field,
        TextCondition x => x.Field,
        DateCondition x => x.Field,
        NumberCondition x => x.Field,
        _ => "",
    };
}

/// <summary>A set of rules joined by All (AND) or Any (OR); the root can hold one level of sub-groups.</summary>
public sealed partial class QueryGroupViewModel : ObservableObject
{
    readonly IReadOnlyList<FieldOption> _fields;
    readonly Func<string, IReadOnlyList<string>> _choices;

    public QueryGroupViewModel(IReadOnlyList<FieldOption> fields, Func<string, IReadOnlyList<string>> choices, bool isRoot, Action<QueryGroupViewModel>? remove = null)
    {
        _fields = fields;
        _choices = choices;
        IsRoot = isRoot;
        RemoveCommand = new RelayCommand(() => remove?.Invoke(this));
    }

    public bool IsRoot { get; }
    public IRelayCommand RemoveCommand { get; }
    public ObservableCollection<QueryRuleViewModel> Rules { get; } = [];
    public ObservableCollection<QueryGroupViewModel> Groups { get; } = [];

    /// <summary>True: any rule may match (OR). False: all must (AND).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JoinText), nameof(MatchAll))]
    public partial bool MatchAny { get; set; }

    /// <summary>The other radio button of the pair.</summary>
    public bool MatchAll
    {
        get => !MatchAny;
        set => MatchAny = !value;
    }

    public string JoinText => MatchAny ? "any" : "all";

    [RelayCommand]
    public void AddRule() => Rules.Add(new QueryRuleViewModel(_fields, _choices, r => Rules.Remove(r)));

    [RelayCommand]
    public void AddGroup()
    {
        if (!IsRoot)
            return;
        var group = new QueryGroupViewModel(_fields, _choices, false, g => Groups.Remove(g));
        group.AddRule();
        Groups.Add(group);
    }

    public ConditionGroup ToCondition() => new(MatchAny ? GroupOp.Or : GroupOp.And,
        Rules.Select(r => r.ToCondition()).OfType<Condition>()
            .Concat(Groups.Select(g => g.ToCondition()).Where(g => g.Items.Count > 0))
            .ToArray());

    public void Load(ConditionGroup group)
    {
        MatchAny = group.Op == GroupOp.Or;
        foreach (var item in group.Items)
        {
            if (item is ConditionGroup sub && IsRoot)
            {
                var child = new QueryGroupViewModel(_fields, _choices, false, g => Groups.Remove(g));
                child.Load(sub);
                Groups.Add(child);
                continue;
            }
            var rule = new QueryRuleViewModel(_fields, _choices, r => Rules.Remove(r));
            if (rule.Load(item))
                Rules.Add(rule);
        }
    }
}
