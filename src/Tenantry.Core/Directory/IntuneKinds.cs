using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tenantry.Core.Directory;

/// <summary>A property's words and editor, for the generic Intune object editor (unknown properties are named from their Graph names).</summary>
/// <param name="Kind">Overrides the editor guessed from the value (a choice, a script).</param>
/// <param name="Hidden">Not shown (Graph's housekeeping, or something edited elsewhere on the card).</param>
public sealed record PropertyLabel(string Label, string? Help = null, SettingKind? Kind = null, IReadOnlyList<(string Value, string Label)>? Choices = null, bool Hidden = false,
    int Min = 0, int Max = int.MaxValue, string? Unit = null);

/// <summary>A box on a New form: where its value goes in the new object's body ("installExperience.runAsAccount"), and how it's typed.</summary>
public sealed record CreateField(string Path, string Label, SettingKind Kind = SettingKind.Text, string? Placeholder = null, IReadOnlyList<(string Value, string Label)>? Choices = null,
    bool Required = true, string? Default = null);

/// <summary>One thing New can make (a web link, a Store app…): its body, and the boxes that fill it in.</summary>
/// <param name="Then">After it's made: a path under it and a body to post there (a notification template's message).</param>
public sealed record CreateOption(string Key, string Title, string Description, JsonObject Body, IReadOnlyList<CreateField> Fields, (string Path, JsonObject Body)? Then = null);

/// <summary>How a kind's assignments are changed.</summary>
public enum AssignMode
{
    /// <summary>One POST to {item}/assign with every assignment.</summary>
    Assign,

    /// <summary>Shown, not changed here (Intune changes them one by one elsewhere).</summary>
    ReadOnly,

    /// <summary>The kind isn't assigned.</summary>
    None,
}

/// <summary>
/// One kind of Intune object the generic page manages: where it's read, how it's assigned, what
/// its properties are called, and what New makes. Adding a kind is one entry in <see cref="IntuneKinds"/>.
/// </summary>
/// <param name="ListUrl">The collection, with any $expand/$filter.</param>
/// <param name="AssignProperty">The assign body's list ("assignments", "mobileAppAssignments", …).</param>
/// <param name="AssignItem">Extra fields every assignment carries (a remediation's schedule).</param>
/// <param name="Types">Which Graph types belong (a shared collection like deviceConfigurations); null for all.</param>
/// <param name="ReadOnlyWhen">Items Microsoft keeps (built-in roles): shown, not changed.</param>
/// <param name="DefaultWhen">Defaults that apply to everyone else: settings change, but they aren't assigned or deleted.</param>
public sealed record IntuneKind(string Key, string Title, string About, string ListUrl, AssignMode Assign = AssignMode.Assign, string AssignProperty = "assignments",
    JsonObject? AssignItem = null, bool ForApps = false, string NameProperty = "displayName", IReadOnlyList<CreateOption>? Creates = null,
    IReadOnlyDictionary<string, PropertyLabel>? Labels = null, Func<string, bool>? Types = null, Func<JsonElement, bool>? ReadOnlyWhen = null, bool ExpandAssignments = true,
    string? ProtectedAppsPlatform = null, bool InstallSummary = false, Func<JsonElement, bool>? DefaultWhen = null)
{
    /// <summary>The collection without its query.</summary>
    public string Collection => ListUrl.Split('?')[0];

    public string ItemUrl(string id) => $"{Collection}/{id}";

    public bool Belongs(string odataType) => Types is null || Types(odataType);

    public PropertyLabel? Label(string property) => Labels?.GetValueOrDefault(property) ?? IntuneKinds.Common.GetValueOrDefault(property);
}

/// <summary>An Intune object as read: its name, Graph type, assignments and every property.</summary>
public sealed record IntuneObject(string Id, string Name, string? Description, string ODataType, DateTimeOffset? Modified, IReadOnlyList<IntuneAssignment> Assignments, JsonElement Raw)
{
    public string TypeText => ODataType.Length == 0 ? "" : SettingsCatalog.Words(ODataType.Replace("#microsoft.graph.", "", StringComparison.Ordinal));
}

/// <summary>How an app's installs stand.</summary>
public sealed record AppInstallSummary(int Installed, int Failed, int Pending, int NotInstalled, int NotApplicable)
{
    public string Text => Installed + Failed + Pending + NotInstalled == 0 ? "No installs reported yet"
        : string.Join(" · ", new[] { $"{Installed:N0} installed", Failed > 0 ? $"{Failed:N0} failed" : null, Pending > 0 ? $"{Pending:N0} pending" : null,
            NotInstalled > 0 ? $"{NotInstalled:N0} not installed" : null }.OfType<string>());
}

/// <summary>Every Intune object kind the generic page manages (13c-13f), with its words.</summary>
public static class IntuneKinds
{
    const string Beta = "https://graph.microsoft.com/beta/";

    static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();

    static readonly (string, string)[] RunAs = [("system", "System"), ("user", "Signed-In User")];
    static readonly (string, string)[] Allowed = [("allowed", "Allowed"), ("required", "Required"), ("blocked", "Blocked")];
    static readonly (string, string)[] OnOffNot = [("notConfigured", "Not Configured"), ("enabled", "Enabled"), ("disabled", "Disabled")];
    static readonly (string, string)[] Transfer = [("allApps", "Any App"), ("managedApps", "Policy-Managed Apps"), ("none", "None")];

