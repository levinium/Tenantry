using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.ViewModels.Intune;

/// <summary>An option to tick in a several-at-once setting.</summary>
public sealed partial class CatalogTick : ObservableObject
{
    readonly Action _changed;
    bool _loading = true;

    public CatalogTick(CatalogOption option, bool on, Action changed)
    {
        Option = option;
        _changed = changed;
        IsOn = on;
        _loading = false;
    }

    public CatalogOption Option { get; }
    public string Name => Option.Name;

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (!_loading)
            _changed();
    }
}

/// <summary>One item of a repeating group: its settings, and Remove.</summary>
public sealed partial class CatalogGroupItem(int number, IReadOnlyList<CatalogNode> children, Action remove, bool canEdit)
{
    public string Title => $"Item {number}";
    public IReadOnlyList<CatalogNode> Children => children;
    public bool CanEdit => canEdit;

    [RelayCommand]
    void Remove() => remove();
}

/// <summary>
/// One settings catalog setting, editing its part of the profile's settings in place: a choice
/// (whose option can bring settings), a value, a list, ticks, a repeating group or a group. Each
/// edit tells the card, which puts the whole profile in Pending changes.
/// </summary>
public sealed partial class CatalogNode : ObservableObject
{
    readonly ConfigurationProfileCard _card;
    readonly JsonObject _instance;
    readonly JsonArray? _owner;
    bool _loading = true;

    public CatalogNode(JsonObject instance, ConfigurationProfileCard card, int depth, JsonArray? owner)
    {
        _instance = instance;
        _card = card;
        _owner = owner;
        Depth = depth;
        Id = (string?)instance["settingDefinitionId"] ?? "";
        Def = card.Definition(Id);
        Kind = SettingsCatalog.KindOf(instance);
        switch (Kind)
        {
            case CatalogKind.Choice:
                Option = Def?.Options.FirstOrDefault(o => o.ItemId == (string?)instance["choiceSettingValue"]?["value"]);
                break;
            case CatalogKind.Simple:
                Text = instance["simpleSettingValue"]?["value"]?.ToString() ?? "";
                break;
            case CatalogKind.SimpleCollection:
                ListText = string.Join(Environment.NewLine, (instance["simpleSettingCollectionValue"] as JsonArray ?? []).Select(v => v?["value"]?.ToString()));
                break;
            case CatalogKind.ChoiceCollection:
                var chosen = (instance["choiceSettingCollectionValue"] as JsonArray ?? []).Select(v => (string?)v?["value"]).ToHashSet();
                Ticks = [.. (Def?.Options ?? []).Select(o => new CatalogTick(o, chosen.Contains(o.ItemId), CommitTicks))];
                break;
        }
        Children = [.. ChildArray()?.OfType<JsonObject>().Select(c => new CatalogNode(c, card, depth + 1, ChildArray())) ?? []];
        if (Kind == CatalogKind.GroupCollection)
            FillItems();
        _loading = false;
    }

    public string Id { get; }
    public CatalogDefinition? Def { get; }
    public CatalogKind Kind { get; }
    public int Depth { get; }
    /// <summary>How far in it sits (inner settings under their parent).</summary>
    public double IndentWidth => Depth * 22;

    public string Name => Def?.Name ?? Id;
    public string? Description => Def?.Description;
    public bool HasDescription => !string.IsNullOrEmpty(Def?.Description);
    public bool CanEdit => _card.CanEdit;

    public bool IsChoice => Kind == CatalogKind.Choice;
    public bool IsSimple => Kind == CatalogKind.Simple;
    public bool IsList => Kind == CatalogKind.SimpleCollection;
    public bool IsTicks => Kind == CatalogKind.ChoiceCollection;
    public bool IsGroupCollection => Kind == CatalogKind.GroupCollection;
    public bool IsOther => Kind is CatalogKind.Other || Def is null;
    public string OtherText => Def is null ? "Tenantry couldn't read what this setting is; it's kept as it is." : "Kept as it is.";

