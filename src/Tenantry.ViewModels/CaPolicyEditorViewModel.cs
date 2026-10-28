using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>
/// The Conditional Access editor (8b-2), as a tab: a new policy (blank or from one of Microsoft's
/// templates; it starts in report-only and leaves the admin out) or an existing one. Every section
/// the Entra admin center has: who, target resources, conditions, grant and session controls, state.
/// The policy is Graph's JSON underneath; the editor rewrites only what its controls cover, so
/// anything it doesn't know stays as it was. Each valid edit goes to Pending changes at once
/// (replacing the last), and the policy reads back in words beside the controls.
/// </summary>
public sealed partial class CaPolicyEditorViewModel : TabViewModel, IDisposable
{
    readonly ChangePipeline _pipeline;
    readonly ICaEditingRecords? _records;
    readonly CaPolicy? _existing;
    readonly CaDirectory _directory;
    readonly IDirectoryIndex _index;
    readonly string? _meId;
    readonly Action<TabViewModel> _close;
    JsonObject _base;
    ChangeOp? _staged;
    bool _filling = true;

    public CaPolicyEditorViewModel(CaPolicy? existing, CaDirectory directory, ChangePipeline pipeline, ICaEditingRecords? records, IRoleRecords? roles,
        IDirectoryIndex index, string? me, Action<TabViewModel> close)
    {
        _existing = existing;
        _directory = directory;
        _pipeline = pipeline;
        _records = records;
        _index = index;
        _close = close;
        _meId = me is null ? null : index.AllUsers.FirstOrDefault(u => string.Equals(u.UserPrincipalName, me, StringComparison.OrdinalIgnoreCase))?.Id;
        _base = existing?.Node ?? Blank();

        void Changed() => Edited();
        IncludePeople = new ChipPicker("Add a person: type a name", () => GroupAccess.People(index), Changed);
        IncludeGroups = new ChipPicker("Add a group: type its name", Groups, Changed);
        IncludeRoles = new ChipPicker("Add an admin role: type its name", () => _roleOptions, Changed);
        ExcludePeople = new ChipPicker("Leave out a person: type a name", () => GroupAccess.People(index), Changed);
        ExcludeGroups = new ChipPicker("Leave out a group: type its name", Groups, Changed);
        ExcludeRoles = new ChipPicker("Leave out an admin role: type its name", () => _roleOptions, Changed);
        Search(out var search);
        IncludeApps = new AppPicker(search, Changed);
        ExcludeApps = new AppPicker(search, Changed);
        IncludeLocations = new ChipPicker("Add a named location", Locations, Changed);
        ExcludeLocations = new ChipPicker("Leave out a named location", Locations, Changed);
        Platforms = [.. PlatformNames.Select(p => new CheckOption(p.Value, p.Label, Changed))];
        ExcludePlatforms = [.. PlatformNames.Select(p => new CheckOption(p.Value, p.Label, Changed))];
        ClientApps = [.. ClientAppNames.Select(c => new CheckOption(c.Value, c.Label, Changed))];
        UserRisk = [.. RiskNames.Select(r => new CheckOption(r.Value, r.Label, Changed))];
        SignInRisk = [.. RiskNames.Select(r => new CheckOption(r.Value, r.Label, Changed))];
        Flows = [new CheckOption("deviceCodeFlow", "Device Code Sign-In", Changed), new CheckOption("authenticationTransfer", "Authentication Transfer", Changed)];
        Controls = [.. ControlNames.Select(c => new CheckOption(c.Value, c.Label, Changed))];
        Strengths = [new EditorChoice("", "None"), .. directory.Names.Strengths.Select(s => new EditorChoice(s.Key, s.Value)).OrderBy(c => c.Label)];
        Fill(_base, isNew: existing is null);
        _filling = false;
        Edited();
        _ = LoadAsync(roles);
        if (existing is null && records is not null)
            _ = LoadTemplatesAsync();
        _pipeline.Changed += OnPendingChanged;
    }

