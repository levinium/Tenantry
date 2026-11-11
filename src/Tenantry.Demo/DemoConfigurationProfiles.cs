using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's configuration profiles, over a small settings catalog of real-looking Windows settings.</summary>
public sealed partial class DemoUserRecords : IConfigurationProfileRecords
{
    static CatalogDefinition Choice(string id, string name, string description, params (string Id, string Name, string[] Children)[] options) =>
        new(id, CatalogKind.Choice, name, description, "cat", [.. options.Select(o => new CatalogOption(o.Id, o.Name, null, o.Children))], options[0].Id, "string", null, null, null, [], null);

    static CatalogDefinition Value(string id, string name, string description, bool integer = false, long? min = null, long? max = null, string? defaultValue = null) =>
        new(id, CatalogKind.Simple, name, description, "cat", [], null, integer ? "integer" : "string", min, max, defaultValue, [], null);

    /// <summary>A few settings, with an option that brings settings of its own, a list and a repeating group.</summary>
    static readonly CatalogDefinition[] DemoCatalog =
    [
        Choice("bitlocker_requiredeviceencryption", "Require Device Encryption", "Encrypt the device's drives with BitLocker.",
            ("bitlocker_requiredeviceencryption_0", "Disabled", []), ("bitlocker_requiredeviceencryption_1", "Enabled", [])),
        Choice("bitlocker_encryptionmethodbydrivetype", "Choose Drive Encryption Method and Cipher Strength", "Which algorithm BitLocker uses, per kind of drive.",
            ("bitlocker_encryptionmethodbydrivetype_0", "Disabled", []), ("bitlocker_encryptionmethodbydrivetype_1", "Enabled", ["bitlocker_encryptionmethodwithxtsosdropdown_name"])),
        Choice("bitlocker_encryptionmethodwithxtsosdropdown_name", "Select the Encryption Method for Operating System Drives", "",
            ("bitlocker_encryptionmethodwithxtsosdropdown_name_6", "XTS-AES 128-bit", []), ("bitlocker_encryptionmethodwithxtsosdropdown_name_7", "XTS-AES 256-bit", [])),
        Choice("defender_allowrealtimemonitoring", "Allow Realtime Monitoring", "Microsoft Defender Antivirus watches files as they're opened.",
            ("defender_allowrealtimemonitoring_0", "Not Allowed", []), ("defender_allowrealtimemonitoring_1", "Allowed", [])),
        Value("defender_daystoretaincleanedmalware", "Days to Retain Cleaned Malware", "How long quarantined items are kept.", true, 0, 90, "0"),
        new("onedrive_kfmoptinwithwizard", CatalogKind.Simple, "Prompt Users to Move Windows Known Folders to OneDrive", "Your tenant ID goes here.", "cat", [], null, "string", null, null, null, [], null),
        new("edge_homepagelocation_list", CatalogKind.SimpleCollection, "Allow Sites to Open Without a Prompt", "Sites, one per line.", "cat", [], null, "string", null, null, null, [], 100),
        new("printers_list", CatalogKind.GroupCollection, "Printers", "Printers added to each device.", "cat", [], null, "string", null, null, null, ["printers_list_name", "printers_list_shared"], 20),
        Value("printers_list_name", "Printer Address", "Like \\\\print01\\floor2."),
        Choice("printers_list_shared", "Set as Default", "", ("printers_list_shared_false", "No", []), ("printers_list_shared_true", "Yes", [])),
        Choice("update_allowautoupdate", "Allow Auto Update", "How Windows Update installs updates.",
            ("update_allowautoupdate_0", "Notify the User Before Downloading", []), ("update_allowautoupdate_2", "Auto Install and Restart", []), ("update_allowautoupdate_5", "Turn Off Automatic Updates", [])),
    ];

    static IReadOnlyDictionary<string, CatalogDefinition> CatalogIndex { get; } = DemoCatalog.ToDictionary(d => d.Id);

    sealed class DemoProfile(string id, JsonObject header, JsonArray settings, List<IntuneAssignment> assignments, string? family = null)
    {
        public string Id { get; } = id;
        public JsonObject Header { get; } = header;
        public JsonArray Settings { get; set; } = settings;
        public List<IntuneAssignment> Assignments { get; set; } = assignments;
        public string? Family { get; } = family;
    }

    List<DemoProfile>? _profiles;

    static JsonObject With(CatalogDefinition def, Action<JsonObject>? set = null)
    {
        var instance = SettingsCatalog.Default(def, CatalogIndex);
        set?.Invoke(instance);
        return instance;
    }

