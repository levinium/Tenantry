using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tenantry.Core.Directory;

/// <summary>
/// A kind of threat policy (anti-spam, anti-phishing…): its Exchange nouns (the policy holds the
/// settings, the rule who it applies to, its order and whether it's on), and the settings Tenantry
/// offers with Microsoft's Standard recommendation for each.
/// </summary>
/// <param name="PolicyNoun">"HostedContentFilterPolicy": Get-/Set-/New-/Remove- it.</param>
/// <param name="RuleNoun">"HostedContentFilterRule": its rule (Enable-/Disable- for on and off).</param>
/// <param name="ScopesSenders">The rule picks senders (outbound spam), not recipients.</param>
/// <param name="NeedsDefender">Needs Microsoft Defender for Office 365 Plan 1.</param>
public sealed record ThreatPolicyKind(string Key, string Title, string About, string PolicyNoun, string RuleNoun, bool ScopesSenders = false, bool NeedsDefender = false)
{
    public string GetPolicy => "Get-" + PolicyNoun;
    public string SetPolicy => "Set-" + PolicyNoun;
    public string GetRule => "Get-" + RuleNoun;
    public string SetRule => "Set-" + RuleNoun;

    /// <summary>The rule's parameter naming its policy (New-HostedContentFilterRule -HostedContentFilterPolicy …).</summary>
    public string RulePolicyParameter => PolicyNoun;

    /// <summary>The rule's "who" parameters: recipients for most, senders for outbound spam.</summary>
    public (string People, string Groups, string Domains) Who => ScopesSenders ? ("From", "FromMemberOf", "SenderDomainIs") : ("SentTo", "SentToMemberOf", "RecipientDomainIs");
}

/// <summary>A threat policy with its rule (none for the default, which covers everyone, last).</summary>
/// <param name="ReadOnly">A preset or built-in policy Microsoft keeps: shown, not changed here.</param>
public sealed record ThreatPolicy(ThreatPolicyKind Kind, string Name, bool IsDefault, bool ReadOnly, JsonElement Policy, JsonElement? Rule)
{
    public bool Enabled => Rule is not { } rule || !rule.TryGetProperty("State", out var s) || !string.Equals(s.GetString(), "Disabled", StringComparison.OrdinalIgnoreCase);

    public int? Priority => Rule is { } rule && rule.TryGetProperty("Priority", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;

    static IReadOnlyList<string> List(JsonElement? row, string name) =>
        row is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(i => i.GetString() ?? "").Where(i => i.Length > 0)] : [];

    /// <summary>"Everyone", or "people: …; groups: …; domains: …", with any exceptions.</summary>
    public string AppliesTo
    {
        get
        {
            if (IsDefault)
                return Kind.ScopesSenders ? "Every sender not in another policy" : "Everyone not in another policy";
            var (people, groups, domains) = Kind.Who;
            string Part(string label, IReadOnlyList<string> items) => items.Count == 0 ? "" : $"{label} {(items.Count <= 3 ? string.Join(", ", items) : $"{items.Count} of them")}";
            var who = new[] { Part("people:", List(Rule, people)), Part("groups:", List(Rule, groups)), Part("domains:", List(Rule, domains)) }.Where(p => p.Length > 0).ToArray();
            var except = new[] { Part("people:", List(Rule, "ExceptIf" + people)), Part("groups:", List(Rule, "ExceptIf" + groups)), Part("domains:", List(Rule, "ExceptIf" + domains)) }
                .Where(p => p.Length > 0).ToArray();
            var text = who.Length == 0 ? "Nobody yet" : string.Join("; ", who);
            return except.Length == 0 ? text : $"{text}; except {string.Join("; ", except)}";
        }
    }
}

/// <summary>A quarantine policy: what people can do with their own quarantined mail, and whether they're told.</summary>
public sealed record QuarantinePolicy(string Name, bool BuiltIn, IReadOnlyDictionary<string, bool> Permissions, bool Notifies)
{
    /// <summary>The permissions in words: "release, preview, delete" or "admins only".</summary>
    public string Describe()
    {
        var granted = QuarantinePermissions.All.Where(p => Permissions.GetValueOrDefault(p.Key)).Select(p => p.Label).ToArray();
        return granted.Length == 0 ? "Only admins can see or release it" : "People can " + string.Join(", ", granted).ToLowerInvariant();
    }
}

