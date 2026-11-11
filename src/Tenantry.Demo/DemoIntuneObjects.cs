using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's apps, app protection, updates, scripts, enrollment and tenant administration, changed in memory.</summary>
public sealed partial class DemoUserRecords : IIntuneObjectRecords
{
    sealed class DemoObject(JsonObject body, List<IntuneAssignment> assignments)
    {
        public JsonObject Body { get; } = body;
        public List<IntuneAssignment> Assignments { get; set; } = assignments;
        public string Id => (string)Body["id"]!;
    }

    Dictionary<string, List<DemoObject>>? _objects;

    static string B64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    async Task<Dictionary<string, List<DemoObject>>> ObjectsAsync(CancellationToken ct)
    {
        if (_objects is not null)
            return _objects;
        var groups = await directory.GetGroupsAsync(ct);
        var staff = groups.FirstOrDefault(g => g.Kind == GroupKind.Security) ?? groups.First();
        var pilot = groups.Where(g => g.Kind == GroupKind.Security).Skip(1).FirstOrDefault() ?? staff;
        IntuneAssignment G(GroupSummary g, string? intent = null) => new(AssignmentKind.Group, g.Id, g.DisplayName, Intent: intent);
        var n = 0;
        DemoObject O(string json, params IntuneAssignment[] assignments)
        {
            var body = JsonNode.Parse(json)!.AsObject();
            body["id"] ??= $"do-{++n}";
            body["lastModifiedDateTime"] ??= DateTimeOffset.UtcNow.AddDays(-n * 7).ToString("o");
            return new DemoObject(body, [.. assignments]);
        }
        _objects = new()
        {
            ["apps"] =
            [
                O("""{"@odata.type":"#microsoft.graph.officeSuiteApp","displayName":"Microsoft 365 Apps for Windows","publisher":"Microsoft","updateChannel":"monthlyEnterprise","officePlatformArchitecture":"x64","useSharedComputerActivation":false,"autoAcceptEula":true,"isFeatured":true}""",
                    IntuneAssignment.AllDevices() with { Intent = "required" }),
                O("""{"@odata.type":"#microsoft.graph.winGetApp","displayName":"Company Portal","publisher":"Microsoft Corporation","packageIdentifier":"9WZDNCRFJ3PZ","installExperience":{"runAsAccount":"user"},"isFeatured":false}""",
                    IntuneAssignment.AllUsers() with { Intent = "available" }),
                O("""{"@odata.type":"#microsoft.graph.win32LobApp","displayName":"Adobe Acrobat Reader","publisher":"Adobe","displayVersion":"25.001","installCommandLine":"setup.exe /sAll","uninstallCommandLine":"msiexec /x {AC76BA86} /qn","setupFilePath":"setup.exe","allowAvailableUninstall":true,"minimumFreeDiskSpaceInMB":500}""",
                    G(staff, "required")),
                O("""{"@odata.type":"#microsoft.graph.webApp","displayName":"Intranet","publisher":"Northgate","appUrl":"https://intranet.northgate.example","useManagedBrowser":true}""", IntuneAssignment.AllUsers() with { Intent = "available" }),
                O("""{"@odata.type":"#microsoft.graph.webApp","displayName":"zz-tenantry-test-app","publisher":"Test","appUrl":"https://test.northgate.example","useManagedBrowser":false}"""),
            ],
            ["mam-ios"] =
            [
                O("""{"@odata.type":"#microsoft.graph.iosManagedAppProtection","displayName":"iPhone Work Data","pinRequired":true,"minimumPinLength":6,"simplePinBlocked":true,"dataBackupBlocked":true,"saveAsBlocked":true,"allowedOutboundDataTransferDestinations":"managedApps","allowedInboundDataTransferSources":"allApps","allowedOutboundClipboardSharingLevel":"managedAppsWithPasteIn","periodOfflineBeforeWipeIsEnforced":"P90D","appDataEncryptionType":"whenDeviceLocked","apps":[{"mobileAppIdentifier":{"@odata.type":"#microsoft.graph.iosMobileAppIdentifier","bundleId":"com.microsoft.Office.Outlook"}},{"mobileAppIdentifier":{"@odata.type":"#microsoft.graph.iosMobileAppIdentifier","bundleId":"com.microsoft.skype.teams"}}]}""",
                    IntuneAssignment.AllUsers()),
            ],
            ["mam-android"] =
            [
                O("""{"@odata.type":"#microsoft.graph.androidManagedAppProtection","displayName":"Android Work Data","pinRequired":true,"minimumPinLength":6,"encryptAppData":true,"screenCaptureBlocked":true,"allowedOutboundDataTransferDestinations":"managedApps","apps":[{"mobileAppIdentifier":{"@odata.type":"#microsoft.graph.androidMobileAppIdentifier","packageId":"com.microsoft.office.outlook"}}]}""", IntuneAssignment.AllUsers()),
            ],
            ["appconfig"] = [O("""{"@odata.type":"#microsoft.graph.targetedManagedAppConfiguration","displayName":"Outlook: Focused Inbox Off","customSettings":[{"name":"com.microsoft.outlook.Mail.FocusedInbox","value":"false"}]}""", G(staff))],
            ["mdmappconfig"] = [],
            ["rings"] =
            [
                O("""{"@odata.type":"#microsoft.graph.windowsUpdateForBusinessConfiguration","displayName":"Pilot Ring","qualityUpdatesDeferralPeriodInDays":0,"featureUpdatesDeferralPeriodInDays":0,"automaticUpdateMode":"autoInstallAtMaintenanceTime","deadlineForQualityUpdatesInDays":2,"deadlineForFeatureUpdatesInDays":5,"deadlineGracePeriodInDays":1,"driversExcluded":false,"microsoftUpdateServiceAllowed":true,"userPauseAccess":"disabled","qualityUpdatesPaused":false,"featureUpdatesPaused":false,"updateNotificationLevel":"defaultNotifications","allowWindows11Upgrade":true}""", G(pilot)),
                O("""{"@odata.type":"#microsoft.graph.windowsUpdateForBusinessConfiguration","displayName":"Broad Ring","qualityUpdatesDeferralPeriodInDays":7,"featureUpdatesDeferralPeriodInDays":30,"automaticUpdateMode":"autoInstallAtMaintenanceTime","deadlineForQualityUpdatesInDays":5,"deadlineForFeatureUpdatesInDays":10,"deadlineGracePeriodInDays":2,"driversExcluded":false,"microsoftUpdateServiceAllowed":true,"userPauseAccess":"enabled","qualityUpdatesPaused":false,"featureUpdatesPaused":false,"updateNotificationLevel":"defaultNotifications","allowWindows11Upgrade":true}""",
                    IntuneAssignment.AllDevices(), new IntuneAssignment(AssignmentKind.ExcludeGroup, pilot.Id, pilot.DisplayName)),
            ],
            ["featureupdates"] = [O("""{"@odata.type":"#microsoft.graph.windowsFeatureUpdateProfile","displayName":"Stay on Windows 11 24H2","featureUpdateVersion":"Windows 11, version 24H2","installLatestWindows10OnWindows11IneligibleDevice":true}""", IntuneAssignment.AllDevices())],
            ["qualityupdates"] = [],
            ["driverupdates"] = [O("""{"@odata.type":"#microsoft.graph.windowsDriverUpdateProfile","displayName":"Drivers: Automatic After a Week","approvalType":"automatic","deploymentDeferralInDays":7}""", IntuneAssignment.AllDevices())],
            ["scripts"] =
            [
                With(O("""{"@odata.type":"#microsoft.graph.deviceManagementScript","displayName":"Set the Time Zone","description":"Eastern time on every PC.","fileName":"timezone.ps1","runAsAccount":"system","enforceSignatureCheck":false,"runAs32Bit":false}""",
                    IntuneAssignment.AllDevices()), ("scriptContent", B64("Set-TimeZone -Id 'Eastern Standard Time'\n"))),
            ],
            ["remediations"] =
            [
                With(O("""{"@odata.type":"#microsoft.graph.deviceHealthScript","displayName":"Clear Old Temp Files","publisher":"Northgate IT","runAsAccount":"system","enforceSignatureCheck":false,"runAs32Bit":false,"deviceHealthScriptType":"deviceHealthScript"}""", G(staff)),
                    ("detectionScriptContent", B64("if ((Get-ChildItem $env:TEMP -Recurse | Measure-Object).Count -gt 1000) { exit 1 }\nexit 0\n")),
                    ("remediationScriptContent", B64("Get-ChildItem $env:TEMP -Recurse | Where-Object LastWriteTime -lt (Get-Date).AddDays(-30) | Remove-Item -Force -Recurse\n"))),
            ],
            ["shellscripts"] = [],
            ["enrollment"] =
            [
                O("""{"@odata.type":"#microsoft.graph.deviceEnrollmentLimitConfiguration","displayName":"Staff Device Limit","priority":1,"limit":3}""", G(staff)),
                O("""{"@odata.type":"#microsoft.graph.deviceEnrollmentLimitConfiguration","displayName":"All users and all devices","description":"This is the default Device Limit Restriction applied with the lowest priority to all users regardless of group membership.","priority":0,"limit":5}""", IntuneAssignment.AllUsers()),
                O("""{"@odata.type":"#microsoft.graph.deviceEnrollmentWindowsHelloForBusinessConfiguration","displayName":"All users and all devices","description":"This is the default Windows Hello for Business configuration applied with the lowest priority to all users regardless of group membership.","priority":0,"state":"enabled","pinMinimumLength":6,"pinMaximumLength":127,"pinUppercaseCharactersUsage":"allowed","pinLowercaseCharactersUsage":"allowed","pinSpecialCharactersUsage":"allowed","securityDeviceRequired":false,"unlockWithBiometricsEnabled":true,"remotePassportEnabled":true,"pinPreviousBlockCount":0,"pinExpirationInDays":0,"enhancedBiometricsState":"notConfigured"}""",
                    IntuneAssignment.AllUsers()),
                O("""{"@odata.type":"#microsoft.graph.windows10EnrollmentCompletionPageConfiguration","displayName":"All users and all devices","description":"This is the default enrollment status screen configuration applied with the lowest priority to all users and all devices regardless of group membership.","priority":0,"showInstallationProgress":false,"installProgressTimeoutInMinutes":60}""",
                    IntuneAssignment.AllUsers()),
            ],
            ["autopilotprofiles"] =
            [
                O("""{"@odata.type":"#microsoft.graph.azureADWindowsAutopilotDeploymentProfile","displayName":"Staff Laptops","deviceNameTemplate":"NG-%SERIAL%","language":"os-default","extractHardwareHash":true,"outOfBoxExperienceSettings":{"hidePrivacySettings":true,"hideEULA":true,"userType":"standard","deviceUsageType":"singleUser","skipKeyboardSelectionPage":true,"hideEscapeLink":true}}""",
                    G(staff)),
            ],
            ["categories"] = [O("""{"displayName":"Reception Kiosk","description":"Shared front-desk PCs."}"""),
                O("""{"displayName":"Staff Laptop","description":"Laptops for staff."}""")],
            ["cleanup"] = [O("""{"displayName":"Hide Windows PCs After 90 Days","deviceCleanupRulePlatformType":"windows","deviceInactivityBeforeRetirementInDays":90}""")],
            ["filters"] = [.. DemoFilters.Select(f => With(O("""{"assignmentFilterManagementType":"devices"}"""), ("id", f.Id), ("displayName", f.Name), ("platform", f.Platform), ("rule", f.Rule)))],
            ["scopetags"] = [O("""{"displayName":"Default","description":"Built-in scope tag","isBuiltIn":true}"""),
                O("""{"displayName":"Front Office","description":"Reception devices.","isBuiltIn":false}""")],
            ["intuneroles"] =
            [
                O("""{"@odata.type":"#microsoft.graph.deviceAndAppManagementRoleDefinition","displayName":"Help Desk Operator","description":"Performs remote tasks on users and devices.","isBuiltIn":true}"""),
                O("""{"@odata.type":"#microsoft.graph.deviceAndAppManagementRoleDefinition","displayName":"Policy and Profile Manager","description":"Manages compliance and configuration policies.","isBuiltIn":true}"""),
                O("""{"@odata.type":"#microsoft.graph.deviceAndAppManagementRoleDefinition","displayName":"Front Office Support","description":"Restarts and syncs front-desk devices.","isBuiltIn":false,"rolePermissions":[{"resourceActions":[{"allowedResourceActions":["Microsoft.Intune_ManagedDevices_Read","Microsoft.Intune_RemoteTasks_RebootNow","Microsoft.Intune_RemoteTasks_SyncDevice"]}]}]}"""),
            ],
            ["terms"] = [O("""{"displayName":"Device Use Policy","title":"Using a work device","bodyText":"Work devices are for work. Report a lost device to IT the same day.","acceptanceStatement":"I have read and accept the device use policy."}""", IntuneAssignment.AllUsers())],
            ["notifications"] =
            [
                O("""{"id":"t-1","displayName":"Your device needs attention","defaultLocale":"en-us","brandingOptions":"includeCompanyLogo,includeCompanyName","localizedNotificationMessages":[{"locale":"en-us","subject":"Your device needs attention","messageTemplate":"Your device doesn't meet the organization's requirements. Open Company Portal to see what to fix.","isDefault":true}]}"""),
                O("""{"id":"t-2","displayName":"Final notice: device will lose access","defaultLocale":"en-us","brandingOptions":"includeCompanyLogo,includeCompanyName,includeContactInformation","localizedNotificationMessages":[{"locale":"en-us","subject":"Final notice","messageTemplate":"Your device will lose access to work email tomorrow unless it's fixed.","isDefault":true}]}"""),
            ],
        };
        return _objects;
    }

