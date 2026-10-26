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
            default:
                Text = RuleValues.ToText(value);
                break;
        }
        _loading = false;
        Problem = RuleValues.Problem(valueType, Value, required, choices);
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

    /// <summary>The grey text in an empty box: what to type, with an example.</summary>
    public string Hint => _valueType switch
    {
        "addresses" or "groups" or "betweenGroups" => "One address per line, like pat@contoso.com",
        "words" or "characterSets" => "One word or phrase per line",
        "patterns" or "headerPatterns" => @"One pattern per line, like invoice\s*#?\d+",
        "headerWords" => "One word or phrase per line",
        "domains" => "One domain per line, like contoso.com",
        "ipRanges" => "One per line: 192.0.2.10, 192.0.2.0/24 or 192.0.2.1-192.0.2.50",
        "adAttributeWords" => "One per line, like Department:Sales,Marketing",
        "adAttributePatterns" => @"One per line, like Title:^Director\b",
        "documentPropertyWords" => "One per line, like Department:Finance,Legal",
        "sensitiveInfoTypes" => "One per line",
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
        Problem = RuleValues.Problem(_valueType, value, _required, Choices);
        _changed(value);
    }

    /// <summary>What the input holds, as the draft keeps it; null when empty.</summary>
    public JsonNode? Value => Input switch
    {
        RuleInput.None => JsonValue.Create(true),
        RuleInput.Lines => RuleValues.SplitLines(Text, _valueType) is { Length: > 0 } lines ? new JsonArray(lines.Select(l => (JsonNode)l).ToArray()) : null,
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
