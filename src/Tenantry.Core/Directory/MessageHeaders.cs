using System.Text.RegularExpressions;

namespace Tenantry.Core.Directory;

/// <summary>How one check on a message came out, in words.</summary>
/// <param name="Check">"Sender address (SPF)", "Signature (DKIM)"…</param>
/// <param name="Result">The result as the header gives it ("pass", "fail", "softfail"…).</param>
/// <param name="Good">True when it passed, false when it failed, null when it says nothing either way.</param>
public sealed record MailVerdict(string Check, string Result, string Meaning, bool? Good);

/// <summary>
/// Reads a message's headers for what Microsoft decided and why: the sender checks
/// (Authentication-Results: SPF, DKIM, DMARC, composite authentication) and the spam verdict
/// (X-Forefront-Antispam-Report: SCL, SFV, CAT), each said in plain words.
/// </summary>
public static partial class MessageHeaders
{
    /// <summary>The value of the first header with this name, unfolded onto one line; null when absent.</summary>
    public static string? Value(string headers, string name)
    {
        var lines = headers.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = lines[i][(name.Length + 1)..].Trim();
            // A folded header carries on in lines that start with a space or tab.
            for (var j = i + 1; j < lines.Length && lines[j].Length > 0 && char.IsWhiteSpace(lines[j][0]); j++)
                value += " " + lines[j].Trim();
            return value;
        }
        return null;
    }

    public static IReadOnlyList<MailVerdict> Verdicts(string headers)
    {
        var verdicts = new List<MailVerdict>();
        if (Value(headers, "Authentication-Results") is { } auth)
        {
            Add(verdicts, auth, "spf", "Sender's server (SPF)", Spf);
            Add(verdicts, auth, "dkim", "Signature (DKIM)", Dkim);
            Add(verdicts, auth, "dmarc", "Sender's domain policy (DMARC)", Dmarc);
            Add(verdicts, auth, "compauth", "Microsoft's sender check", CompAuth);
        }
        if (Value(headers, "X-Forefront-Antispam-Report") is { } report)
        {
            var fields = Fields(report);
            if (fields.TryGetValue("CAT", out var cat))
                verdicts.Add(new("Microsoft's verdict", cat, Category(cat), cat is "NONE" ? true : false));
            if (fields.TryGetValue("SCL", out var scl) && int.TryParse(scl, out var level))
                verdicts.Add(new("Spam confidence", scl, SpamLevel(level), level switch { < 5 => true, _ => false }));
            if (fields.TryGetValue("SFV", out var sfv))
                verdicts.Add(new("Filtering", sfv, Filtering(sfv), sfv is "NSPM" or "SKN" or "SKA" ? true : sfv is "SPM" or "BLK" or "SKQ" ? false : null));
        }
        return verdicts;
    }

    static void Add(List<MailVerdict> verdicts, string auth, string key, string check, Func<string, string> meaning)
    {
        var match = Regex.Match(auth, $@"(?:^|[;\s]){key}=(\w+)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return;
        var result = match.Groups[1].Value.ToLowerInvariant();
        verdicts.Add(new(check, result, meaning(result), result == "pass" ? true : result is "fail" or "softfail" or "permerror" ? false : null));
    }

    /// <summary>"CIP:1.2.3.4;CTRY:US;SCL:5;SFV:SPM" as name → value.</summary>
    static Dictionary<string, string> Fields(string report) =>
        report.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split(':', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First()[1].Trim().ToUpperInvariant());

    static string Spf(string r) => r switch
    {
        "pass" => "The server that sent it is allowed to send for the sender's domain.",
        "fail" => "The server that sent it isn't allowed to send for the sender's domain: often a forged sender.",
        "softfail" => "The sender's domain says this server probably isn't one of theirs.",
        "neutral" => "The sender's domain doesn't say whether this server may send for it.",
        "none" => "The sender's domain publishes no SPF record, so this couldn't be checked.",
        "temperror" => "The check couldn't finish (a temporary DNS problem).",
        "permerror" => "The sender's SPF record is broken, so the check failed.",
        _ => "",
    };

    static string Dkim(string r) => r switch
    {
        "pass" => "The message is signed and wasn't changed on the way.",
        "fail" => "The signature doesn't match: the message was changed on the way, or the signature is forged.",
        "none" => "The message isn't signed.",
        "temperror" => "The signature couldn't be checked (a temporary DNS problem).",
        "permerror" => "The signature couldn't be checked (a broken key or signature).",
        _ => "",
    };

    static string Dmarc(string r) => r switch
    {
        "pass" => "The address people see as the sender matches a domain that passed SPF or DKIM.",
        "fail" => "The address people see as the sender didn't pass SPF or DKIM for its domain: a common sign of spoofing.",
        "bestguesspass" => "The domain has no DMARC policy, but would have passed one.",
        "none" => "The sender's domain has no DMARC policy.",
        "temperror" => "The check couldn't finish (a temporary DNS problem).",
        "permerror" => "The sender's DMARC record is broken.",
        _ => "",
    };

    static string CompAuth(string r) => r switch
    {
        "pass" => "Microsoft is satisfied the sender is who they say they are.",
        "fail" => "Microsoft doubts the sender is who they say they are (spoofing protection).",
        "softpass" => "Microsoft mostly trusts the sender.",
        "none" => "Microsoft didn't make a sender judgment.",
        _ => "",
    };

    static string Category(string cat) => cat switch
    {
        "NONE" => "Nothing wrong found.",
        "SPM" => "Spam.",
        "HSPM" => "High-confidence spam.",
        "BULK" => "Bulk mail (newsletters and marketing).",
        "PHSH" => "Phishing.",
        "HPHSH" or "HPHISH" => "High-confidence phishing.",
        "MALW" => "Malware.",
        "SPOOF" => "Spoofing: the sender's address looks forged.",
        "UIMP" => "Impersonating one of your people.",
        "DIMP" => "Impersonating a domain.",
        "GIMP" => "Impersonation found by mailbox intelligence.",
        "AMP" => "Anti-malware policy.",
        "OSPM" => "Outbound spam.",
        "SAP" => "Safe Attachments policy.",
        "INTOS" => "Intra-organization phishing.",
        _ => "",
    };

    static string SpamLevel(int level) => level switch
    {
        -1 => "Skipped spam filtering (a trusted sender, or a rule said so).",
        >= 0 and <= 4 => "Not spam.",
        5 or 6 => "Spam.",
        >= 7 and <= 9 => "High-confidence spam.",
        _ => $"Level {level} on Microsoft's 0 to 9 scale.",
    };

    static string Filtering(string sfv) => sfv switch
    {
        "NSPM" => "Not spam.",
        "SPM" => "Spam filtering marked it as spam.",
        "BLK" => "The sender or domain is on a block list.",
        "SKN" => "Skipped as not spam (a mail flow rule, for example).",
        "SKA" => "Skipped: the sender or domain is on an allow list.",
        "SKB" => "Marked as spam by a block list in an anti-spam policy.",
        "SKI" => "Skipped: sent within the organization.",
        "SKQ" => "Released from quarantine to its recipients.",
        "SFE" => "Skipped: the sender is on the recipient's safe senders list.",
        "SKS" => "Marked as spam by a mail flow rule.",
        _ => "",
    };
}
