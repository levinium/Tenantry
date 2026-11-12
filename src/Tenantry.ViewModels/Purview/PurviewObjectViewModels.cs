using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Intune;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.ViewModels.Purview;

/// <summary>
/// One Purview item as a card (a label, a DLP policy, a segment…): its settings in words, edited
/// in place; what it shows but can't change here; for a policy, its rules (each a card of its own)
/// and New Rule; and Delete.
/// </summary>
public sealed partial class PurviewObjectCard : ObservableObject
{
    readonly PurviewObjectsViewModel _page;
    readonly PurviewKind _kind;

    public PurviewObjectCard(PurviewObject item, PurviewKind kind, PurviewObjectsViewModel page, IReadOnlyList<PurviewObject> rules)
    {
        Item = item;
        _kind = kind;
        _page = page;
        Target = PurviewChange.TargetFor(kind.Key, item.Name);
        IsReadOnly = kind.Graph is not null || kind.ReadOnlyWhen?.Invoke(item.Raw) == true;
        Build();
        if (kind.RulesOf is { } rulesOf && page.RuleKind is { } ruleKind)
        {
            Rules = [.. rules.Where(r => string.Equals(r.Parent, item.Name, StringComparison.OrdinalIgnoreCase))
                .Select(r => new PurviewObjectCard(r, ruleKind, page, []))];
            RuleCreate = ruleKind.Creates is { Count: > 0 } && page.CanEdit
                ? new PurviewCreateForm(ruleKind, page, () => Rules.Select(r => r.Name), item.Name)
                : null;
        }
    }

    public PurviewObject Item { get; }
    public ChangeTarget Target { get; }
    public string Name => Item.Name;

    /// <summary>A short line under the name: a label's shown name, a policy's mode, a rule's severity.</summary>
    public string? Subtitle => Line("DisplayName") is { } shown && shown != Name ? shown
        : Line("severity") is { } severity ? $"{Display.TitleCase(severity)} Severity · {Display.TitleCase(SettingsCatalog.Words(Line("status") ?? ""))}"
        : Line("Publisher") ?? Line("UserGroupFilter") ?? Line("Description") ?? Line("Comment");

    public bool HasSubtitle => Subtitle is { Length: > 0 };

    /// <summary>Simulation or on, as a badge (DLP and auto-labeling).</summary>
    public string? ModeBadge => Line("Mode") switch
    {
        "Enable" => "On",
        "TestWithNotifications" or "TestWithoutNotifications" => "Simulation",
        "Disable" => "Off",
        _ => null,
    };

    public bool HasModeBadge => ModeBadge is not null;
    public BadgeKind ModeKind => ModeBadge == "On" ? BadgeKind.Success : ModeBadge == "Simulation" ? BadgeKind.Info : BadgeKind.Neutral;

    string? Line(string property) => Item.Raw.ValueKind == JsonValueKind.Object && Item.Raw.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
        && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>Microsoft's own item, an alert, or a kind Tenantry only shows.</summary>
    public bool IsReadOnly { get; }

    public string? KeptText => IsReadOnly ? _kind.ReadOnlyText ?? (_kind.Graph is not null ? null : "Shown as Purview has it.") : null;
    public bool HasKeptText => KeptText is not null;

    public bool CanEdit => _page.CanEdit && !IsReadOnly;
    public bool CanDelete => CanEdit && _kind.Remove is not null && !_kind.Single;

