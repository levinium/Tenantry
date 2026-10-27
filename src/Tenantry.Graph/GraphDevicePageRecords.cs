using System.Net;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// The device page's reads (6b). Intune's hardware details, signed-in users and discovered apps are
/// beta-only (v1.0 refuses <c>usersLoggedOn</c> and has no <c>detectedApps</c>, checked live);
/// a policy's settings are a read of their own (they don't expand).
/// </summary>
public sealed partial class GraphUserRecords : IDevicePageRecords
{
    const string Beta = "https://graph.microsoft.com/beta/";

    const string HardwareSelect = "id,hardwareInformation,physicalMemoryInBytes,processorArchitecture,usersLoggedOn,windowsActiveMalwareCount,"
                                  + "windowsRemediatedMalwareCount,joinType,autopilotEnrolled,enrollmentProfileName,deviceEnrollmentType,"
                                  + "ethernetMacAddress,wiFiMacAddress,isSupervised,jailBroken,managementCertificateExpirationDate,"
                                  + "complianceGracePeriodExpirationDateTime,partnerReportedThreatState";

    /// <summary>Intune refuses a device it doesn't manage (or an admin without an Intune role) with one of these.</summary>
    static bool IsIntuneRefusal(GraphException e) => e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.NotFound;

    const string NoIntune = "Intune details need an Intune role (and Intune in the tenant).";

    public async Task<DeviceLive> GetDeviceAsync(DeviceSummary device, CancellationToken cancellationToken = default)
    {
        Task<List<JsonElement>> People(string relation) => device.ObjectId is { } id
            ? Collect($"devices/{id}/{relation}?$select=id,displayName,userPrincipalName", cancellationToken)
            : Task.FromResult(new List<JsonElement>());
        var owners = People("registeredOwners");
        var users = People("registeredUsers");
        DeviceHardware? hardware = null;
        IReadOnlyList<LoggedOnUser> loggedOn = [];
        string? problem = null;
        if (device.ManagedId is { } managed)
        {
            try
            {
                var m = await graph.GetAsync($"{Beta}deviceManagement/managedDevices/{managed}?$select={HardwareSelect}", cancellationToken);
                hardware = Hardware(m);
                loggedOn = m.TryGetProperty("usersLoggedOn", out var on) && on.ValueKind == JsonValueKind.Array
                    ? on.EnumerateArray().Select(u => Text(u, "userId") is { } id ? new LoggedOnUser(id, When(u, "lastLogOnDateTime")) : null)
                        .OfType<LoggedOnUser>().OrderByDescending(u => u.LastLogOn ?? DateTimeOffset.MinValue).ToArray()
                    : [];
            }
            catch (GraphException e) when (IsIntuneRefusal(e))
            {
                problem = NoIntune;
            }
        }
        static DevicePerson[] Persons(IEnumerable<JsonElement> items) => items
            .Select(p => Text(p, "id") is { } id ? new DevicePerson(id, Text(p, "displayName") ?? Text(p, "userPrincipalName") ?? id, Text(p, "userPrincipalName")) : null)
            .OfType<DevicePerson>().ToArray();
        return new DeviceLive(hardware, loggedOn, Persons(await owners), Persons(await users), problem);
    }

