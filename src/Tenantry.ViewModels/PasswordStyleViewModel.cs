using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;

namespace Tenantry.ViewModels;

/// <summary>One choice in a list, with the words people see.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
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
        _loading = false;
        Changed();
    }

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
    public PasswordStyle Style => new((int)Length, Letters.Value, Numbers, Symbols.Value, Groups, AvoidLookAlikes);

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
    }

    [RelayCommand]
    void NewExample()
    {
        if (Problem is null)
            Example = Style.Create();
    }

    [RelayCommand]
    void UseDefault() => Load(PasswordStyle.Default);
}