    /// <summary>Words for properties many kinds share; a kind's own labels come first.</summary>
    public static readonly IReadOnlyDictionary<string, PropertyLabel> Common = new Dictionary<string, PropertyLabel>(StringComparer.OrdinalIgnoreCase)
    {
        ["displayName"] = new("Name"),
        ["name"] = new("Name"),
        ["description"] = new("Description"),
        ["publisher"] = new("Publisher"),
        ["runAsAccount"] = new("Run As", Kind: SettingKind.Choice, Choices: RunAs),
        ["runAs32Bit"] = new("Run in 32-Bit PowerShell"),
        ["enforceSignatureCheck"] = new("Require a Signed Script"),
        ["fileName"] = new("File Name"),
        ["scriptContent"] = new("Script", Kind: SettingKind.Script),
        ["detectionScriptContent"] = new("Detection Script", "Exits 1 when the problem is found, so the remediation runs.", SettingKind.Script),
        ["remediationScriptContent"] = new("Remediation Script", "Fixes what detection found.", SettingKind.Script),
        ["isGlobalScript"] = new("Global", Hidden: true),
        ["roleScopeTagIds"] = new("Scope Tags", Hidden: true),
        ["supportsScopeTags"] = new("Supports Scope Tags", Hidden: true),
        ["isAssigned"] = new("Assigned", Hidden: true),
        ["uploadState"] = new("Upload State", Hidden: true),
        ["publishingState"] = new("Publishing State", Hidden: true),
        ["dependentAppCount"] = new("Dependent Apps", Hidden: true),
        ["supersedingAppCount"] = new("Superseding Apps", Hidden: true),
        ["supersededAppCount"] = new("Superseded Apps", Hidden: true),
        ["priority"] = new("Priority", "Lower runs first; it decides which applies when several reach a device.", Min: 1, Max: 1000),
        ["deviceEnrollmentConfigurationType"] = new("Type", Hidden: true),
        ["highestAvailableVersion"] = new("Latest Version", Hidden: true),
        ["deviceHealthScriptType"] = new("Script Type", Hidden: true),
        ["isPrivacySet"] = new("Privacy", Hidden: true),
        ["deployedAppCount"] = new("Apps", Hidden: true),
        ["isAssigned@odata.type"] = new("", Hidden: true),
    };

    // ---- Apps (13c) ---------------------------------------------------------------------------

    public static readonly IntuneKind Apps = new("apps", "Apps",
        "Apps Intune installs or offers: Store apps, Microsoft 365 Apps, web links and packaged apps. Each assignment is Required (installed on its own), Available (offered in Company Portal) or Uninstall.",
        Beta + "deviceAppManagement/mobileApps?$expand=assignments", AssignProperty: "mobileAppAssignments", ForApps: true, InstallSummary: true,
        Labels: new Dictionary<string, PropertyLabel>
        {
            ["appUrl"] = new("Web Address"),
            ["useManagedBrowser"] = new("Open in Microsoft Edge (Managed)"),
            ["appStoreUrl"] = new("Store Address"),
            ["bundleId"] = new("Bundle ID"),
            ["packageId"] = new("Package ID"),
            ["packageIdentifier"] = new("Store Package ID"),
            ["isFeatured"] = new("Featured in Company Portal"),
            ["informationUrl"] = new("Information Address"),
            ["privacyInformationUrl"] = new("Privacy Address"),
            ["owner"] = new("Owner"),
            ["developer"] = new("Developer"),
            ["notes"] = new("Notes"),
            ["updateChannel"] = new("Update Channel", Kind: SettingKind.Choice, Choices: [("current", "Current"), ("monthlyEnterprise", "Monthly Enterprise"), ("deferred", "Semi-Annual Enterprise"), ("firstReleaseCurrent", "Current (Preview)")]),
            ["officePlatformArchitecture"] = new("Architecture", Kind: SettingKind.Choice, Choices: [("x64", "64-Bit"), ("x86", "32-Bit")]),
            ["useSharedComputerActivation"] = new("Shared Computer Activation", "For PCs several people use."),
            ["autoAcceptEula"] = new("Accept the License for People"),
            ["installExperience.runAsAccount"] = new("Install As", Kind: SettingKind.Choice, Choices: RunAs),
            ["committedContentVersion"] = new("Content Version", Hidden: true),
            ["size"] = new("Size", Hidden: true),
            ["installCommandLine"] = new("Install Command"),
            ["uninstallCommandLine"] = new("Uninstall Command"),
            ["setupFilePath"] = new("Setup File"),
            ["allowAvailableUninstall"] = new("Allow Uninstall from Company Portal"),
            ["minimumFreeDiskSpaceInMB"] = new("Minimum Free Disk Space", Unit: "MB"),
            ["minimumMemoryInMB"] = new("Minimum Memory", Unit: "MB"),
            ["displayVersion"] = new("Version"),
        },
        Creates:
        [
            new("winget", "Microsoft Store App (Windows)", "An app from the Microsoft Store, kept up to date by the Store.",
                J("""{"@odata.type":"#microsoft.graph.winGetApp","installExperience":{"runAsAccount":"user"}}"""),
                [new("displayName", "Name"), new("publisher", "Publisher"), new("packageIdentifier", "Store Package ID", Placeholder: "Like 9WZDNCRFJ3PZ (from the Store address)"),
                 new("installExperience.runAsAccount", "Install As", SettingKind.Choice, Choices: RunAs, Default: "user")]),
            new("m365", "Microsoft 365 Apps (Windows)", "Word, Excel, PowerPoint, Outlook and Teams, from Microsoft's servers.",
                J("""{"@odata.type":"#microsoft.graph.officeSuiteApp","publisher":"Microsoft","productIds":["o365ProPlusRetail"],"localesToInstall":[],"autoAcceptEula":true,"excludedApps":{"groove":true,"lync":true,"infoPath":true,"sharePointDesigner":true,"bing":true},"description":"Microsoft 365 Apps"}"""),
                [new("displayName", "Name", Default: "Microsoft 365 Apps for Windows"),
                 new("updateChannel", "Update Channel", SettingKind.Choice, Choices: [("current", "Current"), ("monthlyEnterprise", "Monthly Enterprise"), ("deferred", "Semi-Annual Enterprise")], Default: "monthlyEnterprise"),
                 new("officePlatformArchitecture", "Architecture", SettingKind.Choice, Choices: [("x64", "64-Bit"), ("x86", "32-Bit")], Default: "x64")]),
            new("web", "Web Link", "A website, shown in Company Portal like an app.", J("""{"@odata.type":"#microsoft.graph.webApp","useManagedBrowser":false}"""),
                [new("displayName", "Name"), new("publisher", "Publisher"), new("appUrl", "Web Address", Placeholder: "https://…")]),
            new("ios", "iOS/iPadOS Store App", "An app from Apple's App Store.",
                J("""{"@odata.type":"#microsoft.graph.iosStoreApp","applicableDeviceType":{"iPad":true,"iPhoneAndIPod":true},"minimumSupportedOperatingSystem":{"v15_0":true}}"""),
                [new("displayName", "Name"), new("publisher", "Publisher"), new("appStoreUrl", "App Store Address", Placeholder: "https://apps.apple.com/…"), new("bundleId", "Bundle ID")]),
            new("android", "Android Store Link", "A link to an app on Google Play.",
                J("""{"@odata.type":"#microsoft.graph.androidStoreApp","minimumSupportedOperatingSystem":{"v8_0":true}}"""),
                [new("displayName", "Name"), new("publisher", "Publisher"), new("appStoreUrl", "Google Play Address", Placeholder: "https://play.google.com/store/apps/details?id=…"),
                 new("packageId", "Package ID", Placeholder: "Like com.microsoft.office.outlook")]),
        ]);