    internal static DeviceHardware Hardware(JsonElement m)
    {
        var h = m.TryGetProperty("hardwareInformation", out var info) && info.ValueKind == JsonValueKind.Object ? info : default;
        JsonElement? hw = h.ValueKind == JsonValueKind.Object ? h : null;
        string? HwText(string name) => hw is { } x && Text(x, name) is { Length: > 0 } s ? s : null;
        long? HwLong(string name) => hw is { } x ? Long(x, name) : null;
        return new DeviceHardware
        {
            Serial = HwText("serialNumber"),
            Imei = HwText("imei"),
            Meid = HwText("meid"),
            PhoneNumber = HwText("phoneNumber"),
            Carrier = HwText("subscriberCarrier"),
            WifiMac = HwText("wifiMac") ?? NonEmpty(m, "wiFiMacAddress"),
            EthernetMac = NonEmpty(m, "ethernetMacAddress"),
            IpAddress = HwText("ipAddressV4"),
            MemoryBytes = Long(m, "physicalMemoryInBytes") is > 0 and var memory ? memory : null,
            StorageTotal = HwLong("totalStorageSpace") is > 0 and var total ? total : null,
            StorageFree = HwLong("freeStorageSpace"),
            Processor = NonEmpty(m, "processorArchitecture") is { } arch && arch != "unknown" ? arch : null,
            BatteryHealthPercent = (int?)HwLong("batteryHealthPercentage") is > 0 and var battery ? battery : null,
            BatteryCycles = (int?)HwLong("batteryChargeCycles") is > 0 and var cycles ? cycles : null,
            Tpm = HwText("tpmSpecificationVersion"),
            OsEdition = HwText("operatingSystemEdition"),
            OsBuild = HwText("osBuildNumber"),
            OsLanguage = HwText("operatingSystemLanguage"),
            Bios = HwText("systemManagementBIOSVersion"),
            Supervised = Flag(m, "isSupervised"),
            Jailbroken = NonEmpty(m, "jailBroken") switch { "True" => true, "False" => false, _ => null },
            SharedDevice = hw is { } s ? Flag(s, "isSharedDevice") : null,
            EnrollmentType = NonEmpty(m, "deviceEnrollmentType"),
            EnrollmentProfile = NonEmpty(m, "enrollmentProfileName"),
            Autopilot = Flag(m, "autopilotEnrolled"),
            CertificateExpires = Real(When(m, "managementCertificateExpirationDate")),
            GraceExpires = Real(When(m, "complianceGracePeriodExpirationDateTime")),
            ThreatState = NonEmpty(m, "partnerReportedThreatState") is { } threat && threat != "unknown" ? threat : null,
            ActiveMalware = (int?)Long(m, "windowsActiveMalwareCount"),
            RemediatedMalware = (int?)Long(m, "windowsRemediatedMalwareCount"),
        };
    }

    /// <summary>Intune writes "never" as 0001-01-01 or 9999-12-31.</summary>
    static DateTimeOffset? Real(DateTimeOffset? when) => when is { Year: > 1900 and < 9000 } ? when : null;

    static string? NonEmpty(JsonElement e, string name) => Text(e, name) is { Length: > 0 } s ? s : null;

    static bool? Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.ValueKind == JsonValueKind.True : null;

    static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    public Task<Available<IReadOnlyList<PolicyState>>> GetCompliancePoliciesAsync(string managedId, CancellationToken cancellationToken = default) =>
        PoliciesAsync($"deviceManagement/managedDevices/{managedId}/deviceCompliancePolicyStates", cancellationToken);

    public Task<Available<IReadOnlyList<PolicyState>>> GetConfigurationProfilesAsync(string managedId, CancellationToken cancellationToken = default) =>
        PoliciesAsync($"deviceManagement/managedDevices/{managedId}/deviceConfigurationStates", cancellationToken);

