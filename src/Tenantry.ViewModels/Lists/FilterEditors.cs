using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Lists;

namespace Tenantry.ViewModels.Lists;

/// <summary>
/// The editor for one field's filter, opened from the Add filter menu, a pill, or a column
/// header. One subclass per field kind; each turns its inputs into a condition (or none).
/// </summary>
public abstract partial class FilterEditorViewModel(FieldOption field) : ObservableObject
{
    public FieldOption Field { get; } = field;
    public string Title => $"Filter by {Field.Label}";

    /// <summary>The condition the inputs describe, or null for "no filter on this field".</summary>
    public abstract Condition? Build();
}

public sealed partial class ChoiceValueOption : ObservableObject
{
    public ChoiceValueOption(string value, int count)
    {
        Value = value;
        Count = count;
        IsShown = true;
    }

    public string Value { get; }
    public int Count { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial bool IsShown { get; set; }
}

/// <summary>
/// Pick values with checkboxes, then choose whether to show only those or hide them. Simpler to
/// read than a three-state box per value, and it covers both "only blocked" and "not blocked".
/// </summary>
public sealed partial class ChoiceFilterEditor : FilterEditorViewModel
{
    public ChoiceFilterEditor(FieldOption field, IReadOnlyList<FacetValue> facets, ChoiceCondition? existing) : base(field)
    {
        var hide = existing is { Include.Count: 0, Exclude.Count: > 0 };
        var chosen = new HashSet<string>(hide ? existing!.Exclude : existing?.Include ?? [], StringComparer.OrdinalIgnoreCase);
        Values = new ObservableCollection<ChoiceValueOption>(facets.Select(f => new ChoiceValueOption(f.Value, f.Count) { IsChecked = chosen.Contains(f.Value) }));
        // Keep any chosen value the current data no longer has, so reopening never drops it silently.
        foreach (var missing in chosen.Where(c => Values.All(v => !string.Equals(v.Value, c, StringComparison.OrdinalIgnoreCase))))
            Values.Add(new ChoiceValueOption(missing, 0) { IsChecked = true });
        foreach (var v in Values)
            v.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChoiceValueOption.IsChecked))
                    OnPropertyChanged(nameof(SelectedText));
            };
        HideSelected = hide;
        ValueSearch = "";
    }

    public string SelectedText => Values.Count(v => v.IsChecked) switch
    {
        0 => "Nothing selected",
        var n => $"{n} selected",
    };

    public ObservableCollection<ChoiceValueOption> Values { get; }

    /// <summary>False: show only the checked values. True: hide the checked values.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelected))]
    public partial bool HideSelected { get; set; }

    /// <summary>The other radio button of the pair.</summary>
    public bool ShowSelected
    {
        get => !HideSelected;
        set => HideSelected = !value;
    }

    [ObservableProperty]
    public partial string ValueSearch { get; set; }

    public bool HasManyValues => Values.Count > 8;

    partial void OnValueSearchChanged(string value)
    {
        foreach (var v in Values)
            v.IsShown = value.Length == 0 || v.Value.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    void SelectAll()
    {
        foreach (var v in Values.Where(v => v.IsShown))
            v.IsChecked = true;
    }

    [RelayCommand]
    void SelectNone()
    {
        foreach (var v in Values)
            v.IsChecked = false;
    }

    public override Condition? Build()
    {
        var chosen = Values.Where(v => v.IsChecked).Select(v => v.Value).ToArray();
        if (chosen.Length == 0)
            return null;
        return HideSelected ? new ChoiceCondition(Field.Key, [], chosen) : new ChoiceCondition(Field.Key, chosen, []);
    }
}

public sealed record OpOption<TOp>(TOp Op, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class TextFilterEditor : FilterEditorViewModel
{
    public static IReadOnlyList<OpOption<TextOp>> Ops { get; } =
    [
        new(TextOp.Contains, "contains"),
        new(TextOp.NotContains, "doesn't contain"),
        new(TextOp.Is, "is exactly"),
        new(TextOp.StartsWith, "starts with"),
        new(TextOp.IsEmpty, "is empty"),
        new(TextOp.IsNotEmpty, "isn't empty"),
    ];

    public TextFilterEditor(FieldOption field, TextCondition? existing) : base(field)
    {
        Op = Ops.First(o => o.Op == (existing?.Op ?? TextOp.Contains));
        Value = existing?.Value ?? "";
    }

    public IReadOnlyList<OpOption<TextOp>> Operators => Ops;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsValue))]
    public partial OpOption<TextOp> Op { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; }

    public bool NeedsValue => Op.Op is not (TextOp.IsEmpty or TextOp.IsNotEmpty);

    public override Condition? Build() =>
        NeedsValue && string.IsNullOrWhiteSpace(Value) ? null : new TextCondition(Field.Key, Op.Op, NeedsValue ? Value.Trim() : "");
}

