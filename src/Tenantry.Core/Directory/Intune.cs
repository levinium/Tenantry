using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tenantry.Core.Directory;

/// <summary>Who an Intune policy or app reaches: everyone, a group in or out, narrowed by an assignment filter.</summary>
public enum AssignmentKind
{
    AllUsers,
    AllDevices,
    Group,
    ExcludeGroup,
}

/// <summary>One assignment: All users, All devices, a group included or excluded; an include can be narrowed by a filter.</summary>
/// <param name="FilterMode">"include" or "exclude" (devices matching the filter's rule), when there's a filter.</param>
/// <param name="Intent">For an app: "required", "available" or "uninstall"; null for policies.</param>
public sealed record IntuneAssignment(AssignmentKind Kind, string? GroupId = null, string? GroupName = null, string? FilterId = null, string? FilterName = null,
    string? FilterMode = null, string? Intent = null)
{
    public static IntuneAssignment AllUsers() => new(AssignmentKind.AllUsers);
    public static IntuneAssignment AllDevices() => new(AssignmentKind.AllDevices);

    /// <summary>"All Users", a group's name, or "Not: …", with its filter.</summary>
    public string Text
    {
        get
        {
            var who = Kind switch
            {
                AssignmentKind.AllUsers => "All Users",
                AssignmentKind.AllDevices => "All Devices",
                AssignmentKind.ExcludeGroup => $"Not {GroupName ?? "a group"}",
                _ => GroupName ?? "A group",
            };
            var filter = FilterName is null ? "" : $" ({(FilterMode == "exclude" ? "except" : "only")} devices matching {FilterName})";
            return (Intent is { } i ? $"{IntentText(i)}: " : "") + who + filter;
        }
    }

    public static string IntentText(string intent) => intent switch
    {
        "required" => "Required",
        "available" => "Available",
        "uninstall" => "Uninstall",
        "availableWithoutEnrollment" => "Available Without Enrollment",
        _ => intent,
    };

    /// <summary>The same assignment ignoring the names (for comparing lists).</summary>
    public (AssignmentKind, string?, string?, string?, string?) Identity => (Kind, GroupId?.ToLowerInvariant(), FilterId?.ToLowerInvariant(), FilterMode, Intent);

    /// <summary>Graph's assignment target for it.</summary>
    public JsonObject Target()
    {
        var target = new JsonObject
        {
            ["@odata.type"] = Kind switch
            {
                AssignmentKind.AllUsers => "#microsoft.graph.allLicensedUsersAssignmentTarget",
                AssignmentKind.AllDevices => "#microsoft.graph.allDevicesAssignmentTarget",
                AssignmentKind.ExcludeGroup => "#microsoft.graph.exclusionGroupAssignmentTarget",
                _ => "#microsoft.graph.groupAssignmentTarget",
            },
        };
        if (GroupId is not null)
            target["groupId"] = GroupId;
        if (FilterId is not null && Kind != AssignmentKind.ExcludeGroup)
        {
            target["deviceAndAppManagementAssignmentFilterId"] = FilterId;
            target["deviceAndAppManagementAssignmentFilterType"] = FilterMode ?? "include";
        }
        return target;
    }

    /// <summary>Reads a Graph assignment (its target and, for apps, intent); group names are filled in by the caller.</summary>
    public static IntuneAssignment? From(JsonElement assignment)
    {
        if (!assignment.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object)
            return null;
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var kind = Str(target, "@odata.type") switch
        {
            "#microsoft.graph.allLicensedUsersAssignmentTarget" => AssignmentKind.AllUsers,
            "#microsoft.graph.allDevicesAssignmentTarget" => AssignmentKind.AllDevices,
            "#microsoft.graph.exclusionGroupAssignmentTarget" => AssignmentKind.ExcludeGroup,
            "#microsoft.graph.groupAssignmentTarget" => AssignmentKind.Group,
            _ => (AssignmentKind?)null,
        };
        if (kind is null)
            return null;
        var filter = Str(target, "deviceAndAppManagementAssignmentFilterId") is { Length: > 0 } f && f != "00000000-0000-0000-0000-000000000000" ? f : null;
        var mode = filter is null ? null : Str(target, "deviceAndAppManagementAssignmentFilterType") is "exclude" ? "exclude" : "include";
        return new IntuneAssignment(kind.Value, Str(target, "groupId"), null, filter, null, mode, Str(assignment, "intent"));
    }
}