    static DemoObject With(DemoObject o, params (string Key, string Value)[] values)
    {
        foreach (var (k, v) in values)
            o.Body[k] = v;
        return o;
    }

    static JsonElement Element(JsonObject o) => JsonDocument.Parse(o.ToJsonString()).RootElement.Clone();

    public async Task<IReadOnlyList<IntuneObject>> GetObjectsAsync(IntuneKind kind, CancellationToken cancellationToken = default) =>
        [.. ((await ObjectsAsync(cancellationToken)).GetValueOrDefault(kind.Key) ?? []).Select(o => new IntuneObject(o.Id, (string?)o.Body[kind.NameProperty] ?? (string?)o.Body["displayName"] ?? "An item",
            (string?)o.Body["description"] is { Length: > 0 } d ? d : null, (string?)o.Body["@odata.type"] ?? "", DateTimeOffset.UtcNow.AddDays(-20), [.. o.Assignments], Element(o.Body)))];

    public async Task<JsonElement> GetObjectAsync(IntuneKind kind, string id, CancellationToken cancellationToken = default) =>
        Element((await ObjectsAsync(cancellationToken))[kind.Key].Single(o => o.Id == id).Body);

    public Task<AppInstallSummary?> GetInstallSummaryAsync(string appId, CancellationToken cancellationToken = default) =>
        Task.FromResult<AppInstallSummary?>(new AppInstallSummary(140 + appId.Length * 3, appId.Length % 3, 4, 2, 0));

