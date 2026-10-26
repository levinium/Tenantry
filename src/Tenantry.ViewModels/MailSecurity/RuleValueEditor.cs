using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.MailFlow;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>One fixed choice in a pick-several list.</summary>
public sealed partial class CheckChoice(RuleChoice choice, bool isChecked, Action changed) : ObservableObject
{
    public RuleChoice Choice { get; } = choice;
    public string Label => Choice.Label;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>One entry of a list value (an address, a word, a domain…), shown as a chip with ×; red when it's wrong.</summary>
public sealed class ValueChip
{
    public ValueChip(string text, string? problem, Action<ValueChip> remove)
    {
        Text = text;
        Problem = problem;
        RemoveCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => remove(this));
    }

    public string Text { get; }
    public string? Problem { get; }
    public bool HasProblem => Problem is not null;
    public string Tip => Problem is null ? $"Remove {Text}" : $"{Problem}. Remove it and add it again corrected.";
    public CommunityToolkit.Mvvm.Input.IRelayCommand RemoveCommand { get; }
}

/// <summary>
/// The input for one value of a rule (a condition's addresses, a header's name, a size…), shaped by
/// the catalog's value type: a box, a list with one entry per line, a choice, a size with its unit,
/// a date or a switch. Reports each change, and what's wrong with it, as the admin types.
/// </summary>
public sealed partial class RuleValueEditor : ObservableObject
{
    readonly string _valueType;
    readonly bool _required;
    readonly Action<JsonNode?> _changed;
    readonly bool _loading;

    public static IReadOnlyList<string> SizeUnits { get; } = ["KB", "MB", "GB"];

