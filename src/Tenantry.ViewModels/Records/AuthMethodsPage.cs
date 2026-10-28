using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>A group in an editor's who-list, with ×.</summary>
public sealed partial class TargetChip(string id, string name, Action<TargetChip> remove)
{
    public string Id => id;
    public string Name => name;

    [RelayCommand]
    void Remove() => remove(this);
}

/// <summary>One setting's control: a switch, a number or a choice, written into the item as it changes.</summary>
public sealed partial class AuthSettingEditor : ObservableObject
{
    readonly AuthSetting _setting;
    readonly Action<JsonNode?> _write;
    bool _filling = true;

    public AuthSettingEditor(AuthSetting setting, JsonObject item, bool canEdit, Action<JsonNode?> write)
    {
        _setting = setting;
        _write = write;
        CanEdit = canEdit;
        Choices = [.. (setting.Options ?? []).Select(o => new EditorChoice(o.Value, o.Label, ""))];
        var value = setting.Read(item);
        switch (setting.Kind)
        {
            case AuthSettingKind.Bool:
                IsOn = value?.GetValue<bool>() == true;
                break;
            case AuthSettingKind.Number:
                Number = value is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
                break;
            default:
                Choice = Choices.FirstOrDefault(c => c.Value == value?.GetValue<string>()) ?? Choices.FirstOrDefault();
                break;
        }
        _filling = false;
    }

    public string Label => _setting.Label;
    public string Detail => _setting.Detail;
    public bool HasDetail => _setting.Detail.Length > 0;
    public bool CanEdit { get; }
    public bool IsBool => _setting.Kind == AuthSettingKind.Bool;
    public bool IsNumber => _setting.Kind == AuthSettingKind.Number;
    public bool IsChoice => _setting.Kind == AuthSettingKind.Choice;
    public decimal Min => _setting.Min;
    public decimal Max => _setting.Max;
    public string Unit => _setting.Unit;
    public IReadOnlyList<EditorChoice> Choices { get; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial decimal? Number { get; set; }

    [ObservableProperty]
    public partial EditorChoice? Choice { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (!_filling)
            _write(JsonValue.Create(value));
    }

    partial void OnNumberChanged(decimal? value)
    {
        if (!_filling && value is { } n)
            _write(JsonValue.Create((int)n));
    }

    partial void OnChoiceChanged(EditorChoice? value)
    {
        if (!_filling && value is not null)
            _write(JsonValue.Create(value.Value));
    }
}

/// <summary>
/// One sign-in method or policy section: on or off, who it covers (everyone or chosen groups, minus
/// excluded groups) and its settings. Every edit puts one change in Pending changes, replacing the
/// last; editing it back to how it was takes it out. Settings Entra would refuse are listed instead.
/// </summary>
public sealed partial class AuthItemEditor : ObservableObject
{
    readonly JsonObject _original;
    readonly JsonObject _item;
    readonly ChangePipeline? _pipeline;
    readonly IReadOnlyDictionary<string, string> _names;
    readonly IDirectoryIndex _index;
    bool _filling = true;

    public AuthItemEditor(string id, JsonObject original, IReadOnlyDictionary<string, string> names, ChangePipeline? pipeline, bool canEdit, IDirectoryIndex index)
    {
        Id = id;
        Info = AuthMethods.Info(id) ?? new AuthMethodInfo(id, id, "", []);
        _original = original;
        _names = names;
        _pipeline = pipeline;
        _index = index;
        CanEdit = canEdit && pipeline is not null;
        _item = original.DeepClone().AsObject();
        if (Waiting is { } waiting)
            Merge(_item, JsonNode.Parse(waiting.Json)!.AsObject());
        States = [.. AuthMethods.States(id).Select(s => new EditorChoice(s.Value, s.Label, ""))];
        State = States.FirstOrDefault(s => s.Value == _item["state"]?.GetValue<string>()) ?? States[0];
        Settings = [.. Info.Settings.Select(s => new AuthSettingEditor(s, _item, CanEdit, v => { s.Write(_item, v); Changed(); }))];
        IncludePicker = new PersonPicker("Add a group: type its name", () => Groups(IncludeGroups), o => AddTarget(o, include: true));
        ExcludePicker = new PersonPicker("Leave out a group: type its name", () => Groups(ExcludeGroups), o => AddTarget(o, include: false));
        ShowTargets();
        _filling = false;
        Problems = AuthMethods.Problems(id, _item);
    }

