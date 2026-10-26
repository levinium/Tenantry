using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Tenantry.Core.Directory;

/// <summary>The kinds of entry on the Tenant Allow/Block List.</summary>
public enum ListKind
{
    /// <summary>An email address or a whole domain.</summary>
    Sender,
    Url,
    /// <summary>A file, by its SHA256 hash.</summary>
    File,
    /// <summary>An IPv6 address or range (Microsoft's list takes IPv6 only).</summary>
    IP,
    /// <summary>A spoofed sender: who mail claims to be from, and the infrastructure actually sending it.</summary>
    Spoof,
}

/// <summary>One entry on the Tenant Allow/Block List.</summary>
/// <param name="Identity">Exchange's ID for it (for changing or removing).</param>
/// <param name="RemoveAfterDays">Set when the entry goes this many days after it was last used (allows).</param>
/// <param name="Source">"Tenant" (added by an admin) or "Submission" (from a reported message), etc.</param>
/// <param name="Infrastructure">Spoofed senders: the sending infrastructure (a domain, or an IP range).</param>
/// <param name="SpoofType">Spoofed senders: "Internal" or "External".</param>
public sealed record ListEntry(
    string Identity,
    ListKind Kind,
    string Value,
    bool Block,
    string? Notes,
    DateTimeOffset? Expires,
    int? RemoveAfterDays,
    DateTimeOffset? LastUsed,
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    string? ModifiedBy,
    string? Source,
    string? Infrastructure = null,
    string? SpoofType = null)
{
    public string KindText => AllowBlock.KindName(Kind);

    public string ExpiryText => Expires is { } at ? $"Until {at.ToLocalTime():d MMM yyyy}"
        : RemoveAfterDays is { } d ? $"{d} days after last use"
        : "Never expires";
}

/// <summary>When a new or changed entry stops applying.</summary>
/// <param name="On">A date (both null: never).</param>
/// <param name="AfterLastUseDays">This many days after it last matched mail (Microsoft's rule for allows).</param>
public sealed record ListExpiry(DateTimeOffset? On = null, int? AfterLastUseDays = null)
{
    public static readonly ListExpiry Never = new();

    /// <summary>Microsoft removes an allowed entry 45 days after it last matched.</summary>
    public static readonly ListExpiry AllowDefault = new(AfterLastUseDays: 45);

    public string Text => On is { } at ? $"until {at.ToLocalTime():d MMM yyyy}" : AfterLastUseDays is { } d ? $"{d} days after last use" : "never expires";
}

public static partial class AllowBlock
{
    public static string KindName(ListKind kind) => kind switch
    {
        ListKind.Sender => "Sender or domain",
        ListKind.Url => "Link (URL)",
        ListKind.File => "File",
        ListKind.IP => "IP address",
        _ => "Spoofed sender",
    };

    /// <summary>Exchange's ListType for each kind (spoofed senders have cmdlets of their own).</summary>
    public static string ListType(ListKind kind) => kind switch
    {
        ListKind.Sender => "Sender",
        ListKind.Url => "Url",
        ListKind.File => "FileHash",
        ListKind.IP => "IP",
        _ => "Spoof",
    };

    /// <summary>What's wrong with a value typed for a kind of entry, or null when it's fine.</summary>
    public static string? Problem(ListKind kind, string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return "Empty.";
        if (value.Any(char.IsWhiteSpace))
            return "Contains a space.";
        return kind switch
        {
            ListKind.Sender or ListKind.Spoof => SenderPattern().IsMatch(value) ? null : "Not an email address or domain (like name@example.com or example.com).",
            ListKind.Url => value.Length > 250 ? "Longer than 250 characters." : value.Contains('.') ? null : "Not a link (like example.com/path or *.example.com).",
            ListKind.File => HashPattern().IsMatch(value) ? null : "Not a SHA256 file hash (64 letters and numbers, 0-9 and a-f).",
            ListKind.IP => IsIPv6(value) ? null : "Not an IPv6 address or range (Microsoft's list takes IPv6 only, like 2001:db8::1 or 2001:db8::/32).",
            _ => null,
        };
    }

    static bool IsIPv6(string value)
    {
        var parts = value.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            return false;
        return parts.Length == 1 || int.TryParse(parts[1], out var prefix) && prefix is >= 1 and <= 128;
    }

    /// <summary>Several values pasted at once: one per line (commas and semicolons also split), blanks and repeats dropped.</summary>
    public static IReadOnlyList<string> Split(string text) =>
        text.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    [GeneratedRegex(@"^([^@\s]+@)?([a-z0-9-]+\.)+[a-z]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex SenderPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.IgnoreCase)]
    private static partial Regex HashPattern();
}