    [ObservableProperty]
    public partial IReadOnlyList<SettingGroup> Groups { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<SummaryLineText> Details { get; private set; } = [];

    public bool HasDetails => Details.Count > 0;

    partial void OnDetailsChanged(IReadOnlyList<SummaryLineText> value) => OnPropertyChanged(nameof(HasDetails));

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    // ---- Rules -----------------------------------------------------------------------------------

    public IReadOnlyList<PurviewObjectCard> Rules { get; } = [];
    public bool HasRules => _kind.RulesOf is not null;
    public string RulesEmptyText => Rules.Count == 0 ? "No rules yet: it finds nothing until it has one." : "";
    public bool HasNoRules => HasRules && Rules.Count == 0;
    public PurviewCreateForm? RuleCreate { get; }
    public bool CanAddRule => RuleCreate is not null && CanEdit;

    // ---- Settings ----------------------------------------------------------------------------------

    /// <summary>Purview's housekeeping, never shown.</summary>
    static readonly HashSet<string> Housekeeping = new(StringComparer.OrdinalIgnoreCase)
    {
        "Identity", "Id", "Guid", "ExchangeObjectId", "ImmutableId", "DistinguishedName", "ObjectVersion", "OrganizationId", "Organization", "WhenCreated", "WhenChanged",
        "WhenCreatedUTC", "WhenChangedUTC", "IsValid", "ObjectState", "ExchangeVersion", "ObjectClass", "OriginatingServer", "PSComputerName", "RunspaceId", "PSShowComputerName",
        "CreatedBy", "LastModifiedBy", "ModificationTimeUtc", "CreationTimeUtc", "ReadOnly", "Name", "LabelActions", "Settings", "LocaleSettings", "Conditions", "ParentId",
        "ParentPolicyName", "Policy", "DistributionResults", "Workload", "ObjectId", "OrganizationalUnitRoot", "IsLabelGroup", "Type@odata.type", "@odata.type", "id",
        "ExchangeLocationException", "SharePointLocationException", "OneDriveLocationException", "PolicyRBACScopes", "CreatedDateTime", "IsSimulationPolicy",
    };

    void Build()
    {
        var raw = Item.Raw;
        var flat = new JsonObject();
        var defs = new List<SettingDef>();
        var details = new List<SummaryLineText>();
        var appliesTo = Line("ContentType") ?? "";
        foreach (var field in _kind.Fields ?? [])
        {
            // A label for files and email has no site settings, and one for sites no markings.
            if (_kind.LabelActions && appliesTo.Length > 0 && LabelGroup(field.Property) is { } labelGroup
                && (labelGroup == "Sites and Groups") != (appliesTo.Contains("Site", StringComparison.OrdinalIgnoreCase) || appliesTo.Contains("UnifiedGroup", StringComparison.OrdinalIgnoreCase))
                && !(labelGroup != "Sites and Groups" && (appliesTo.Contains("File", StringComparison.OrdinalIgnoreCase) || appliesTo.Contains("Email", StringComparison.OrdinalIgnoreCase))))
                continue;
            var value = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(field.Property, out var v) ? v : default;
            var shown = Shown(field, value);
            var editable = CanEdit && (field.CanChange || (_kind.Members?.Property == field.Property));
            if (!editable)
            {
                if (Words(field, shown) is { Length: > 0 } words)
                    details.Add(new SummaryLineText(field.Label, words));
                continue;
            }
            flat[field.Property] = shown?.DeepClone();
            var group = field.Value == PurviewValue.Places ? "Where It Applies" : field.Property is "Comment" or "Description" or "DisplayName" or "Tooltip" ? "About"
                : _kind.LabelActions && LabelGroup(field.Property) is { } g ? g : "Settings";
            defs.Add(new SettingDef(group, field.Property, field.Label, field.Help ?? "", _kind.Get, _kind.Set ?? "", field.Property, field.Kind, Choices: field.Choices,
                Min: field.Min, Max: field.Max, Unit: field.Unit));
        }
        // What else Purview keeps, in words, when the kind doesn't name it.
        if (raw.ValueKind == JsonValueKind.Object && _kind.Graph is null)
            foreach (var p in raw.EnumerateObject())
            {
                if (Housekeeping.Contains(p.Name) || _kind.Field(p.Name) is not null || p.Name == _kind.NameProperty)
                    continue;
                if (Summarize(p.Value) is { Length: > 0 } text)
                    details.Add(new SummaryLineText(Display.TitleCase(SettingsCatalog.Words(p.Name)), text));
            }
        var row = JsonDocument.Parse(flat.ToJsonString()).RootElement.Clone();
        var context = new SettingContext(_page.Pipeline, CanEdit ? Availability.Yes : Availability.No(_page.EditReason), Make, "Not set");
        Groups = SettingCards.Build(defs.OrderBy(d => d.Group switch { "About" => 0, "Settings" => 1, "Markings" => 2, "Encryption" => 3, "Sites and Groups" => 4, _ => 5 }), d => d.Read(row), context, Target, null);
        Details = details;
    }

    /// <summary>Where a label setting sits on its card.</summary>
    static string? LabelGroup(string property) =>
        property.StartsWith("ApplyContent", StringComparison.Ordinal) || property.StartsWith("ApplyWater", StringComparison.Ordinal) ? "Markings"
        : property.StartsWith("Encryption", StringComparison.Ordinal) ? "Encryption"
        : property.StartsWith("Site", StringComparison.Ordinal) ? "Sites and Groups" : null;

    /// <summary>A property as the editor shows it: sensitive types and keywords as lines, numbers and switches from Purview's text.</summary>
    static JsonNode? Shown(PurviewField field, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return field.Kind == SettingKind.List ? new JsonArray() : null;
        switch (field.Value)
        {
            case PurviewValue.SensitiveTypes:
                return SensitiveTypeMatch.From(value) is { Count: > 0 } matches ? JsonValue.Create(SensitiveTypeMatch.Lines(matches)) : null;
            case PurviewValue.Keywords:
                return JsonValue.Create(string.Join(Environment.NewLine, (value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString())
                    .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }
        return field.Kind switch
        {
            SettingKind.OnOff => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? JsonValue.Create(value.GetBoolean())
                : bool.TryParse(value.ToString(), out var b) ? JsonValue.Create(b) : null,
            SettingKind.Number => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? JsonValue.Create(n)
                : int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? JsonValue.Create(parsed) : null,
            SettingKind.List => new JsonArray([.. ListOf(value).Select(i => (JsonNode)i)]),
            _ => value.ValueKind == JsonValueKind.String ? JsonValue.Create(value.GetString()) : JsonValue.Create(value.ToString()),
        };
    }

    /// <summary>A list from Purview: an array (of names, or of objects with a name), or text separated by commas.</summary>
    static IReadOnlyList<string> ListOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => [.. value.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString()
            : i.ValueKind == JsonValueKind.Object ? Str(i, "Name") ?? Str(i, "DisplayName") ?? Str(i, "name") : i.ToString()).OfType<string>().Where(s => s.Length > 0)],
        JsonValueKind.String => [.. (value.GetString() ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        _ => [],
    };

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string Words(PurviewField field, JsonNode? value) => value switch
    {
        null => "",
        JsonArray a => a.Count == 0 ? "" : string.Join(", ", a.Select(i => i?.ToString())),
        JsonValue v when field.Kind == SettingKind.OnOff && v.TryGetValue<bool>(out var on) => on ? "Yes" : "No",
        JsonValue v when field.Choices?.FirstOrDefault(c => c.Value.Equals(v.ToString(), StringComparison.OrdinalIgnoreCase)).Label is { Length: > 0 } label => label,
        JsonValue v when field.Property.EndsWith("DateTime", StringComparison.Ordinal) && DateTimeOffset.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            => d.ToLocalTime().ToString("d MMM yyyy, h:mm tt", CultureInfo.CurrentCulture),
        _ => field.Value == PurviewValue.SensitiveTypes ? value.ToString().Replace(Environment.NewLine, "; ", StringComparison.Ordinal) : value.ToString(),
    };

    static string? Summarize(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "Yes",
            JsonValueKind.False => null,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.Array => value.GetArrayLength() == 0 ? null : string.Join(", ", ListOf(value)),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text) || text is "0" or "None" or "{}" or "[]")
            return null;
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    /// <summary>A setting's change: lists piece by piece where Purview takes them that way, everything else with Set-.</summary>
    ChangeOp Make(SettingDef def, JsonNode? value, string oldText, string newText, bool caution)
    {
        var field = _kind.Field(def.Key)!;
        var read = def.Read(JsonDocument.Parse(new JsonObject { [field.Property] = Shown(field, Item.Raw.TryGetProperty(field.Property, out var r) ? r : default)?.DeepClone() }.ToJsonString()).RootElement);
        IReadOnlyList<string> Items(JsonNode? n) => n is JsonArray a ? [.. a.Select(i => i?.ToString()).OfType<string>()] : [];
        if (_kind.Members is { } members && members.Property == field.Property)
            return new SetPurviewList(Target, Display.TitleCase(_kind.Noun), "", field.Label, null, null, Items(read), Items(value), Guard: true, MemberCmdlets: members);
        if (field.Add is not null)
            return new SetPurviewList(Target, Display.TitleCase(_kind.Noun), _kind.Set!, field.Label, field.Add, field.Remove, Items(read), Items(value), field.IsGuarded);
        var sent = value;
        if (field.Value == PurviewValue.SensitiveTypes)
        {
            var matches = SensitiveTypeMatch.Parse(value?.ToString() ?? "");
            sent = SensitiveTypeMatch.ToParameter(matches);
            newText = matches.Count == 0 ? "Nothing" : string.Join("; ", matches.Select(m => m.Text));
            oldText = SensitiveTypeMatch.Parse(read?.ToString() ?? "") is { Count: > 0 } old ? string.Join("; ", old.Select(m => m.Text)) : "Nothing";
        }
        var cautious = caution || (field.CautionValues?.Contains(value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase) == true)
                       || (field.CautionOff && value is JsonValue on && on.TryGetValue<bool>(out var isOn) && !isOn)
                       || field.Value == PurviewValue.SensitiveTypes && oldText != newText;
        return new SetPurviewSetting(Target, Display.TitleCase(_kind.Noun), _kind.Set!, field.Parameter!, field.Label, sent, oldText, newText, cautious,
            field.IsGuarded ? Items(value).Except(Items(read), StringComparer.OrdinalIgnoreCase).ToArray() : null);
    }

    // ---- Delete --------------------------------------------------------------------------------

    public string DeleteText => _page.Pipeline?.PendingFor(Target.Id, "delete") is not null ? "Undo Delete" : "Delete";

    [RelayCommand]
    void Delete()
    {
        if (!CanDelete)
            return;
        if (_page.Pipeline!.PendingFor(Target.Id, "delete") is { } pending)
            _page.Pipeline.Unstage(pending);
        else
            _page.Pipeline.Stage(new DeletePurviewObject(Target, Display.TitleCase(_kind.Noun), _kind.Remove!, Subtitle ?? _kind.Title));
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(DeleteText));
        foreach (var r in Rules)
            r.Refresh();
    }
}

/// <summary>New: what a kind can make (or a rule for one policy), with its boxes.</summary>
public sealed partial class PurviewCreateForm : ObservableObject
{
    readonly PurviewKind _kind;
    readonly PurviewObjectsViewModel _page;
    readonly Func<IEnumerable<string>> _taken;
    readonly string? _parent;