    static readonly Dictionary<string, PropertyLabel> Mam = new()
    {
        ["pinRequired"] = new("Require a PIN for Access"),
        ["minimumPinLength"] = new("Minimum PIN Length", Min: 4, Max: 16),
        ["simplePinBlocked"] = new("Block Simple PINs"),
        ["fingerprintBlocked"] = new("Block Fingerprint Instead of PIN"),
        ["disableAppPinIfDevicePinIsSet"] = new("No App PIN When the Device Has One"),
        ["dataBackupBlocked"] = new("Block Backing Up Work Data"),
        ["deviceComplianceRequired"] = new("Require a Compliant Device"),
        ["managedBrowserToOpenLinksRequired"] = new("Open Links in Microsoft Edge"),
        ["saveAsBlocked"] = new("Block Save As"),
        ["printBlocked"] = new("Block Printing"),
        ["contactSyncBlocked"] = new("Block Syncing Contacts"),
        ["organizationalCredentialsRequired"] = new("Require Work Credentials"),
        ["allowedInboundDataTransferSources"] = new("Receive Data From", Kind: SettingKind.Choice, Choices: Transfer),
        ["allowedOutboundDataTransferDestinations"] = new("Send Data To", Kind: SettingKind.Choice, Choices: Transfer),
        ["allowedOutboundClipboardSharingLevel"] = new("Copy and Paste To", Kind: SettingKind.Choice,
            Choices: [("allApps", "Any App"), ("managedAppsWithPasteIn", "Policy-Managed Apps, Paste In Allowed"), ("managedApps", "Policy-Managed Apps"), ("blocked", "Blocked")]),
        ["maximumPinRetries"] = new("PIN Attempts Before Reset", Min: 1, Max: 10),
        ["periodOfflineBeforeAccessCheck"] = new("Recheck After Offline For", "A duration, like PT12H (12 hours)."),
        ["periodOfflineBeforeWipeIsEnforced"] = new("Wipe Work Data After Offline For", "A duration, like P90D (90 days)."),
        ["minimumRequiredOsVersion"] = new("Minimum OS Version"),
        ["minimumRequiredAppVersion"] = new("Minimum App Version"),
        ["appDataEncryptionType"] = new("Encrypt Work Data", Kind: SettingKind.Choice,
            Choices: [("useDeviceSettings", "Use Device Settings"), ("afterDeviceRestart", "After Device Restart"), ("whenDeviceLockedExceptOpenFiles", "When Locked, Except Open Files"), ("whenDeviceLocked", "When Locked")]),
        ["encryptAppData"] = new("Encrypt Work Data"),
        ["screenCaptureBlocked"] = new("Block Screen Capture"),
        ["isAssigned"] = new("Assigned", Hidden: true),
        ["deployedAppCount"] = new("Apps", Hidden: true),
        ["version"] = new("Version", Hidden: true),
    };

    public static readonly IntuneKind IosProtection = new("mam-ios", "iOS App Protection",
        "Protects work data inside apps on iPhones and iPads, with or without enrolling the device: a PIN, no copying out to personal apps, a wipe after long offline.",
        "deviceAppManagement/iosManagedAppProtections?$expand=assignments,apps", Labels: Mam, ProtectedAppsPlatform: "ios",
        Creates: [new("new", "iOS App Protection Policy", "Protects Microsoft's apps (add others after).", J("""{"@odata.type":"#microsoft.graph.iosManagedAppProtection","pinRequired":true,"appDataEncryptionType":"whenDeviceLocked"}"""),
            [new("displayName", "Name")])]);

