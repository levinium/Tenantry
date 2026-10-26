using System.Net;
using System.Text.RegularExpressions;

namespace Tenantry.Core.Directory;

/// <summary>
/// Mail bodies as plain text. Quarantined mail may be malicious, so it is never rendered as HTML
/// (no scripts, images or tracking pixels load); links show as their text with the address after.
/// </summary>
public static partial class PlainText
{
    public static string FromHtml(string html)
    {
        var text = Hidden().Replace(html, "");
        text = Link().Replace(text, m => $"{m.Groups["text"].Value} [{m.Groups["href"].Value}]");
        text = Break().Replace(text, "\n");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = Spaces().Replace(text, " ");
        text = BlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"<(script|style|head)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<a\b[^>]*?href\s*=\s*[""']?(?<href>[^""'\s>]+)[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Link();

    [GeneratedRegex(@"<(br|/p|/div|/tr|/li|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Break();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*\n\s*\n\s*")]
    private static partial Regex BlankLines();
}