    public PurviewCreateForm(PurviewKind kind, PurviewObjectsViewModel page, Func<IEnumerable<string>> taken, string? parent = null)
    {
        _kind = kind;
        _page = page;
        _taken = taken;
        _parent = parent;
        Options = kind.Creates ?? [];
        Option = Options.FirstOrDefault();
    }

    public IReadOnlyList<PurviewCreate> Options { get; }
    public bool HasManyOptions => Options.Count > 1;
    public string ButtonText => _parent is null ? "New" : "New Rule";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    public partial PurviewCreate? Option { get; set; }

    partial void OnOptionChanged(PurviewCreate? value) => Fields = value is null ? [] : [.. value.Fields.Select(f => new CreateFieldRow(f))];

    public string? Description => Option?.Description;

    [ObservableProperty]
    public partial IReadOnlyList<CreateFieldRow> Fields { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    /// <summary>The sensitive information types there are, for the rule form's hint.</summary>
    [ObservableProperty]
    public partial string? TypesHint { get; private set; }

    [RelayCommand]
    async Task Toggle()
    {
        IsOpen = !IsOpen;
        Problem = null;
        OnOptionChanged(Option);
        if (IsOpen && TypesHint is null && Fields.Any(f => f.Field.Path == "ContentContainsSensitiveInformation") && _page.Records is { } records)
        {
            var names = await records.GetSensitiveTypeNamesAsync();
            TypesHint = names.Count == 0 ? null : $"Types you can use: {string.Join(", ", names.Take(40))}{(names.Count > 40 ? $", and {names.Count - 40:N0} more (see Sensitive Info Types)" : "")}.";
        }
    }

    [RelayCommand]
    void Add()
    {
        if (Option is not { } option || !_page.CanEdit)
            return;
        var parameters = new JsonObject();
        foreach (var (k, v) in option.Fixed)
            parameters[k] = v?.DeepClone();
        var guarded = new List<string>();
        foreach (var f in Fields)
        {
            var (value, problem) = f.Read();
            if (problem is not null)
            {
                Problem = problem;
                return;
            }
            if (value is null || value is JsonArray { Count: 0 })
                continue;
            var path = f.Field.Path;
            if (path == "ContentContainsSensitiveInformation" && value is JsonArray names)
            {
                var known = _page.KnownTypes;
                var unknown = names.Select(n => n?.ToString()).OfType<string>().FirstOrDefault(n => known.Count > 0 && !known.Contains(n, StringComparer.OrdinalIgnoreCase));
                if (unknown is not null)
                {
                    Problem = $"\"{unknown}\" isn't a sensitive information type here (names must match exactly).";
                    return;
                }
                value = SensitiveTypeMatch.ToParameter(SensitiveTypeMatch.Parse(string.Join("\n", names.Select(n => n?.ToString()))));
            }
            else if (value is JsonValue choice && f.Field.Kind == SettingKind.Choice && choice.ToString() is "true" or "false")
                value = JsonValue.Create(choice.ToString() == "true");
            parameters[path] = value;
            if (PurviewKinds.IsGuardedParameter(_kind, path))
                guarded.AddRange(value is JsonArray a ? a.Select(i => i?.ToString()).OfType<string>() : [value.ToString()]);
        }
        var name = (string?)parameters["Name"] ?? "";
        if (_taken().Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Problem = "One has that name already.";
            return;
        }
        if (_parent is not null)
            parameters[_kind.ParentParameter] = _parent;
        var summary = string.Join("; ", Fields.Where(f => f.Field.Path != "Name").Select(f => $"{f.Field.Label}: {(f.IsChoice ? f.Choice?.Label : f.Text.Trim().Replace("\n", ", ", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal))}")
            .Where(s => !s.EndsWith(": ", StringComparison.Ordinal)));
        if (option.Fixed.TryGetValue("Mode", out var mode) && mode?.ToString() is { } m)
            summary = $"Starts in {(m.StartsWith("Test", StringComparison.Ordinal) ? "simulation" : m)}" + (summary.Length > 0 ? "; " + summary : "");
        _page.Pipeline!.Stage(new CreatePurviewObject(PurviewChange.TargetFor(_kind.Key, name), _parent is null ? option.Title : $"Rule in \"{_parent}\"", _kind.New!, parameters,
            summary.Length == 0 ? option.Title : summary, guarded));
        Problem = null;
        IsOpen = false;
    }
}

/// <summary>
/// One Purview kind as a list of cards, read live when its page is first shown: what it is, New
/// (and the page's organization-wide action), a search, and every item's card. A policy kind reads
/// its rules too and shows them in each policy's card.
/// </summary>
public sealed partial class PurviewObjectsViewModel : LivePage
{
    IReadOnlyList<PurviewObjectCard> _all = [];