/// <summary>The end-user quarantine permissions and their bits (EndUserQuarantinePermissionsValue).</summary>
public static class QuarantinePermissions
{
    public static readonly IReadOnlyList<(string Key, string Label, int Bit)> All =
    [
        ("PermissionToViewHeader", "See headers", 128), ("PermissionToDownload", "Download", 64), ("PermissionToBlockSender", "Block the sender", 16),
        ("PermissionToRequestRelease", "Ask for release", 8), ("PermissionToRelease", "Release", 4), ("PermissionToPreview", "Preview", 2), ("PermissionToDelete", "Delete", 1),
    ];

    public static string Label(string key) => All.FirstOrDefault(p => p.Key == key).Label ?? key;

    /// <summary>Microsoft's three presets: none, limited (ask for release) and full.</summary>
    public static readonly IReadOnlyList<(string Label, int Value)> Presets =
        [("No Access", 0), ("Limited: Ask for Release, Preview, Delete, Block the Sender", 27), ("Full: Release, Preview, Delete, Block the Sender", 23)];

    /// <summary>Reads "PermissionToRelease: True\nPermissionToDelete: False…" (as Exchange formats it) into flags.</summary>
    public static IReadOnlyDictionary<string, bool> Parse(string text) =>
        text.Trim('[', ']').Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0])
            .ToDictionary(g => g.Key, g => g.First()[1].Equals("True", StringComparison.OrdinalIgnoreCase));

    public static int Value(IReadOnlyDictionary<string, bool> flags) => All.Where(p => flags.GetValueOrDefault(p.Key)).Sum(p => p.Bit);
}

/// <summary>The threat policies Tenantry offers and their settings.</summary>
public static class ThreatPolicyCatalog
{
    static readonly (string, string)[] SpamActions =
        [("MoveToJmf", "Move to Junk Email"), ("Quarantine", "Quarantine"), ("AddXHeader", "Add a Header"), ("ModifySubject", "Add Text to the Subject"), ("Delete", "Delete")];

    static readonly (string, string)[] JunkOrQuarantine = [("MoveToJmf", "Move to Junk Email"), ("Quarantine", "Quarantine")];

    public static readonly ThreatPolicyKind AntiSpam = new("antispam", "Anti-Spam", "What happens to spam, bulk mail and phishing that arrives.", "HostedContentFilterPolicy", "HostedContentFilterRule");
    public static readonly ThreatPolicyKind Outbound = new("outbound", "Outbound Spam", "Sending limits, and what happens when someone sends too much (usually a hacked account).",
        "HostedOutboundSpamFilterPolicy", "HostedOutboundSpamFilterRule", ScopesSenders: true);
    public static readonly ThreatPolicyKind AntiPhish = new("antiphish", "Anti-Phishing", "Spoofing and impersonation: mail pretending to be someone it isn't.", "AntiPhishPolicy", "AntiPhishRule");
    public static readonly ThreatPolicyKind AntiMalware = new("antimalware", "Anti-Malware", "Attachments with malware, and file types that are blocked outright.", "MalwareFilterPolicy", "MalwareFilterRule");
    public static readonly ThreatPolicyKind SafeLinks = new("safelinks", "Safe Links", "Links in mail, Teams and Office checked when clicked.", "SafeLinksPolicy", "SafeLinksRule", NeedsDefender: true);
    public static readonly ThreatPolicyKind SafeAttachments = new("safeattach", "Safe Attachments", "Attachments opened in a safe place first, to catch new malware.", "SafeAttachmentPolicy", "SafeAttachmentRule", NeedsDefender: true);

    public static IReadOnlyList<ThreatPolicyKind> Kinds { get; } = [AntiSpam, Outbound, AntiPhish, AntiMalware, SafeLinks, SafeAttachments];

