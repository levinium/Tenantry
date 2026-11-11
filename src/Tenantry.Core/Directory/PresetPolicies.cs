namespace Tenantry.Core.Directory;

/// <summary>Microsoft's preset security policies, strongest first (the order Exchange applies them).</summary>
public enum PresetKind
{
    Strict,
    Standard,
    BuiltIn,
}

/// <summary>A "who" setting of a preset's rule: people, groups or domains, included or excepted.</summary>
public sealed record PresetScopePart(string Parameter, string Label, bool IsException, string Help);

/// <summary>
/// A preset security policy as its rules stand: whether the tenant has ever turned it on (the
/// Defender portal makes its policies and rules the first time; they can't be made any other way),
/// whether it's on, and who it covers. Standard and Strict have an EOP rule (anti-spam,
/// anti-phishing, anti-malware) and, with Defender for Office 365, an ATP rule (Safe Links and
/// Safe Attachments) kept to the same people. Built-in protection is the ATP rule alone, for
/// everyone, with exceptions only.
/// </summary>
/// <param name="Exists">Its rule exists (it's been turned on at least once).</param>
/// <param name="HasDefender">The tenant has Defender for Office 365, so the ATP rule is managed too.</param>
/// <param name="Scope">Each <see cref="PresetScopePart.Parameter"/>'s values.</param>
public sealed record PresetPolicy(PresetKind Kind, bool Exists, bool Enabled, bool HasDefender, IReadOnlyDictionary<string, IReadOnlyList<string>> Scope)
{
    public string RuleName => Presets.RuleName(Kind);

    public IReadOnlyList<string> Values(string parameter) => Scope.GetValueOrDefault(parameter) ?? [];

    /// <summary>"Everyone", "Nobody yet", or the people, groups and domains it covers, with exceptions.</summary>
    public string AppliesTo
    {
        get
        {
            static string Part(string label, IReadOnlyList<string> items) =>
                items.Count == 0 ? "" : $"{label} {(items.Count <= 3 ? string.Join(", ", items) : $"{items.Count} of them")}";
            var parts = Presets.ScopeParts(Kind);
            var who = parts.Where(p => !p.IsException).Select(p => Part(p.Label.ToLowerInvariant() + ":", Values(p.Parameter))).Where(p => p.Length > 0).ToArray();
            var except = parts.Where(p => p.IsException).Select(p => Part(p.Label.Replace("Except ", "", StringComparison.Ordinal).ToLowerInvariant() + ":", Values(p.Parameter)))
                .Where(p => p.Length > 0).ToArray();
            var text = Kind == PresetKind.BuiltIn ? "Everyone not in a Safe Links or Safe Attachments policy" : who.Length == 0 ? "Everyone" : string.Join("; ", who);
            return except.Length == 0 ? text : $"{text}; except {string.Join("; ", except)}";
        }
    }
}

/// <summary>The words, rule names and scope settings for preset security policies.</summary>
public static class Presets
{
    public static string RuleName(PresetKind kind) => kind switch
    {
        PresetKind.Strict => "Strict Preset Security Policy",
        PresetKind.Standard => "Standard Preset Security Policy",
        _ => "ATP Built-In Protection Rule",
    };

    public static string Title(PresetKind kind) => kind switch
    {
        PresetKind.Strict => "Strict Protection",
        PresetKind.Standard => "Standard Protection",
        _ => "Built-In Protection",
    };

    public static string About(PresetKind kind) => kind switch
    {
        PresetKind.Strict => "Microsoft's strongest settings, for people attackers aim at (leaders, finance, admins). More mail is quarantined, so expect a few more release requests.",
        PresetKind.Standard => "Microsoft's recommended settings for most people. Microsoft keeps them up to date as threats change.",
        _ => "Safe Links and Safe Attachments for everyone not in another Safe Links or Safe Attachments policy. Microsoft turns it on for every tenant with Defender for Office 365.",
    };

    /// <summary>Where to turn a preset on for the first time (the only way its policies can be made).</summary>
    public const string PortalUrl = "https://security.microsoft.com/presetSecurityPolicies";

    /// <summary>What a preset does, in words: the main ways Standard and Strict differ from each other and from the defaults.</summary>
    public static IReadOnlyList<string> WhatItSets(PresetKind kind) => kind switch
    {
        PresetKind.Strict =>
        [
            "Spam and bulk mail are quarantined (bulk threshold 5).",
            "Phishing and high-confidence spam are quarantined; phishing threshold 4 (most aggressive).",
            "Mail that fails spoofing checks, or impersonates your people or domains, is quarantined.",
            "Malware is quarantined and risky file types are blocked.",
            "With Defender: Safe Links checks every link when clicked; Safe Attachments holds attachments until checked.",
        ],
        PresetKind.Standard =>
        [
            "Spam and bulk mail go to Junk Email (bulk threshold 6).",
            "Phishing and high-confidence spam are quarantined; phishing threshold 3.",
            "Mail that fails spoofing checks goes to Junk Email; impersonation of your people or domains is quarantined.",
            "Malware is quarantined and risky file types are blocked.",
            "With Defender: Safe Links checks every link when clicked; Safe Attachments holds attachments until checked.",
        ],
        _ =>
        [
            "Safe Links checks links in mail, Teams and Office apps when clicked.",
            "Safe Attachments opens attachments in a safe place before they're delivered.",
        ],
    };

    static readonly PresetScopePart[] Includes =
    [
        new("SentTo", "People", false, "These people, by address."),
        new("SentToMemberOf", "Groups", false, "Members of these groups."),
        new("RecipientDomainIs", "Domains", false, "Everyone at these domains."),
    ];

    static readonly PresetScopePart[] Exceptions =
    [
        new("ExceptIfSentTo", "Except People", true, "Never these people."),
        new("ExceptIfSentToMemberOf", "Except Groups", true, "Never members of these groups."),
        new("ExceptIfRecipientDomainIs", "Except Domains", true, "Never anyone at these domains."),
    ];

    /// <summary>The scope settings a preset takes: built-in protection takes exceptions only.</summary>
    public static IReadOnlyList<PresetScopePart> ScopeParts(PresetKind kind) => kind == PresetKind.BuiltIn ? Exceptions : [.. Includes, .. Exceptions];
}

/// <summary>Preset security policies (Strict, Standard, Built-in protection).</summary>
public interface IPresetPolicyRecords
{
    /// <summary>Strict, Standard and Built-in protection, in that order.</summary>
    Task<IReadOnlyList<PresetPolicy>> GetPresetPoliciesAsync(CancellationToken cancellationToken = default);
}
