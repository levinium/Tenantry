using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace Tenantry.Core.Directory;

/// <summary>
/// Graph's ISO 8601 lengths of time ("P90D", "PT12H") in words people read and type ("90 days",
/// "12 hours"), and back.
/// </summary>
public static partial class IsoDuration
{
    [GeneratedRegex(@"^-?P(?=\d|T\d)(\d+Y)?(\d+M)?(\d+W)?(\d+D)?(T(?=\d)(\d+H)?(\d+M)?(\d+(\.\d+)?S)?)?$")]
    private static partial Regex IsoPattern();

    [GeneratedRegex(@"(\d+)\s*(d|day|days|h|hr|hrs|hour|hours|m|min|mins|minute|minutes|w|week|weeks)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WordPart();

    /// <summary>The text is an ISO duration ("P90D", "PT30M").</summary>
    public static bool IsIso(string? text) => text is { Length: > 1 } && IsoPattern().IsMatch(text);

    /// <summary>"P90D" → "90 days"; "PT12H" → "12 hours"; "P1DT12H" → "1 day 12 hours". Not a duration: as it is.</summary>
    public static string Words(string? iso)
    {
        if (string.IsNullOrEmpty(iso) || !IsIso(iso))
            return iso ?? "";
        TimeSpan span;
        try
        {
            span = XmlConvert.ToTimeSpan(iso);
        }
        catch (FormatException)
        {
            return iso;
        }
        return Words(span);
    }

    public static string Words(TimeSpan span)
    {
        if (span == TimeSpan.Zero)
            return "0 days";
        var parts = new List<string>();
        void Add(int n, string unit)
        {
            if (n > 0)
                parts.Add($"{n.ToString("N0", CultureInfo.CurrentCulture)} {unit}{(n == 1 ? "" : "s")}");
        }
        Add((int)span.TotalDays, "day");
        Add(span.Hours, "hour");
        Add(span.Minutes, "minute");
        Add(span.Seconds, "second");
        return string.Join(' ', parts);
    }

    /// <summary>"90 days", "12 hours", "1 day 12 hours", "90" (days) or an ISO duration → the ISO duration; null when it isn't one.</summary>
    public static string? Parse(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return null;
        if (IsIso(trimmed.ToUpperInvariant()))
            return trimmed.ToUpperInvariant();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out var bare) && bare >= 0)
            return XmlConvert.ToString(TimeSpan.FromDays(bare));
        var matches = WordPart().Matches(trimmed);
        if (matches.Count == 0 || WordPart().Replace(trimmed, "").Trim(' ', ',').Replace("and", "", StringComparison.OrdinalIgnoreCase).Trim().Length > 0)
            return null;
        var span = TimeSpan.Zero;
        foreach (Match m in matches)
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            span += char.ToLowerInvariant(m.Groups[2].Value[0]) switch
            {
                'w' => TimeSpan.FromDays(7 * n),
                'd' => TimeSpan.FromDays(n),
                'h' => TimeSpan.FromHours(n),
                _ => TimeSpan.FromMinutes(n),
            };
        }
        return XmlConvert.ToString(span);
    }
}