    /// <summary>Only a setting at the top can be removed; inner ones go with their parent's choice.</summary>
    public bool CanRemove => CanEdit && Depth == 0;

    public IReadOnlyList<CatalogOption> Options => Def?.Options ?? [];

    [ObservableProperty]
    public partial CatalogOption? Option { get; set; }

    partial void OnOptionChanged(CatalogOption? value)
    {
        if (_loading || value is null || !CanEdit)
            return;
        _ = _card.ChooseAsync(this, value);
    }

    internal JsonObject Instance => _instance;

    /// <summary>A choice's new option: its value, and that option's settings at their defaults.</summary>
    internal void SetOption(CatalogOption option, IReadOnlyDictionary<string, CatalogDefinition> defs)
    {
        var value = _instance["choiceSettingValue"]!.AsObject();
        value["value"] = option.ItemId;
        value["children"] = new JsonArray([.. option.Children.Select(defs.GetValueOrDefault).OfType<CatalogDefinition>().Select(d => (JsonNode)SettingsCatalog.Default(d, defs))]);
    }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    public string Placeholder => Def is null ? "" : Def.IsInteger ? Def.Min is { } min && Def.Max is { } max ? $"{min:N0} to {max:N0}" : "A whole number" : "Text";

    [RelayCommand]
    void CommitText()
    {
        if (Def is null || !CanEdit)
            return;
        var text = Text.Trim();
        if (Def.IsInteger && (!long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) || n < (Def.Min ?? long.MinValue) || n > (Def.Max ?? long.MaxValue)))
        {
            Problem = Def.Min is { } min && Def.Max is { } max ? $"A whole number from {min:N0} to {max:N0}." : "A whole number.";
            return;
        }
        Problem = null;
        _instance["simpleSettingValue"] = SettingsCatalog.SimpleValue(Def, Def.IsInteger ? long.Parse(text, CultureInfo.CurrentCulture).ToString(CultureInfo.InvariantCulture) : text);
        _card.Changed();
    }

    [ObservableProperty]
    public partial string ListText { get; set; } = "";

    [RelayCommand]
    void CommitList()
    {
        if (Def is null || !CanEdit)
            return;
        var items = ListText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        if (Def.MaxCount is { } most && items.Length > most)
        {
            Problem = $"At most {most:N0}.";
            return;
        }
        Problem = null;
        _instance["simpleSettingCollectionValue"] = new JsonArray([.. items.Select(i => (JsonNode)SettingsCatalog.SimpleValue(Def, i))]);
        _card.Changed();
    }

    public IReadOnlyList<CatalogTick> Ticks { get; } = [];

    void CommitTicks()
    {
        if (Def is null || !CanEdit)
            return;
        _instance["choiceSettingCollectionValue"] = new JsonArray([.. Ticks.Where(t => t.IsOn).Select(t => (JsonNode)new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.deviceManagementConfigurationChoiceSettingValue", ["value"] = t.Option.ItemId, ["children"] = new JsonArray(),
        })]);
        _card.Changed();
    }

    /// <summary>The settings this one holds: a choice's option's, or a group's.</summary>
    JsonArray? ChildArray() => Kind switch
    {
        CatalogKind.Choice => _instance["choiceSettingValue"]?["children"] as JsonArray,
        CatalogKind.Group => _instance["groupSettingValue"]?["children"] as JsonArray,
        _ => null,
    };

    public IReadOnlyList<CatalogNode> Children { get; }

    public ObservableCollection<CatalogGroupItem> Items { get; } = [];

    public bool CanAddItem => CanEdit && (Def?.MaxCount is not { } most || Items.Count < most);

    void FillItems()
    {
        Items.Clear();
        var items = _instance["groupSettingCollectionValue"] as JsonArray ?? [];
        var number = 0;
        foreach (var item in items.OfType<JsonObject>())
        {
            var children = item["children"] as JsonArray ?? [];
            var captured = item;
            Items.Add(new CatalogGroupItem(++number, [.. children.OfType<JsonObject>().Select(c => new CatalogNode(c, _card, Depth + 1, children))], () =>
            {
                items.Remove(captured);
                _card.Changed(structure: true);
            }, CanEdit));
        }
        OnPropertyChanged(nameof(CanAddItem));
    }

    [RelayCommand]
    Task AddItem() => _card.AddGroupItemAsync(this);

    internal void AppendItem(IReadOnlyDictionary<string, CatalogDefinition> defs)
    {
        if (_instance["groupSettingCollectionValue"] is not JsonArray items)
            _instance["groupSettingCollectionValue"] = items = [];
        items.Add(SettingsCatalog.GroupItem(Def!, defs));
    }

    [RelayCommand]
    void Remove() => _card.RemoveRoot(this);
}

