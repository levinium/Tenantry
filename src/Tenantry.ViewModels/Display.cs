using System.Globalization;

namespace Tenantry.ViewModels;

/// <summary>How a status reads at a glance; the view maps each to a colour.</summary>
public enum BadgeKind
{
    Neutral,
    Info,
    Success,
    Warning,
    Danger,
}

/// <summary>Formatting shared by list and record pages, so the same value reads the same everywhere.</summary>
public static class Display
{
    /// <summary>"Ada Lovelace" → "AL"; single names and empty strings degrade gracefully.</summary>
    public static string Initials(string? name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => char.IsLetterOrDigit(p[0]))
            .ToArray();
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => string.Concat(parts[0][0], parts[^1][0]).ToUpperInvariant(),
        };
    }

    /// <summary>"just now", "5 minutes ago", "3 days ago", "Mar 4, 2025"; "Never" for null.</summary>
    public static string Relative(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is not { } w)
            return "Never";
        var ago = now - w;
        if (ago < TimeSpan.Zero)
            return w.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        return ago switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalMinutes: < 2 } => "1 minute ago",
            { TotalHours: < 1 } => $"{(int)ago.TotalMinutes} minutes ago",
            { TotalHours: < 2 } => "1 hour ago",
            { TotalDays: < 1 } => $"{(int)ago.TotalHours} hours ago",
            { TotalDays: < 2 } => "yesterday",
            { TotalDays: < 30 } => $"{(int)ago.TotalDays} days ago",
            _ => w.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture),
        };
    }

    /// <summary>"at 10:42 AM" today; "Oct 19, 10:42 AM" on another day. Local time.</summary>
    public static string Clock(DateTimeOffset when, DateTimeOffset now)
    {
        var local = when.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? "at " + local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("MMM d, ", CultureInfo.CurrentCulture) + local.ToString("t", CultureInfo.CurrentCulture);
    }

    public static string Exact(DateTimeOffset? when) =>
        when?.ToLocalTime().ToString("f", CultureInfo.CurrentCulture) ?? "Never signed in";

    /// <summary>Short words that stay lower case in a title unless they start it.</summary>
    static readonly HashSet<string> MinorWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "but", "by", "for", "from", "if", "in", "into", "nor", "of", "on", "or", "so", "the", "to", "via", "with",
    };

    /// <summary>
    /// "set the message header to this value" → "Set the Message Header to This Value". Words already
    /// carrying capitals ("SCL", "(SCL)", and "To" in "the To box") are names and stay as they are.
    /// </summary>
    public static string TitleCase(string text)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            var letter = word.IndexOf(word.FirstOrDefault(char.IsLetter));
            if (letter < 0 || word.Skip(letter + 1).Any(char.IsUpper) || i > 0 && char.IsUpper(word[letter]))
                continue;
            words[i] = i > 0 && i < words.Length - 1 && MinorWords.Contains(word)
                ? word.ToLowerInvariant()
                : word[..letter] + char.ToUpperInvariant(word[letter]) + word[(letter + 1)..];
        }
        return string.Join(' ', words);
    }
}