    public PurviewObjectsViewModel(PurviewKind kind, IPurviewObjectRecords? records, ChangePipeline? pipeline, Availability canEdit) : base(pipeline, canEdit)
    {
        Kind = kind;
        Records = records;
        Pipeline = pipeline;
        RuleKind = kind.RulesOf is { } rules ? PurviewKinds.Get(rules) : null;
        Create = kind.Creates is { Count: > 0 } && kind.New is not null ? new PurviewCreateForm(kind, this, () => _all.Select(c => c.Name)) : null;
        if (pipeline is not null)
            pipeline.Changed += (_, _) =>
            {
                foreach (var c in _all)
                    c.Refresh();
                OnPropertyChanged(nameof(ActionText));
            };
    }

    public PurviewKind Kind { get; }
    public PurviewKind? RuleKind { get; }
    internal IPurviewObjectRecords? Records { get; }
    internal ChangePipeline? Pipeline { get; }
    internal new string EditReason => base.EditReason;
    public string Title => Kind.Title;
    public string About => Kind.About;
    public PurviewCreateForm? Create { get; }
    public bool CanCreate => CanEdit && Create is not null;

    /// <summary>Sensitive information types there are (for checking a new rule's names); empty until read.</summary>
    internal IReadOnlyList<string> KnownTypes { get; private set; } = [];