/// <summary>A setting found in the catalog, to add.</summary>
public sealed partial class CatalogFound(CatalogDefinition def, Func<CatalogDefinition, Task> add)
{
    public string Name => def.Name;
    public string? Description => def.Description;
    public bool HasDescription => !string.IsNullOrEmpty(def.Description);

    [RelayCommand]
    Task Add() => add(def);
}

/// <summary>
/// A settings catalog profile as a card: who it reaches; opened, every setting it sets in words
/// (nested as Intune nests them), editable in place, Remove for each and a search of the whole catalog
/// to add more; its name, assignments and Delete. Its settings go to Pending changes as one change.
/// </summary>
public sealed partial class ConfigurationProfileCard : ObservableObject
{
    readonly ConfigurationProfilesViewModel _page;
    readonly Dictionary<string, CatalogDefinition> _defs = new(StringComparer.OrdinalIgnoreCase);
    JsonArray? _original;
    JsonArray? _working;

    public ConfigurationProfileCard(ConfigurationProfile profile, ConfigurationProfilesViewModel page, IDirectoryIndex index, IReadOnlyList<AssignmentFilter> filters)
    {
        Profile = profile;
        _page = page;
        Target = IntuneChange.TargetFor($"{SettingsCatalog.Beta}configurationPolicies/{profile.Id}", profile.Name);
        Assignments = new AssignmentEditor(Target, "Configuration Profile", profile.Assignments, page.Pipeline, page.CanEdit, index, filters);
        var context = new SettingContext(page.Pipeline, page.CanEdit ? Availability.Yes : Availability.No(page.EditReason),
            (def, value, oldText, newText, caution) => new SetIntuneSetting(Target, "", def.Parameter, def.Label, value, oldText, newText, caution), "Not set");
        About = SettingCards.Build(
        [
            new("About", "name", "Name", "", "intune", "", "name", SettingKind.Text),
            new("About", "description", "Description", "", "intune", "", "description", SettingKind.Text),
        ], d => d.Read(profile.Header), context, Target, null);
    }

    public ConfigurationProfile Profile { get; }
    public ChangeTarget Target { get; }
    public bool CanEdit => _page.CanEdit;
    public string Name => Profile.Name;
    public string PlatformText => Profile.PlatformText;
    public string KindText => SettingsCatalog.TemplateText(Profile.TemplateFamily);
    public string SettingsText => $"{Profile.SettingCount} {(Profile.SettingCount == 1 ? "setting" : "settings")}";
    public string? Description => Profile.Description;
    public bool HasDescription => Profile.Description is not null;
    public AssignmentEditor Assignments { get; }
    public IReadOnlyList<SettingGroup> About { get; }

