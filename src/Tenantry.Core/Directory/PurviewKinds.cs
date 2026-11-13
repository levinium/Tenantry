using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tenantry.Core.Directory;

/// <summary>What a Purview property holds beyond Exchange's plain kinds.</summary>
public enum PurviewValue
{
    /// <summary>As its <see cref="SettingKind"/> says.</summary>
    Plain,

    /// <summary>Places (people, groups, sites): added and taken away by their own parameters, and judged by Test mode.</summary>
    Places,

    /// <summary>Sensitive information types to look for, each with how many must be found (a DLP or auto-labeling rule's condition).</summary>
    SensitiveTypes,

    /// <summary>A keyword dictionary's words, sent as Purview's file data.</summary>
    Keywords,

    /// <summary>Names of other test-guarded items (an information barrier's segments): judged by Test mode.</summary>
    Names,
}

/// <summary>
/// One property of a Purview object: its words and editor, and how it's changed. Without a
/// <see cref="Parameter"/> it's shown, not changed.
/// </summary>
/// <param name="Property">As Purview's Get- cmdlet reads it.</param>
/// <param name="Parameter">The Set- cmdlet's parameter; null when it can't be changed here.</param>
/// <param name="Add">For lists changed piece by piece: the Set- parameter that adds ("AddExchangeLocation").</param>
/// <param name="Remove">And the one that takes away.</param>
/// <param name="CautionValues">Choosing one of these asks to confirm (turning a DLP policy on).</param>
public sealed record PurviewField(string Property, string Label, SettingKind Kind = SettingKind.Text, string? Help = null, string? Parameter = null,
    IReadOnlyList<(string Value, string Label)>? Choices = null, PurviewValue Value = PurviewValue.Plain, string? Add = null, string? Remove = null,
    IReadOnlyList<string>? CautionValues = null, int Min = 0, int Max = int.MaxValue, string? Unit = null, bool CautionOff = false)
{
    public bool CanChange => Parameter is not null || Add is not null;

    /// <summary>Places or names Test mode judges.</summary>
    public bool IsGuarded => Value is PurviewValue.Places or PurviewValue.Names;
}

/// <summary>One thing New can make: its fixed parameters and the boxes that fill in the rest (each box's Path is a New- parameter).</summary>
public sealed record PurviewCreate(string Key, string Title, string Description, IReadOnlyDictionary<string, JsonNode?> Fixed, IReadOnlyList<CreateField> Fields);

/// <summary>An organization-wide action a page offers (start applying information barriers).</summary>
public sealed record PurviewAction(string Title, string Cmdlet, string Description, string Done);

/// <summary>Members changed by their own cmdlets (a role group's).</summary>
public sealed record PurviewMembers(string Property, string AddCmdlet, string RemoveCmdlet, string MemberParameter = "Member");

/// <summary>
/// One kind of Purview object the generic page manages: the cmdlets that read, change, make and
/// delete it, what its properties are called, and what New makes. Adding a kind is one entry in
/// <see cref="PurviewKinds"/>.
/// </summary>
/// <param name="Noun">One of it, in a sentence ("sensitivity label").</param>
/// <param name="RulesOf">For a policy: the kind holding its rules, shown inside its card.</param>
/// <param name="ParentProperties">For a rule: the properties that name its policy (Purview varies).</param>
/// <param name="ParentParameter">For a rule: New-'s parameter naming its policy.</param>
/// <param name="Graph">Read from Microsoft Graph instead (a path whose answer has "value"); shown only.</param>
/// <param name="Licensing">Said when Purview refuses for licensing.</param>
/// <param name="Single">One organization-wide object (settings), not a list.</param>
/// <param name="ReadOnlyWhen">Microsoft's own items (built-in information types): shown, not changed or deleted.</param>
public sealed record PurviewKind(string Key, string Title, string About, string Noun, string Get, string? Set = null, string? New = null, string? Remove = null,
    IReadOnlyList<PurviewField>? Fields = null, IReadOnlyList<PurviewCreate>? Creates = null, string? RulesOf = null, IReadOnlyList<string>? ParentProperties = null,
    string ParentParameter = "Policy", string? Graph = null, string? Licensing = null, bool Single = false, Func<JsonElement, bool>? ReadOnlyWhen = null,
    string NameProperty = "Name", PurviewAction? Action = null, PurviewMembers? Members = null, bool LabelActions = false, string? ReadOnlyText = null)
{
    public bool IsRule => ParentProperties is not null;

    public PurviewField? Field(string property) => Fields?.FirstOrDefault(f => string.Equals(f.Property, property, StringComparison.OrdinalIgnoreCase));

    /// <summary>The policy a rule belongs to, from its row.</summary>
    public string? ParentOf(JsonElement row)
    {
        foreach (var p in ParentProperties ?? [])
            if (row.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } parent)
                return parent;
        return null;
    }
}

/// <summary>A Purview object as read: its name, the policy it belongs to (for a rule), and every property.</summary>
public sealed record PurviewObject(string Id, string Name, string? Parent, DateTimeOffset? Modified, JsonElement Raw);

/// <summary>A kind's items as read, and why they couldn't be (licensing) when Purview refused.</summary>
public sealed record PurviewRead(IReadOnlyList<PurviewObject> Items, string? Note = null);