    async Task<List<DemoProfile>> ProfilesAsync(CancellationToken ct)
    {
        if (_profiles is not null)
            return _profiles;
        var groups = await directory.GetGroupsAsync(ct);
        var staff = groups.FirstOrDefault(g => g.Kind == GroupKind.Security) ?? groups.First();
        JsonObject H(string name, string description) => new() { ["name"] = name, ["description"] = description, ["platforms"] = "windows10", ["technologies"] = "mdm", ["roleScopeTagIds"] = new JsonArray("0") };
        var encryption = With(CatalogIndex["bitlocker_encryptionmethodbydrivetype"], i =>
        {
            i["choiceSettingValue"]!["value"] = "bitlocker_encryptionmethodbydrivetype_1";
            i["choiceSettingValue"]!["children"] = new JsonArray(With(CatalogIndex["bitlocker_encryptionmethodwithxtsosdropdown_name"],
                c => c["choiceSettingValue"]!["value"] = "bitlocker_encryptionmethodwithxtsosdropdown_name_7"));
        });
        var printers = With(CatalogIndex["printers_list"], i =>
        {
            var items = i["groupSettingCollectionValue"]!.AsArray();
            items[0]!["children"]![0]!["simpleSettingValue"]!["value"] = @"\\print01\reception";
            items.Add(SettingsCatalog.GroupItem(CatalogIndex["printers_list"], CatalogIndex));
            items[1]!["children"]![0]!["simpleSettingValue"]!["value"] = @"\\print01\floor2";
        });
        _profiles =
        [
            new("dp-bitlocker", H("BitLocker for Windows", "Encrypt every Windows PC with XTS-AES 256."),
                [With(CatalogIndex["bitlocker_requiredeviceencryption"], i => i["choiceSettingValue"]!["value"] = "bitlocker_requiredeviceencryption_1"), encryption],
                [IntuneAssignment.AllDevices()]),
            new("dp-defender", H("Defender Antivirus", ""), [With(CatalogIndex["defender_allowrealtimemonitoring"], i => i["choiceSettingValue"]!["value"] = "defender_allowrealtimemonitoring_1"),
                With(CatalogIndex["defender_daystoretaincleanedmalware"], i => i["simpleSettingValue"]!["value"] = 30)], [new IntuneAssignment(AssignmentKind.Group, staff.Id, staff.DisplayName)],
                "endpointSecurityAntivirus"),
            new("dp-printers", H("Office Printers", "Printers each PC gets."), [printers], [new IntuneAssignment(AssignmentKind.Group, staff.Id, staff.DisplayName)]),
            new("dp-test", H("zz-tenantry-test-profile", "A test profile."), [With(CatalogIndex["update_allowautoupdate"])], []),
        ];
        return _profiles;
    }

    static ConfigurationProfile ToProfile(DemoProfile p) => new(p.Id, p.Header["name"]!.GetValue<string>(), p.Header["description"]?.GetValue<string>() is { Length: > 0 } d ? d : null,
        p.Header["platforms"]!.GetValue<string>(), p.Header["technologies"]!.GetValue<string>(), p.Family ?? "none", p.Family is null ? null : SettingsCatalog.TemplateText(p.Family),
        p.Settings.Count, DateTimeOffset.UtcNow.AddDays(-12), [.. p.Assignments], JsonDocument.Parse(p.Header.ToJsonString()).RootElement.Clone());

    public async Task<IReadOnlyList<ConfigurationProfile>> GetConfigurationProfilesAsync(CancellationToken cancellationToken = default) =>
        [.. (await ProfilesAsync(cancellationToken)).Select(ToProfile)];

    public async Task<(JsonArray Settings, IReadOnlyDictionary<string, CatalogDefinition> Definitions)> GetProfileSettingsAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profile = (await ProfilesAsync(cancellationToken)).Single(p => p.Id == profileId);
        return ((JsonArray)profile.Settings.DeepClone(), CatalogIndex);
    }

    public Task<IReadOnlyList<CatalogDefinition>> SearchSettingsAsync(string platforms, string technologies, string words, CancellationToken cancellationToken = default)
    {
        var parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var children = DemoCatalog.SelectMany(SettingsCatalog.Needs).ToHashSet();
        return Task.FromResult<IReadOnlyList<CatalogDefinition>>([.. DemoCatalog.Where(d => !children.Contains(d.Id)
            && parts.All(w => d.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || (d.Description?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))]);
    }

    public Task<IReadOnlyList<CatalogDefinition>> GetDefinitionsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CatalogDefinition>>([.. ids.Select(CatalogIndex.GetValueOrDefault).OfType<CatalogDefinition>()]);

    public Task<IReadOnlyList<TemplateProfile>> GetTemplateProfilesAsync(CancellationToken cancellationToken = default)
    {
        var raw = JsonDocument.Parse("""
            {"@odata.type":"#microsoft.graph.windows10GeneralConfiguration","displayName":"Lock Screen (old template)","description":"",
             "lockScreenAllowTimeoutConfiguration":true,"lockScreenTimeoutInSeconds":300,"passwordRequired":true,"cameraBlocked":false,"edgeHomepageUrls":["https://intranet.northgate.example"]}
            """).RootElement.Clone();
        return Task.FromResult<IReadOnlyList<TemplateProfile>>([new TemplateProfile("dt-lock", "Lock Screen (old template)", null, "#microsoft.graph.windows10GeneralConfiguration",
            DateTimeOffset.UtcNow.AddDays(-300), [], raw)]);
    }

    /// <summary>A settings catalog change to the demo; null when it isn't one.</summary>
    ApplyResult? ApplyCatalog(IntuneChange change)
    {
        if (_profiles is null)
            return null;
        if (change is CreateConfigurationProfile c)
        {
            _profiles.Add(new DemoProfile($"dp-{Guid.NewGuid():N}", new JsonObject
            {
                ["name"] = c.Policy.Name, ["description"] = c.Description ?? "", ["platforms"] = c.Platforms, ["technologies"] = c.Technologies, ["roleScopeTagIds"] = new JsonArray("0"),
            }, (JsonArray)c.Settings.DeepClone(), []));
            return new ApplyResult(ChangeOutcome.Applied);
        }
        var profile = _profiles.FirstOrDefault(p => change.Path.EndsWith("/" + p.Id, StringComparison.Ordinal));
        if (profile is null)
            return null;
        switch (change)
        {
            case SetCatalogSettings s:
                profile.Settings = (JsonArray)s.New.DeepClone();
                break;
            case SetIntuneSetting s:
                profile.Header[s.Property] = s.Value?.DeepClone();
                break;
            case SetIntuneAssignments a:
                profile.Assignments = [.. a.New];
                break;
            case DeleteIntunePolicy:
                _profiles.Remove(profile);
                break;
        }
        return new ApplyResult(ChangeOutcome.Applied);
    }
}