    internal CatalogDefinition? Definition(string id) => _defs.GetValueOrDefault(id);

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && _original is null)
            _ = LoadAsync();
    }

    [ObservableProperty]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    public ObservableCollection<CatalogNode> Roots { get; } = [];

    SetCatalogSettings? Pending => _page.Pipeline?.PendingFor(Target.Id, "settings") as SetCatalogSettings;

    public async Task LoadAsync()
    {
        IsReading = true;
        Note = null;
        try
        {
            var (settings, defs) = await _page.Records!.GetProfileSettingsAsync(Profile.Id);
            foreach (var d in defs)
                _defs[d.Key] = d.Value;
            _original = settings;
            _working = (JsonArray)(Pending?.New ?? settings).DeepClone();
            Fill();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note = $"Couldn't read its settings: {Friendly.Message(e)}";
        }
        finally
        {
            IsReading = false;
        }
    }

    void Fill()
    {
        Roots.Clear();
        foreach (var instance in _working!.OfType<JsonObject>())
            Roots.Add(new CatalogNode(instance, this, 0, _working));
        if (Roots.Count == 0)
            Note = "No settings yet: search below to add some.";
        else if (Note?.StartsWith("No settings", StringComparison.Ordinal) == true)
            Note = null;
    }

    /// <summary>The working settings changed: the whole profile goes to Pending changes (or comes out, when it's back as read).</summary>
    bool _restaging;

    internal void Changed(bool structure = false)
    {
        if (_original is null || _working is null || !CanEdit)
            return;
        if (_working.Count == 0)
        {
            // Intune refuses a profile with no settings: put the last one back and say so.
            _working = (JsonArray)(Pending?.New ?? _original).DeepClone();
            Fill();
            Note = "A profile needs at least one setting. To stop all of them, delete the profile.";
            return;
        }
        var pipeline = _page.Pipeline!;
        // Replacing the change takes the old one out first; that isn't a discard (see Refresh).
        _restaging = true;
        try
        {
            if (Pending is { } pending)
                pipeline.Unstage(pending);
            var (before, after) = SettingsCatalog.Describe(_original, _working, _defs);
            var change = new SetCatalogSettings(Target, JsonNode.Parse(Profile.Header.GetRawText())!.AsObject(), (JsonArray)_original.DeepClone(), (JsonArray)_working.DeepClone(), before, after);
            if (!change.IsNoChange)
                pipeline.Stage(change);
        }
        finally
        {
            _restaging = false;
        }
        if (structure)
            Fill();
    }

    /// <summary>Reads definitions not yet known, and the ones they need, all the way down.</summary>
    async Task EnsureAsync(IEnumerable<string> ids)
    {
        var wanted = ids.Where(i => !_defs.ContainsKey(i)).ToList();
        while (wanted.Count > 0)
        {
            var read = await _page.Records!.GetDefinitionsAsync(wanted);
            foreach (var d in read)
                _defs[d.Id] = d;
            wanted = [.. read.SelectMany(SettingsCatalog.Needs).Where(i => !_defs.ContainsKey(i)).Distinct()];
            if (read.Count == 0)
                break;
        }
    }

    internal async Task ChooseAsync(CatalogNode node, CatalogOption option)
    {
        await EnsureAsync(option.Children);
        node.SetOption(option, _defs);
        Changed(structure: true);
    }

    internal async Task AddGroupItemAsync(CatalogNode node)
    {
        if (node.Def is null)
            return;
        await EnsureAsync(node.Def.ChildIds);
        node.AppendItem(_defs);
        Changed(structure: true);
    }

    internal void RemoveRoot(CatalogNode node)
    {
        _working?.Remove(node.Instance);
        Changed(structure: true);
    }

    // ---- Adding a setting --------------------------------------------------------------------

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    public ObservableCollection<CatalogFound> Found { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchNote))]
    public partial string? SearchNote { get; private set; }

    public bool HasSearchNote => SearchNote is not null;

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    [RelayCommand]
    async Task Search()
    {
        if (SearchText.Trim().Length < 2)
        {
            SearchNote = "Type at least two letters.";
            return;
        }
        IsSearching = true;
        SearchNote = null;
        Found.Clear();
        try
        {
            var have = _working?.Select(n => (string?)n?["settingDefinitionId"]).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var found = await _page.Records!.SearchSettingsAsync(Profile.Platforms, Profile.Technologies, SearchText);
            foreach (var d in found.Where(d => !have.Contains(d.Id)))
                Found.Add(new CatalogFound(d, AddSettingAsync));
            SearchNote = Found.Count == 0 ? "Nothing in the catalog matches (settings already in the profile aren't listed)." : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SearchNote = $"Couldn't search the catalog: {Friendly.Message(e)}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    async Task AddSettingAsync(CatalogDefinition def)
    {
        if (!CanEdit || _working is null)
            return;
        _defs[def.Id] = def;
        await EnsureAsync(SettingsCatalog.Needs(def));
        _working.Add(SettingsCatalog.Default(def, _defs));
        Found.Remove(Found.First(f => f.Name == def.Name));
        Changed(structure: true);
    }

    // ---- Delete --------------------------------------------------------------------------------

    public string DeleteText => _page.Pipeline?.PendingFor(Target.Id, "delete") is not null ? "Undo Delete" : "Delete Profile";

    [RelayCommand]
    void Delete()
    {
        if (!CanEdit)
            return;
        if (_page.Pipeline!.PendingFor(Target.Id, "delete") is { } pending)
            _page.Pipeline.Unstage(pending);
        else
            _page.Pipeline.Stage(new DeleteIntunePolicy(Target, "Configuration Profile", $"{SettingsText}; {Assignments.Summary}"));
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(DeleteText));
        // Pending changes discarded from the tray: show the settings as read again.
        if (!_restaging && _original is not null && Pending is null && _working is not null && !JsonNode.DeepEquals(_working, _original))
        {
            _working = (JsonArray)_original.DeepClone();
            Fill();
        }
    }
}