    public static readonly IntuneKind AndroidProtection = new("mam-android", "Android App Protection",
        "Protects work data inside apps on Android phones, with or without enrolling the device.",
        "deviceAppManagement/androidManagedAppProtections?$expand=assignments,apps", Labels: Mam, ProtectedAppsPlatform: "android",
        Creates: [new("new", "Android App Protection Policy", "Protects Microsoft's apps (add others after).", J("""{"@odata.type":"#microsoft.graph.androidManagedAppProtection","pinRequired":true,"encryptAppData":true}"""),
            [new("displayName", "Name")])]);

    public static readonly IntuneKind AppConfiguration = new("appconfig", "App Configuration",
        "Settings handed to apps (a default server, a feature on or off), as key and value pairs the app understands.",
        "deviceAppManagement/targetedManagedAppConfigurations?$expand=assignments",
        Creates: [new("new", "App Configuration Policy", "Key and value settings for managed apps.", J("""{"@odata.type":"#microsoft.graph.targetedManagedAppConfiguration","customSettings":[]}"""),
            [new("displayName", "Name")])]);

    public static readonly IntuneKind DeviceAppConfiguration = new("mdmappconfig", "App Configuration (Managed Devices)",
        "App settings delivered through device management, for iOS and Android Enterprise apps.",
        "deviceAppManagement/mobileAppConfigurations?$expand=assignments");

    // ---- Updates (13d) ------------------------------------------------------------------------

    static readonly HashSet<string> UpdateTypes = ["#microsoft.graph.windowsUpdateForBusinessConfiguration", "#microsoft.graph.iosUpdateConfiguration", "#microsoft.graph.macOSSoftwareUpdateConfiguration"];

    /// <summary>Update policies live among the template profiles; the configuration page leaves them to the update pages.</summary>
    public static bool IsUpdateType(string odataType) => UpdateTypes.Contains(odataType);

    public static readonly IntuneKind UpdateRings = new("rings", "Update Rings",
        "When Windows (and iOS and macOS) devices take Microsoft's updates: how long to wait, when to restart, and the deadline after which updates install anyway.",
        "deviceManagement/deviceConfigurations?$expand=assignments", Types: IsUpdateType,
        Labels: new Dictionary<string, PropertyLabel>
        {
            ["qualityUpdatesDeferralPeriodInDays"] = new("Wait for Monthly Updates", Min: 0, Max: 30, Unit: "days"),
            ["featureUpdatesDeferralPeriodInDays"] = new("Wait for Feature Updates", Min: 0, Max: 365, Unit: "days"),
            ["qualityUpdatesPaused"] = new("Pause Monthly Updates"),
            ["featureUpdatesPaused"] = new("Pause Feature Updates"),
            ["driversExcluded"] = new("Leave Out Driver Updates"),
            ["microsoftUpdateServiceAllowed"] = new("Also Update Other Microsoft Products"),
            ["automaticUpdateMode"] = new("Install Updates", Kind: SettingKind.Choice, Choices:
            [
                ("userDefined", "Let People Choose"), ("notifyDownload", "Notify Before Downloading"), ("autoInstallAtMaintenanceTime", "Install at Maintenance Time"),
                ("autoInstallAndRebootAtMaintenanceTime", "Install and Restart at Maintenance Time"), ("autoInstallAndRebootAtScheduledTime", "Install and Restart at a Scheduled Time"),
                ("autoInstallAndRebootWithoutEndUserControl", "Install and Restart Without Asking"),
            ]),
            ["deadlineForFeatureUpdatesInDays"] = new("Deadline for Feature Updates", Min: 0, Max: 30, Unit: "days"),
            ["deadlineForQualityUpdatesInDays"] = new("Deadline for Monthly Updates", Min: 0, Max: 30, Unit: "days"),
            ["deadlineGracePeriodInDays"] = new("Grace Period Before Restart", Min: 0, Max: 7, Unit: "days"),
            ["postponeRebootUntilAfterDeadline"] = new("Don't Restart Before the Deadline"),
            ["userPauseAccess"] = new("People Can Pause Updates", Kind: SettingKind.Choice, Choices: OnOffNot),
            ["userWindowsUpdateScanAccess"] = new("People Can Check for Updates", Kind: SettingKind.Choice, Choices: OnOffNot),
            ["updateNotificationLevel"] = new("Update Notifications", Kind: SettingKind.Choice, Choices:
                [("defaultNotifications", "Default"), ("restartWarningsOnly", "Restart Warnings Only"), ("disableAllNotifications", "None")]),
            ["businessReadyUpdatesOnly"] = new("Update Channel", Hidden: true),
            ["skipChecksBeforeRestart"] = new("Skip Checks Before Restart"),
            ["engagedRestartDeadlineInDays"] = new("Engaged Restart Deadline", Unit: "days"),
            ["allowWindows11Upgrade"] = new("Allow the Upgrade to Windows 11"),
        },
        Creates: [new("ring", "Windows Update Ring", "Monthly updates after a short wait, installed by a deadline.",
            J("""{"@odata.type":"#microsoft.graph.windowsUpdateForBusinessConfiguration","qualityUpdatesDeferralPeriodInDays":0,"featureUpdatesDeferralPeriodInDays":0,"automaticUpdateMode":"autoInstallAtMaintenanceTime","microsoftUpdateServiceAllowed":true,"driversExcluded":false,"deadlineForFeatureUpdatesInDays":7,"deadlineForQualityUpdatesInDays":3,"deadlineGracePeriodInDays":2,"postponeRebootUntilAfterDeadline":false,"userPauseAccess":"enabled","userWindowsUpdateScanAccess":"enabled","updateNotificationLevel":"defaultNotifications","businessReadyUpdatesOnly":"userDefined","allowWindows11Upgrade":true}"""),
            [new("displayName", "Name"), new("qualityUpdatesDeferralPeriodInDays", "Wait for Monthly Updates (Days)", SettingKind.Number, Default: "0"),
             new("deadlineForQualityUpdatesInDays", "Deadline for Monthly Updates (Days)", SettingKind.Number, Default: "3")])]);