    public string Id { get; }
    public AuthMethodInfo Info { get; }
    public string Title => Info.Name;
    public string Description => Info.Description;
    public string? ManagedElsewhere => Info.ManagedElsewhere;
    public bool HasManagedElsewhere => Info.ManagedElsewhere is not null;
    public bool CanEdit { get; }
    public bool IsSection => AuthMethods.IsSection(Id);

    public ChangeTarget Target => new(IsSection ? "authpolicy" : "authmethod", Id, Info.Name, null);

    SetAuthMethodConfig? Waiting => _pipeline?.Pending.OfType<SetAuthMethodConfig>().FirstOrDefault(c => c.Method.Id == Id);

    public bool HasPending => Waiting is not null;

    public IReadOnlyList<EditorChoice> States { get; }

    [ObservableProperty]
    public partial EditorChoice State { get; set; }

    partial void OnStateChanged(EditorChoice value)
    {
        if (_filling)
            return;
        _item["state"] = value.Value;
        Changed();
    }

    public IReadOnlyList<AuthSettingEditor> Settings { get; }
    public bool HasSettings => Settings.Count > 0;

    // ---- Who ------------------------------------------------------------------------------------

    public bool HasTargets => Info.Targets != "none";
    public bool HasExclusions => Info.Targets == "list";

    [ObservableProperty]
    public partial bool Everyone { get; set; }

    partial void OnEveryoneChanged(bool value)
    {
        if (_filling)
            return;
        if (Info.Targets == "single")
            _item["includeTarget"] = new JsonObject { ["targetType"] = "group", ["id"] = value ? AuthMethods.Everyone : IncludeGroups.FirstOrDefault()?.Id ?? AuthMethods.Everyone };
        else
            _item["includeTargets"] = value ? new JsonArray(AuthMethods.TargetLike(_original, AuthMethods.Everyone)) : new JsonArray();
        ShowTargets();
        Changed();
    }

    public ObservableCollection<TargetChip> IncludeGroups { get; } = [];
    public ObservableCollection<TargetChip> ExcludeGroups { get; } = [];

    public PersonPicker IncludePicker { get; }
    public PersonPicker ExcludePicker { get; }

    IEnumerable<PickOption> Groups(IEnumerable<TargetChip> already)
    {
        var have = already.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _index.AllGroups.Where(g => g.Kind is GroupKind.Security or GroupKind.Microsoft365 && !have.Contains(g.Id)).Select(PickOption.Of);
    }

    void AddTarget(PickOption group, bool include)
    {
        if (!CanEdit)
            return;
        if (include)
        {
            if (Info.Targets == "single")
                _item["includeTarget"] = new JsonObject { ["targetType"] = "group", ["id"] = group.Id };
            else
            {
                var list = _item["includeTargets"] as JsonArray ?? [];
                // A group alongside everyone means nothing: choosing one switches to chosen groups.
                foreach (var all in list.OfType<JsonObject>().Where(t => t["id"]?.GetValue<string>() == AuthMethods.Everyone).ToArray())
                    list.Remove(all);
                list.Add(AuthMethods.TargetLike(_original, group.Id));
                _item["includeTargets"] = list;
            }
        }
        else
        {
            var list = _item["excludeTargets"] as JsonArray ?? [];
            list.Add(new JsonObject { ["targetType"] = "group", ["id"] = group.Id });
            _item["excludeTargets"] = list;
        }
        Names[group.Id] = group.Name;
        ShowTargets();
        Changed();
    }

    Dictionary<string, string> Names { get; } = [];

    string NameOf(string id) => Names.TryGetValue(id, out var n) ? n : _names.TryGetValue(id, out var m) ? m : _index.Group(id)?.DisplayName ?? "A group";

    void RemoveTarget(TargetChip chip, bool include)
    {
        if (!CanEdit)
            return;
        if (include && Info.Targets == "single")
            _item["includeTarget"] = new JsonObject { ["targetType"] = "group", ["id"] = AuthMethods.Everyone };
        else if (_item[include ? "includeTargets" : "excludeTargets"] is JsonArray list)
            foreach (var t in list.OfType<JsonObject>().Where(t => t["id"]?.GetValue<string>() == chip.Id).ToArray())
                list.Remove(t);
        ShowTargets();
        Changed();
    }