    /// <summary>A kind's settings; quarantine policy names fill the quarantine choices.</summary>
    public static IReadOnlyList<SettingDef> Settings(ThreatPolicyKind kind, IReadOnlyList<string> quarantinePolicies)
    {
        var tags = quarantinePolicies.Select(q => (q, q switch
        {
            "AdminOnlyAccessPolicy" => "Admins Only",
            "DefaultFullAccessPolicy" => "People Can Release (Not Told)",
            "DefaultFullAccessWithNotificationPolicy" => "People Can Release (Told)",
            _ => q,
        })).ToArray();
        SettingDef S(string group, string key, string label, string help, string parameter, SettingKind kind2 = SettingKind.OnOff, (string, string)[]? choices = null,
            JsonNode? recommended = null, bool? caution = null, int min = 0, int max = int.MaxValue, string? unit = null) =>
            new(group, key, label, help, kind.GetPolicy, kind.SetPolicy, parameter, kind2, Choices: choices, Recommended: recommended, CautionWhen: caution, Min: min, Max: max, Unit: unit);
        JsonNode V(string s) => JsonValue.Create(s);
        JsonNode B(bool b) => JsonValue.Create(b);
        JsonNode N(int n) => JsonValue.Create(n);
        return kind.Key switch
        {
            "antispam" =>
            [
                S("Spam", "spam", "Spam", "Mail Microsoft is fairly sure is spam.", "SpamAction", SettingKind.Choice, SpamActions, V("MoveToJmf")),
                S("Spam", "hcspam", "High-Confidence Spam", "Mail Microsoft is very sure is spam.", "HighConfidenceSpamAction", SettingKind.Choice, SpamActions, V("Quarantine")),
                S("Spam", "bulk", "Bulk Mail", "Newsletters and marketing above the bulk threshold.", "BulkSpamAction", SettingKind.Choice, SpamActions, V("MoveToJmf")),
                S("Spam", "bulkthreshold", "Bulk Threshold", "1 catches the most bulk mail, 9 the least.", "BulkThreshold", SettingKind.Number, recommended: N(6), min: 1, max: 9),
                S("Phishing", "phish", "Phishing", "Mail Microsoft thinks is phishing.", "PhishSpamAction", SettingKind.Choice, SpamActions, V("Quarantine"), caution: null),
                S("Quarantine", "spamtag", "Who Can Release Quarantined Spam", "The quarantine policy for spam it quarantines.", "SpamQuarantineTag", SettingKind.Choice, tags, V("DefaultFullAccessPolicy")),
                S("Quarantine", "hcspamtag", "Who Can Release High-Confidence Spam", "", "HighConfidenceSpamQuarantineTag", SettingKind.Choice, tags, V("DefaultFullAccessPolicy")),
                S("Quarantine", "phishtag", "Who Can Release Phishing", "", "PhishQuarantineTag", SettingKind.Choice, tags, V("AdminOnlyAccessPolicy")),
                S("Quarantine", "hcphishtag", "Who Can Release High-Confidence Phishing", "People can never release this themselves, only ask.", "HighConfidencePhishQuarantineTag", SettingKind.Choice, tags, V("AdminOnlyAccessPolicy")),
                S("Quarantine", "bulktag", "Who Can Release Bulk Mail", "", "BulkQuarantineTag", SettingKind.Choice, tags, V("DefaultFullAccessPolicy")),
                S("Quarantine", "retention", "Keep Quarantined Mail For", "Days before it's deleted.", "QuarantineRetentionPeriod", SettingKind.Number, recommended: N(30), min: 1, max: 30, unit: "days"),
                S("Protection", "spamzap", "Remove Spam Found Later (ZAP)", "Pull spam out of inboxes if Microsoft recognizes it after delivery.", "SpamZapEnabled", recommended: B(true), caution: false),
                S("Protection", "phishzap", "Remove Phishing Found Later (ZAP)", "Pull phishing out of inboxes if recognized after delivery.", "PhishZapEnabled", recommended: B(true), caution: false),
                S("Protection", "tips", "Safety Tips", "Show a warning on suspicious messages in Outlook.", "InlineSafetyTipsEnabled", recommended: B(true)),
                S("Lists", "allowsenders", "Always Allow These Senders", "Skips spam filtering for them. Microsoft recommends the Tenant Allow/Block List instead: it expires and is checked for phishing.", "AllowedSenders", SettingKind.List),
                S("Lists", "allowdomains", "Always Allow These Domains", "Skips spam filtering for the whole domain; risky, since anyone can pretend to be from it.", "AllowedSenderDomains", SettingKind.List),
                S("Lists", "blocksenders", "Always Block These Senders", "", "BlockedSenders", SettingKind.List),
                S("Lists", "blockdomains", "Always Block These Domains", "", "BlockedSenderDomains", SettingKind.List),
            ],
            "outbound" =>
            [
                S("Limits", "external", "Outside Recipients per Hour", "0 means Microsoft's limit.", "RecipientLimitExternalPerHour", SettingKind.Number, recommended: N(500), max: 10000, unit: "per hour"),
                S("Limits", "internal", "Inside Recipients per Hour", "0 means Microsoft's limit.", "RecipientLimitInternalPerHour", SettingKind.Number, recommended: N(1000), max: 10000, unit: "per hour"),
                S("Limits", "daily", "Recipients per Day", "0 means Microsoft's limit.", "RecipientLimitPerDay", SettingKind.Number, recommended: N(1000), max: 10000, unit: "per day"),
                S("Limits", "over", "When Someone Goes Over", "What happens to the account.", "ActionWhenThresholdReached", SettingKind.Choice,
                    [("Alert", "Just Alert Admins"), ("BlockUserForToday", "Block Sending until Tomorrow"), ("BlockUser", "Block Sending until an Admin Unblocks")], V("BlockUser")),
                S("Forwarding", "forward", "Automatic Forwarding Outside", "People's forwarding rules that send mail outside the organization. Attackers use them to copy a hacked mailbox.",
                    "AutoForwardingMode", SettingKind.Choice, [("Automatic", "Off (Microsoft Decides)"), ("Off", "Off"), ("On", "On")], V("Automatic")),
                S("Alerts", "notify", "Tell Admins When Someone Is Blocked", "", "NotifyOutboundSpam"),
                S("Alerts", "notifywho", "Who to Tell", "", "NotifyOutboundSpamRecipients", SettingKind.List),
            ],
            "antiphish" =>
            [
                S("Spoofing", "spoofintel", "Spoof Intelligence", "Catch mail where the sender's domain is forged.", "EnableSpoofIntelligence", recommended: B(true), caution: false),
                S("Spoofing", "spoofaction", "Mail That Fails Spoofing Checks", "", "AuthenticationFailAction", SettingKind.Choice, JunkOrQuarantine, V("MoveToJmf")),
                S("Spoofing", "dmarc", "Follow the Sender's DMARC Policy", "Do what the sender's domain asks with mail that fails its checks.", "HonorDmarcPolicy", recommended: B(true)),
                S("Spoofing", "dmarcq", "When DMARC Says Quarantine", "", "DmarcQuarantineAction", SettingKind.Choice, JunkOrQuarantine, V("Quarantine")),
                S("Spoofing", "dmarcr", "When DMARC Says Reject", "", "DmarcRejectAction", SettingKind.Choice, [("Quarantine", "Quarantine"), ("Reject", "Reject")], V("Reject")),
                S("Spoofing", "spooftag", "Who Can Release Spoofed Mail", "", "SpoofQuarantineTag", SettingKind.Choice, tags, V("DefaultFullAccessPolicy")),
                S("Safety Tips", "firstcontact", "First-Contact Tip", "Tell people when someone writes to them for the first time.", "EnableFirstContactSafetyTips"),
                S("Safety Tips", "unauth", "Question Mark for Unverified Senders", "Show \"?\" on the sender's picture when their domain can't be verified.", "EnableUnauthenticatedSender", recommended: B(true)),
                S("Safety Tips", "via", "\"Via\" Tag", "Show which service actually sent it when that differs from the sender's domain.", "EnableViaTag", recommended: B(true)),
                S("Impersonation (Defender)", "threshold", "Phishing Threshold", "1 standard, 2 aggressive, 3 more aggressive, 4 most aggressive.", "PhishThresholdLevel", SettingKind.Number, recommended: N(3), min: 1, max: 4),
                S("Impersonation (Defender)", "orgdomains", "Protect Your Own Domains", "Catch mail from look-alikes of your domains.", "EnableOrganizationDomainsProtection", recommended: B(true)),
                S("Impersonation (Defender)", "mbi", "Mailbox Intelligence", "Learn who each person normally writes to, to spot impersonators.", "EnableMailboxIntelligence", recommended: B(true)),
                S("Impersonation (Defender)", "mbiprotect", "Act on Mailbox Intelligence", "", "EnableMailboxIntelligenceProtection", recommended: B(true)),
                S("Impersonation (Defender)", "mbiaction", "Impersonation Found by Mailbox Intelligence", "", "MailboxIntelligenceProtectionAction", SettingKind.Choice,
                    [("NoAction", "Nothing"), ("MoveToJmf", "Move to Junk Email"), ("Quarantine", "Quarantine")], V("MoveToJmf")),
                S("Impersonation (Defender)", "users", "Protect These People", "Leaders and others likely to be impersonated (up to 350, as \"Name;address\").", "TargetedUsersToProtect", SettingKind.List),
                S("Impersonation (Defender)", "usersprotect", "Act on Impersonated People", "", "EnableTargetedUserProtection"),
                S("Impersonation (Defender)", "useraction", "Mail Impersonating Those People", "", "TargetedUserProtectionAction", SettingKind.Choice,
                    [("NoAction", "Nothing"), ("MoveToJmf", "Move to Junk Email"), ("Quarantine", "Quarantine")], V("Quarantine")),
                S("Impersonation (Defender)", "domainaction", "Mail Impersonating Your Domains", "", "TargetedDomainProtectionAction", SettingKind.Choice,
                    [("NoAction", "Nothing"), ("MoveToJmf", "Move to Junk Email"), ("Quarantine", "Quarantine")], V("Quarantine")),
            ],
            "antimalware" =>
            [
                S("Protection", "filefilter", "Block Risky File Types", "Stop attachments of types that are almost never legitimate (.exe, .js, .iso…).", "EnableFileFilter", recommended: B(true), caution: false),
                S("Protection", "fileaction", "When a Blocked File Type Arrives", "", "FileTypeAction", SettingKind.Choice, [("Reject", "Bounce It"), ("Quarantine", "Quarantine")], V("Reject")),
                S("Protection", "filetypes", "Blocked File Types", "File extensions, one per line.", "FileTypes", SettingKind.List),
                S("Protection", "zap", "Remove Malware Found Later (ZAP)", "Pull it out of inboxes if recognized after delivery.", "ZapEnabled", recommended: B(true), caution: false),
                S("Protection", "tag", "Who Can Release Quarantined Malware", "", "QuarantineTag", SettingKind.Choice, tags, V("AdminOnlyAccessPolicy")),
                S("Alerts", "internalnotify", "Tell an Admin When Inside Senders Send Malware", "The admin address is set in the policy; this turns the notice on.", "EnableInternalSenderAdminNotifications"),
            ],
            "safelinks" =>
            [
                S("Where", "email", "Check Links in Email", "", "EnableSafeLinksForEmail", recommended: B(true), caution: false),
                S("Where", "teams", "Check Links in Teams", "", "EnableSafeLinksForTeams", recommended: B(true)),
                S("Where", "office", "Check Links in Office Apps", "Word, Excel, PowerPoint and the rest.", "EnableSafeLinksForOffice", recommended: B(true)),
                S("Where", "internal", "Check Mail Between Your People", "", "EnableForInternalSenders", recommended: B(true)),
                S("How", "scan", "Check Downloads Behind Links", "Scan files a link leads to, in real time.", "ScanUrls", recommended: B(true)),
                S("How", "waitscan", "Deliver Only After Checking", "Hold a message until its links are checked.", "DeliverMessageAfterScan", recommended: B(true)),
                S("How", "norewrite", "Leave Links As They Are", "Don't rewrite links in mail (they're still checked when clicked in Outlook).", "DisableUrlRewrite", recommended: B(false)),
                S("How", "track", "Track Clicks", "Record who clicked what, for investigations.", "TrackClicks", recommended: B(true)),
                S("How", "clickthrough", "Let People Click Through to a Bad Site", "", "AllowClickThrough", recommended: B(false), caution: true),
                S("How", "norewritelist", "Never Rewrite These Links", "", "DoNotRewriteUrls", SettingKind.List),
            ],
            "safeattach" =>
            [
                S("Protection", "enable", "Check Attachments", "", "Enable", recommended: B(true), caution: false),
                S("Protection", "action", "What to Do", "Block holds the message until checked; Dynamic Delivery delivers it at once and adds the attachment when it's safe.", "Action",
                    SettingKind.Choice, [("Block", "Block (Hold Until Checked)"), ("DynamicDelivery", "Dynamic Delivery"), ("Allow", "Allow (Only Check)")], V("Block")),
                S("Protection", "tag", "Who Can Release Quarantined Attachments", "", "QuarantineTag", SettingKind.Choice, tags, V("AdminOnlyAccessPolicy")),
                S("Protection", "redirect", "Send Malware Copies to an Admin", "", "Redirect"),
            ],
            _ => [],
        };
    }
}

/// <summary>Threat policies, quarantine policies and the connection filter (12f).</summary>
public interface IThreatPolicyRecords
{
    /// <summary>A kind's policies, each with its rule; the default last. Null when the tenant hasn't the licence for it.</summary>
    Task<IReadOnlyList<ThreatPolicy>?> GetThreatPoliciesAsync(ThreatPolicyKind kind, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuarantinePolicy>> GetQuarantinePoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>The organization's quarantine notification settings (frequency, branding).</summary>
    Task<JsonElement> GetQuarantineGlobalAsync(CancellationToken cancellationToken = default);

    Task<JsonElement> GetConnectionFilterAsync(CancellationToken cancellationToken = default);
}