    public static readonly IntuneKind FeatureUpdates = new("featureupdates", "Feature Updates",
        "Which Windows version devices move to (and stay on), instead of the newest.",
        Beta + "deviceManagement/windowsFeatureUpdateProfiles?$expand=assignments",
        Labels: new Dictionary<string, PropertyLabel>
        {
            ["featureUpdateVersion"] = new("Windows Version", Kind: SettingKind.Choice, Choices:
                [("Windows 11, version 25H2", "Windows 11, Version 25H2"), ("Windows 11, version 24H2", "Windows 11, Version 24H2"), ("Windows 11, version 23H2", "Windows 11, Version 23H2"), ("Windows 10, version 22H2", "Windows 10, Version 22H2")]),
            ["installLatestWindows10OnWindows11IneligibleDevice"] = new("Keep Windows 10 Up to Date Where Windows 11 Can't Run"),
            ["deployableContentDisplayName"] = new("Content", Hidden: true),
            ["endOfSupportDate"] = new("End of Support", Hidden: true),
        },
        Creates: [new("new", "Feature Update Profile", "Moves devices to a Windows version.", J("""{"@odata.type":"#microsoft.graph.windowsFeatureUpdateProfile"}"""),
            [new("displayName", "Name"), new("featureUpdateVersion", "Windows Version", SettingKind.Choice,
                Choices: [("Windows 11, version 25H2", "Windows 11, Version 25H2"), ("Windows 11, version 24H2", "Windows 11, Version 24H2"), ("Windows 11, version 23H2", "Windows 11, Version 23H2"), ("Windows 10, version 22H2", "Windows 10, Version 22H2")],
                Default: "Windows 11, version 24H2")])]);

    public static readonly IntuneKind QualityUpdates = new("qualityupdates", "Expedited Quality Updates",
        "Rushes a security update out to devices that are behind, ahead of their update ring.",
        Beta + "deviceManagement/windowsQualityUpdateProfiles?$expand=assignments",
        Labels: new Dictionary<string, PropertyLabel>
        {
            ["expeditedUpdateSettings.qualityUpdateRelease"] = new("Bring Devices Up to the Release Of", "A release date, like 2026-11-11T00:00:00Z."),
            ["expeditedUpdateSettings.daysUntilForcedReboot"] = new("Restart Within", Min: 0, Max: 2, Unit: "days"),
            ["deployableContentDisplayName"] = new("Update", Hidden: true),
            ["releaseDateDisplayName"] = new("Release", Hidden: true),
        },
        Creates: [new("new", "Expedited Update Profile", "Brings devices up to a monthly update's release.",
            J("""{"@odata.type":"#microsoft.graph.windowsQualityUpdateProfile","expeditedUpdateSettings":{"daysUntilForcedReboot":1}}"""),
            [new("displayName", "Name"), new("expeditedUpdateSettings.qualityUpdateRelease", "Release (Date)", Placeholder: "2026-11-11T00:00:00Z")])]);

    public static readonly IntuneKind DriverUpdates = new("driverupdates", "Driver Updates",
        "Which driver and firmware updates devices get: approved by hand, or automatically after a wait.",
        Beta + "deviceManagement/windowsDriverUpdateProfiles?$expand=assignments",
        Labels: new Dictionary<string, PropertyLabel>
        {
            ["approvalType"] = new("Approve Updates", Kind: SettingKind.Choice, Choices: [("manual", "By Hand"), ("automatic", "Automatically")]),
            ["deploymentDeferralInDays"] = new("Wait Before Installing", Min: 0, Max: 30, Unit: "days"),
            ["newUpdates"] = new("New Updates", Hidden: true),
            ["deviceReporting"] = new("Devices Reporting", Hidden: true),
        },
        Creates: [new("new", "Driver Update Profile", "Manages driver and firmware updates.", J("""{"@odata.type":"#microsoft.graph.windowsDriverUpdateProfile","approvalType":"automatic"}"""),
            [new("displayName", "Name"), new("deploymentDeferralInDays", "Wait Before Installing (Days)", SettingKind.Number, Default: "7")])]);

    // ---- Scripts (13d) ------------------------------------------------------------------------

    public static readonly IntuneKind Scripts = new("scripts", "Windows Scripts",
        "PowerShell scripts Intune runs once on Windows devices (as the system or the person signed in).",
        Beta + "deviceManagement/deviceManagementScripts?$expand=assignments", AssignProperty: "deviceManagementScriptAssignments",
        Creates: [new("new", "PowerShell Script", "Runs once on each device it reaches.",
            J("""{"@odata.type":"#microsoft.graph.deviceManagementScript","runAsAccount":"system","enforceSignatureCheck":false,"runAs32Bit":false,"fileName":"script.ps1"}"""),
            [new("displayName", "Name"), new("scriptContent", "Script", SettingKind.Script), new("runAsAccount", "Run As", SettingKind.Choice, Choices: RunAs, Default: "system")])]);