/// <summary>An older template profile as a card: its properties (plain ones editable), assignments and Delete.</summary>
public sealed partial class TemplateProfileCard : ObservableObject
{
    readonly ConfigurationProfilesViewModel _page;

    public TemplateProfileCard(TemplateProfile profile, ConfigurationProfilesViewModel page, IDirectoryIndex index, IReadOnlyList<AssignmentFilter> filters)
    {
        Profile = profile;
        _page = page;
        Target = IntuneChange.TargetFor($"deviceManagement/deviceConfigurations/{profile.Id}", profile.Name);
        Assignments = new AssignmentEditor(Target, "Configuration Profile", profile.Assignments, page.Pipeline, page.CanEdit, index, filters);
        var defs = new List<SettingDef>();
        var others = new List<SummaryLineText>();
        foreach (var p in profile.Raw.EnumerateObject())
        {
            if (CompliancePlatforms.Housekeeping.Contains(p.Name) && p.Name is not ("displayName" or "description") || p.Name.Contains('@'))
                continue;
            var label = Display.TitleCase(SettingsCatalog.Words(p.Name));
            var group = p.Name is "displayName" or "description" ? "About" : "Settings";
            var kind = p.Value.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => SettingKind.OnOff,
                JsonValueKind.Number => SettingKind.Number,
                JsonValueKind.String or JsonValueKind.Null => SettingKind.Text,
                JsonValueKind.Array when p.Value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String) => SettingKind.List,
                _ => (SettingKind?)null,
            };
            if (kind is { } k)
                defs.Add(new SettingDef(group, p.Name, p.Name == "displayName" ? "Name" : label, "", "intune", profile.ODataType, p.Name, k));
            else
                others.Add(new SummaryLineText(label, p.Value.ToString().Length > 200 ? p.Value.ToString()[..200] + "…" : p.Value.ToString()));
        }
        var context = new SettingContext(page.Pipeline, page.CanEdit ? Availability.Yes : Availability.No(page.EditReason),
            (def, value, oldText, newText, caution) => new SetIntuneSetting(Target, def.SetCmdlet, def.Parameter, def.Label, value, oldText, newText, caution), "Not set");
        Groups = SettingCards.Build(defs.OrderBy(d => d.Group == "About" ? 0 : 1), d => d.Read(profile.Raw), context, Target, null);
        Others = others;
    }

    public TemplateProfile Profile { get; }
    public ChangeTarget Target { get; }
    public bool CanEdit => _page.CanEdit;
    public string Name => Profile.Name;
    public string TypeText => Profile.TypeText;
    public AssignmentEditor Assignments { get; }
    public IReadOnlyList<SettingGroup> Groups { get; }
    public IReadOnlyList<SummaryLineText> Others { get; }
    public bool HasOthers => Others.Count > 0;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public string DeleteText => _page.Pipeline?.PendingFor(Target.Id, "delete") is not null ? "Undo Delete" : "Delete Profile";

    [RelayCommand]
    void Delete()
    {
        if (!CanEdit)
            return;
        if (_page.Pipeline!.PendingFor(Target.Id, "delete") is { } pending)
            _page.Pipeline.Unstage(pending);
        else
            _page.Pipeline.Stage(new DeleteIntunePolicy(Target, "Configuration Profile", $"{TypeText}; {Assignments.Summary}"));
    }

    internal void Refresh() => OnPropertyChanged(nameof(DeleteText));
}