/// <summary>An assignment filter: devices matching a rule (model, OS, ownership…), used to narrow an assignment.</summary>
public sealed record AssignmentFilter(string Id, string Name, string Platform, string Rule);

/// <summary>An email Intune can send people whose devices aren't compliant.</summary>
public sealed record NotificationTemplate(string Id, string Name);

/// <summary>A step Intune takes when a device doesn't meet a compliance policy, after a grace period.</summary>
/// <param name="Action">"block" (mark noncompliant), "notification" (email), "pushNotification", "remoteLock", "retire".</param>
public sealed record ComplianceAction(string Action, int GraceHours, string? TemplateId = null, IReadOnlyList<string>? CcGroupIds = null)
{
    public static string ActionText(string action) => action switch
    {
        "block" => "Mark the device noncompliant",
        "notification" => "Email the user",
        "pushNotification" => "Send a notification to the device",
        "remoteLock" => "Lock the device",
        "retire" => "Add the device to the retire list",
        _ => action,
    };

    public string Text => $"{ActionText(Action)} {(GraceHours == 0 ? "at once" : GraceHours % 24 == 0 ? $"after {GraceHours / 24} {(GraceHours == 24 ? "day" : "days")}" : $"after {GraceHours} hours")}";
}

/// <summary>How many devices meet a policy.</summary>
public sealed record ComplianceCounts(int Compliant, int NonCompliant, int Error, int Conflict, int NotApplicable, int Pending)
{
    public int Total => Compliant + NonCompliant + Error + Conflict + Pending;
}

/// <summary>A compliance policy: its platform (Graph's type), settings as read, who it reaches and what happens to devices that don't comply.</summary>
public sealed record CompliancePolicy(string Id, string Name, string? Description, string ODataType, DateTimeOffset? Modified, JsonElement Raw,
    IReadOnlyList<IntuneAssignment> Assignments, IReadOnlyList<ComplianceAction> Actions, ComplianceCounts? Counts)
{
    public string Platform => CompliancePlatforms.Title(ODataType);
}

/// <summary>One device's state under a compliance policy.</summary>
public sealed record DeviceComplianceStatus(string DeviceName, string? UserName, string Status, DateTimeOffset? LastReported)
{
    public static string StatusText(string status) => status switch
    {
        "compliant" => "Compliant",
        "nonCompliant" => "Not Compliant",
        "error" => "Error",
        "conflict" => "Conflict",
        "notApplicable" => "Not Applicable",
        "inGracePeriod" => "In Grace Period",
        "remediated" => "Remediated",
        "unknown" => "Not Reported Yet",
        _ => status,
    };
}

/// <summary>The compliance policy platforms Intune offers, and the settings Tenantry shows for each.</summary>
public static class CompliancePlatforms
{
    public const string Windows = "#microsoft.graph.windows10CompliancePolicy";
    public const string MacOS = "#microsoft.graph.macOSCompliancePolicy";
    public const string IOS = "#microsoft.graph.iosCompliancePolicy";
    public const string AndroidWorkProfile = "#microsoft.graph.androidWorkProfileCompliancePolicy";
    public const string AndroidDeviceOwner = "#microsoft.graph.androidDeviceOwnerCompliancePolicy";
    public const string AndroidAdmin = "#microsoft.graph.androidCompliancePolicy";
    public const string Aosp = "#microsoft.graph.aospDeviceOwnerCompliancePolicy";

    public static IReadOnlyList<(string ODataType, string Title)> All { get; } =
    [
        (Windows, "Windows 10 and Later"),
        (MacOS, "macOS"),
        (IOS, "iOS/iPadOS"),
        (AndroidWorkProfile, "Android Personally Owned Work Profile"),
        (AndroidDeviceOwner, "Android Fully Managed, Dedicated and Corporate Work Profile"),
        (Aosp, "Android (AOSP)"),
        (AndroidAdmin, "Android Device Administrator"),
    ];

