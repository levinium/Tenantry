using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tenantry.Core.Changes;

/// <summary>What one part of a password pattern is made of.</summary>
public enum PatternKind
{
    /// <summary>Capital or small letters.</summary>
    Letters,
    Capitals,
    SmallLetters,
    Digits,

    /// <summary>Symbols on a phone keyboard's first symbols page.</summary>
    PhoneSymbols,
    AnySymbols,

    /// <summary>Letters or digits.</summary>
    LettersOrDigits,

    /// <summary>Always the same text (a separator, a prefix).</summary>
    Exact,
}

/// <summary>
/// One part of a password pattern, in order: a kind of character, how many (a range when Min and
/// Max differ), and for letters how many must be capital or small. Exact parts carry their text.
/// </summary>
public sealed record PatternPart(PatternKind Kind, int Min, int Max, int MinCapitals = 0, int MinSmall = 0, string? Text = null)
{
    public bool IsRange => Max > Min;

    /// <summary>The part as pattern text: "L4+1U", "D6-8", "S2", "'-'".</summary>
    public override string ToString()
    {
        if (Kind == PatternKind.Exact)
            return $"'{Text}'";
        var code = Kind switch
        {
            PatternKind.Letters => "L",
            PatternKind.Capitals => "U",
            PatternKind.SmallLetters => "l",
            PatternKind.Digits => "D",
            PatternKind.PhoneSymbols => "S",
            PatternKind.AnySymbols => "P",
            _ => "A",
        };
        var text = new StringBuilder(code).Append(Min.ToString(CultureInfo.InvariantCulture));
        if (IsRange)
            text.Append('-').Append(Max.ToString(CultureInfo.InvariantCulture));
        if (MinCapitals > 0)
            text.Append('+').Append(MinCapitals.ToString(CultureInfo.InvariantCulture)).Append('U');
        if (MinSmall > 0)
            text.Append('+').Append(MinSmall.ToString(CultureInfo.InvariantCulture)).Append('l');
        return text.ToString();
    }

    /// <summary>"3 letters (at least 1 capital)", "7 digits", "the text '-'".</summary>
    public string Describe()
    {
        if (Kind == PatternKind.Exact)
            return $"the text '{Text}'";
        var count = IsRange ? $"{Min} to {Max}" : Min.ToString(CultureInfo.InvariantCulture);
        var noun = Kind switch
        {
            PatternKind.Letters => "letters",
            PatternKind.Capitals => "capital letters",
            PatternKind.SmallLetters => "small letters",
            PatternKind.Digits => "digits",
            PatternKind.PhoneSymbols => "symbols that are easy to type on a phone",
            PatternKind.AnySymbols => "symbols",
            _ => "letters or digits",
        };
        if (Min == 1 && !IsRange)
            noun = noun switch
            {
                "letters" => "letter",
                "capital letters" => "capital letter",
                "small letters" => "small letter",
                "digits" => "digit",
                "symbols" => "symbol",
                "letters or digits" => "letter or digit",
                _ => noun.Replace("symbols that are", "symbol that is", StringComparison.Ordinal),
            };
        var rules = new[] { MinCapitals > 0 ? $"at least {MinCapitals} capital" : null, MinSmall > 0 ? $"at least {MinSmall} small" : null }.OfType<string>().ToArray();
        return $"{count} {noun}" + (rules.Length == 0 ? "" : $" ({string.Join(", ", rules)})");
    }
}

/// <summary>
/// A password format as parts in order, written as short text: L letters, U capitals, l small
/// letters, D digits, S symbols easy to type on a phone, P any symbols, A letters or digits, and
/// 'text' for exact text. Each kind takes a count (D7) or a range (D6-8); letters can require
/// capitals or small ones (L4+1U). Example: "L5+1U D3 S2".
/// </summary>
public static class PasswordPattern
{
    const string Capitals = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    const string Small = "abcdefghijklmnopqrstuvwxyz";
    const string Digits = "0123456789";
    const string LookAlikes = "0Oo1lI5S2Z";
    public const string PhoneSymbols = "!@#$%&*?+=";
    const string AnySymbols = "!@#$%^&*()-_=+[]{};:,.<>/?~";

    /// <summary>Microsoft 365 takes passwords of up to 256 characters; a pattern stays well under.</summary>
    public const int MaxCount = 64;