/// <summary>
/// Configuration Profiles: settings catalog profiles (and endpoint security ones, which use it),
/// then older template profiles; a search of the list, and a new settings catalog profile.
/// </summary>
public sealed partial class ConfigurationProfilesViewModel : LivePage
{
    readonly IDirectoryIndex _index;
    IReadOnlyList<ConfigurationProfileCard> _all = [];

    public ConfigurationProfilesViewModel(IConfigurationProfileRecords? records, ChangePipeline? pipeline, Availability canEdit, IDirectoryIndex index,
        IIntunePolicyRecords? filters) : base(pipeline, canEdit)
    {
        Records = records;
        _filters = filters;
        _index = index;
        Pipeline = pipeline;
        if (pipeline is not null)
            pipeline.Changed += (_, _) =>
            {
                foreach (var c in _all)
                    c.Refresh();
                foreach (var t in Templates)
                    t.Refresh();
            };
    }

    readonly IIntunePolicyRecords? _filters;

    internal IConfigurationProfileRecords? Records { get; }
    internal ChangePipeline? Pipeline { get; }

    public ObservableCollection<ConfigurationProfileCard> Cards { get; } = [];
    public ObservableCollection<TemplateProfileCard> Templates { get; } = [];
    public bool HasTemplates => Templates.Count > 0;
    public bool IsEmpty => Cards.Count == 0 && Templates.Count == 0;

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    partial void OnFilterTextChanged(string value) => ShowCards();