/// <summary>A sensitive information type a rule looks for, and how many must be found.</summary>
public sealed record SensitiveTypeMatch(string Name, int MinCount = 1)
{
    public string Text => MinCount <= 1 ? Name : $"{Name} (at least {MinCount})";

    /// <summary>"Name" or "Name, 5" per line.</summary>
    public static IReadOnlyList<SensitiveTypeMatch> Parse(string text) =>
    [
        .. text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line =>
        {
            var comma = line.LastIndexOf(',');
            return comma > 0 && int.TryParse(line[(comma + 1)..].Trim(), out var n) && n > 0
                ? new SensitiveTypeMatch(line[..comma].Trim(), n)
                : new SensitiveTypeMatch(line);
        }).DistinctBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
    ];

    public static string Lines(IEnumerable<SensitiveTypeMatch> matches) => string.Join(Environment.NewLine, matches.Select(m => m.MinCount <= 1 ? m.Name : $"{m.Name}, {m.MinCount}"));

    /// <summary>Purview's condition: a list of tables with name and minCount.</summary>
    public static JsonArray ToParameter(IEnumerable<SensitiveTypeMatch> matches) =>
        new([.. matches.Select(m => (JsonNode)new JsonObject { ["Name"] = m.Name, ["minCount"] = m.MinCount.ToString(System.Globalization.CultureInfo.InvariantCulture) })]);

    /// <summary>
    /// From a rule's row: Purview gives a list of tables (name, mincount…), sometimes as text, and
    /// newer rules nest them in groups under "groups".
    /// </summary>
    public static IReadOnlyList<SensitiveTypeMatch> From(JsonElement value)
    {
        var found = new List<SensitiveTypeMatch>();
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var i in e.EnumerateArray())
                        Walk(i);
                    break;
                case JsonValueKind.Object:
                    string? name = null;
                    var min = 1;
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.Name.Equals("name", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                            name = p.Value.GetString();
                        else if (p.Name.Equals("mincount", StringComparison.OrdinalIgnoreCase))
                            min = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : int.TryParse(p.Value.GetString(), out var m) ? m : 1;
                        else if (p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                            Walk(p.Value);
                    }
                    // A group has a name too, but holds types under "sensitivetypes"; only leaves count.
                    if (name is { Length: > 0 } && !e.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Array))
                        found.Add(new SensitiveTypeMatch(name, Math.Max(1, min)));
                    break;
                case JsonValueKind.String when e.GetString() is { Length: > 1 } text && (text.TrimStart().StartsWith('[') || text.TrimStart().StartsWith('{')):
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        Walk(doc.RootElement);
                    }
                    catch (JsonException)
                    {
                    }
                    break;
            }
        }
        Walk(value);
        return [.. found.DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>Every Purview protection kind the generic page manages (14a-14f), with its words.</summary>
public static class PurviewKinds
{
    static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();
    static IReadOnlyDictionary<string, JsonNode?> Fixed(string json) => J(json).ToDictionary(p => p.Key, p => p.Value?.DeepClone());

    static readonly (string, string)[] Severity = [("Low", "Low"), ("Medium", "Medium"), ("High", "High"), ("None", "None")];
    static readonly (string, string)[] YesNo = [("true", "Yes"), ("false", "No")];

    /// <summary>A DLP or auto-labeling policy's mode: simulation first, on only by choice (Mark, 2026-11-12).</summary>
    static readonly (string, string)[] Modes =
    [
        ("TestWithoutNotifications", "Simulation"), ("TestWithNotifications", "Simulation with Policy Tips"), ("Enable", "On"), ("Disable", "Off"),
    ];

    static PurviewField Place(string kind, string label, string? help = null) =>
        new(kind + "Location", label, SettingKind.List, help, Value: PurviewValue.Places, Add: "Add" + kind + "Location", Remove: "Remove" + kind + "Location");

    static PurviewField Comment(string label = "Description") => new("Comment", label, Parameter: "Comment");

    // ---- Sensitivity labels (14a) ------------------------------------------------------------

    /// <summary>
    /// Get-Label keeps a label's markings, encryption and site settings inside LabelActions; the
    /// reader lifts them out under the names Set-Label takes.
    /// </summary>
    public static readonly PurviewKind Labels = new("labels", "Sensitivity Labels",
        "Labels people (or policies) put on email, files, sites and groups: they can mark content with a header, footer or watermark, encrypt it, and set a site's privacy. Publish them with a label policy so people can choose them.",
        "sensitivity label", "Get-Label", "Set-Label", "New-Label", "Remove-Label", LabelActions: true,
        Fields:
        [
            new("DisplayName", "Name People See", Parameter: "DisplayName"),
            new("Tooltip", "Description for People", Help: "Shown when people pick a label.", Parameter: "Tooltip"),
            Comment("Description for Admins"),
            new("ContentType", "Applies To", Help: "What it can be put on (files, email, sites, groups, meetings)."),
            new("Priority", "Order", SettingKind.Number, "Higher numbers are more sensitive."),
            new("ParentLabelDisplayName", "Inside Label"),
            new("ApplyContentMarkingHeaderEnabled", "Add a Header", SettingKind.OnOff, Parameter: "ApplyContentMarkingHeaderEnabled"),
            new("ApplyContentMarkingHeaderText", "Header Text", Parameter: "ApplyContentMarkingHeaderText"),
            new("ApplyContentMarkingFooterEnabled", "Add a Footer", SettingKind.OnOff, Parameter: "ApplyContentMarkingFooterEnabled"),
            new("ApplyContentMarkingFooterText", "Footer Text", Parameter: "ApplyContentMarkingFooterText"),
            new("ApplyWaterMarkingEnabled", "Add a Watermark", SettingKind.OnOff, Parameter: "ApplyWaterMarkingEnabled"),
            new("ApplyWaterMarkingText", "Watermark Text", Parameter: "ApplyWaterMarkingText"),
            new("EncryptionEnabled", "Encrypt", SettingKind.OnOff, "Only the people it allows can open the content.", Parameter: "EncryptionEnabled", CautionOff: true),
            new("EncryptionProtectionType", "Who Decides Permissions", SettingKind.Choice, Parameter: "EncryptionProtectionType",
                Choices: [("Template", "Set Here"), ("UserDefined", "The Person Applying It"), ("RemoveProtection", "Remove Encryption")]),
            new("EncryptionDoNotForward", "Do Not Forward (Email)", SettingKind.OnOff, Parameter: "EncryptionDoNotForward"),
            new("EncryptionContentExpiredOnDateInDaysOrNever", "Access Expires", Help: "\"Never\", or a number of days.", Parameter: "EncryptionContentExpiredOnDateInDaysOrNever"),
            new("SiteAndGroupProtectionEnabled", "Protect Sites and Groups", SettingKind.OnOff, Parameter: "SiteAndGroupProtectionEnabled"),
            new("SiteAndGroupProtectionPrivacy", "Site and Group Privacy", SettingKind.Choice, Parameter: "SiteAndGroupProtectionPrivacy", Choices: [("Public", "Public"), ("Private", "Private")]),
            new("SiteAndGroupProtectionAllowAccessToGuestUsers", "Let Guests In", SettingKind.OnOff, Parameter: "SiteAndGroupProtectionAllowAccessToGuestUsers"),
            new("SiteExternalSharingControlType", "Sharing Outside", SettingKind.Choice, Parameter: "SiteExternalSharingControlType",
                Choices: [("ExternalUserAndGuestSharing", "Anyone"), ("ExternalUserSharingOnly", "New and Existing Guests"), ("ExistingExternalUserSharingOnly", "Existing Guests"), ("Disabled", "Only People Inside")]),
        ],
        Creates:
        [
            new("label", "Sensitivity Label", "Marks nothing and encrypts nothing until you turn those on in its card. Publish it with a label policy so people can choose it.",
                Fixed("""{}"""),
                [new("Name", "Name"), new("DisplayName", "Name People See"), new("Tooltip", "Description for People"), new("Comment", "Description for Admins", Required: false),
                 new("ContentType", "Applies To", SettingKind.Choice, Choices: [("File, Email", "Files and Email"), ("Site, UnifiedGroup", "Sites and Groups"), ("File, Email, Site, UnifiedGroup", "Files, Email, Sites and Groups")], Default: "File, Email")]),
        ]);

    public static readonly PurviewKind LabelPolicies = new("labelpolicies", "Label Policies",
        "Publish labels to people: which labels they can choose, and settings like a default label or requiring one.",
        "label policy", "Get-LabelPolicy", "Set-LabelPolicy", "New-LabelPolicy", "Remove-LabelPolicy",
        Fields:
        [
            Comment(),
            new("Labels", "Labels It Publishes", SettingKind.List, Add: "AddLabels", Remove: "RemoveLabels"),
            Place("Exchange", "People and Groups", "Addresses, or All."),
            Place("ModernGroup", "Microsoft 365 Groups (Sites and Groups Labels)"),
            new("Priority", "Order", SettingKind.Number),
            new("Mode", "State"),
            new("DistributionStatus", "Put in Place"),
        ],
        Creates:
        [
            new("policy", "Label Policy", "Publishes labels to the people you name.", Fixed("""{}"""),
                [new("Name", "Name"), new("Labels", "Labels (One per Line)", SettingKind.List), new("ExchangeLocation", "People and Groups (One Address per Line)", SettingKind.List),
                 new("Comment", "Description", Required: false)]),
        ]);

    // ---- Auto-labeling (14b) ------------------------------------------------------------------

    public static readonly PurviewKind AutoLabelPolicies = new("autolabel", "Auto-Labeling",
        "Puts a label on email and files automatically when they hold sensitive information. New policies run in simulation, so you can see what they'd label before turning them on.",
        "auto-labeling policy", "Get-AutoSensitivityLabelPolicy", "Set-AutoSensitivityLabelPolicy", "New-AutoSensitivityLabelPolicy", "Remove-AutoSensitivityLabelPolicy",
        RulesOf: "autolabelrules", Licensing: "Auto-labeling needs Microsoft 365 E5 (or E5 Compliance or Information Protection and Governance).",
        Fields:
        [
            Comment(),
            new("ApplySensitivityLabel", "Label It Applies", Parameter: "ApplySensitivityLabel"),
            new("Mode", "Mode", SettingKind.Choice, "Simulation shows what it would label; On labels it.", Parameter: "Mode", Choices: Modes, CautionValues: ["Enable"]),
            Place("Exchange", "Mailboxes", "Addresses, or All."),
            Place("SharePoint", "SharePoint Sites", "Site addresses, or All."),
            Place("OneDrive", "OneDrive Accounts", "OneDrive addresses, or All."),
            new("Priority", "Order", SettingKind.Number),
            new("DistributionStatus", "Put in Place"),
        ],
        Creates:
        [
            new("policy", "Auto-Labeling Policy", "Starts in simulation. Add a rule (what to look for) in its card.", Fixed("""{"Mode":"TestWithoutNotifications"}"""),
                [new("Name", "Name"), new("ApplySensitivityLabel", "Label to Apply"), new("ExchangeLocation", "Mailboxes (One Address per Line)", SettingKind.List, Required: false),
                 new("OneDriveLocation", "OneDrive Accounts (One Address per Line)", SettingKind.List, Required: false), new("Comment", "Description", Required: false)]),
        ]);

    public static readonly PurviewKind AutoLabelRules = new("autolabelrules", "Auto-Labeling Rules", "What an auto-labeling policy looks for.",
        "rule", "Get-AutoSensitivityLabelRule", "Set-AutoSensitivityLabelRule", "New-AutoSensitivityLabelRule", "Remove-AutoSensitivityLabelRule",
        ParentProperties: ["ParentPolicyName", "Policy"],
        Fields:
        [
            Comment(),
            new("Workload", "Looks In"),
            new("Disabled", "Off", SettingKind.OnOff, Parameter: "Disabled"),
            new("ContentContainsSensitiveInformation", "Sensitive Information", SettingKind.LongText, "One type per line; add \", 5\" to need at least 5.",
                Parameter: "ContentContainsSensitiveInformation", Value: PurviewValue.SensitiveTypes),
            new("ReportSeverityLevel", "Severity", SettingKind.Choice, Parameter: "ReportSeverityLevel", Choices: Severity),
        ],
        Creates:
        [
            new("rule", "Rule", "Labels content holding the sensitive information you list.", Fixed("""{}"""),
                [new("Name", "Name"), new("Workload", "Looks In", SettingKind.Choice, Choices: [("Exchange", "Email"), ("SharePoint", "SharePoint"), ("OneDriveForBusiness", "OneDrive")], Default: "Exchange"),
                 new("ContentContainsSensitiveInformation", "Sensitive Information (One Type per Line)", SettingKind.List, Placeholder: "Credit Card Number")]),
        ]);

    // ---- Data loss prevention (14c) -----------------------------------------------------------

    public static readonly PurviewKind DlpPolicies = new("dlp", "DLP Policies",
        "Data loss prevention: finds sensitive information in email, files, Teams and devices, and warns, reports or blocks sharing it. New policies run in simulation; turning one on is a separate, confirmed change.",
        "DLP policy", "Get-DlpCompliancePolicy", "Set-DlpCompliancePolicy", "New-DlpCompliancePolicy", "Remove-DlpCompliancePolicy", RulesOf: "dlprules",
        Fields:
        [
            Comment(),
            new("Mode", "Mode", SettingKind.Choice, "Simulation reports what it would do; On does it.", Parameter: "Mode", Choices: Modes, CautionValues: ["Enable"]),
            Place("Exchange", "Email", "Purview takes All here, or nothing."),
            Place("SharePoint", "SharePoint Sites", "Site addresses, or All."),
            Place("OneDrive", "OneDrive Accounts", "OneDrive addresses, or All."),
            Place("Teams", "Teams Chats and Channels", "People and groups, or All."),
            Place("EndpointDlp", "Devices", "People and groups, or All (needs devices onboarded)."),
            new("ExchangeSenderMemberOf", "Only Email From Members Of", SettingKind.List, Parameter: "ExchangeSenderMemberOf", Value: PurviewValue.Names),
            new("Priority", "Order", SettingKind.Number),
            new("DistributionStatus", "Put in Place"),
        ],
        Creates:
        [
            new("policy", "DLP Policy", "Starts in simulation. Add rules (what to look for and what to do) in its card.", Fixed("""{"Mode":"TestWithoutNotifications"}"""),
                [new("Name", "Name"), new("TeamsLocation", "Teams: People and Groups (One per Line)", SettingKind.List, Required: false),
                 new("OneDriveLocation", "OneDrive Accounts (One Address per Line)", SettingKind.List, Required: false),
                 new("SharePointLocation", "SharePoint Sites (One Address per Line)", SettingKind.List, Required: false),
                 new("ExchangeLocation", "Email (All, or Leave Blank)", SettingKind.List, Required: false), new("Comment", "Description", Required: false)]),
        ]);

    public static readonly PurviewKind DlpRules = new("dlprules", "DLP Rules", "What a DLP policy looks for and what it does.",
        "rule", "Get-DlpComplianceRule", "Set-DlpComplianceRule", "New-DlpComplianceRule", "Remove-DlpComplianceRule", ParentProperties: ["ParentPolicyName", "Policy"],
        Fields:
        [
            Comment(),
            new("Disabled", "Off", SettingKind.OnOff, Parameter: "Disabled"),
            new("ContentContainsSensitiveInformation", "Sensitive Information", SettingKind.LongText, "One type per line; add \", 5\" to need at least 5.",
                Parameter: "ContentContainsSensitiveInformation", Value: PurviewValue.SensitiveTypes),
            new("AccessScope", "Shared", SettingKind.Choice, Parameter: "AccessScope", Choices: [("NotInOrganization", "Outside the Organization"), ("InOrganization", "Inside the Organization"), ("None", "Anywhere")]),
            new("BlockAccess", "Block Access", SettingKind.OnOff, "Stops the email or file reaching the people it's shared with.", Parameter: "BlockAccess"),
            new("BlockAccessScope", "Block For", SettingKind.Choice, Parameter: "BlockAccessScope", Choices: [("All", "Everyone"), ("PerUser", "People Outside"), ("PerAnonymousUser", "Anyone-with-the-Link")]),
            new("NotifyUser", "Tell", SettingKind.List, "Owner, LastModifier, SiteAdmin, or addresses.", Parameter: "NotifyUser"),
            new("NotifyPolicyTipCustomText", "Policy Tip Text", Parameter: "NotifyPolicyTipCustomText"),
            new("GenerateIncidentReport", "Send Incident Reports To", SettingKind.List, "Addresses, or SiteAdmin.", Parameter: "GenerateIncidentReport"),
            new("GenerateAlert", "Alert", SettingKind.List, "Addresses, or SiteAdmin.", Parameter: "GenerateAlert"),
            new("ReportSeverityLevel", "Severity", SettingKind.Choice, Parameter: "ReportSeverityLevel", Choices: Severity),
            new("Priority", "Order", SettingKind.Number),
        ],
        Creates:
        [
            new("rule", "Rule", "Looks for the sensitive information you list, shared the way you choose.", Fixed("""{}"""),
                [new("Name", "Name"), new("ContentContainsSensitiveInformation", "Sensitive Information (One Type per Line)", SettingKind.List, Placeholder: "Credit Card Number"),
                 new("AccessScope", "Shared", SettingKind.Choice, Choices: [("NotInOrganization", "Outside the Organization"), ("InOrganization", "Inside the Organization"), ("None", "Anywhere")], Default: "NotInOrganization"),
                 new("BlockAccess", "Block Access", SettingKind.Choice, Choices: YesNo, Default: "false"),
                 new("ReportSeverityLevel", "Severity", SettingKind.Choice, Choices: Severity, Default: "Low")]),
        ]);

    public static readonly PurviewKind DlpAlerts = new("dlpalerts", "DLP Alerts",
        "Alerts DLP policies raised, newest first. Investigate them in Microsoft Defender or Purview.",
        "alert", "", Graph: "security/alerts_v2?$filter=serviceSource eq 'dataLossPrevention'&$top=200", NameProperty: "title",
        Fields:
        [
            new("severity", "Severity"), new("status", "Status"), new("description", "What Happened"), new("createdDateTime", "Raised"), new("assignedTo", "Assigned To"),
            new("classification", "Classification"), new("policyName", "Policy"),
        ]);

    public static readonly PurviewKind EndpointDlp = new("endpointdlp", "Endpoint DLP Settings",
        "Organization-wide settings for DLP on devices: browsers and apps it watches, paths it skips, and more. Shown as Purview keeps them.",
        "settings", "Get-PolicyConfig", Single: true, NameProperty: "Name",
        Fields: [new("EnableLabelCoauth", "Co-Authoring of Encrypted Files", SettingKind.OnOff), new("EnableSpoAipMigration", "Labels in SharePoint and OneDrive", SettingKind.OnOff)]);

    // ---- Classifiers (14d) --------------------------------------------------------------------

    public static readonly PurviewKind InfoTypes = new("infotypes", "Sensitive Info Types",
        "What DLP and auto-labeling can look for: Microsoft's types (card numbers, ID numbers…) and your own. Custom types made from patterns are built in Purview; Tenantry shows them and can delete them.",
        "information type", "Get-DlpSensitiveInformationType", Remove: "Remove-DlpSensitiveInformationType",
        ReadOnlyWhen: r => r.TryGetProperty("Publisher", out var p) && (p.GetString() ?? "").StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase),
        ReadOnlyText: "Microsoft's: it can't be changed or deleted.",
        Fields:
        [
            new("Publisher", "Made By"), new("Description", "Description"), new("Type", "Kind"), new("RecommendedConfidence", "Recommended Confidence", SettingKind.Number),
            new("Locale", "Language"),
        ]);

    public static readonly PurviewKind KeywordDictionaries = new("keywords", "Keyword Dictionaries",
        "Lists of words (product names, project code names) a custom information type can look for.",
        "keyword dictionary", "Get-DlpKeywordDictionary", "Set-DlpKeywordDictionary", "New-DlpKeywordDictionary", "Remove-DlpKeywordDictionary",
        Fields:
        [
            new("Description", "Description", Parameter: "Description"),
            new("KeywordDictionary", "Words", SettingKind.LongText, "One per line.", Parameter: "FileData", Value: PurviewValue.Keywords),
        ],
        Creates:
        [
            new("dictionary", "Keyword Dictionary", "A list of words to look for.", Fixed("""{}"""),
                [new("Name", "Name"), new("Description", "Description"), new("FileData", "Words (One per Line)", SettingKind.List)]),
        ]);

    public static readonly PurviewKind EdmSchemas = new("edm", "Exact Data Match Schemas",
        "The columns of a table of real data (employee IDs, account numbers) that exact data match types compare content against. The data itself is uploaded with Microsoft's upload agent.",
        "schema", "Get-DlpEdmSchema", Remove: "Remove-DlpEdmSchema", Licensing: "Exact data match needs Microsoft 365 E5 (or E5 Compliance).",
        Fields: [new("Description", "Description"), new("DataStoreName", "Data Store")]);

    // ---- Insider risk and communication compliance (14e) --------------------------------------

    public static readonly PurviewKind InsiderRisk = new("insiderrisk", "Insider Risk Policies",
        "Spots risky activity (data theft by people leaving, leaks) from signals across Microsoft 365. Policies are made from Microsoft's templates in Purview; Tenantry shows and deletes them.",
        "insider risk policy", "Get-InsiderRiskPolicy", Remove: "Remove-InsiderRiskPolicy",
        Licensing: "Insider risk management needs Microsoft 365 E5 (or E5 Compliance or E5 Insider Risk Management).",
        Fields: [new("InsiderRiskScenario", "Template"), new("Enabled", "On", SettingKind.OnOff), new("Comment", "Description")]);

    public static readonly PurviewKind CommunicationCompliance = new("commcompliance", "Communication Compliance",
        "Reviews a sample of messages (email, Teams) for inappropriate or sensitive content, sent to the reviewers you name.",
        "communication compliance policy", "Get-SupervisoryReviewPolicyV2", "Set-SupervisoryReviewPolicyV2", "New-SupervisoryReviewPolicyV2", "Remove-SupervisoryReviewPolicyV2",
        RulesOf: "commrules", Licensing: "Communication compliance needs Microsoft 365 E5 (or E5 Compliance or E5 Insider Risk Management).",
        Fields: [Comment(), new("Reviewers", "Reviewers", SettingKind.List, "Addresses.", Parameter: "Reviewers", Value: PurviewValue.Names), new("Enabled", "On", SettingKind.OnOff)],
        Creates:
        [
            new("policy", "Communication Compliance Policy", "Add a rule (what it reviews and how much) in its card.", Fixed("""{}"""),
                [new("Name", "Name"), new("Reviewers", "Reviewers (One Address per Line)", SettingKind.List), new("Comment", "Description", Required: false)]),
        ]);

    public static readonly PurviewKind CommunicationRules = new("commrules", "Communication Compliance Rules", "What a communication compliance policy reviews.",
        "rule", "Get-SupervisoryReviewRule", "Set-SupervisoryReviewRule", "New-SupervisoryReviewRule", "Remove-SupervisoryReviewRule", ParentProperties: ["Policy", "ParentPolicyName"],
        Fields:
        [
            new("SamplingRate", "Share of Messages Reviewed", SettingKind.Number, Parameter: "SamplingRate", Min: 1, Max: 100, Unit: "%"),
            new("Condition", "Messages That Match", Help: "A condition in Purview's words, like (Reviewee:someone@example.com).", Parameter: "Condition"),
            Place("Exchange", "People Reviewed"),
        ],
        Creates:
        [
            new("rule", "Rule", "Reviews a share of the messages the people you name send.", Fixed("""{}"""),
                [new("Name", "Name"), new("SamplingRate", "Share Reviewed (%)", SettingKind.Number, Default: "10"),
                 new("ExchangeLocation", "People Reviewed (One Address per Line)", SettingKind.List)]),
        ]);

    // ---- Information barriers, file plan, roles (14f) ------------------------------------------

    public static readonly PurviewKind Segments = new("segments", "Segments",
        "Groups of people, picked by a directory attribute (a department), that information barrier policies keep apart or let talk.",
        "segment", "Get-OrganizationSegment", "Set-OrganizationSegment", "New-OrganizationSegment", "Remove-OrganizationSegment",
        Fields: [new("UserGroupFilter", "Who's In It", Help: "Like Department -eq 'Research'.", Parameter: "UserGroupFilter")],
        Creates: [new("segment", "Segment", "", Fixed("""{}"""), [new("Name", "Name"), new("UserGroupFilter", "Who's In It", Placeholder: "Department -eq 'Research'")])]);

    public static readonly PurviewKind Barriers = new("barriers", "Information Barriers",
        "Policies that stop one segment finding, messaging or sharing with another in Teams, SharePoint and OneDrive. Start Applying puts every active policy in place (it can take hours).",
        "information barrier policy", "Get-InformationBarrierPolicy", "Set-InformationBarrierPolicy", "New-InformationBarrierPolicy", "Remove-InformationBarrierPolicy",
        Action: new("Start Applying", "Start-InformationBarrierPoliciesApplication", "Puts every active information barrier policy in place for everyone.", "Applying; Microsoft takes a while (hours for large organizations)."),
        Fields:
        [
            new("AssignedSegment", "For Segment"),
            new("State", "State", SettingKind.Choice, Parameter: "State", Choices: [("Inactive", "Inactive"), ("Active", "Active")], CautionValues: ["Active"]),
            new("SegmentsBlocked", "Can't Talk To", SettingKind.List, Parameter: "SegmentsBlocked", Value: PurviewValue.Names),
            new("SegmentsAllowed", "Can Only Talk To", SettingKind.List, Parameter: "SegmentsAllowed", Value: PurviewValue.Names),
            new("ModerationAllowed", "Moderation Allowed", SettingKind.OnOff),
        ],
        Creates:
        [
            new("policy", "Information Barrier Policy", "Starts inactive; make it active and use Start Applying.", Fixed("""{"State":"Inactive"}"""),
                [new("Name", "Name"), new("AssignedSegment", "For Segment"), new("SegmentsBlocked", "Can't Talk To (One Segment per Line)", SettingKind.List)]),
        ]);

    static PurviewKind FilePlan(string key, string what, string title, string about) => new(key, title, about, what.ToLowerInvariant(),
        $"Get-FilePlanProperty{what}", New: $"New-FilePlanProperty{what}", Remove: $"Remove-FilePlanProperty{what}",
        Creates: [new("new", title.TrimEnd('s'), "", Fixed("""{}"""), [new("Name", "Name")])]);

    public static readonly PurviewKind FilePlanDepartments = FilePlan("fp-departments", "Department", "Departments", "Who a retention label's records belong to.");
    public static readonly PurviewKind FilePlanCategories = FilePlan("fp-categories", "Category", "Categories", "Broad kinds of records (Finance, HR).");
    public static readonly PurviewKind FilePlanAuthorities = FilePlan("fp-authorities", "Authority", "Authorities", "Why records are kept (business, legal, regulatory).");
    public static readonly PurviewKind FilePlanReferences = FilePlan("fp-references", "ReferenceId", "Reference IDs", "Your own codes for labels.");

    public static readonly PurviewKind FilePlanSubCategories = new("fp-subcategories", "Subcategories", "Narrower kinds of records, inside a category.", "subcategory",
        "Get-FilePlanPropertySubCategory", New: "New-FilePlanPropertySubCategory", Remove: "Remove-FilePlanPropertySubCategory",
        Fields: [new("ParentId", "Category")],
        Creates: [new("new", "Subcategory", "", Fixed("""{}"""), [new("Name", "Name"), new("ParentId", "Category")])]);

    public static readonly PurviewKind FilePlanCitations = new("fp-citations", "Citations", "The laws or rules a label follows, with a link.", "citation",
        "Get-FilePlanPropertyCitation", "Set-FilePlanPropertyCitation", "New-FilePlanPropertyCitation", "Remove-FilePlanPropertyCitation",
        Fields: [new("CitationUrl", "Link", Parameter: "CitationUrl"), new("CitationJurisdiction", "Jurisdiction", Parameter: "CitationJurisdiction")],
        Creates: [new("new", "Citation", "", Fixed("""{}"""), [new("Name", "Name"), new("CitationUrl", "Link", Required: false), new("CitationJurisdiction", "Jurisdiction", Required: false)])]);

    public static readonly PurviewKind RoleGroups = new("purviewroles", "Purview Role Groups",
        "Who can do what in Purview (eDiscovery managers, compliance administrators…): each group's roles, and its members.",
        "role group", "Get-RoleGroup", Members: new("Members", "Add-RoleGroupMember", "Remove-RoleGroupMember"),
        Fields: [new("Description", "Description"), new("Roles", "Roles", SettingKind.List), new("Members", "Members", SettingKind.List)]);

    /// <summary>Every kind, by key.</summary>
    public static IReadOnlyList<PurviewKind> All { get; } =
    [
        Labels, LabelPolicies, AutoLabelPolicies, AutoLabelRules, DlpPolicies, DlpRules, DlpAlerts, EndpointDlp, InfoTypes, KeywordDictionaries, EdmSchemas,
        InsiderRisk, CommunicationCompliance, CommunicationRules, Segments, Barriers,
        FilePlanDepartments, FilePlanCategories, FilePlanSubCategories, FilePlanAuthorities, FilePlanCitations, FilePlanReferences, RoleGroups,
    ];

    public static PurviewKind Get(string key) => All.Single(k => k.Key == key);

    /// <summary>
    /// The pages: most show one kind; the file plan shows its six together. Rules show inside
    /// their policies' cards, not on their own pages.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<PurviewKind>> Pages { get; } = new Dictionary<string, IReadOnlyList<PurviewKind>>
    {
        ["labels"] = [Labels], ["labelpolicies"] = [LabelPolicies], ["autolabel"] = [AutoLabelPolicies],
        ["dlp"] = [DlpPolicies], ["dlpalerts"] = [DlpAlerts], ["endpointdlp"] = [EndpointDlp],
        ["infotypes"] = [InfoTypes], ["keywords"] = [KeywordDictionaries], ["edm"] = [EdmSchemas],
        ["insiderrisk"] = [InsiderRisk], ["commcompliance"] = [CommunicationCompliance], ["segments"] = [Segments], ["barriers"] = [Barriers],
        ["fileplan"] = [FilePlanDepartments, FilePlanCategories, FilePlanSubCategories, FilePlanAuthorities, FilePlanCitations, FilePlanReferences],
        ["purviewroles"] = [RoleGroups],
    };

    /// <summary>Whether a New- parameter holds places (Test mode judges each).</summary>
    public static bool IsPlace(string parameter) => parameter.EndsWith("Location", StringComparison.Ordinal) || parameter.EndsWith("LocationException", StringComparison.Ordinal);

    /// <summary>Whether a New- parameter holds names or people Test mode judges.</summary>
    public static bool IsGuardedParameter(PurviewKind kind, string parameter) =>
        IsPlace(parameter) || parameter is "Reviewers" or "SegmentsBlocked" or "SegmentsAllowed" or "AssignedSegment"
        || kind.Field(parameter)?.IsGuarded == true;

    /// <summary>
    /// Get-Label keeps a label's settings in LabelActions (one JSON text per action: type, subtype,
    /// settings as key/value pairs); these are lifted out under Set-Label's names.
    /// </summary>
    public static JsonObject LiftLabelActions(JsonElement label)
    {
        var lifted = new JsonObject();
        if (!label.TryGetProperty("LabelActions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            return lifted;
        foreach (var a in actions.EnumerateArray())
        {
            JsonElement action;
            try
            {
                action = a.ValueKind == JsonValueKind.String ? JsonDocument.Parse(a.GetString()!).RootElement.Clone() : a;
            }
            catch (JsonException)
            {
                continue;
            }
            var type = action.TryGetProperty("Type", out var t) ? t.GetString()?.ToLowerInvariant() : null;
            var sub = action.TryGetProperty("SubType", out var st) ? st.GetString()?.ToLowerInvariant() : null;
            var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (action.TryGetProperty("Settings", out var s) && s.ValueKind == JsonValueKind.Array)
                foreach (var kv in s.EnumerateArray())
                    if (kv.TryGetProperty("Key", out var k) && k.GetString() is { } key)
                        settings[key] = kv.TryGetProperty("Value", out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
            bool? Flag(string key) => settings.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : null;
            switch (type)
            {
                case "applycontentmarking" when sub == "header":
                    lifted["ApplyContentMarkingHeaderEnabled"] = true;
                    lifted["ApplyContentMarkingHeaderText"] = settings.GetValueOrDefault("text");
                    break;
                case "applycontentmarking" when sub == "footer":
                    lifted["ApplyContentMarkingFooterEnabled"] = true;
                    lifted["ApplyContentMarkingFooterText"] = settings.GetValueOrDefault("text");
                    break;
                case "applywatermarking":
                    lifted["ApplyWaterMarkingEnabled"] = true;
                    lifted["ApplyWaterMarkingText"] = settings.GetValueOrDefault("text");
                    break;
                case "encrypt":
                    lifted["EncryptionEnabled"] = !string.Equals(settings.GetValueOrDefault("disabled"), "true", StringComparison.OrdinalIgnoreCase);
                    if (settings.GetValueOrDefault("protectiontype") is { } protection)
                        lifted["EncryptionProtectionType"] = protection;
                    if (Flag("donotforward") is { } dnf)
                        lifted["EncryptionDoNotForward"] = dnf;
                    if (settings.GetValueOrDefault("contentexpiredondateindaysornever") is { } expires)
                        lifted["EncryptionContentExpiredOnDateInDaysOrNever"] = expires;
                    break;
                case "protectgroup" or "protectsite":
                    lifted["SiteAndGroupProtectionEnabled"] = !string.Equals(settings.GetValueOrDefault("disabled"), "true", StringComparison.OrdinalIgnoreCase);
                    if (settings.GetValueOrDefault("privacy") is { } privacy)
                        lifted["SiteAndGroupProtectionPrivacy"] = privacy;
                    if (Flag("allowaccesstoguestusers") is { } guests)
                        lifted["SiteAndGroupProtectionAllowAccessToGuestUsers"] = guests;
                    if (settings.GetValueOrDefault("externalsharingcontroltype") is { } sharing)
                        lifted["SiteExternalSharingControlType"] = sharing;
                    break;
            }
        }
        foreach (var off in new[] { "ApplyContentMarkingHeaderEnabled", "ApplyContentMarkingFooterEnabled", "ApplyWaterMarkingEnabled", "EncryptionEnabled", "SiteAndGroupProtectionEnabled" })
            lifted[off] ??= false;
        return lifted;
    }
}

/// <summary>The generic Purview objects (14a-14f).</summary>
public interface IPurviewObjectRecords
{
    /// <summary>A kind's items (for a rule kind, every policy's rules), or why Purview refused.</summary>
    Task<PurviewRead> GetObjectsAsync(PurviewKind kind, CancellationToken cancellationToken = default);

    /// <summary>The names of every sensitive information type, for rule editors.</summary>
    Task<IReadOnlyList<string>> GetSensitiveTypeNamesAsync(CancellationToken cancellationToken = default);
}