    void ShowTargets()
    {
        var include = Info.Targets == "single"
            ? _item["includeTarget"]?["id"]?.GetValue<string>() is { } one ? [one] : Array.Empty<string>()
            : (_item["includeTargets"] as JsonArray ?? []).Select(t => t?["id"]?.GetValue<string>()).OfType<string>().ToArray();
        var was = _filling;
        _filling = true;
        Everyone = include.Contains(AuthMethods.Everyone);
        _filling = was;
        IncludeGroups.Clear();
        foreach (var id in include.Where(i => i != AuthMethods.Everyone))
            IncludeGroups.Add(new TargetChip(id, NameOf(id), c => RemoveTarget(c, include: true)));
        ExcludeGroups.Clear();
        foreach (var id in (_item["excludeTargets"] as JsonArray ?? []).Select(t => t?["id"]?.GetValue<string>()).OfType<string>())
            ExcludeGroups.Add(new TargetChip(id, NameOf(id), c => RemoveTarget(c, include: false)));
        OnPropertyChanged(nameof(WhoText));
    }

    public string WhoText => AuthMethods.Who(_item, AllNames);

    IReadOnlyDictionary<string, string> AllNames => _names.Concat(Names).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

    // ---- Staging --------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    JsonObject Body(JsonObject item) => IsSection ? AuthMethods.SectionBody(Id, item) : AuthMethods.MethodBody(item);

    void Changed()
    {
        if (_filling || !CanEdit)
            return;
        Problems = AuthMethods.Problems(Id, _item);
        var waiting = Waiting;
        var body = Body(_item);
        if (Problems.Count > 0 || JsonNode.DeepEquals(body, Body(_original)))
        {
            if (waiting is not null)
                _pipeline!.Unstage(waiting);
        }
        else
        {
            var stateChanged = _item["state"]?.GetValue<string>() != _original["state"]?.GetValue<string>();
            _pipeline!.Stage(new SetAuthMethodConfig(Target, body.ToJsonString(), AuthMethods.Describe(Id, _original, AllNames), AuthMethods.Describe(Id, _item, AllNames),
                stateChanged || (Info.Weak && _item["state"]?.GetValue<string>() == "enabled" && WhoText != AuthMethods.Who(_original, AllNames))));
        }
        OnPropertyChanged(nameof(HasPending));
    }

    static void Merge(JsonObject into, JsonObject body)
    {
        var source = body["registrationEnforcement"]?["authenticationMethodsRegistrationCampaign"] as JsonObject
                     ?? (body.Count == 1 && body.First().Value is JsonObject only && !body.ContainsKey("state") ? only : body);
        foreach (var (k, v) in source)
            into[k] = v?.DeepClone();
    }
}

/// <summary>Security defaults: one switch, which Entra refuses while Conditional Access policies are on.</summary>
public sealed partial class SecurityDefaultsEditor : ObservableObject
{
    readonly bool _original;
    readonly ChangePipeline? _pipeline;
    bool _filling = true;

    public SecurityDefaultsEditor(bool on, ChangePipeline? pipeline, bool canEdit)
    {
        _original = on;
        _pipeline = pipeline;
        CanEdit = canEdit && pipeline is not null;
        IsOn = pipeline?.Pending.OfType<SetSecurityDefaults>().FirstOrDefault()?.New ?? on;
        _filling = false;
    }

    public bool CanEdit { get; }

    public string Title => "Security Defaults";

    public string Description =>
        "Microsoft's basic protections for organizations without Conditional Access: everyone registers for MFA, admins always use it, and old sign-in protocols are blocked.";

    public string Note => "Entra won't turn them on while any Conditional Access policy is on: use one or the other.";

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (_filling || !CanEdit)
            return;
        var waiting = _pipeline!.Pending.OfType<SetSecurityDefaults>().FirstOrDefault();
        if (value == _original)
        {
            if (waiting is not null)
                _pipeline.Unstage(waiting);
        }
        else
            _pipeline.Stage(new SetSecurityDefaults(_original, value));
        OnPropertyChanged(nameof(HasPending));
    }

    public bool HasPending => _pipeline?.Pending.OfType<SetSecurityDefaults>().Any() == true;
}

/// <summary>A row in the list on the left: a method or setting, its state and who it covers.</summary>
public sealed record AuthItemRow(string Id, string Name, string StateText, BadgeKind StateKind, string Who, bool Pending)
{
    public bool HasWho => Who.Length > 0;
}

/// <summary>
/// The Authentication Methods page (8d-1): every sign-in method and the policy's own settings in a
/// list, with the chosen one's editor beside it. Read live when first shown and after changes apply.
/// </summary>
public sealed partial class AuthMethodsViewModel : ObservableObject
{
    readonly IAuthMethodRecords? _records;
    readonly ChangePipeline? _pipeline;
    readonly IDirectoryIndex _index;
    AuthMethodsPolicy? _policy;
    bool _read;