    public ObservableCollection<PurviewObjectCard> Cards { get; } = [];
    public bool IsEmpty => Cards.Count == 0 && IsLoaded && !HasError && !HasNote;
    public string EmptyText => _all.Count == 0 ? $"No {Kind.Title.ToLowerInvariant()} yet." : "Nothing matches.";
    public bool ShowFilter => !Kind.Single;

    /// <summary>Why Purview couldn't list them (licensing), above the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote), nameof(IsEmpty))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    partial void OnFilterTextChanged(string value) => Show();

    void Show()
    {
        var words = FilterText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Cards.Clear();
        foreach (var c in _all.Where(c => words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || (c.Subtitle ?? "").Contains(w, StringComparison.OrdinalIgnoreCase))))
            Cards.Add(c);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    protected override bool Concerns(ChangeOp change) => change is PurviewChange p
        && (p.Item.Id.StartsWith(Kind.Key + ":", StringComparison.Ordinal) || RuleKind is { } r && p.Item.Id.StartsWith(r.Key + ":", StringComparison.Ordinal));

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (Records is null)
            return;
        var rulesRead = RuleKind is { } ruleKind ? Records.GetObjectsAsync(ruleKind, cancellationToken) : Task.FromResult(new PurviewRead([]));
        var read = await Records.GetObjectsAsync(Kind, cancellationToken);
        var rules = await rulesRead;
        Note = read.Note ?? rules.Note;
        if (Create is not null || RuleKind?.Creates is { Count: > 0 })
            KnownTypes = await Records.GetSensitiveTypeNamesAsync(cancellationToken);
        var open = _all.Where(c => c.IsExpanded).Select(c => c.Item.Id).ToHashSet();
        IEnumerable<PurviewObject> items = Kind.Graph is not null ? read.Items : read.Items.OrderBy(i => i.Raw.TryGetProperty("Priority", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : int.MaxValue)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);
        _all = [.. items.Select(i => new PurviewObjectCard(i, Kind, this, rules.Items) { IsExpanded = open.Contains(i.Id) || Kind.Single })];
        Show();
    }