    async Task<Available<IReadOnlyList<PolicyState>>> PoliciesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var items = await Collect(path, cancellationToken);
            return new(items
                .Select(p => new PolicyState(Text(p, "id") ?? "", Text(p, "displayName") ?? "A policy", Text(p, "state") ?? "unknown",
                    (int)(Long(p, "settingCount") ?? 0)))
                .ToArray());
        }
        catch (GraphException e) when (IsIntuneRefusal(e))
        {
            return Available<IReadOnlyList<PolicyState>>.Because(NoIntune);
        }
    }

    public async Task<IReadOnlyList<PolicySetting>> GetPolicySettingsAsync(string managedId, string policyStateId, bool compliance, CancellationToken cancellationToken = default)
    {
        var kind = compliance ? "deviceCompliancePolicyStates" : "deviceConfigurationStates";
        var items = await Collect($"deviceManagement/managedDevices/{managedId}/{kind}/{policyStateId}/settingStates", cancellationToken);
        return items
            .Select(s => new PolicySetting(
                NonEmpty(s, "settingName") ?? NonEmpty(s, "setting") ?? "A setting",
                Text(s, "state") ?? "unknown",
                NonEmpty(s, "errorDescription"),
                NonEmpty(s, "currentValue")))
            .OrderBy(s => s.State is "nonCompliant" or "error" or "conflict" ? 0 : 1).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<Available<IReadOnlyList<DeviceAppState>>> GetAppStatesAsync(string userId, string managedId, CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await graph.GetAsync($"{Beta}users/{userId}/mobileAppIntentAndStates/{managedId}", cancellationToken);
            var apps = state.TryGetProperty("mobileAppList", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(a => new DeviceAppState(Text(a, "displayName") ?? "An app", NonEmpty(a, "displayVersion"),
                    Text(a, "mobileAppIntent") ?? "", Text(a, "installState") ?? "unknown")).ToArray()
                : [];
            return new(apps);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
        {
            // No apps assigned to them for this device.
            return new([]);
        }
        catch (GraphException e) when (IsIntuneRefusal(e))
        {
            return Available<IReadOnlyList<DeviceAppState>>.Because(NoIntune);
        }
    }

    public async Task<Available<IReadOnlyList<DetectedApp>>> GetDetectedAppsAsync(string managedId, CancellationToken cancellationToken = default)
    {
        try
        {
            var items = await Collect($"{Beta}deviceManagement/managedDevices/{managedId}/detectedApps", cancellationToken);
            return new(items
                .Select(a => new DetectedApp(Text(a, "displayName") ?? "An app", NonEmpty(a, "version"), NonEmpty(a, "publisher"), Long(a, "sizeInByte") is > 0 and var size ? size : null))
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray());
        }
        catch (GraphException e) when (IsIntuneRefusal(e))
        {
            return Available<IReadOnlyList<DetectedApp>>.Because(NoIntune);
        }
    }

    public async Task<Available<DeviceProtection>> GetProtectionAsync(string managedId, CancellationToken cancellationToken = default)
    {
        var path = $"deviceManagement/managedDevices/{managedId}/windowsProtectionState";
        JsonElement state;
        try
        {
            state = await graph.GetAsync(path, cancellationToken);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return Available<DeviceProtection>.Because("This device doesn't report Microsoft Defender's state (only Windows devices do).");
        }
        catch (GraphException e) when (IsIntuneRefusal(e))
        {
            return Available<DeviceProtection>.Because(NoIntune);
        }
        var malware = await Collect($"{path}/detectedMalwareState", cancellationToken);
        return new(new DeviceProtection(
            new ProtectionState(
                Flag(state, "malwareProtectionEnabled"), Flag(state, "realTimeProtectionEnabled"), Flag(state, "tamperProtectionEnabled"),
                Flag(state, "quickScanOverdue") == true, Flag(state, "fullScanOverdue") == true, Flag(state, "signatureUpdateOverdue") == true,
                Flag(state, "rebootRequired") == true, Flag(state, "fullScanRequired") == true,
                NonEmpty(state, "deviceState"), NonEmpty(state, "productStatus"), NonEmpty(state, "signatureVersion"),
                When(state, "lastQuickScanDateTime"), When(state, "lastFullScanDateTime"), When(state, "lastReportedDateTime")),
            malware.Select(x => new MalwareFound(Text(x, "displayName") ?? "Malware", NonEmpty(x, "severity"), NonEmpty(x, "state"), NonEmpty(x, "category"),
                    When(x, "lastStateChangeDateTime") ?? When(x, "initialDetectionDateTime")))
                .ToArray()));
    }

    public async Task<Available<IReadOnlyList<DeviceGroup>>> GetGroupsAsync(string objectId, CancellationToken cancellationToken = default)
    {
        var items = await Collect($"devices/{objectId}/memberOf/microsoft.graph.group?$select=id,displayName,groupTypes", cancellationToken);
        return new(items
            .Select(g => new DeviceGroup(Text(g, "id") ?? "", Text(g, "displayName") ?? "A group",
                g.TryGetProperty("groupTypes", out var t) && t.ValueKind == JsonValueKind.Array
                && t.EnumerateArray().Any(x => string.Equals(x.GetString(), "DynamicMembership", StringComparison.OrdinalIgnoreCase))))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray());
    }
}
