using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;

namespace Tenantry.ViewModels;

/// <summary>One choice in a list, with the words people see.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One part of a password pattern as a row: what kind, how many, and for letters the capital and small-letter rules.</summary>
public sealed partial class PatternPartRow : ObservableObject
{
    readonly Action _changed;
    readonly Action<PatternPartRow, int> _move;
    readonly Action<PatternPartRow> _remove;
    bool _loading;

    public static IReadOnlyList<Choice<PatternKind>> Kinds { get; } =
    [
        new(PatternKind.Letters, "Letters (Capital or Small)"),
        new(PatternKind.Capitals, "Capital Letters"),
        new(PatternKind.SmallLetters, "Small Letters"),
        new(PatternKind.Digits, "Digits"),
        new(PatternKind.PhoneSymbols, "Symbols Easy to Type on a Phone"),
        new(PatternKind.AnySymbols, "Any Symbols"),
        new(PatternKind.LettersOrDigits, "Letters or Digits"),
        new(PatternKind.Exact, "Exact Text"),
    ];

    public PatternPartRow(PatternPart part, Action changed, Action<PatternPartRow, int> move, Action<PatternPartRow> remove)
    {
        _changed = changed;
        _move = move;
        _remove = remove;
        _loading = true;
        Kind = Kinds.First(k => k.Value == part.Kind);
        Count = part.Min;
        UpTo = part.IsRange ? part.Max : null;
        MinCapitals = part.MinCapitals;
        MinSmall = part.MinSmall;
        Text = part.Text ?? "";
        _loading = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLetterRules), nameof(IsExact), nameof(HasCount))]
    public partial Choice<PatternKind> Kind { get; set; }

    [ObservableProperty]
    public partial decimal Count { get; set; }

    /// <summary>When set, a range: from Count up to this.</summary>
    [ObservableProperty]
    public partial decimal? UpTo { get; set; }

    [ObservableProperty]
    public partial decimal MinCapitals { get; set; }

    [ObservableProperty]
    public partial decimal MinSmall { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; }

    public bool HasLetterRules => Kind.Value is PatternKind.Letters or PatternKind.LettersOrDigits;
    public bool IsExact => Kind.Value == PatternKind.Exact;
    public bool HasCount => !IsExact;

    partial void OnKindChanged(Choice<PatternKind> value) => Edited();
    partial void OnCountChanged(decimal value) => Edited();
    partial void OnUpToChanged(decimal? value) => Edited();
    partial void OnMinCapitalsChanged(decimal value) => Edited();
    partial void OnMinSmallChanged(decimal value) => Edited();
    partial void OnTextChanged(string value) => Edited();

    void Edited()
    {
        if (!_loading)
            _changed();
    }

    public PatternPart ToPart() => IsExact
        ? new PatternPart(PatternKind.Exact, 1, 1, Text: Text)
        : new PatternPart(Kind.Value, (int)Count, UpTo is { } max && max > Count ? (int)max : (int)Count,
            HasLetterRules ? (int)MinCapitals : 0, HasLetterRules ? (int)MinSmall : 0);

    [RelayCommand]
    void MoveUp() => _move(this, -1);

    [RelayCommand]
    void MoveDown() => _move(this, 1);

    [RelayCommand]
    void Remove() => _remove(this);
}

/// <summary>
/// Settings › Passwords: how Tenantry makes new passwords for this tenant. Each choice saves at once
/// when the result is a password Microsoft 365 accepts; otherwise it says what to change. An
/// example shows what the passwords will look like.
/// </summary>
public sealed partial class PasswordStyleViewModel : ObservableObject
{
    readonly IPasswordStyleStore _store;
    bool _loading;

    public static IReadOnlyList<Choice<PasswordLetters>> LetterChoices { get; } =
    [
        new(PasswordLetters.Mixed, "Capital and Small Letters (Aa)"),
        new(PasswordLetters.LowerOnly, "Small Letters Only (a)"),
        new(PasswordLetters.UpperOnly, "Capital Letters Only (A)"),
        new(PasswordLetters.None, "No Letters"),
    ];

    public static IReadOnlyList<Choice<PasswordSymbols>> SymbolChoices { get; } =
    [
        new(PasswordSymbols.None, "No Symbols"),
        new(PasswordSymbols.EasyToType, "Symbols That Are Easy to Type on a Phone:  ! @ # $ % & * ? + ="),
        new(PasswordSymbols.Any, "Any Symbols, Including Ones That Are Hard to Find on a Phone"),
    ];

    public PasswordStyleViewModel(IPasswordStyleStore store)
    {
        _store = store;
        Load(store.Load());
    }

    void Load(PasswordStyle style)
    {
        _loading = true;
        Length = style.Length;
        Letters = LetterChoices.First(c => c.Value == style.Letters);
        Numbers = style.Numbers;
        Symbols = SymbolChoices.First(c => c.Value == style.Symbols);
        Groups = style.Groups;
        AvoidLookAlikes = style.AvoidLookAlikes;
        UsePattern = style.UsesPattern;
        SetPatternText(style.Pattern ?? StarterPattern, fromRows: false);
        _loading = false;
        Changed();
    }