    /// <summary>A generic Intune change to the demo; null when it isn't one of these objects.</summary>
    ApplyResult? ApplyObject(IntuneChange change)
    {
        if (_objects is null)
            return null;
        if (change is CreateIntuneObject c)
        {
            var kind = IntuneKinds.All.FirstOrDefault(k => k.Collection == c.Collection);
            if (kind is null)
                return null;
            var body = c.Body.DeepClone().AsObject();
            body["id"] = $"do-{Guid.NewGuid():N}";
            if (c.ThenPath is { } then && c.ThenBody is { } thenBody)
                body[then] = new JsonArray(thenBody.DeepClone());
            _objects[kind.Key].Add(new DemoObject(body, []));
            return new ApplyResult(ChangeOutcome.Applied);
        }
        foreach (var (key, list) in _objects)
        {
            var kind = IntuneKinds.All.Single(k => k.Key == key);
            if (list.FirstOrDefault(o => kind.ItemUrl(o.Id) == change.Path) is not { } target)
                continue;
            switch (change)
            {
                case SetIntuneSetting s when s.Property.Split('.', 2) is [var outer, var inner]:
                    (target.Body[outer] ??= new JsonObject())[inner] = s.Value?.DeepClone();
                    break;
                case SetIntuneSetting s:
                    target.Body[s.Property] = s.Value?.DeepClone();
                    break;
                case SetIntuneAssignments a:
                    target.Assignments = [.. a.New];
                    break;
                case SetProtectedApps p:
                    target.Body["apps"] = JsonNode.Parse(GraphFreeTargetApps(p))!;
                    break;
                case DeleteIntunePolicy:
                    list.Remove(target);
                    break;
            }
            return new ApplyResult(ChangeOutcome.Applied);
        }
        return null;
    }

    static string GraphFreeTargetApps(SetProtectedApps p) => new JsonArray([.. p.New.Select(id => (JsonNode)new JsonObject
    {
        ["mobileAppIdentifier"] = p.Platform == "ios"
            ? new JsonObject { ["@odata.type"] = "#microsoft.graph.iosMobileAppIdentifier", ["bundleId"] = id }
            : new JsonObject { ["@odata.type"] = "#microsoft.graph.androidMobileAppIdentifier", ["packageId"] = id },
    })]).ToJsonString();
}