    public static readonly IntuneKind Remediations = new("remediations", "Remediations",
        "A detection script and a fix, run on a schedule: when detection finds the problem, the remediation fixes it.",
        Beta + "deviceManagement/deviceHealthScripts?$expand=assignments", AssignProperty: "deviceHealthScriptAssignments",
        AssignItem: J("""{"runRemediationScript":true,"runSchedule":{"@odata.type":"#microsoft.graph.deviceHealthScriptDailySchedule","interval":1,"useUtc":false,"time":"09:00:00"}}"""),
        ReadOnlyWhen: r => r.TryGetProperty("deviceHealthScriptType", out var t) && t.GetString() == "managedInstallerScript",
        Creates: [new("new", "Remediation", "Runs daily at 9:00 on each device it reaches.",
            J("""{"@odata.type":"#microsoft.graph.deviceHealthScript","runAsAccount":"system","enforceSignatureCheck":false,"runAs32Bit":false}"""),
            [new("displayName", "Name"), new("publisher", "Publisher"), new("detectionScriptContent", "Detection Script", SettingKind.Script),
             new("remediationScriptContent", "Remediation Script", SettingKind.Script, Required: false)])]);

    public static readonly IntuneKind ShellScripts = new("shellscripts", "macOS Scripts",
        "Shell scripts Intune runs on Macs.",
        Beta + "deviceManagement/deviceShellScripts?$expand=assignments", AssignProperty: "deviceManagementScriptAssignments",
        Labels: new Dictionary<string, PropertyLabel>
        {
            ["executionFrequency"] = new("Run Every", "A duration, like PT1H; P0D runs once."),
            ["retryCount"] = new("Retries", Min: 0, Max: 3),
            ["blockExecutionNotifications"] = new("Hide Notifications"),
        },
        Creates: [new("new", "Shell Script", "Runs on each Mac it reaches.",
            J("""{"@odata.type":"#microsoft.graph.deviceShellScript","runAsAccount":"system","fileName":"script.sh","executionFrequency":"P0D","retryCount":0,"blockExecutionNotifications":true}"""),
            [new("displayName", "Name"), new("scriptContent", "Script", SettingKind.Script)])]);

    // ---- Enrollment (13e) ---------------------------------------------------------------------

    public static readonly IntuneKind Enrollment = new("enrollment", "Enrollment Settings",
        "How devices join: how many a person may enroll, which platforms, Windows Hello for Business, and the setup status page. The defaults (last) apply to everyone else.",
        "deviceManagement/deviceEnrollmentConfigurations?$expand=assignments", AssignProperty: "enrollmentConfigurationAssignments",
        // The defaults (priority 0) apply to everyone not reached by another: their settings change, but they aren't assigned or deleted.
        DefaultWhen: r => r.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() == 0,
        Labels: new Dictionary<string, PropertyLabel>
        {
            ["limit"] = new("Devices per Person", Min: 1, Max: 15),
            ["state"] = new("Windows Hello for Business", Kind: SettingKind.Choice, Choices: OnOffNot),
            ["pinMinimumLength"] = new("Minimum PIN Length", Min: 4, Max: 127),
            ["pinMaximumLength"] = new("Maximum PIN Length", Min: 4, Max: 127),
            ["pinUppercaseCharactersUsage"] = new("Capital Letters in the PIN", Kind: SettingKind.Choice, Choices: Allowed),
            ["pinLowercaseCharactersUsage"] = new("Small Letters in the PIN", Kind: SettingKind.Choice, Choices: Allowed),
            ["pinSpecialCharactersUsage"] = new("Symbols in the PIN", Kind: SettingKind.Choice, Choices: Allowed),
            ["securityDeviceRequired"] = new("Require a TPM"),
            ["unlockWithBiometricsEnabled"] = new("Allow Face and Fingerprint"),
            ["remotePassportEnabled"] = new("Use a Phone as a Companion Device"),
            ["pinPreviousBlockCount"] = new("PINs Remembered", Min: 0, Max: 50),
            ["pinExpirationInDays"] = new("PIN Expires After", Min: 0, Max: 730, Unit: "days"),
            ["enhancedBiometricsState"] = new("Enhanced Anti-Spoofing", Kind: SettingKind.Choice, Choices: OnOffNot),
            ["showInstallationProgress"] = new("Show Setup Progress"),
            ["blockDeviceSetupRetryByUser"] = new("Block Retrying Setup"),
            ["allowDeviceResetOnInstallFailure"] = new("Allow Reset When Setup Fails"),
            ["allowLogCollectionOnInstallFailure"] = new("Collect Logs When Setup Fails"),
            ["customErrorMessage"] = new("Message When Setup Fails"),
            ["installProgressTimeoutInMinutes"] = new("Give Up After", Min: 1, Max: 1440, Unit: "minutes"),
            ["allowDeviceUseOnInstallFailure"] = new("Let People Use the Device When Setup Fails"),
            ["trackInstallProgressForAutopilotOnly"] = new("Only for Autopilot Devices"),
            ["disableUserStatusTrackingAfterFirstUser"] = new("Only for the First Person"),
        },
        Creates:
        [
            new("limit", "Device Limit", "How many devices the people it reaches may enroll.", J("""{"@odata.type":"#microsoft.graph.deviceEnrollmentLimitConfiguration"}"""),
                [new("displayName", "Name"), new("limit", "Devices per Person", SettingKind.Number, Default: "5")]),
            new("esp", "Enrollment Status Page", "Shows setup progress while apps and policies install on a new Windows PC.",
                J("""{"@odata.type":"#microsoft.graph.windows10EnrollmentCompletionPageConfiguration","showInstallationProgress":true,"blockDeviceSetupRetryByUser":false,"allowDeviceResetOnInstallFailure":true,"allowLogCollectionOnInstallFailure":true,"allowDeviceUseOnInstallFailure":true,"trackInstallProgressForAutopilotOnly":false}"""),
                [new("displayName", "Name"), new("installProgressTimeoutInMinutes", "Give Up After (Minutes)", SettingKind.Number, Default: "60")]),
        ]);

