using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's compliance policies: a Windows baseline for everyone, phones, a Mac policy, and a test one; statuses from the demo's devices.</summary>
public sealed partial class DemoUserRecords : IIntunePolicyRecords
{
    sealed class DemoCompliance(string id, string odataType, JsonObject settings, List<IntuneAssignment> assignments, List<ComplianceAction> actions)
    {
        public string Id { get; } = id;
        public string ODataType { get; } = odataType;
        public JsonObject Settings { get; } = settings;
        public List<IntuneAssignment> Assignments { get; set; } = assignments;
        public List<ComplianceAction> Actions { get; set; } = actions;
    }

    List<DemoCompliance>? _compliance;

    static readonly AssignmentFilter[] DemoFilters =
    [
        new("f-corp", "Company-Owned Windows", "windows10AndLater", "(device.deviceOwnership -eq \"Corporate\")"),
        new("f-kiosk", "Kiosks", "windows10AndLater", "(device.deviceName -startsWith \"KIOSK\")"),
    ];

    async Task<List<DemoCompliance>> ComplianceAsync(CancellationToken ct)
    {
        if (_compliance is not null)
            return _compliance;
        var groups = await directory.GetGroupsAsync(ct);
        var staff = groups.FirstOrDefault(g => g.Kind == GroupKind.Security) ?? groups.First();
        var phones = groups.Where(g => g.Kind == GroupKind.Microsoft365).Skip(1).FirstOrDefault() ?? staff;
        JsonObject O(string odataType, string name, string description, params (string, JsonNode?)[] values)
        {
            var o = new JsonObject { ["@odata.type"] = odataType, ["displayName"] = name, ["description"] = description, ["lastModifiedDateTime"] = DateTimeOffset.UtcNow.AddDays(-40).ToString("o") };
            foreach (var (k, v) in values)
                o[k] = v;
            return o;
        }
        _compliance =
        [
            new("cp-windows", CompliancePlatforms.Windows, O(CompliancePlatforms.Windows, "Windows Baseline", "BitLocker, Secure Boot, Defender and a password on every Windows PC.",
                ("bitLockerEnabled", true), ("secureBootEnabled", true), ("codeIntegrityEnabled", true), ("defenderEnabled", true), ("rtpEnabled", true), ("activeFirewallRequired", true),
                ("passwordRequired", true), ("passwordMinimumLength", 8), ("passwordRequiredType", "alphanumeric"), ("osMinimumVersion", "10.0.19045"),
                ("deviceThreatProtectionRequiredSecurityLevel", "medium")),
                [IntuneAssignment.AllDevices() with { FilterId = "f-corp", FilterName = "Company-Owned Windows", FilterMode = "include" },
                 new IntuneAssignment(AssignmentKind.ExcludeGroup, phones.Id, phones.DisplayName)],
                [new("block", 24), new("notification", 24, "t-1"), new("retire", 24 * 60)]),
            new("cp-ios", CompliancePlatforms.IOS, O(CompliancePlatforms.IOS, "iPhones and iPads", "",
                ("securityBlockJailbrokenDevices", true), ("passcodeRequired", true), ("passcodeMinimumLength", 6), ("osMinimumVersion", "17.0")),
                [new IntuneAssignment(AssignmentKind.Group, staff.Id, staff.DisplayName)], [new("block", 0)]),
            new("cp-android", CompliancePlatforms.AndroidWorkProfile, O(CompliancePlatforms.AndroidWorkProfile, "Android Work Profile", "Personal phones with a work profile.",
                ("securityBlockJailbrokenDevices", true), ("securityRequireSafetyNetAttestationBasicIntegrity", true), ("passwordRequired", true), ("minAndroidSecurityPatchLevel", "2026-01-01")),
                [new IntuneAssignment(AssignmentKind.Group, phones.Id, phones.DisplayName)], [new("block", 72), new("notification", 0, "t-1")]),
            new("cp-mac", CompliancePlatforms.MacOS, O(CompliancePlatforms.MacOS, "Macs", "", ("storageRequireEncryption", true), ("systemIntegrityProtectionEnabled", true),
                ("firewallEnabled", true), ("gatekeeperAllowedAppSource", "macAppStoreAndIdentifiedDevelopers")), [], [new("block", 24)]),
            new("cp-test", CompliancePlatforms.Windows, O(CompliancePlatforms.Windows, "zz-tenantry-test-compliance", "A test policy.", ("passwordRequired", false)), [], [new("block", 24)]),
        ];
        return _compliance;
    }