    public static string Title(string odataType) => All.FirstOrDefault(p => p.ODataType == odataType).Title ?? odataType.Replace("#microsoft.graph.", "", StringComparison.Ordinal);

    static SettingDef S(string odataType, string group, string property, string label, string help = "", SettingKind kind = SettingKind.OnOff,
        (string, string)[]? choices = null, int min = 0, int max = int.MaxValue, string? unit = null, bool? caution = null) =>
        new(group, property, label, help, "intune", odataType, property, kind, Choices: choices, Min: min, Max: max, Unit: unit, CautionWhen: caution);

    static readonly (string, string)[] ThreatLevels =
        [("unavailable", "Not Required"), ("secured", "Secured (No Threats)"), ("low", "Low"), ("medium", "Medium"), ("high", "High")];

    /// <summary>The Defender for Endpoint risk level a device may have at most (Windows calls it the machine risk score).</summary>
    static SettingDef Threat(string t, string property = "deviceThreatProtectionRequiredSecurityLevel") =>
        S(t, "Device Health", property, "Highest Allowed Threat Level", "From Microsoft Defender for Endpoint or a partner threat defense app.", SettingKind.Choice, ThreatLevels);

    static IEnumerable<SettingDef> Os(string t, bool build = false) =>
    [
        S(t, "Device Properties", "osMinimumVersion", "Minimum OS Version", "Older devices are noncompliant (like 10.0.19045).", SettingKind.Text),
        S(t, "Device Properties", "osMaximumVersion", "Maximum OS Version", "Newer (unapproved) versions are noncompliant.", SettingKind.Text),
        .. build ? [S(t, "Device Properties", "osMinimumBuildVersion", "Minimum OS Build", "", SettingKind.Text)] : Array.Empty<SettingDef>(),
    ];

    static IEnumerable<SettingDef> Password(string t, string prefix, (string, string)[] types, string blockCount) =>
    [
        S(t, "Password", prefix + "Required", "Require a Password to Unlock", "", caution: false),
        S(t, "Password", prefix + "MinimumLength", "Minimum Length", "", SettingKind.Number, min: 4, max: 16, unit: "characters"),
        S(t, "Password", prefix + "RequiredType", "Type of Password", "", SettingKind.Choice, types),
        S(t, "Password", prefix + "MinutesOfInactivityBeforeLock", "Lock After Inactivity", "Minutes idle before the password is asked for again.", SettingKind.Number, min: 1, max: 1440, unit: "minutes"),
        S(t, "Password", prefix + "ExpirationDays", "Password Expires After", "", SettingKind.Number, min: 1, max: 730, unit: "days"),
        S(t, "Password", blockCount, "Passwords Remembered", "How many previous passwords can't be used again.", SettingKind.Number, min: 1, max: 24),
    ];

    static readonly (string, string)[] SimpleTypes = [("deviceDefault", "Device Default"), ("alphanumeric", "Letters and Numbers"), ("numeric", "Numbers")];

    static readonly (string, string)[] AndroidTypes =
    [
        ("deviceDefault", "Device Default"), ("lowSecurityBiometric", "Low Security Biometric"), ("required", "At Least Numeric"), ("numeric", "Numeric"),
        ("numericComplex", "Numeric Complex"), ("alphabetic", "Alphabetic"), ("alphanumeric", "Alphanumeric"), ("alphanumericWithSymbols", "Alphanumeric with Symbols"),
    ];