    // ---- The page's organization-wide action -------------------------------------------------

    public bool HasAction => Kind.Action is not null && CanEdit;
    public string? ActionDescription => Kind.Action?.Description;

    ChangeTarget ActionTarget => RunPurviewAction.Organization(Kind.Action?.Title ?? "");

    public string ActionText => Kind.Action is not { } a ? "" : Pipeline?.PendingFor(ActionTarget.Id, "action:" + a.Cmdlet) is not null ? $"Undo {a.Title}" : a.Title;

    [RelayCommand]
    void RunAction()
    {
        if (Kind.Action is not { } a || Pipeline is null || !CanEdit)
            return;
        if (Pipeline.PendingFor(ActionTarget.Id, "action:" + a.Cmdlet) is { } pending)
            Pipeline.Unstage(pending);
        else
            Pipeline.Stage(new RunPurviewAction(ActionTarget, a.Title, a.Cmdlet, a.Done));
        OnPropertyChanged(nameof(ActionText));
    }
}

/// <summary>A Security &amp; Compliance page of Purview kinds (most hold one; the file plan holds six).</summary>
public sealed class PurviewPageViewModel(IReadOnlyList<PurviewObjectsViewModel> kinds)
{
    public IReadOnlyList<PurviewObjectsViewModel> Kinds { get; } = kinds;

    public void Shown()
    {
        foreach (var k in Kinds.Where(k => !k.IsLoaded && !k.IsLoading))
            _ = k.Refresh();
    }
}