    static JsonObject Blank() => new()
    {
        ["displayName"] = "",
        ["state"] = "enabledForReportingButNotEnforced",
        ["conditions"] = new JsonObject
        {
            ["users"] = new JsonObject { ["includeUsers"] = new JsonArray("None") },
            ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray() },
            ["clientAppTypes"] = new JsonArray("all"),
        },
    };

    void Search(out Func<string, CancellationToken, Task<IReadOnlyList<(string AppId, string Name)>>> search)
    {
        var records = _records;
        search = (text, ct) => records?.SearchAppsAsync(text, ct) ?? Task.FromResult<IReadOnlyList<(string, string)>>([]);
    }

    IEnumerable<PickOption> Groups() =>
        _index.AllGroups.Where(g => g.Kind is GroupKind.Security or GroupKind.Microsoft365 or GroupKind.MailEnabledSecurity).Select(PickOption.Of);

    IEnumerable<PickOption> Locations() =>
        _directory.Locations.Select(l => new PickOption(l.Id, l.Name, $"{l.Kind}{(l.IsTrusted ? " · Trusted" : "")}", "location", null, l.Name.ToLowerInvariant()));

    IReadOnlyList<PickOption> _roleOptions = [];

    async Task LoadAsync(IRoleRecords? roles)
    {
        try
        {
            if (roles is not null)
                _roleOptions = [.. (await roles.GetRolesAsync()).Roles.Where(r => r.Role.IsBuiltIn)
                    .Select(r => new PickOption(r.Role.Id, r.Role.Name, r.Role.IsPrivileged ? "Privileged admin role" : "Admin role", "role", null, r.Role.Name.ToLowerInvariant()))];
            if (_records is not null)
            {
                var terms = await _records.GetTermsOfUseAsync();
                TermsNote = terms.Unavailable;
                var chosen = CaJson.Strings(_base, "grantControls/termsOfUse");
                foreach (var t in terms.Value ?? [])
                {
                    var option = new CheckOption(t.Id, t.Name, Edited);
                    option.Fill(chosen.Contains(t.Id));
                    Terms.Add(option);
                }
                OnPropertyChanged(nameof(HasTerms));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            TermsNote = $"Couldn't read terms of use: {Friendly.Message(e)}";
        }
    }

    // ---- Starting from a template ----------------------------------------------------------------

    public bool IsNew => _existing is null;

    /// <summary>The policy being edited, or null for a new one.</summary>
    public string? EditingId => _existing?.Id;

    public ObservableCollection<CaTemplate> Templates { get; } = [];

    [ObservableProperty]
    public partial bool ShowStarters { get; private set; }

    async Task LoadTemplatesAsync()
    {
        try
        {
            foreach (var t in await _records!.GetTemplatesAsync())
                Templates.Add(t);
            ShowStarters = Templates.Count > 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ShowStarters = false;
        }
    }

    /// <summary>Starts the new policy from a template: its conditions and controls, its name; report-only, leaving the admin out.</summary>
    [RelayCommand]
    void Start(CaTemplate template)
    {
        var details = JsonNode.Parse(template.Details)!.AsObject();
        var policy = Blank();
        foreach (var key in new[] { "conditions", "grantControls", "sessionControls" })
            policy[key] = details[key]?.DeepClone();
        policy["displayName"] = template.Name;
        _base = policy;
        _filling = true;
        Fill(policy, isNew: true);
        _filling = false;
        ShowStarters = false;
        Edited();
    }

    [RelayCommand]
    void HideStarters() => ShowStarters = false;

    // ---- Name and state ---------------------------------------------------------------------------

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(Title));
        Edited();
    }

    public override string Title => IsNew ? (Name.Trim().Length > 0 ? $"New: {Name.Trim()}" : "New Policy") : $"Edit: {_existing!.Name}";

    public IReadOnlyList<CaStateChoice> States => CaStateChoice.All;

    [ObservableProperty]
    public partial CaStateChoice? State { get; set; }

    partial void OnStateChanged(CaStateChoice? value) => Edited();

    // ---- Users ---------------------------------------------------------------------------------------

    public static IReadOnlyList<EditorChoice> WhoChoices { get; } =
    [
        new("none", "Nobody Yet"),
        new("all", "Everyone"),
        new("chosen", "Chosen People, Groups and Roles"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhoIsChosen))]
    public partial EditorChoice? Who { get; set; }

    public bool WhoIsChosen => Who?.Value == "chosen";

    partial void OnWhoChanged(EditorChoice? value) => Edited();

    public ChipPicker IncludePeople { get; }
    public ChipPicker IncludeGroups { get; }
    public ChipPicker IncludeRoles { get; }

    [ObservableProperty]
    public partial bool IncludeGuests { get; set; }

    partial void OnIncludeGuestsChanged(bool value) => Edited();

    public ChipPicker ExcludePeople { get; }
    public ChipPicker ExcludeGroups { get; }
    public ChipPicker ExcludeRoles { get; }

    [ObservableProperty]
    public partial bool ExcludeGuests { get; set; }

    partial void OnExcludeGuestsChanged(bool value) => Edited();

    /// <summary>Leave the signed-in admin out, so a policy can't lock them out (on for new policies).</summary>
    [ObservableProperty]
    public partial bool LeaveMeOut { get; set; }

    partial void OnLeaveMeOutChanged(bool value) => Edited();

    public bool CanLeaveMeOut => _meId is not null;

    // ---- Target resources ------------------------------------------------------------------------

    public static IReadOnlyList<EditorChoice> TargetChoices { get; } =
    [
        new("All", "Every Cloud App"),
        new("Office365", "Office 365"),
        new("MicrosoftAdminPortals", "Microsoft Admin Portals"),
        new("chosen", "Chosen Apps"),
        new("urn:user:registersecurityinfo", "Registering Security Info (a user action)"),
        new("urn:user:registerdevice", "Registering or Joining Devices (a user action)"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppsAreChosen), nameof(IsAppTarget))]
    public partial EditorChoice? Target { get; set; }

    public bool AppsAreChosen => Target?.Value == "chosen";
    public bool IsAppTarget => Target?.Value.StartsWith("urn:", StringComparison.Ordinal) != true;

    partial void OnTargetChanged(EditorChoice? value) => Edited();

    public AppPicker IncludeApps { get; }
    public AppPicker ExcludeApps { get; }

    // ---- Conditions ------------------------------------------------------------------------------------

    static readonly EditorChoice[] PlatformNames =
        [new("android", "Android"), new("iOS", "iOS"), new("windows", "Windows"), new("macOS", "macOS"), new("linux", "Linux"), new("windowsPhone", "Windows Phone")];

    static readonly EditorChoice[] ClientAppNames =
        [new("browser", "Browser"), new("mobileAppsAndDesktopClients", "Mobile and Desktop Apps"), new("exchangeActiveSync", "Exchange ActiveSync"), new("other", "Older (Legacy) Sign-In")];

    static readonly EditorChoice[] RiskNames = [new("high", "High"), new("medium", "Medium"), new("low", "Low")];

    public static IReadOnlyList<EditorChoice> PlatformModes { get; } = [new("none", "Any Device (Not Set)"), new("all", "Every Platform"), new("chosen", "Chosen Platforms")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlatformsAreChosen), nameof(PlatformsSet))]
    public partial EditorChoice? PlatformMode { get; set; }

    public bool PlatformsAreChosen => PlatformMode?.Value == "chosen";
    public bool PlatformsSet => PlatformMode?.Value != "none";

    partial void OnPlatformModeChanged(EditorChoice? value) => Edited();

    public IReadOnlyList<CheckOption> Platforms { get; }
    public IReadOnlyList<CheckOption> ExcludePlatforms { get; }

    public static IReadOnlyList<EditorChoice> LocationModes { get; } =
        [new("none", "Anywhere (Not Set)"), new("All", "Any Location"), new("AllTrusted", "Trusted Locations"), new("chosen", "Chosen Named Locations")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationsAreChosen), nameof(LocationsSet))]
    public partial EditorChoice? LocationMode { get; set; }

    public bool LocationsAreChosen => LocationMode?.Value == "chosen";
    public bool LocationsSet => LocationMode?.Value != "none";

    partial void OnLocationModeChanged(EditorChoice? value) => Edited();

    public ChipPicker IncludeLocations { get; }
    public ChipPicker ExcludeLocations { get; }

    [ObservableProperty]
    public partial bool ExcludeTrusted { get; set; }

    partial void OnExcludeTrustedChanged(bool value) => Edited();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClientAppsAreChosen))]
    public partial bool AnyClientApp { get; set; }

    public bool ClientAppsAreChosen => !AnyClientApp;

    partial void OnAnyClientAppChanged(bool value) => Edited();

    public IReadOnlyList<CheckOption> ClientApps { get; }
    public IReadOnlyList<CheckOption> UserRisk { get; }
    public IReadOnlyList<CheckOption> SignInRisk { get; }
    public IReadOnlyList<CheckOption> Flows { get; }

    public static IReadOnlyList<EditorChoice> FilterModes { get; } = [new("", "No Device Filter"), new("include", "Only Devices Matching"), new("exclude", "Except Devices Matching")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceFilter))]
    public partial EditorChoice? FilterMode { get; set; }

    public bool HasDeviceFilter => FilterMode?.Value is { Length: > 0 };

    partial void OnFilterModeChanged(EditorChoice? value) => Edited();

    [ObservableProperty]
    public partial string FilterRule { get; set; } = "";

    partial void OnFilterRuleChanged(string value) => Edited();

    // ---- Grant ------------------------------------------------------------------------------------------

    public static IReadOnlyList<EditorChoice> GrantModes { get; } =
    [
        new("none", "No Requirement (Session Controls Only)"),
        new("block", "Block Access"),
        new("grant", "Allow Access, Requiring…"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGrant))]
    public partial EditorChoice? GrantMode { get; set; }

    public bool IsGrant => GrantMode?.Value == "grant";

    partial void OnGrantModeChanged(EditorChoice? value) => Edited();

    static readonly EditorChoice[] ControlNames =
    [
        new("mfa", "Multifactor Authentication"),
        new("compliantDevice", "A Compliant Device (Intune)"),
        new("domainJoinedDevice", "A Hybrid-Joined Device"),
        new("approvedApplication", "An Approved Client App"),
        new("compliantApplication", "An App Protection Policy"),
        new("passwordChange", "A Password Change"),
    ];

    public IReadOnlyList<CheckOption> Controls { get; }

    public IReadOnlyList<EditorChoice> Strengths { get; }

    [ObservableProperty]
    public partial EditorChoice? Strength { get; set; }

    partial void OnStrengthChanged(EditorChoice? value) => Edited();

    public ObservableCollection<CheckOption> Terms { get; } = [];

    public bool HasTerms => Terms.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTermsNote))]
    public partial string? TermsNote { get; private set; }

    public bool HasTermsNote => TermsNote is not null;

    public static IReadOnlyList<EditorChoice> Operators { get; } = [new("OR", "Any One of Them"), new("AND", "All of Them")];

    [ObservableProperty]
    public partial EditorChoice? Operator { get; set; }

    partial void OnOperatorChanged(EditorChoice? value) => Edited();

    // ---- Session ----------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool AppEnforced { get; set; }

    partial void OnAppEnforcedChanged(bool value) => Edited();

    public static IReadOnlyList<EditorChoice> CloudAppModes { get; } =
        [new("", "Off"), new("monitorOnly", "Monitor Only"), new("blockDownloads", "Block Downloads"), new("mcasConfigured", "Use a Custom Policy")];

    [ObservableProperty]
    public partial EditorChoice? CloudApps { get; set; }

    partial void OnCloudAppsChanged(EditorChoice? value) => Edited();

    public static IReadOnlyList<EditorChoice> FrequencyModes { get; } = [new("", "Off"), new("timeBased", "Every…"), new("everyTime", "Every Time")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrequencyIsTimed))]
    public partial EditorChoice? Frequency { get; set; }

    public bool FrequencyIsTimed => Frequency?.Value == "timeBased";

    partial void OnFrequencyChanged(EditorChoice? value) => Edited();

    [ObservableProperty]
    public partial decimal? FrequencyValue { get; set; } = 1;

    partial void OnFrequencyValueChanged(decimal? value) => Edited();

    public static IReadOnlyList<EditorChoice> FrequencyUnits { get; } = [new("hours", "Hours"), new("days", "Days")];

    [ObservableProperty]
    public partial EditorChoice? FrequencyUnit { get; set; }

    partial void OnFrequencyUnitChanged(EditorChoice? value) => Edited();

    public static IReadOnlyList<EditorChoice> BrowserModes { get; } = [new("", "Not Set"), new("always", "Always Stays Signed In"), new("never", "Never Stays Signed In")];

    [ObservableProperty]
    public partial EditorChoice? Browser { get; set; }

    partial void OnBrowserChanged(EditorChoice? value) => Edited();

    public static IReadOnlyList<EditorChoice> CaeModes { get; } =
        [new("", "Microsoft's Default"), new("disabled", "Off"), new("strictEnforcement", "Strict Enforcement")];

    [ObservableProperty]
    public partial EditorChoice? Cae { get; set; }

    partial void OnCaeChanged(EditorChoice? value) => Edited();

    [ObservableProperty]
    public partial bool NoResilience { get; set; }

    partial void OnNoResilienceChanged(bool value) => Edited();

    // ---- Reading a policy into the controls ---------------------------------------------------------------

    PickOption Named(string id, IReadOnlyDictionary<string, string> names, string type) =>
        new(id, names.TryGetValue(id, out var n) ? n : _index.User(id)?.DisplayName ?? _index.Group(id)?.DisplayName ?? "Not in the directory", type, type, null, "");

    void Fill(JsonObject p, bool isNew)
    {
        static EditorChoice Pick(IReadOnlyList<EditorChoice> choices, string value) => choices.FirstOrDefault(c => c.Value == value) ?? choices[0];
        var names = _directory.Names;
        Name = p["displayName"]?.GetValue<string>() ?? "";
        State = CaStateChoice.All.FirstOrDefault(s => s.State == p["state"]?.GetValue<string>()) ?? CaStateChoice.All[1];

        var include = CaJson.Strings(p, "conditions/users/includeUsers");
        Who = Pick(WhoChoices, include.Contains("All") ? "all" : include.Count == 0 || include.SequenceEqual(["None"])
            ? (CaJson.Strings(p, "conditions/users/includeGroups").Count + CaJson.Strings(p, "conditions/users/includeRoles").Count > 0 ? "chosen" : "none")
            : "chosen");
        IncludePeople.Fill(include.Where(i => Guid.TryParse(i, out _)).Select(i => Named(i, names.People, "user")));
        IncludeGuests = include.Contains("GuestsOrExternalUsers") || p["conditions"]?["users"]?["includeGuestsOrExternalUsers"] is JsonObject;
        IncludeGroups.Fill(CaJson.Strings(p, "conditions/users/includeGroups").Select(g => Named(g, names.Groups, "group")));
        IncludeRoles.Fill(CaJson.Strings(p, "conditions/users/includeRoles").Select(r => Named(r, names.Roles, "role")));
        var exclude = CaJson.Strings(p, "conditions/users/excludeUsers");
        LeaveMeOut = _meId is not null && (isNew || exclude.Contains(_meId, StringComparer.OrdinalIgnoreCase));
        ExcludePeople.Fill(exclude.Where(i => Guid.TryParse(i, out _) && !string.Equals(i, _meId, StringComparison.OrdinalIgnoreCase)).Select(i => Named(i, names.People, "user")));
        ExcludeGuests = exclude.Contains("GuestsOrExternalUsers") || p["conditions"]?["users"]?["excludeGuestsOrExternalUsers"] is JsonObject;
        ExcludeGroups.Fill(CaJson.Strings(p, "conditions/users/excludeGroups").Select(g => Named(g, names.Groups, "group")));
        ExcludeRoles.Fill(CaJson.Strings(p, "conditions/users/excludeRoles").Select(r => Named(r, names.Roles, "role")));

        var apps = CaJson.Strings(p, "conditions/applications/includeApplications");
        var actions = CaJson.Strings(p, "conditions/applications/includeUserActions");
        Target = actions.Count > 0 ? Pick(TargetChoices, actions[0])
            : apps.Count == 1 && apps[0] is "All" or "Office365" or "MicrosoftAdminPortals" ? Pick(TargetChoices, apps[0])
            : apps.Count == 0 ? TargetChoices[0] : Pick(TargetChoices, "chosen");
        IncludeApps.Fill(apps.Where(a => Guid.TryParse(a, out _)).Select(a => Named(a, names.Apps, "app")));
        ExcludeApps.Fill(CaJson.Strings(p, "conditions/applications/excludeApplications").Select(a => AppName(a, names)));

        var platforms = CaJson.Strings(p, "conditions/platforms/includePlatforms");
        PlatformMode = Pick(PlatformModes, platforms.Count == 0 ? "none" : platforms.Contains("all") ? "all" : "chosen");
        foreach (var o in Platforms)
            o.Fill(platforms.Contains(o.Value));
        var notPlatforms = CaJson.Strings(p, "conditions/platforms/excludePlatforms");
        foreach (var o in ExcludePlatforms)
            o.Fill(notPlatforms.Contains(o.Value));

        var places = CaJson.Strings(p, "conditions/locations/includeLocations");
        LocationMode = Pick(LocationModes, places.Count == 0 ? "none" : places[0] is "All" or "AllTrusted" && places.Count == 1 ? places[0] : "chosen");
        IncludeLocations.Fill(places.Where(l => l is not ("All" or "AllTrusted")).Select(l => Named(l, names.Locations, "location")));
        var notPlaces = CaJson.Strings(p, "conditions/locations/excludeLocations");
        ExcludeTrusted = notPlaces.Contains("AllTrusted");
        ExcludeLocations.Fill(notPlaces.Where(l => l != "AllTrusted").Select(l => Named(l, names.Locations, "location")));

        var clients = CaJson.Strings(p, "conditions/clientAppTypes");
        AnyClientApp = clients.Count == 0 || clients.Contains("all");
        foreach (var o in ClientApps)
            o.Fill(clients.Contains(o.Value));
        var userRisk = CaJson.Strings(p, "conditions/userRiskLevels");
        foreach (var o in UserRisk)
            o.Fill(userRisk.Contains(o.Value));
        var signInRisk = CaJson.Strings(p, "conditions/signInRiskLevels");
        foreach (var o in SignInRisk)
            o.Fill(signInRisk.Contains(o.Value));
        var flows = p["conditions"]?["authenticationFlows"]?["transferMethods"]?.GetValue<string>() ?? "";
        foreach (var o in Flows)
            o.Fill(flows.Contains(o.Value, StringComparison.Ordinal));
        var filter = p["conditions"]?["devices"]?["deviceFilter"];
        FilterMode = Pick(FilterModes, filter?["mode"]?.GetValue<string>() ?? "");
        FilterRule = filter?["rule"]?.GetValue<string>() ?? "";

        var controls = CaJson.Strings(p, "grantControls/builtInControls");
        var strength = p["grantControls"]?["authenticationStrength"]?["id"]?.GetValue<string>();
        GrantMode = Pick(GrantModes, controls.Contains("block") ? "block"
            : controls.Count > 0 || strength is not null || CaJson.Strings(p, "grantControls/termsOfUse").Count > 0 ? "grant"
            : p["grantControls"] is null && p["sessionControls"] is JsonObject ? "none" : "grant");
        foreach (var o in Controls)
            o.Fill(controls.Contains(o.Value));
        Strength = Strengths.FirstOrDefault(s => s.Value == strength) ?? Strengths[0];
        Operator = Pick(Operators, p["grantControls"]?["operator"]?.GetValue<string>() ?? "OR");
        foreach (var t in Terms)
            t.Fill(CaJson.Strings(p, "grantControls/termsOfUse").Contains(t.Value));

        var s = p["sessionControls"];
        AppEnforced = s?["applicationEnforcedRestrictions"]?["isEnabled"]?.GetValue<bool>() == true;
        CloudApps = Pick(CloudAppModes, s?["cloudAppSecurity"]?["isEnabled"]?.GetValue<bool>() == true ? s["cloudAppSecurity"]!["cloudAppSecurityType"]?.GetValue<string>() ?? "" : "");
        var f = s?["signInFrequency"];
        Frequency = Pick(FrequencyModes, f?["isEnabled"]?.GetValue<bool>() == true ? f["frequencyInterval"]?.GetValue<string>() ?? "timeBased" : "");
        FrequencyValue = f?["value"]?.GetValue<int>() ?? 1;
        FrequencyUnit = Pick(FrequencyUnits, f?["type"]?.GetValue<string>() ?? "hours");
        Browser = Pick(BrowserModes, s?["persistentBrowser"]?["isEnabled"]?.GetValue<bool>() == true ? s["persistentBrowser"]!["mode"]?.GetValue<string>() ?? "" : "");
        Cae = Pick(CaeModes, s?["continuousAccessEvaluation"]?["mode"]?.GetValue<string>() ?? "");
        NoResilience = s?["disableResilienceDefaults"]?.GetValue<bool>() == true;
    }

    PickOption AppName(string id, CaNames names) => id switch
    {
        "Office365" => new(id, "Office 365", "app", "app", null, ""),
        "MicrosoftAdminPortals" => new(id, "Microsoft Admin Portals", "app", "app", null, ""),
        _ => Named(id, names.Apps, "app"),
    };

    // ---- Writing the controls into the policy ------------------------------------------------------------

    /// <summary>The policy as the controls now describe it, on top of what it was (anything the editor doesn't cover is kept).</summary>
    public JsonObject Policy()
    {
        var p = _base.DeepClone().AsObject();
        p["displayName"] = Name.Trim();
        p["state"] = State?.State ?? "enabledForReportingButNotEnforced";

        var users = CaJson.Object(p, "conditions/users");
        var chosen = Who?.Value == "chosen";
        string[] included = Who?.Value switch
        {
            "all" => ["All"],
            "chosen" => [.. IncludePeople.Ids, .. IncludeGuests ? ["GuestsOrExternalUsers"] : Array.Empty<string>()],
            _ => ["None"],
        };
        CaJson.SetStrings(p, "conditions/users/includeUsers", included);
        if (chosen && CaJson.Strings(p, "conditions/users/includeUsers").Count == 0 && !IncludeGroups.HasItems && !IncludeRoles.HasItems)
            CaJson.SetStrings(p, "conditions/users/includeUsers", ["None"]);
        CaJson.SetStrings(p, "conditions/users/includeGroups", chosen ? IncludeGroups.Ids : []);
        CaJson.SetStrings(p, "conditions/users/includeRoles", chosen ? IncludeRoles.Ids : []);
        if (!chosen || !IncludeGuests)
            users.Remove("includeGuestsOrExternalUsers");
        CaJson.SetStrings(p, "conditions/users/excludeUsers",
            [.. ExcludePeople.Ids, .. LeaveMeOut && _meId is not null ? [_meId] : Array.Empty<string>(), .. ExcludeGuests ? ["GuestsOrExternalUsers"] : Array.Empty<string>()]);
        if (!ExcludeGuests)
            users.Remove("excludeGuestsOrExternalUsers");
        CaJson.SetStrings(p, "conditions/users/excludeGroups", ExcludeGroups.Ids);
        CaJson.SetStrings(p, "conditions/users/excludeRoles", ExcludeRoles.Ids);

        var target = Target?.Value ?? "All";
        var action = target.StartsWith("urn:", StringComparison.Ordinal);
        CaJson.SetStrings(p, "conditions/applications/includeApplications", action ? [] : target == "chosen" ? IncludeApps.Ids : [target]);
        CaJson.SetStrings(p, "conditions/applications/includeUserActions", action ? [target] : []);
        CaJson.SetStrings(p, "conditions/applications/excludeApplications", action ? [] : ExcludeApps.Ids);

        var conditions = CaJson.Object(p, "conditions");
        if (PlatformMode?.Value is "all" or "chosen")
        {
            CaJson.SetStrings(p, "conditions/platforms/includePlatforms", PlatformMode.Value == "all" ? ["all"] : Platforms.Where(o => o.IsChecked).Select(o => o.Value));
            CaJson.SetStrings(p, "conditions/platforms/excludePlatforms", ExcludePlatforms.Where(o => o.IsChecked).Select(o => o.Value));
        }
        else
            conditions["platforms"] = null;

        if (LocationMode?.Value is { } place && place != "none")
        {
            CaJson.SetStrings(p, "conditions/locations/includeLocations", place == "chosen" ? IncludeLocations.Ids : [place]);
            CaJson.SetStrings(p, "conditions/locations/excludeLocations", [.. ExcludeTrusted ? ["AllTrusted"] : Array.Empty<string>(), .. ExcludeLocations.Ids]);
        }
        else
            conditions["locations"] = null;

        CaJson.SetStrings(p, "conditions/clientAppTypes", AnyClientApp ? ["all"] : ClientApps.Where(o => o.IsChecked).Select(o => o.Value));
        CaJson.SetStrings(p, "conditions/userRiskLevels", UserRisk.Where(o => o.IsChecked).Select(o => o.Value));
        CaJson.SetStrings(p, "conditions/signInRiskLevels", SignInRisk.Where(o => o.IsChecked).Select(o => o.Value));
        conditions["authenticationFlows"] = Flows.Any(o => o.IsChecked)
            ? new JsonObject { ["transferMethods"] = string.Join(',', Flows.Where(o => o.IsChecked).Select(o => o.Value)) }
            : null;
        conditions["devices"] = HasDeviceFilter && FilterRule.Trim().Length > 0
            ? new JsonObject { ["deviceFilter"] = new JsonObject { ["mode"] = FilterMode!.Value, ["rule"] = FilterRule.Trim() } }
            : null;

        p["grantControls"] = GrantMode?.Value switch
        {
            "block" => new JsonObject { ["operator"] = "OR", ["builtInControls"] = new JsonArray("block") },
            "grant" => GrantControls(p["grantControls"] as JsonObject),
            _ => null,
        };

        var session = (p["sessionControls"] as JsonObject)?.DeepClone().AsObject() ?? [];
        session["applicationEnforcedRestrictions"] = AppEnforced ? new JsonObject { ["isEnabled"] = true } : null;
        session["cloudAppSecurity"] = CloudApps?.Value is { Length: > 0 } mcas ? new JsonObject { ["isEnabled"] = true, ["cloudAppSecurityType"] = mcas } : null;
        session["signInFrequency"] = Frequency?.Value switch
        {
            "timeBased" => new JsonObject
            {
                ["isEnabled"] = true, ["value"] = (int)Math.Max(1, FrequencyValue ?? 1), ["type"] = FrequencyUnit?.Value ?? "hours",
                ["frequencyInterval"] = "timeBased", ["authenticationType"] = "primaryAndSecondaryAuthentication",
            },
            "everyTime" => new JsonObject { ["isEnabled"] = true, ["frequencyInterval"] = "everyTime", ["authenticationType"] = "primaryAndSecondaryAuthentication" },
            _ => null,
        };
        session["persistentBrowser"] = Browser?.Value is { Length: > 0 } browser ? new JsonObject { ["isEnabled"] = true, ["mode"] = browser } : null;
        session["continuousAccessEvaluation"] = Cae?.Value is { Length: > 0 } cae ? new JsonObject { ["mode"] = cae } : null;
        session["disableResilienceDefaults"] = NoResilience ? true : null;
        p["sessionControls"] = session.Any(kv => kv.Value is not null) ? session : null;
        return p;
    }

    JsonObject GrantControls(JsonObject? before)
    {
        var grant = before?.DeepClone().AsObject() ?? [];
        grant["operator"] = Operator?.Value ?? "OR";
        grant["builtInControls"] = new JsonArray([.. Controls.Where(o => o.IsChecked).Select(o => (JsonNode)o.Value)]);
        grant["authenticationStrength"] = Strength?.Value is { Length: > 0 } id ? new JsonObject { ["id"] = id, ["displayName"] = Strength.Label } : null;
        grant["termsOfUse"] = new JsonArray([.. Terms.Where(o => o.IsChecked).Select(o => (JsonNode)o.Value)]);
        return grant;
    }

    // ---- What it says, what's wrong, and staging ------------------------------------------------------------

    public ObservableCollection<SummaryLine> Lines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; private set; }

    public bool HasStatus => Status is not null;

    static string Sentence(JsonObject policy, CaNames names) =>
        string.Join("; ", CaText.Lines(policy, names).Select(l => $"{l.Label}: {l.Text}"));

    void Edited()
    {
        if (_filling)
            return;
        var policy = Policy();
        var names = Names();
        Lines.Clear();
        foreach (var (label, text) in CaText.Lines(policy, names))
            Lines.Add(new SummaryLine(label, text));

        var problems = new List<string>();
        var name = Name.Trim();
        if (name.Length == 0)
            problems.Add("Give it a name.");
        else if (_directory.Policies.Any(p => p.Id != _existing?.Id && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"There's already a policy called \"{name}\".");
        if (Who?.Value == "chosen" && !IncludePeople.HasItems && !IncludeGroups.HasItems && !IncludeRoles.HasItems && !IncludeGuests)
            problems.Add("Choose the people, groups or roles it covers.");
        if (AppsAreChosen && !IncludeApps.HasItems)
            problems.Add("Choose the apps it covers.");
        if (!AnyClientApp && !ClientApps.Any(o => o.IsChecked))
            problems.Add("Choose the kinds of app it covers, or any app.");
        if (PlatformsAreChosen && !Platforms.Any(o => o.IsChecked))
            problems.Add("Choose the device platforms it covers.");
        if (LocationsAreChosen && !IncludeLocations.HasItems)
            problems.Add("Choose the named locations it covers.");
        problems.AddRange(CaJson.Problems(policy, _meId));
        Problems = [.. problems.Distinct()];
        Stage(policy, names);
    }

    CaNames Names()
    {
        var n = _directory.Names;
        Dictionary<string, string> With(IReadOnlyDictionary<string, string> known, IEnumerable<PickOption> chosen)
        {
            var d = new Dictionary<string, string>(known, StringComparer.OrdinalIgnoreCase);
            foreach (var c in chosen)
                d[c.Id] = c.Name;
            return d;
        }
        return n with
        {
            People = With(n.People, IncludePeople.Items.Concat(ExcludePeople.Items)),
            Groups = With(n.Groups, IncludeGroups.Items.Concat(ExcludeGroups.Items)),
            Roles = With(n.Roles, IncludeRoles.Items.Concat(ExcludeRoles.Items)),
            Apps = With(n.Apps, IncludeApps.Items.Concat(ExcludeApps.Items)),
        };
    }

    void Stage(JsonObject policy, CaNames names)
    {
        if (_staged is not null)
            _pipeline.Unstage(_staged);
        _staged = null;
        if (Problems.Count > 0)
        {
            Status = "Not in Pending changes until the problems above are fixed.";
            return;
        }
        var name = Name.Trim();
        var json = policy.ToJsonString();
        if (_existing is null)
            _staged = new CreateCaPolicy(new ChangeTarget("capolicy", name, name, null), json, Sentence(policy, names));
        else
        {
            var old = Sentence(_existing.Node, _directory.Names);
            var now = Sentence(policy, names);
            var turnsOn = _existing.State != "enabled" && policy["state"]?.GetValue<string>() == "enabled";
            var renamed = _existing.Name != name;
            if (old == now && !turnsOn && !renamed && _existing.State == policy["state"]?.GetValue<string>())
            {
                Status = "No changes yet.";
                return;
            }
            _staged = new UpdateCaPolicy(new ChangeTarget("capolicy", _existing.Id, _existing.Name, null), json, old,
                renamed ? $"Named \"{name}\"; {now}" : now, turnsOn);
        }
        _pipeline.Stage(_staged);
        Status = "In Pending changes; it changes when you apply them.";
    }

    /// <summary>Taken out of Pending changes elsewhere (Undo there): forget it, so the next edit stages afresh.</summary>
    void OnPendingChanged(object? sender, EventArgs e)
    {
        if (_staged is not null && !_pipeline.Pending.Contains(_staged))
        {
            _staged = null;
            Status = "Taken out of Pending changes; edit again to add it back.";
        }
    }

    [RelayCommand]
    void Done() => _close(this);

    public void Dispose() => _pipeline.Changed -= OnPendingChanged;
}