    /// <summary>A platform's settings, by group, as the admin center's compliance wizard arranges them.</summary>
    public static IReadOnlyList<SettingDef> Settings(string odataType)
    {
        var t = odataType;
        return t switch
        {
            Windows =>
            [
                S(t, "Device Health", "bitLockerEnabled", "Require BitLocker", "The drive is encrypted, reported by Windows Device Health Attestation."),
                S(t, "Device Health", "secureBootEnabled", "Require Secure Boot", "Only trusted software can start the device."),
                S(t, "Device Health", "codeIntegrityEnabled", "Require Code Integrity", "Windows checks drivers and system files when they load."),
                S(t, "Device Health", "earlyLaunchAntiMalwareDriverEnabled", "Require Early Launch Anti-Malware", ""),
                Threat(t),
                .. Os(t),
                S(t, "Device Properties", "mobileOsMinimumVersion", "Minimum Mobile OS Version", "", SettingKind.Text),
                S(t, "Configuration Manager", "configurationManagerComplianceRequired", "Require Configuration Manager Compliance", "Also count Configuration Manager's compliance."),
                S(t, "System Security", "passwordRequired", "Require a Password to Unlock", "", caution: false),
                S(t, "System Security", "passwordBlockSimple", "Block Simple Passwords", "No 1234 or 1111."),
                S(t, "System Security", "passwordRequiredType", "Type of Password", "", SettingKind.Choice, SimpleTypes),
                S(t, "System Security", "passwordMinimumLength", "Minimum Password Length", "", SettingKind.Number, min: 4, max: 16, unit: "characters"),
                S(t, "System Security", "passwordMinutesOfInactivityBeforeLock", "Lock After Inactivity", "", SettingKind.Number, min: 1, max: 1440, unit: "minutes"),
                S(t, "System Security", "passwordExpirationDays", "Password Expires After", "", SettingKind.Number, min: 1, max: 730, unit: "days"),
                S(t, "System Security", "passwordPreviousPasswordBlockCount", "Passwords Remembered", "", SettingKind.Number, min: 1, max: 24),
                S(t, "System Security", "passwordRequiredToUnlockFromIdle", "Require a Password When Waking", ""),
                S(t, "System Security", "storageRequireEncryption", "Require Encryption of Data Storage", ""),
                S(t, "System Security", "activeFirewallRequired", "Require the Firewall", ""),
                S(t, "System Security", "tpmRequired", "Require a Trusted Platform Module (TPM)", ""),
                S(t, "System Security", "antivirusRequired", "Require Antivirus", "Registered with Windows Security Center."),
                S(t, "System Security", "antiSpywareRequired", "Require Antispyware", ""),
                S(t, "Defender", "defenderEnabled", "Require Microsoft Defender Antimalware", ""),
                S(t, "Defender", "defenderVersion", "Minimum Defender Antimalware Version", "Like 4.18.1906.3.", SettingKind.Text),
                S(t, "Defender", "signatureOutOfDate", "Require Up-to-Date Defender Definitions", ""),
                S(t, "Defender", "rtpEnabled", "Require Real-Time Protection", ""),
            ],
            MacOS =>
            [
                S(t, "Device Health", "systemIntegrityProtectionEnabled", "Require System Integrity Protection", ""),
                Threat(t),
                .. Os(t, build: true),
                .. Password(t, "password", SimpleTypes, "passwordPreviousPasswordBlockCount"),
                S(t, "Password", "passwordBlockSimple", "Block Simple Passwords", ""),
                S(t, "System Security", "storageRequireEncryption", "Require Encryption (FileVault)", ""),
                S(t, "System Security", "firewallEnabled", "Require the Firewall", ""),
                S(t, "System Security", "firewallBlockAllIncoming", "Block All Incoming Connections", ""),
                S(t, "System Security", "firewallEnableStealthMode", "Stealth Mode", "Don't answer probing requests."),
                S(t, "System Security", "gatekeeperAllowedAppSource", "Allow Apps From", "Gatekeeper.", SettingKind.Choice,
                    [("notConfigured", "Not Configured"), ("macAppStore", "Mac App Store"), ("macAppStoreAndIdentifiedDevelopers", "Mac App Store and Identified Developers"), ("anywhere", "Anywhere")]),
            ],
            IOS =>
            [
                S(t, "Device Health", "securityBlockJailbrokenDevices", "Block Jailbroken Devices", ""),
                Threat(t),
                .. Os(t, build: true),
                .. Password(t, "passcode", SimpleTypes, "passcodePreviousPasscodeBlockCount"),
                S(t, "Password", "passcodeBlockSimple", "Block Simple Passcodes", ""),
                S(t, "Password", "passcodeMinutesOfInactivityBeforeScreenTimeout", "Screen Timeout", "", SettingKind.Number, min: 1, max: 60, unit: "minutes"),
                S(t, "Email", "managedEmailProfileRequired", "Require a Managed Email Profile", ""),
            ],
            AndroidWorkProfile or AndroidAdmin =>
            [
                S(t, "Device Health", "securityBlockJailbrokenDevices", "Block Rooted Devices", ""),
                S(t, "Device Health", "securityRequireSafetyNetAttestationBasicIntegrity", "Require Play Integrity: Basic", ""),
                S(t, "Device Health", "securityRequireSafetyNetAttestationCertifiedDevice", "Require Play Integrity: Certified Device", ""),
                S(t, "Device Health", "securityRequireGooglePlayServices", "Require Google Play Services", ""),
                S(t, "Device Health", "securityRequireUpToDateSecurityProviders", "Require an Up-to-Date Security Provider", ""),
                Threat(t),
                .. Os(t),
                S(t, "Device Properties", "minAndroidSecurityPatchLevel", "Minimum Security Patch Level", "A date, like 2026-01-01.", SettingKind.Text),
                .. Password(t, "password", AndroidTypes, "passwordPreviousPasswordBlockCount"),
                S(t, "System Security", "storageRequireEncryption", "Require Encryption", ""),
                S(t, "System Security", "securityPreventInstallAppsFromUnknownSources", "Block Apps from Unknown Sources", ""),
                S(t, "System Security", "securityDisableUsbDebugging", "Block USB Debugging", ""),
                S(t, "System Security", "securityRequireVerifyApps", "Require Threat Scan on Apps", ""),
                S(t, "System Security", "securityRequireCompanyPortalAppIntegrity", "Require Company Portal App Integrity", ""),
            ],
            AndroidDeviceOwner or Aosp =>
            [
                S(t, "Device Health", "securityRequireSafetyNetAttestationBasicIntegrity", "Require Play Integrity: Basic", ""),
                S(t, "Device Health", "securityRequireSafetyNetAttestationCertifiedDevice", "Require Play Integrity: Certified Device", ""),
                Threat(t),
                .. Os(t),
                S(t, "Device Properties", "minAndroidSecurityPatchLevel", "Minimum Security Patch Level", "A date, like 2026-01-01.", SettingKind.Text),
                .. Password(t, "password", AndroidTypes, "passwordPreviousPasswordCountToBlock"),
                S(t, "System Security", "storageRequireEncryption", "Require Encryption", ""),
                S(t, "System Security", "securityRequireIntuneAppIntegrity", "Require Intune App Integrity", ""),
            ],
            _ => [],
        };
    }