    /// <param name="fallback">What Exchange uses when the value isn't set; a choice shows it selected.</param>
    public RuleValueEditor(string label, string valueType, IReadOnlyList<RuleChoice> choices, bool required, JsonNode? value, Action<JsonNode?> changed,
        string? fallback = null)
    {
        _loading = true;
        Label = label;
        _valueType = valueType;
        _required = required;
        _changed = changed;
        Choices = choices;
        Input = RuleValues.InputFor(valueType, choices);
        MultiChoices = Input == RuleInput.Choices
            ? choices.Select(c => new CheckChoice(c, value is JsonArray list && list.Any(i => string.Equals(i?.ToString(), c.Value, StringComparison.OrdinalIgnoreCase)), OnEdited)).ToArray()
            : [];
        switch (Input)
        {
            case RuleInput.Choice:
                var raw = value is null ? fallback : RuleValues.ToText(value);
                SelectedChoice = choices.FirstOrDefault(c => string.Equals(c.Value, raw, StringComparison.OrdinalIgnoreCase));
                break;
            case RuleInput.Size:
                var bytes = value is JsonValue v && v.TryGetValue<long>(out var b) ? b : Tenantry.Core.Directory.MailDetails.ParseBytes(RuleValues.ToText(value)) ?? 0;
                (SizeAmount, SizeUnit) = bytes switch
                {
                    0 => ((decimal?)null, "MB"),
                    _ when bytes % (1L << 30) == 0 => (bytes / (1L << 30), "GB"),
                    _ when bytes % (1L << 20) == 0 => (bytes / (1L << 20), "MB"),
                    _ => (Math.Round(bytes / 1024m, 2), "KB"),
                };
                break;
            case RuleInput.Date:
                Date = DateTime.TryParse(RuleValues.ToText(value), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.Date : null;
                break;
            case RuleInput.Switch:
                IsOn = value is JsonValue on && on.GetValueKind() == JsonValueKind.True;
                break;
            case RuleInput.Lines:
                foreach (var entry in value is JsonArray entries ? entries.Select(e => e?.ToString() ?? "") : RuleValues.SplitLines(RuleValues.ToText(value), valueType))
                    if (entry.Length > 0)
                        Chips.Add(NewChip(entry));
                break;
            default:
                Text = RuleValues.ToText(value);
                break;
        }
        _loading = false;
        Problem = FindProblem();
    }

    public string Label { get; }

    public RuleInput Input { get; }

    public bool IsText => Input == RuleInput.Text;
    public bool IsLongText => Input == RuleInput.LongText;
    public bool IsLines => Input == RuleInput.Lines;
    public bool IsChoice => Input == RuleInput.Choice;
    public bool IsChoices => Input == RuleInput.Choices;
    public bool IsNumber => Input == RuleInput.Number;
    public bool IsSize => Input == RuleInput.Size;
    public bool IsDate => Input == RuleInput.Date;
    public bool IsSwitch => Input == RuleInput.Switch;

    /// <summary>The grey text in an empty box: what to type, with an example. A list adds each entry on Enter.</summary>
    public string Hint => _valueType switch
    {
        "addresses" or "betweenGroups" => Chips.Count == 0 ? "Type an address, like pat@contoso.com, and press Enter" : "Add another address",
        "groups" => Chips.Count == 0 ? "Type a group's address and press Enter" : "Add another group",
        "words" or "headerWords" => Chips.Count == 0 ? "Type a word or phrase and press Enter" : "Add another word or phrase",
        "patterns" or "headerPatterns" => Chips.Count == 0 ? @"Type a pattern, like invoice\s*#?\d+, and press Enter" : "Add another pattern",
        "domains" => Chips.Count == 0 ? "Type a domain, like contoso.com, and press Enter" : "Add another domain",
        "ipRanges" => Chips.Count == 0 ? "Type 192.0.2.10, 192.0.2.0/24 or 192.0.2.1-192.0.2.50 and press Enter" : "Add another address or range",
        "adAttributeWords" => Chips.Count == 0 ? "Type Property:words, like Department:Sales,Marketing, and press Enter" : "Add another property",
        "adAttributePatterns" => Chips.Count == 0 ? @"Type Property:patterns, like Title:^Director\b, and press Enter" : "Add another property",
        "documentPropertyWords" => Chips.Count == 0 ? "Type Property:words, like Department:Finance, and press Enter" : "Add another property",
        "sensitiveInfoTypes" => "Type one and press Enter",
        "headerName" => "Like X-Contoso-Checked",
        "dsnCode" => "5.7.1, or 5.7.900 to 5.7.999",
        "html" => "Text or HTML. %%From%%, %%To%%, %%Subject%% and other tokens are filled in",
        "address" => "pat@contoso.com",
        _ => "",
    };

    /// <summary>The property names a "Name:values" list accepts, to show beside it.</summary>
    public string? NamesHint => _valueType is "adAttributeWords" or "adAttributePatterns" or "documentPropertyWords" && Choices.Count > 0
        ? "Properties: " + string.Join(", ", Choices.Select(c => c.Value))
        : null;

    public bool HasNamesHint => NamesHint is not null;

    public IReadOnlyList<RuleChoice> Choices { get; }

    public IReadOnlyList<CheckChoice> MultiChoices { get; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    // ---- Lists: one chip per entry ----------------------------------------------------------

    public System.Collections.ObjectModel.ObservableCollection<ValueChip> Chips { get; } = [];

    public bool HasChips => Chips.Count > 0;

    /// <summary>What's being typed into a list, before Enter adds it. Pasting a list (or typing a comma) adds each entry at once.</summary>
    [ObservableProperty]
    public partial string NewEntry { get; set; } = "";

    partial void OnNewEntryChanged(string value)
    {
        if (value.Contains('\n') || RuleValues.CommasSeparate(_valueType) && (value.Contains(',') || value.Contains(';')))
            AddEntry();
        else
            OnEdited();
    }

    ValueChip NewChip(string entry) => new(entry, RuleValues.EntryProblem(_valueType, entry, Choices), RemoveChip);

    /// <summary>Adds what's typed as one chip (or several, from a pasted list); repeats are skipped.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void AddEntry()
    {
        var entries = RuleValues.SplitLines(NewEntry, _valueType);
        foreach (var entry in entries)
            if (!Chips.Any(c => string.Equals(c.Text, entry, StringComparison.OrdinalIgnoreCase)))
                Chips.Add(NewChip(entry));
        NewEntry = "";
        ChipsChanged();
    }

    void RemoveChip(ValueChip chip)
    {
        Chips.Remove(chip);
        ChipsChanged();
    }

    /// <summary>Backspace in an empty box takes the last chip back into the box to fix.</summary>
    public void EditLastChip()
    {
        if (Chips.Count == 0 || NewEntry.Length > 0)
            return;
        var last = Chips[^1];
        Chips.Remove(last);
        NewEntry = last.Text;
        ChipsChanged();
    }

    /// <summary>What's wrong, in plain words: for a list, the first entry that needs fixing, by name.</summary>
    string? FindProblem()
    {
        if (Input != RuleInput.Lines)
            return RuleValues.Problem(_valueType, Value, _required, Choices);
        foreach (var entry in Chips.Select(c => c.Text).Concat(RuleValues.SplitLines(NewEntry, _valueType)))
            if (RuleValues.EntryProblem(_valueType, entry, Choices) is { } problem)
                return problem.StartsWith($"'{entry}'", StringComparison.Ordinal) ? problem : $"'{entry}': {problem}";
        return RuleValues.Problem(_valueType, Value, _required, Choices);
    }

    void ChipsChanged()
    {
        OnPropertyChanged(nameof(HasChips));
        OnPropertyChanged(nameof(Hint));
        OnEdited();
    }

    [ObservableProperty]
    public partial RuleChoice? SelectedChoice { get; set; }

    [ObservableProperty]
    public partial decimal? SizeAmount { get; set; }

    [ObservableProperty]
    public partial string SizeUnit { get; set; } = "MB";

    [ObservableProperty]
    public partial DateTime? Date { get; set; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    partial void OnTextChanged(string value) => OnEdited();
    partial void OnSelectedChoiceChanged(RuleChoice? value) => OnEdited();
    partial void OnSizeAmountChanged(decimal? value) => OnEdited();
    partial void OnSizeUnitChanged(string value) => OnEdited();
    partial void OnDateChanged(DateTime? value) => OnEdited();
    partial void OnIsOnChanged(bool value) => OnEdited();

    void OnEdited()
    {
        if (_loading)
            return;
        var value = Value;
        Problem = FindProblem();
        _changed(value);
    }

    /// <summary>What the input holds, as the draft keeps it; null when empty.</summary>
    public JsonNode? Value => Input switch
    {
        RuleInput.None => JsonValue.Create(true),
        // What's typed but not yet added counts too: leaving the box shouldn't lose it.
        RuleInput.Lines => Chips.Select(c => c.Text).Concat(RuleValues.SplitLines(NewEntry, _valueType)).ToArray() is { Length: > 0 } lines
            ? new JsonArray(lines.Select(l => (JsonNode)l).ToArray())
            : null,
        RuleInput.Choice => SelectedChoice is { } c ? JsonValue.Create(c.Value) : null,
        RuleInput.Choices => MultiChoices.Where(c => c.IsChecked).Select(c => (JsonNode)c.Choice.Value).ToArray() is { Length: > 0 } picked ? new JsonArray(picked) : null,
        RuleInput.Number => long.TryParse(Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? JsonValue.Create(n) : string.IsNullOrWhiteSpace(Text) ? null : JsonValue.Create(Text.Trim()),
        RuleInput.Size => SizeAmount is { } amount && amount > 0
            ? JsonValue.Create((long)Math.Round(amount *(SizeUnit switch { "GB" => 1L << 30, "KB" => 1L << 10, _ => 1L << 20 })))
            : null,
        RuleInput.Date => Date is { } d ? JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : null,
        RuleInput.Switch => IsOn ? JsonValue.Create(true) : null,
        _ => string.IsNullOrWhiteSpace(Text) ? null : JsonValue.Create(Text),
    };
}