/// <summary>A date filter choice: a preset, a custom number of days, or a range.</summary>
public sealed record DatePreset(string Label, DateOp Op, int Days = 0, bool CustomDays = false, bool Range = false)
{
    public override string ToString() => Label;
}

public sealed partial class DateFilterEditor : FilterEditorViewModel
{
    public static IReadOnlyList<DatePreset> Presets { get; } =
    [
        new("In the last 7 days", DateOp.WithinLastDays, 7),
        new("In the last 30 days", DateOp.WithinLastDays, 30),
        new("In the last 90 days", DateOp.WithinLastDays, 90),
        new("More than 30 days ago", DateOp.OlderThanDays, 30),
        new("More than 90 days ago", DateOp.OlderThanDays, 90),
        new("More than 180 days ago", DateOp.OlderThanDays, 180),
        new("In the last … days", DateOp.WithinLastDays, CustomDays: true),
        new("More than … days ago", DateOp.OlderThanDays, CustomDays: true),
        new("Between two dates", DateOp.Between, Range: true),
        new("Never / no date", DateOp.Never),
        new("Has any date", DateOp.Any),
    ];

    public DateFilterEditor(FieldOption field, DateCondition? existing) : base(field)
    {
        Preset = existing switch
        {
            null => Presets[0],
            { Op: DateOp.Between } => Presets.First(p => p.Range),
            { Op: DateOp.Never or DateOp.Any } e => Presets.First(p => p.Op == e.Op),
            var e => Presets.FirstOrDefault(p => !p.CustomDays && p.Op == e.Op && p.Days == e.Days)
                     ?? Presets.First(p => p.CustomDays && p.Op == e.Op),
        };
        DaysText = existing is { Op: DateOp.WithinLastDays or DateOp.OlderThanDays } d ? d.Days.ToString(CultureInfo.CurrentCulture) : "60";
        From = existing?.From?.LocalDateTime.Date;
        To = existing?.To?.LocalDateTime.Date;
    }

    public IReadOnlyList<DatePreset> Choices => Presets;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsDays), nameof(NeedsRange))]
    public partial DatePreset Preset { get; set; }

    [ObservableProperty]
    public partial string DaysText { get; set; }

    /// <summary>Local calendar days, as a date picker gives them.</summary>
    [ObservableProperty]
    public partial DateTime? From { get; set; }

    [ObservableProperty]
    public partial DateTime? To { get; set; }

    public bool NeedsDays => Preset.CustomDays;
    public bool NeedsRange => Preset.Range;

    public override Condition? Build()
    {
        if (Preset.Range)
        {
            if (From is null && To is null)
                return null;
            // Both are local days: "from" starts at its midnight, "to" includes all of its day.
            DateTimeOffset? start = From is { } f ? new DateTimeOffset(DateTime.SpecifyKind(f.Date, DateTimeKind.Local)) : null;
            DateTimeOffset? endOfDay = To is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local)) : null;
            return new DateCondition(Field.Key, DateOp.Between, From: start, To: endOfDay);
        }
        if (Preset.CustomDays)
            return int.TryParse(DaysText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var days) && days > 0
                ? new DateCondition(Field.Key, Preset.Op, days)
                : null;
        return new DateCondition(Field.Key, Preset.Op, Preset.Days);
    }
}

public sealed partial class NumberFilterEditor : FilterEditorViewModel
{
    public NumberFilterEditor(FieldOption field, NumberCondition? existing) : base(field)
    {
        MinText = existing?.Min?.ToString(CultureInfo.CurrentCulture) ?? "";
        MaxText = existing?.Max?.ToString(CultureInfo.CurrentCulture) ?? "";
    }

    [ObservableProperty]
    public partial string MinText { get; set; }

    [ObservableProperty]
    public partial string MaxText { get; set; }

    static double? Parse(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out var n) ? n : null;

    public override Condition? Build()
    {
        var (min, max) = (Parse(MinText), Parse(MaxText));
        return min is null && max is null ? null : new NumberCondition(Field.Key, min, max);
    }
}