    /// <summary>The settings Intune sends back on every policy that Tenantry doesn't show (names, dates, IDs).</summary>
    public static readonly IReadOnlySet<string> Housekeeping = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "@odata.type", "id", "displayName", "description", "createdDateTime", "lastModifiedDateTime", "version", "roleScopeTagIds", "assignments",
        "scheduledActionsForRule", "deviceStatuses", "userStatuses", "deviceStatusOverview", "userStatusOverview", "deviceSettingStateSummaries",
        "assignments@odata.context", "scheduledActionsForRule@odata.context",
    };
}

/// <summary>Intune's policies (13a: compliance) and what they need from the tenant (groups' names, filters, email templates).</summary>
public interface IIntunePolicyRecords
{
    /// <summary>Every compliance policy, with its assignments, actions for noncompliance and device counts.</summary>
    Task<IReadOnlyList<CompliancePolicy>> GetCompliancePoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>Each device's state under one compliance policy.</summary>
    Task<IReadOnlyList<DeviceComplianceStatus>> GetComplianceStatusesAsync(string policyId, CancellationToken cancellationToken = default);

    /// <summary>The tenant-wide compliance settings (Graph's deviceManagement settings).</summary>
    Task<JsonElement> GetComplianceSettingsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssignmentFilter>> GetAssignmentFiltersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationTemplate>> GetNotificationTemplatesAsync(CancellationToken cancellationToken = default);
}