    public static readonly IntuneKind AutopilotProfiles = new("autopilotprofiles", "Autopilot Profiles",
        "How a new Windows PC sets itself up for the organization: its name, what setup screens are skipped, and whether the person is an admin.",
        Beta + "deviceManagement/windowsAutopilotDeploymentProfiles?$expand=assignments", Assign: AssignMode.ReadOnly,
        Labels: new Dictionary<string, PropertyLabel>
        {
            ["deviceNameTemplate"] = new("Device Name", "Like NG-%SERIAL% or NG-%RAND:5%."),
            ["language"] = new("Language", "Like en-US, or os-default."),
            ["extractHardwareHash"] = new("Register Existing Devices"),
            ["deviceType"] = new("Device Type", Hidden: true),
            ["outOfBoxExperienceSettings.hidePrivacySettings"] = new("Skip Privacy Settings"),
            ["outOfBoxExperienceSettings.hideEULA"] = new("Skip the License Terms"),
            ["outOfBoxExperienceSettings.userType"] = new("People Are", Kind: SettingKind.Choice, Choices: [("standard", "Standard Users"), ("administrator", "Administrators")]),
            ["outOfBoxExperienceSettings.deviceUsageType"] = new("Used By", Kind: SettingKind.Choice, Choices: [("singleUser", "One Person"), ("shared", "Several People")]),
            ["outOfBoxExperienceSettings.skipKeyboardSelectionPage"] = new("Skip Keyboard Selection"),
            ["outOfBoxExperienceSettings.hideEscapeLink"] = new("Hide Change Account"),
        },
        Creates: [new("new", "Autopilot Profile (Entra Joined)", "Joins the PC to Entra and sets it up for one person.",
            J("""{"@odata.type":"#microsoft.graph.azureADWindowsAutopilotDeploymentProfile","description":"","deviceType":"windowsPc","enableWhiteGlove":false,"roleScopeTagIds":["0"],"language":"os-default","extractHardwareHash":false,"outOfBoxExperienceSettings":{"hidePrivacySettings":true,"hideEULA":true,"userType":"standard","deviceUsageType":"singleUser","skipKeyboardSelectionPage":true,"hideEscapeLink":true}}"""),
            [new("displayName", "Name"), new("deviceNameTemplate", "Device Name", Placeholder: "Like NG-%SERIAL%", Required: false)])]);

    public static readonly IntuneKind Categories = new("categories", "Device Categories",
        "Categories people choose when they enroll (Sales, Kiosk…), which dynamic groups can use.", "deviceManagement/deviceCategories", Assign: AssignMode.None,
        Creates: [new("new", "Device Category", "", J("""{}"""), [new("displayName", "Name"), new("description", "Description", Required: false)])]);

    public static readonly IntuneKind CleanupRules = new("cleanup", "Device Clean-Up Rules",
        "Hides devices from Intune after they haven't checked in for a while, so the lists show what's still in use.",
        Beta + "deviceManagement/managedDeviceCleanupRules", Assign: AssignMode.None,
        Labels: new Dictionary<string, PropertyLabel>
        {
            ["deviceCleanupRulePlatformType"] = new("Platform", Kind: SettingKind.Choice, Choices: [("all", "All"), ("windows", "Windows"), ("ios", "iOS/iPadOS"), ("macOS", "macOS"), ("androidAOSP", "Android (AOSP)"), ("androidDeviceAdministrator", "Android Device Administrator"), ("androidDedicatedAndFullyManagedCorporateOwnedWorkProfile", "Android Enterprise (Corporate)"), ("androidPersonallyOwnedWorkProfile", "Android Work Profile")]),
            ["deviceInactivityBeforeRetirementInDays"] = new("Hide After No Check-In For", Min: 30, Max: 270, Unit: "days"),
        },
        Creates: [new("new", "Clean-Up Rule", "", J("""{}"""), [new("displayName", "Name"),
            new("deviceCleanupRulePlatformType", "Platform", SettingKind.Choice, Choices: [("all", "All"), ("windows", "Windows"), ("ios", "iOS/iPadOS"), ("macOS", "macOS")], Default: "all"),
            new("deviceInactivityBeforeRetirementInDays", "Days Without Check-In", SettingKind.Number, Default: "90")])]);

    // ---- Tenant administration (13f) ----------------------------------------------------------

    public static readonly IntuneKind Filters = new("filters", "Assignment Filters",
        "Rules that narrow an assignment to some devices (company-owned, a model, an OS version), chosen when assigning.",
        Beta + "deviceManagement/assignmentFilters", Assign: AssignMode.None,
        Labels: new Dictionary<string, PropertyLabel>
        {
            ["rule"] = new("Rule", "Like (device.deviceOwnership -eq \"Corporate\")."),
            ["platform"] = new("Platform", Hidden: true),
            ["assignmentFilterManagementType"] = new("Applies To", Hidden: true),
            ["payloads"] = new("Used By", Hidden: true),
        },
        Creates: [new("new", "Assignment Filter", "", J("""{"assignmentFilterManagementType":"devices"}"""),
            [new("displayName", "Name"), new("platform", "Platform", SettingKind.Choice, Choices: [("windows10AndLater", "Windows"), ("iOS", "iOS/iPadOS"), ("macOS", "macOS"), ("androidForWork", "Android Enterprise"), ("android", "Android Device Administrator")], Default: "windows10AndLater"),
             new("rule", "Rule", Placeholder: "(device.deviceOwnership -eq \"Corporate\")")])]);