    /// <summary>The parts of a pattern, or why the text can't be read.</summary>
    public static (IReadOnlyList<PatternPart> Parts, string? Problem) Parse(string text)
    {
        var parts = new List<PatternPart>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            if (text[i] == '\'')
            {
                var end = text.IndexOf('\'', i + 1);
                if (end < 0)
                    return ([], "Close the exact text with a ' .");
                if (end == i + 1)
                    return ([], "Exact text in quotes can't be empty.");
                parts.Add(new PatternPart(PatternKind.Exact, 1, 1, Text: text[(i + 1)..end]));
                i = end + 1;
                continue;
            }
            PatternKind? kind = text[i] switch
            {
                'L' => PatternKind.Letters,
                'U' => PatternKind.Capitals,
                'l' => PatternKind.SmallLetters,
                'D' or 'd' => PatternKind.Digits,
                'S' or 's' => PatternKind.PhoneSymbols,
                'P' or 'p' => PatternKind.AnySymbols,
                'A' or 'a' => PatternKind.LettersOrDigits,
                _ => null,
            };
            if (kind is null)
                return ([], $"'{text[i]}' isn't a part. Use L, U, l, D, S, P, A or 'text'.");
            i++;
            var min = Number(text, ref i);
            if (min is null)
                return ([], $"Say how many after {text[i - 1]}, like {text[i - 1]}3.");
            var max = min.Value;
            if (i < text.Length && text[i] == '-')
            {
                i++;
                max = Number(text, ref i) ?? -1;
                if (max < min)
                    return ([], "A range goes from the smaller number to the larger, like D6-8.");
            }
            int capitals = 0, small = 0;
            while (i < text.Length && text[i] == '+')
            {
                i++;
                var count = Number(text, ref i);
                if (count is null || i >= text.Length || text[i] is not ('U' or 'l'))
                    return ([], "Write a rule like +1U (at least 1 capital) or +1l (at least 1 small letter).");
                if (kind is not (PatternKind.Letters or PatternKind.LettersOrDigits))
                    return ([], "Only L (letters) and A (letters or digits) parts take capital or small-letter rules.");
                if (text[i] == 'U')
                    capitals = count.Value;
                else
                    small = count.Value;
                i++;
            }
            if (min == 0 && max == 0)
                return ([], "A part needs at least one character.");
            if (max > MaxCount)
                return ([], $"A part can have at most {MaxCount} characters.");
            if (capitals + small > min)
                return ([], $"{new PatternPart(kind.Value, min.Value, max, capitals, small)} asks for more capitals and small letters than it has letters.");
            parts.Add(new PatternPart(kind.Value, min.Value, max, capitals, small));
        }
        return parts.Count == 0 ? ([], "Add at least one part, like L5+1U D3 S2.") : (parts, null);
    }

    static int? Number(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
            i++;
        return i > start && int.TryParse(text[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    public static string ToText(IEnumerable<PatternPart> parts) => string.Join(' ', parts);

    /// <summary>
    /// Why passwords from these parts wouldn't be accepted, or null. Microsoft 365 needs at least 8
    /// characters and three of: capitals, small letters, digits, symbols. A kind counts only if
    /// every password is sure to have it.
    /// </summary>
    public static string? Problem(IReadOnlyList<PatternPart> parts)
    {
        if (parts.Count == 0)
            return "Add at least one part.";
        var shortest = parts.Sum(p => p.Kind == PatternKind.Exact ? p.Text!.Length : p.Min);
        if (shortest < 8)
            return $"Passwords from this pattern can be {shortest} characters; Microsoft 365 needs at least 8.";
        var sure = new HashSet<string>();
        foreach (var p in parts)
        {
            switch (p.Kind)
            {
                case PatternKind.Capitals:
                    sure.Add("capitals");
                    break;
                case PatternKind.SmallLetters:
                    sure.Add("small letters");
                    break;
                case PatternKind.Digits:
                    sure.Add("digits");
                    break;
                case PatternKind.PhoneSymbols or PatternKind.AnySymbols:
                    sure.Add("symbols");
                    break;
                case PatternKind.Exact:
                    var exact = p.Text ?? "";
                    if (exact.Any(char.IsUpper)) sure.Add("capitals");
                    if (exact.Any(char.IsLower)) sure.Add("small letters");
                    if (exact.Any(char.IsDigit)) sure.Add("digits");
                    if (exact.Any(c => !char.IsLetterOrDigit(c))) sure.Add("symbols");
                    break;
            }
            if (p.MinCapitals > 0)
                sure.Add("capitals");
            if (p.MinSmall > 0)
                sure.Add("small letters");
        }
        if (sure.Count < 3)
        {
            var missing = new[] { "capitals", "small letters", "digits", "symbols" }.Where(k => !sure.Contains(k));
            return $"Microsoft 365 needs three kinds of character in every password; this pattern only guarantees {(sure.Count == 0 ? "none" : string.Join(" and ", sure))}. " +
                   $"Add {string.Join(" or ", missing)}, or a rule like L4+1U.";
        }
        return null;
    }

    /// <summary>"5 letters (at least 1 capital), then 3 digits, then 2 symbols that are easy to type on a phone."</summary>
    public static string Describe(IReadOnlyList<PatternPart> parts)
    {
        var text = string.Join(", then ", parts.Select(p => p.Describe()));
        return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    /// <summary>A password following the parts in order; within a letters part, the required capitals and small letters land at random places.</summary>
    public static string Create(IReadOnlyList<PatternPart> parts, bool avoidLookAlikes)
    {
        string Clean(string set) => avoidLookAlikes ? new string(set.Where(c => !LookAlikes.Contains(c)).ToArray()) : set;
        var password = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Kind == PatternKind.Exact)
            {
                password.Append(part.Text);
                continue;
            }
            var count = part.IsRange ? RandomNumberGenerator.GetInt32(part.Min, part.Max + 1) : part.Min;
            var from = Clean(part.Kind switch
            {
                PatternKind.Letters => Capitals + Small,
                PatternKind.Capitals => Capitals,
                PatternKind.SmallLetters => Small,
                PatternKind.Digits => Digits,
                PatternKind.PhoneSymbols => PhoneSymbols,
                PatternKind.AnySymbols => AnySymbols,
                _ => Capitals + Small + Digits,
            });
            var chars = new char[count];
            for (var i = 0; i < count; i++)
                chars[i] = Pick(from);
            var places = Enumerable.Range(0, count).ToArray();
            RandomNumberGenerator.Shuffle(places.AsSpan());
            var next = 0;
            for (var i = 0; i < part.MinCapitals && next < count; i++)
                chars[places[next++]] = Pick(Clean(Capitals));
            for (var i = 0; i < part.MinSmall && next < count; i++)
                chars[places[next++]] = Pick(Clean(Small));
            password.Append(chars);
        }
        return password.ToString();
    }

    static char Pick(string from) => from[RandomNumberGenerator.GetInt32(from.Length)];
}