    public AuthMethodsViewModel(IAuthMethodRecords? records, ChangePipeline? pipeline, bool canEdit, string? cantEdit, IDirectoryIndex? index)
    {
        _records = records;
        _pipeline = pipeline;
        CanEdit = canEdit && pipeline is not null;
        EditReason = cantEdit ?? "";
        _index = index ?? EmptyIndex.Instance;
        if (pipeline is not null)
        {
            pipeline.Changed += (_, _) => Rows();
            pipeline.Applied += (_, applied) =>
            {
                if (_read && applied.Any(c => c is SetAuthMethodConfig or SetSecurityDefaults))
                    _ = ReadAsync();
            };
        }
    }

    public bool CanEdit { get; }
    public string EditReason { get; }

    public ObservableCollection<AuthItemRow> Methods { get; } = [];
    public ObservableCollection<AuthItemRow> PolicySettings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor), nameof(IsDefaults))]
    public partial object? Editor { get; private set; }

    public bool HasEditor => Editor is not null;
    public bool IsDefaults => Editor is SecurityDefaultsEditor;

    [ObservableProperty]
    public partial AuthItemRow? Selected { get; set; }

    partial void OnSelectedChanged(AuthItemRow? value) => Open(value?.Id);

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    public void Shown()
    {
        if (!_read)
            _ = ReadAsync();
    }

    [RelayCommand]
    async Task ReadAsync()
    {
        _read = true;
        if (_records is null)
        {
            Note = "Sign-in methods can't be read for this tenant.";
            return;
        }
        IsLoading = true;
        Note = null;
        try
        {
            _policy = await _records.GetAuthMethodsAsync();
            Rows();
            Open(Selected?.Id ?? "MicrosoftAuthenticator");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note = $"Couldn't read the sign-in methods: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    JsonObject? Item(string id, AuthMethodsPolicy policy) => AuthMethods.IsSection(id)
        ? policy.Section(id)
        : policy.Methods.FirstOrDefault(m => m["id"]?.GetValue<string>() == id);

    void Rows()
    {
        if (_policy is not { } policy)
            return;
        var pending = _pipeline?.Pending.OfType<SetAuthMethodConfig>().Select(c => c.Method.Id).ToHashSet() ?? [];
        AuthItemRow Row(AuthMethodInfo info)
        {
            var item = Item(info.Id, policy)!;
            var state = item["state"]?.GetValue<string>();
            var (text, kind) = state switch
            {
                "enabled" => ("On", BadgeKind.Success),
                "disabled" => ("Off", BadgeKind.Neutral),
                _ => ("Microsoft Managed", BadgeKind.Info),
            };
            return new AuthItemRow(info.Id, info.Name, text, kind, info.Targets == "none" ? "" : AuthMethods.Who(item, policy.GroupNames), pending.Contains(info.Id));
        }
        var selected = Selected?.Id;
        Methods.Clear();
        foreach (var info in AuthMethods.Catalog.Where(i => Item(i.Id, policy) is not null))
            Methods.Add(Row(info));
        PolicySettings.Clear();
        foreach (var info in AuthMethods.Sections.Where(i => Item(i.Id, policy) is not null))
            PolicySettings.Add(Row(info));
        var defaultsPending = _pipeline?.Pending.OfType<SetSecurityDefaults>().Any() == true;
        PolicySettings.Add(new AuthItemRow("securityDefaults", "Security Defaults", policy.SecurityDefaultsOn ? "On" : "Off",
            policy.SecurityDefaultsOn ? BadgeKind.Success : BadgeKind.Neutral, "", defaultsPending));
        // Keep the selection without reopening the editor (it holds the edit in progress).
        _keepEditor = true;
        Selected = Methods.Concat(PolicySettings).FirstOrDefault(r => r.Id == selected);
        _keepEditor = false;
    }

    bool _keepEditor;

    void Open(string? id)
    {
        if (_keepEditor || _policy is not { } policy || id is null)
            return;
        if (id == "securityDefaults")
            Editor = new SecurityDefaultsEditor(policy.SecurityDefaultsOn, _pipeline, CanEdit);
        else if (Item(id, policy) is { } item)
            Editor = new AuthItemEditor(id, item, policy.GroupNames, _pipeline, CanEdit, _index);
        if (Selected?.Id != id)
        {
            _keepEditor = true;
            Selected = Methods.Concat(PolicySettings).FirstOrDefault(r => r.Id == id);
            _keepEditor = false;
        }
    }
}
