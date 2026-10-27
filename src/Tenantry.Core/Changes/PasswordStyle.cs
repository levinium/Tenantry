using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tenantry.Core.Changes;

/// <summary>Which letters a generated password uses.</summary>
public enum PasswordLetters
{
    /// <summary>Upper and lower case.</summary>
    Mixed,
    LowerOnly,
    UpperOnly,
    None,
}

/// <summary>Which symbols a generated password uses.</summary>
public enum PasswordSymbols
{
    None,

    /// <summary>Symbols on a phone keyboard's first symbols page: ! @ # $ % & * ? + = .</summary>
    EasyToType,

    /// <summary>Any printable symbol.</summary>
    Any,
}

/// <summary>
/// How Tenantry makes new passwords for a tenant (Settings › Passwords): the length, which kinds of
/// characters, and whether to break it into groups of four with dashes so it's easy to read out.
/// Every kind chosen appears at least once. Kept per tenant on this computer, never with the passwords.
/// </summary>
/// <param name="Length">Characters, not counting the dashes between groups.</param>
/// <param name="AvoidLookAlikes">Leaves out characters people misread: 0 O o, 1 l I, 5 S, 2 Z.</param>
/// <param name="Pattern">
/// A format in order (<see cref="PasswordPattern"/>, like "L5+1U D3 S2"). When set it's used
/// instead of the simple choices, which are kept for switching back.
/// </param>
public sealed record PasswordStyle(
    int Length = 16,
    PasswordLetters Letters = PasswordLetters.Mixed,
    bool Numbers = true,
    PasswordSymbols Symbols = PasswordSymbols.None,
    bool Groups = true,
    bool AvoidLookAlikes = true,
    string? Pattern = null)
{
    [JsonIgnore]
    public bool UsesPattern => Pattern is not null;
    public const int MinLength = 8;
    public const int MaxLength = 64;

    public static readonly PasswordStyle Default = new();

    const string UpperAll = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    const string LowerAll = "abcdefghijklmnopqrstuvwxyz";
    const string DigitsAll = "0123456789";
    const string LookAlikes = "0Oo1lI5S2Z";
    const string EasySymbols = "!@#$%&*?+=";
    const string AllSymbols = "!@#$%^&*()-_=+[]{};:,.<>/?~";

    static string Without(string set, bool avoid) => avoid ? new string(set.Where(c => !LookAlikes.Contains(c)).ToArray()) : set;

    /// <summary>The kinds of character this style uses, each as the set it picks from.</summary>
    IReadOnlyList<string> Sets
    {
        get
        {
            var sets = new List<string>();
            if (Letters is PasswordLetters.Mixed or PasswordLetters.UpperOnly)
                sets.Add(Without(UpperAll, AvoidLookAlikes));
            if (Letters is PasswordLetters.Mixed or PasswordLetters.LowerOnly)
                sets.Add(Without(LowerAll, AvoidLookAlikes));
            if (Numbers)
                sets.Add(Without(DigitsAll, AvoidLookAlikes));
            if (Symbols != PasswordSymbols.None)
                sets.Add(Symbols == PasswordSymbols.EasyToType ? EasySymbols : AllSymbols);
            return sets;
        }
    }

    /// <summary>
    /// Why this style can't be used, or null. Entra needs at least 8 characters and three of: upper
    /// case, lower case, numbers, symbols (the dashes between groups count as symbols).
    /// </summary>
    [JsonIgnore]
    public string? Problem
    {
        get
        {
            if (Pattern is not null)
            {
                var (parts, unreadable) = PasswordPattern.Parse(Pattern);
                return unreadable ?? PasswordPattern.Problem(parts);
            }
            if (Sets.Count == 0)
                return "Choose at least one kind of character.";
            if (Length is < MinLength or > MaxLength)
                return $"Choose a length from {MinLength} to {MaxLength}.";
            var kinds = Sets.Count + (Groups && Symbols == PasswordSymbols.None ? 1 : 0);
            if (kinds < 3)
                return "Microsoft 365 needs three kinds of character (upper case, lower case, numbers, symbols). Add another kind, or turn on groups with dashes.";
            if (Length < Sets.Count)
                return "Make it longer: it needs room for one of each kind.";
            return null;
        }
    }

    /// <summary>"16 characters: upper and lower case letters and numbers, in groups of four."</summary>
    [JsonIgnore]
    public string Description
    {
        get
        {
            if (Pattern is not null)
                return PasswordPattern.Describe(PasswordPattern.Parse(Pattern).Parts);
            var kinds = new List<string>();
            kinds.Add(Letters switch
            {
                PasswordLetters.Mixed => "upper and lower case letters",
                PasswordLetters.LowerOnly => "lower case letters",
                PasswordLetters.UpperOnly => "capital letters",
                _ => "",
            });
            if (Numbers)
                kinds.Add("numbers");
            if (Symbols != PasswordSymbols.None)
                kinds.Add(Symbols == PasswordSymbols.EasyToType ? "symbols that are easy to type on a phone" : "any symbols");
            kinds.RemoveAll(k => k.Length == 0);
            var list = kinds.Count <= 1 ? string.Concat(kinds) : string.Join(", ", kinds.Take(kinds.Count - 1)) + " and " + kinds[^1];
            return $"{Length} characters: {list}{(Groups ? ", in groups of four" : "")}.";
        }
    }

    /// <summary>A new password in this style (the default style if this one has a problem).</summary>
    public string Create()
    {
        var style = Problem is null ? this : Default;
        if (style.Pattern is not null)
            return PasswordPattern.Create(PasswordPattern.Parse(style.Pattern).Parts, style.AvoidLookAlikes);
        var sets = style.Sets;
        var chars = new char[style.Length];
        // One of each kind at random places, then the rest from all kinds together.
        var all = string.Concat(sets);
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Pick(all);
        var places = Enumerable.Range(0, chars.Length).ToArray();
        RandomNumberGenerator.Shuffle(places.AsSpan());
        for (var k = 0; k < sets.Count; k++)
            chars[places[k]] = Pick(sets[k]);
        if (!style.Groups)
            return new string(chars);
        return string.Join('-', chars.Chunk(4).Select(g => new string(g)));
    }

    static char Pick(string from) => from[RandomNumberGenerator.GetInt32(from.Length)];
}

public interface IPasswordStyleStore
{
    PasswordStyle Load();
    void Save(PasswordStyle style);
}

public sealed class MemoryPasswordStyleStore(PasswordStyle? initial = null) : IPasswordStyleStore
{
    PasswordStyle _style = initial ?? PasswordStyle.Default;
    public PasswordStyle Load() => _style;
    public void Save(PasswordStyle style) => _style = style;
}

/// <summary>Kept in the tenant's folder as password-style.json; unreadable or missing means the default.</summary>
public sealed class JsonPasswordStyleStore(string tenantFolder) : IPasswordStyleStore
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    string Path => System.IO.Path.Combine(tenantFolder, "password-style.json");

    public PasswordStyle Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<PasswordStyle>(File.ReadAllText(Path), Json) ?? PasswordStyle.Default : PasswordStyle.Default;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return PasswordStyle.Default;
        }
    }

    public void Save(PasswordStyle style)
    {
        System.IO.Directory.CreateDirectory(tenantFolder);
        File.WriteAllText(Path, JsonSerializer.Serialize(style, Json));
    }
}