    public async Task<IReadOnlyList<CompliancePolicy>> GetCompliancePoliciesAsync(CancellationToken cancellationToken = default)
    {
        var devices = (await directory.GetDevicesAsync(cancellationToken)).Count(d => d.IsManaged);
        return [.. (await ComplianceAsync(cancellationToken)).Select((p, i) =>
        {
            var raw = JsonDocument.Parse(p.Settings.ToJsonString()).RootElement.Clone();
            var reached = p.Assignments.Count == 0 ? 0 : Math.Max(1, devices / (i + 2));
            var bad = reached == 0 ? 0 : reached / 6 + i % 2;
            return new CompliancePolicy(p.Id, p.Settings["displayName"]!.GetValue<string>(), p.Settings["description"]?.GetValue<string>() is { Length: > 0 } d ? d : null, p.ODataType,
                DateTimeOffset.UtcNow.AddDays(-40), raw, [.. p.Assignments], [.. p.Actions], new ComplianceCounts(reached - bad, bad, i == 0 ? 1 : 0, 0, 0, i));
        })];
    }

    public async Task<IReadOnlyList<DeviceComplianceStatus>> GetComplianceStatusesAsync(string policyId, CancellationToken cancellationToken = default)
    {
        var devices = (await directory.GetDevicesAsync(cancellationToken)).Where(d => d.IsManaged).Take(12).ToArray();
        string[] states = ["compliant", "compliant", "compliant", "nonCompliant", "compliant", "inGracePeriod", "compliant", "error"];
        return [.. devices.Select((d, i) => new DeviceComplianceStatus(d.Name, d.UserPrincipalName, states[i % states.Length], d.LastCheckIn))
            .OrderBy(s => s.Status == "compliant").ThenBy(s => s.DeviceName)];
    }

    JsonObject _complianceSettings = new() { ["secureByDefault"] = false, ["deviceComplianceCheckinThresholdDays"] = 30, ["isScheduledActionEnabled"] = true, ["enhancedJailBreak"] = false };

    public Task<JsonElement> GetComplianceSettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(JsonDocument.Parse(_complianceSettings.ToJsonString()).RootElement.Clone());

    public Task<IReadOnlyList<AssignmentFilter>> GetAssignmentFiltersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AssignmentFilter>>(DemoFilters);

    public Task<IReadOnlyList<NotificationTemplate>> GetNotificationTemplatesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NotificationTemplate>>([new("t-1", "Your device needs attention"), new("t-2", "Final notice: device will lose access")]);

    /// <summary>Applies an Intune change to the demo; null when it isn't one.</summary>
    public ApplyResult? ApplyIntune(ChangeOp change)
    {
        if (change is not IntuneChange i || _compliance is null)
            return null;
        var policy = _compliance.FirstOrDefault(p => i.Path.EndsWith("/" + p.Id, StringComparison.Ordinal));
        switch (change)
        {
            case SetIntuneSetting s when s.Path == "deviceManagement":
                _complianceSettings[s.Property.Split('.').Last()] = s.Value?.DeepClone();
                break;
            case SetIntuneSetting s when policy is not null:
                policy.Settings[s.Property] = s.Value?.DeepClone();
                break;
            case SetIntuneAssignments a when policy is not null:
                policy.Assignments = [.. a.New];
                break;
            case SetComplianceActions c when policy is not null:
                policy.Actions = [.. c.New];
                break;
            case CreateCompliancePolicy n:
                _compliance.Add(new DemoCompliance($"cp-{Guid.NewGuid():N}", n.ODataType,
                    new JsonObject { ["@odata.type"] = n.ODataType, ["displayName"] = n.Policy.Name, ["description"] = n.Description }, [.. n.Assignments], [new("block", n.GraceDays * 24)]));
                break;
            case DeleteIntunePolicy when policy is not null:
                _compliance.Remove(policy);
                break;
        }
        return new ApplyResult(ChangeOutcome.Applied);
    }
}