    public static readonly IntuneKind ScopeTags = new("scopetags", "Scope Tags",
        "Labels on policies and devices that limit which Intune admins see them.",
        Beta + "deviceManagement/roleScopeTags", Assign: AssignMode.None, ReadOnlyWhen: r => r.TryGetProperty("isBuiltIn", out var b) && b.ValueKind == JsonValueKind.True,
        Creates: [new("new", "Scope Tag", "", J("""{}"""), [new("displayName", "Name"), new("description", "Description", Required: false)])]);

    public static readonly IntuneKind Roles = new("intuneroles", "Intune Roles",
        "What Intune admins can do. Microsoft's roles are fixed; custom roles take any of Intune's permissions.",
        "deviceManagement/roleDefinitions", Assign: AssignMode.None, ReadOnlyWhen: r => r.TryGetProperty("isBuiltIn", out var b) && b.ValueKind == JsonValueKind.True,
        Labels: new Dictionary<string, PropertyLabel> { ["isBuiltIn"] = new("Microsoft's", Hidden: true), ["roleScopeTagIds"] = new("Scope Tags", Hidden: true) },
        Creates: [new("new", "Custom Intune Role", "Starts with reading devices and policies; add permissions after.",
            J("""{"@odata.type":"#microsoft.graph.deviceAndAppManagementRoleDefinition","isBuiltIn":false,"rolePermissions":[{"resourceActions":[{"allowedResourceActions":[]}]}]}"""),
            [new("displayName", "Name"), new("description", "Description", Required: false),
             new("rolePermissions.0.resourceActions.0.allowedResourceActions", "Permissions", SettingKind.List, Placeholder: "One per line, like Microsoft.Intune_ManagedDevices_Read",
                Default: "Microsoft.Intune_ManagedDevices_Read\nMicrosoft.Intune_DeviceConfigurations_Read")])]);

    public static readonly IntuneKind Terms = new("terms", "Terms and Conditions",
        "Terms people accept in Company Portal before enrolling.", "deviceManagement/termsAndConditions", Assign: AssignMode.ReadOnly, ExpandAssignments: false,
        Labels: new Dictionary<string, PropertyLabel>
        {
            ["title"] = new("Title"),
            ["bodyText"] = new("Terms", Kind: SettingKind.LongText),
            ["acceptanceStatement"] = new("Acceptance Statement"),
        },
        Creates: [new("new", "Terms and Conditions", "", J("""{"@odata.type":"#microsoft.graph.termsAndConditions","description":""}"""),
            [new("displayName", "Name"), new("title", "Title"), new("bodyText", "Terms", SettingKind.LongText), new("acceptanceStatement", "Acceptance Statement", Default: "I accept these terms.")])]);

    public static readonly IntuneKind Notifications = new("notifications", "Notification Messages",
        "The emails compliance policies send people whose devices don't comply.",
        "deviceManagement/notificationMessageTemplates?$expand=localizedNotificationMessages", Assign: AssignMode.None,
        Labels: new Dictionary<string, PropertyLabel>
        {
            ["defaultLocale"] = new("Default Language"),
            ["brandingOptions"] = new("Branding", "Any of includeCompanyLogo, includeCompanyName, includeContactInformation, includeCompanyPortalLink, separated by commas."),
        },
        Creates: [new("new", "Notification Message", "One message, in English.", J("""{"@odata.type":"#microsoft.graph.notificationMessageTemplate","defaultLocale":"en-us","brandingOptions":"includeCompanyLogo,includeCompanyName,includeContactInformation","roleScopeTagIds":["0"]}"""),
            [new("displayName", "Name"), new("Then.subject", "Subject"), new("Then.messageTemplate", "Message", SettingKind.LongText)],
            ("localizedNotificationMessages", J("""{"locale":"en-us","isDefault":true}""")))]);

    /// <summary>Every kind, by page key.</summary>
    public static IReadOnlyList<IntuneKind> All { get; } =
    [
        Apps, IosProtection, AndroidProtection, AppConfiguration, DeviceAppConfiguration, UpdateRings, FeatureUpdates, QualityUpdates, DriverUpdates,
        Scripts, Remediations, ShellScripts, Enrollment, AutopilotProfiles, Categories, CleanupRules, Filters, ScopeTags, Roles, Terms, Notifications,
    ];

    /// <summary>Puts a value into a new object's body at a path ("installExperience.runAsAccount", "rolePermissions.0.resourceActions.0.allowedResourceActions").</summary>
    public static void Set(JsonObject body, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        JsonNode node = body;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var next = int.TryParse(parts[i + 1], out _) ? (JsonNode)new JsonArray() : new JsonObject();
            if (node is JsonArray array && int.TryParse(parts[i], out var index))
            {
                while (array.Count <= index)
                    array.Add(next.DeepClone());
                node = array[index]!;
            }
            else if (node is JsonObject obj)
                node = obj[parts[i]] ??= next;
        }
        var last = parts[^1];
        if (node is JsonArray a && int.TryParse(last, out var at))
        {
            while (a.Count <= at)
                a.Add(null);
            a[at] = value;
        }
        else if (node is JsonObject o)
            o[last] = value;
    }
}

/// <summary>The generic Intune objects (13c-13f): read a kind's list, one in full, and an app's installs.</summary>
public interface IIntuneObjectRecords
{
    Task<IReadOnlyList<IntuneObject>> GetObjectsAsync(IntuneKind kind, CancellationToken cancellationToken = default);

    /// <summary>One object with every property (lists leave some out, like a script's content).</summary>
    Task<JsonElement> GetObjectAsync(IntuneKind kind, string id, CancellationToken cancellationToken = default);

    Task<AppInstallSummary?> GetInstallSummaryAsync(string appId, CancellationToken cancellationToken = default);
}