    void ShowCards()
    {
        var words = FilterText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Cards.Clear();
        foreach (var c in _all.Where(c => words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || c.KindText.Contains(w, StringComparison.OrdinalIgnoreCase))))
            Cards.Add(c);
        OnPropertyChanged(nameof(IsEmpty));
    }

    protected override bool Concerns(ChangeOp change) => change is IntuneChange i && (i.Path.Contains("configurationPolicies", StringComparison.Ordinal)
        || i.Path.Contains("deviceConfigurations", StringComparison.Ordinal) || i is CreateConfigurationProfile);

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (Records is null)
            return;
        var filters = _filters is null ? Task.FromResult<IReadOnlyList<AssignmentFilter>>([]) : _filters.GetAssignmentFiltersAsync(cancellationToken);
        var templates = Records.GetTemplateProfilesAsync(cancellationToken);
        var profiles = await Records.GetConfigurationProfilesAsync(cancellationToken);
        var filterList = await filters;
        var open = _all.Where(c => c.IsExpanded).Select(c => c.Profile.Id).ToHashSet();
        _all = [.. profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => new ConfigurationProfileCard(p, this, _index, filterList))];
        foreach (var c in _all.Where(c => open.Contains(c.Profile.Id)))
            c.IsExpanded = true;
        ShowCards();
        Templates.Clear();
        // Update rings live among them in Graph; they have their own page.
        foreach (var t in (await templates).Where(t => !IntuneKinds.IsUpdateType(t.ODataType)).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            Templates.Add(new TemplateProfileCard(t, this, _index, filterList));
        OnPropertyChanged(nameof(HasTemplates));
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ---- A new profile -------------------------------------------------------------------------

    public static IReadOnlyList<EditorChoice> Platforms { get; } = [.. SettingsCatalog.NewProfilePlatforms.Select(p => new EditorChoice($"{p.Platforms}|{p.Technologies}", p.Title))];

    [ObservableProperty]
    public partial bool IsAddOpen { get; set; }

    [ObservableProperty]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    public partial EditorChoice NewPlatform { get; set; } = Platforms[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddProblem))]
    public partial string? AddProblem { get; private set; }

    public bool HasAddProblem => AddProblem is not null;

    [RelayCommand]
    void ToggleAdd() => IsAddOpen = !IsAddOpen;

    partial void OnNewPlatformChanged(EditorChoice value)
    {
        NewFound.Clear();
        NewSetting = null;
    }

    /// <summary>The first setting's search (Intune refuses a profile with no settings).</summary>
    [ObservableProperty]
    public partial string NewSearchText { get; set; } = "";

    public ObservableCollection<CatalogFound> NewFound { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewSetting), nameof(NewSettingText))]
    public partial CatalogDefinition? NewSetting { get; private set; }

    public bool HasNewSetting => NewSetting is not null;

    public string NewSettingText => NewSetting is null ? "" : $"First setting: {NewSetting.Name}";

    [RelayCommand]
    async Task SearchNew()
    {
        if (Records is null || NewSearchText.Trim().Length < 2)
            return;
        var parts = NewPlatform.Value.Split('|');
        NewFound.Clear();
        try
        {
            foreach (var d in await Records.SearchSettingsAsync(parts[0], parts[1], NewSearchText))
                NewFound.Add(new CatalogFound(d, def =>
                {
                    NewSetting = def;
                    NewFound.Clear();
                    return Task.CompletedTask;
                }));
            AddProblem = NewFound.Count == 0 ? "Nothing in the catalog matches." : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AddProblem = $"Couldn't search the catalog: {Friendly.Message(e)}";
        }
    }

    [RelayCommand]
    async Task Add()
    {
        var name = NewName.Trim();
        AddProblem = name.Length == 0 ? "Give the profile a name."
            : _all.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? "A profile has that name already."
            : NewSetting is null ? "Choose a first setting: Intune won't make a profile with none." : null;
        if (AddProblem is not null || !CanEdit || Records is null)
            return;
        // The first setting at Microsoft's defaults, with the settings its default option brings.
        var defs = new Dictionary<string, CatalogDefinition>(StringComparer.OrdinalIgnoreCase) { [NewSetting!.Id] = NewSetting };
        var wanted = SettingsCatalog.Needs(NewSetting).ToList();
        while (wanted.Count > 0)
        {
            var read = await Records.GetDefinitionsAsync(wanted);
            foreach (var d in read)
                defs[d.Id] = d;
            wanted = [.. read.SelectMany(SettingsCatalog.Needs).Where(i => !defs.ContainsKey(i)).Distinct()];
            if (read.Count == 0)
                break;
        }
        var instance = SettingsCatalog.Default(NewSetting, defs);
        var parts = NewPlatform.Value.Split('|');
        Pipeline!.Stage(new CreateConfigurationProfile(IntuneChange.TargetFor($"new:{name}", name), parts[0], parts[1], null, new JsonArray(instance),
            $"{NewSetting.Name}: {SettingsCatalog.Words(instance, defs)}"));
        NewName = "";
        NewSearchText = "";
        NewSetting = null;
        IsAddOpen = false;
    }
}