    // ---- Pattern ------------------------------------------------------------------------------------

    /// <summary>Offered when switching to a pattern for the first time: passes Microsoft 365's rules.</summary>
    public const string StarterPattern = "L5+1U+1l D3 S2";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseSimple))]
    public partial bool UsePattern { get; set; }

    public bool UseSimple
    {
        get => !UsePattern;
        set => UsePattern = !value;
    }

    partial void OnUsePatternChanged(bool value) => Changed();

    public System.Collections.ObjectModel.ObservableCollection<PatternPartRow> Parts { get; } = [];

    string _patternText = "";
    bool _syncing;

    /// <summary>The pattern as short text; typing a valid one rebuilds the rows, and editing the rows rewrites it.</summary>
    public string PatternText
    {
        get => _patternText;
        set
        {
            if (_patternText == value)
                return;
            SetPatternText(value, fromRows: false);
            Changed();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPatternTextProblem))]
    public partial string? PatternTextProblem { get; private set; }

    public bool HasPatternTextProblem => PatternTextProblem is not null;

    void SetPatternText(string text, bool fromRows)
    {
        _patternText = text;
        OnPropertyChanged(nameof(PatternText));
        if (fromRows)
            return;
        var (parts, problem) = PasswordPattern.Parse(text);
        PatternTextProblem = problem;
        if (problem is not null)
            return;
        _syncing = true;
        Parts.Clear();
        foreach (var part in parts)
            Parts.Add(Row(part));
        _syncing = false;
    }

    PatternPartRow Row(PatternPart part) => new(part, RowsChanged, MoveRow, RemoveRow);

    /// <summary>A row changed: the text follows.</summary>
    void RowsChanged()
    {
        if (_syncing)
            return;
        PatternTextProblem = null;
        SetPatternText(PasswordPattern.ToText(Parts.Select(r => r.ToPart())), fromRows: true);
        Changed();
    }

    void MoveRow(PatternPartRow row, int by)
    {
        var at = Parts.IndexOf(row);
        var to = at + by;
        if (to < 0 || to >= Parts.Count)
            return;
        Parts.Move(at, to);
        RowsChanged();
    }

    void RemoveRow(PatternPartRow row)
    {
        Parts.Remove(row);
        RowsChanged();
    }

    [RelayCommand]
    void AddPart()
    {
        Parts.Add(Row(new PatternPart(PatternKind.Digits, 1, 1)));
        RowsChanged();
    }

    /// <summary>The pattern's parts in words: "5 letters (at least 1 capital), then 3 digits…".</summary>
    public string PatternHelp =>
        "Each part is a letter code and how many, in the order they appear in the password. " +
        "L is letters, U capitals, l small letters, D digits, S symbols that are easy to type on a phone, P any symbol, A a letter or digit, and 'text' is that exact text. " +
        "D3 means three digits; D6-8 means six to eight. L5+1U means five letters with at least one capital.";

    public int MinLength => PasswordStyle.MinLength;
    public int MaxLength => PasswordStyle.MaxLength;

    [ObservableProperty]
    public partial decimal Length { get; set; }

    [ObservableProperty]
    public partial Choice<PasswordLetters> Letters { get; set; }

    [ObservableProperty]
    public partial bool Numbers { get; set; }

    [ObservableProperty]
    public partial Choice<PasswordSymbols> Symbols { get; set; }

    [ObservableProperty]
    public partial bool Groups { get; set; }

    [ObservableProperty]
    public partial bool AvoidLookAlikes { get; set; }

    partial void OnLengthChanged(decimal value) => Changed();
    partial void OnLettersChanged(Choice<PasswordLetters> value) => Changed();
    partial void OnNumbersChanged(bool value) => Changed();
    partial void OnSymbolsChanged(Choice<PasswordSymbols> value) => Changed();
    partial void OnGroupsChanged(bool value) => Changed();
    partial void OnAvoidLookAlikesChanged(bool value) => Changed();

    /// <summary>The style as chosen on screen (saved or not).</summary>
    public PasswordStyle Style => new((int)Length, Letters.Value, Numbers, Symbols.Value, Groups, AvoidLookAlikes, UsePattern ? PatternText : null);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    [ObservableProperty]
    public partial string Description { get; private set; } = "";

    [ObservableProperty]
    public partial string Example { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsDefault { get; private set; }

    void Changed()
    {
        if (_loading)
            return;
        var style = Style;
        Problem = style.Problem;
        Description = style.Description;
        IsDefault = style == PasswordStyle.Default;
        if (Problem is not null)
        {
            Example = "";
            return;
        }
        _store.Save(style);
        Example = style.Create();
        Examples = Enumerable.Range(0, 3).Select(_ => style.Create()).ToArray();
    }

    /// <summary>A few at once, so a pattern's variety shows.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Examples { get; private set; } = [];

    [RelayCommand]
    void NewExample()
    {
        if (Problem is null)
        {
            Example = Style.Create();
            Examples = Enumerable.Range(0, 3).Select(_ => Style.Create()).ToArray();
        }
    }

    [RelayCommand]
    void UseDefault() => Load(PasswordStyle.Default);
}
