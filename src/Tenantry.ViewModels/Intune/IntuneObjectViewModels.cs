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

/// <summary>
/// One Intune object as a card (an app, an update ring, a script…): its settings, named from the
/// kind's words or Graph's names and edited in place; for app protection, the apps it protects;
/// who it reaches; and Delete. Defaults and Microsoft's own items show their settings but don't go.
/// </summary>
public sealed partial class IntuneObjectCard : ObservableObject
{
    readonly IntuneObjectsViewModel _page;
    readonly IntuneKind _kind;
    JsonElement _raw;

    public IntuneObjectCard(IntuneObject item, IntuneKind kind, IntuneObjectsViewModel page, IDirectoryIndex index, IReadOnlyList<AssignmentFilter> filters)
    {
        Item = item;
        _kind = kind;
        _page = page;
        _raw = item.Raw;
        Target = IntuneChange.TargetFor(kind.ItemUrl(item.Id), item.Name);
        IsReadOnly = kind.ReadOnlyWhen?.Invoke(item.Raw) == true;
        // A new app Microsoft is still preparing refuses changes (seen live: "PublishingState is not 'Published'").
        IsPreparing = item.Raw.ValueKind == JsonValueKind.Object && item.Raw.TryGetProperty("publishingState", out var state)
            && state.GetString() is "processing" or "notPublished";
        IsDefault = kind.DefaultWhen?.Invoke(item.Raw) == true;
        var assignable = kind.Assign == AssignMode.Assign && !IsReadOnly && !IsDefault && !IsPreparing;
        Assignments = new AssignmentEditor(Target, kind.Title, item.Assignments, page.Pipeline, page.CanEdit && assignable, index, filters, kind.ForApps, kind.AssignProperty, kind.AssignItem);
        Build();
    }

    public IntuneObject Item { get; }
    public ChangeTarget Target { get; }
    public string Name => Item.Name;
    public string TypeText => Item.TypeText;
    public bool HasType => Item.TypeText.Length > 0;
    public string? Description => Item.Description;
    public bool HasDescription => Item.Description is not null;

    /// <summary>Microsoft keeps it (a built-in role): shown, not changed.</summary>
    public bool IsReadOnly { get; }

    /// <summary>A default that applies to everyone else: its settings change, but it isn't assigned or deleted.</summary>
    public bool IsDefault { get; }

    /// <summary>A new app Microsoft is still preparing: changes wait until it's published.</summary>
    public bool IsPreparing { get; }

    public string? KeptText => IsReadOnly ? "Microsoft's: it can't be changed."
        : IsPreparing ? "Microsoft is still preparing this app (it can take a few minutes). Refresh to change or assign it once it's ready."
        : IsDefault ? "The default: it applies to everyone no other reaches, so it isn't assigned or deleted." : null;
    public bool HasKeptText => KeptText is not null;

    public bool CanEdit => _page.CanEdit && !IsReadOnly && !IsPreparing;
    public bool CanDelete => _page.CanEdit && !IsReadOnly && !IsDefault;
    public bool HasAssignments => _kind.Assign != AssignMode.None && !IsDefault;
    public bool AssignmentsReadOnly => _kind.Assign == AssignMode.ReadOnly;
    public AssignmentEditor Assignments { get; }

    [ObservableProperty]
    public partial IReadOnlyList<SettingGroup> Groups { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<SummaryLineText> Others { get; private set; } = [];

    public bool HasOthers => Others.Count > 0;

    partial void OnOthersChanged(IReadOnlyList<SummaryLineText> value) => OnPropertyChanged(nameof(HasOthers));

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    bool _fullRead;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_fullRead)
            _ = ReadFullAsync();
    }

    [ObservableProperty]
    public partial string? InstallText { get; private set; }

    /// <summary>Lists leave some properties out (a script's content): opening reads it whole.</summary>
    async Task ReadFullAsync()
    {
        _fullRead = true;
        try
        {
            _raw = await _page.Records!.GetObjectAsync(_kind, Item.Id);
            Build();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note = $"Couldn't read it in full: {Friendly.Message(e)}";
        }
        if (_kind.InstallSummary && await _page.Records!.GetInstallSummaryAsync(Item.Id) is { } summary)
            InstallText = summary.Text;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "@odata.type", "id", "createdDateTime", "lastModifiedDateTime", "lastModifiedTime", "createdTime", "version", "assignments", "apps", "deviceStatuses", "userStatuses",
        "deviceStatusOverview", "userStatusOverview", "largeIcon", "installSummary", "localizedNotificationMessages", "rolePermissions", "deviceStates", "runSummary",
    };

    /// <summary>Settings as rows: plain properties, and one level into objects ("installExperience.runAsAccount"); the rest as lines.</summary>
    void Build()
    {
        var flat = new JsonObject();
        var defs = new List<SettingDef>();
        var others = new List<SummaryLineText>();
        void Add(string path, JsonElement value, string fallback)
        {
            var label = _kind.Label(path);
            if (label?.Hidden == true)
                return;
            var kind = label?.Kind ?? value.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => SettingKind.OnOff,
                JsonValueKind.Number when value.TryGetInt32(out _) => SettingKind.Number,
                JsonValueKind.String or JsonValueKind.Null => SettingKind.Text,
                JsonValueKind.Array when value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String) => SettingKind.List,
                _ => (SettingKind?)null,
            };
            var name = label?.Label ?? Display.TitleCase(SettingsCatalog.Words(fallback));
            if (kind is not { } k)
            {
                others.Add(new SummaryLineText(name, Summarize(value)));
                return;
            }
            flat[path] = JsonNode.Parse(value.GetRawText());
            var group = path is "displayName" or "name" or "description" ? "About" : path.Contains('.') ? Display.TitleCase(SettingsCatalog.Words(path.Split('.')[0])) : "Settings";
            defs.Add(new SettingDef(group, path, name, label?.Help ?? "", "intune", Item.ODataType, path, k, Choices: label?.Choices, Min: label?.Min ?? 0, Max: label?.Max ?? int.MaxValue, Unit: label?.Unit));
        }
        if (_raw.ValueKind == JsonValueKind.Object)
            foreach (var p in _raw.EnumerateObject())
            {
                if (Skipped.Contains(p.Name) || p.Name.Contains('@'))
                    continue;
                if (p.Value.ValueKind == JsonValueKind.Object && p.Value.EnumerateObject().Any())
                {
                    foreach (var inner in p.Value.EnumerateObject().Where(i => !i.Name.Contains('@')))
                        Add($"{p.Name}.{inner.Name}", inner.Value, inner.Name);
                    continue;
                }
                Add(p.Name, p.Value, p.Name);
            }
        var row = JsonDocument.Parse(flat.ToJsonString()).RootElement.Clone();
        var context = new SettingContext(_page.Pipeline, CanEdit ? Availability.Yes : Availability.No(IsReadOnly ? "Microsoft's item" : _page.EditReason),
            (def, value, oldText, newText, caution) => new SetIntuneSetting(Target, def.SetCmdlet, def.Parameter, def.Label, value, oldText, newText, caution), "Not set");
        Groups = SettingCards.Build(defs.OrderBy(d => d.Group == "About" ? 0 : d.Group == "Settings" ? 1 : 2), d => d.Read(row), context, Target, null);
        Others = others;
        ProtectedApps = AppIds();
        ProtectedText = string.Join(Environment.NewLine, ProtectedApps);
        // Messages a notification template sends.
        if (_raw.ValueKind == JsonValueKind.Object && _raw.TryGetProperty("localizedNotificationMessages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            Others = [.. Others, .. messages.EnumerateArray().Select(m => new SummaryLineText($"Message ({Str(m, "locale")})", $"{Str(m, "subject")}: {Str(m, "messageTemplate")}"))];
        if (_raw.ValueKind == JsonValueKind.Object && _raw.TryGetProperty("rolePermissions", out var perms) && perms.ValueKind == JsonValueKind.Array)
            Others = [.. Others, new SummaryLineText("Permissions", string.Join(", ", perms.EnumerateArray().SelectMany(r => r.TryGetProperty("resourceActions", out var ra) ? ra.EnumerateArray() : [])
                .SelectMany(a => a.TryGetProperty("allowedResourceActions", out var al) ? al.EnumerateArray().Select(x => x.GetString()) : []).Select(x => x?.Replace("Microsoft.Intune_", "", StringComparison.Ordinal))))];
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : null;

    static string Summarize(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.Array ? $"{value.GetArrayLength()} {(value.GetArrayLength() == 1 ? "entry" : "entries")}" : value.ToString();
        return text.Length > 160 ? text[..160] + "…" : text;
    }

    // ---- The apps an app protection policy protects ---------------------------------------

    public bool HasProtectedApps => _kind.ProtectedAppsPlatform is not null;

    public string ProtectedAppsHelp => _kind.ProtectedAppsPlatform == "ios"
        ? "Bundle IDs, one per line (like com.microsoft.Office.Outlook)." : "Package IDs, one per line (like com.microsoft.office.outlook).";

    public IReadOnlyList<string> ProtectedApps { get; private set; } = [];

    [ObservableProperty]
    public partial string ProtectedText { get; set; } = "";

    IReadOnlyList<string> AppIds() => _raw.ValueKind == JsonValueKind.Object && _raw.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array
        ? [.. apps.EnumerateArray().Select(a => a.TryGetProperty("mobileAppIdentifier", out var id) ? Str(id, "bundleId") ?? Str(id, "packageId") : null).OfType<string>()]
        : [];

    [RelayCommand]
    void CommitProtected()
    {
        if (!CanEdit || _kind.ProtectedAppsPlatform is not { } platform)
            return;
        var ids = ProtectedText.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var pipeline = _page.Pipeline!;
        if (pipeline.PendingFor(Target.Id, "apps") is { } pending)
            pipeline.Unstage(pending);
        var change = new SetProtectedApps(Target, platform, ProtectedApps, ids);
        if (!change.IsNoChange)
            pipeline.Stage(change);
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
            _page.Pipeline.Stage(new DeleteIntunePolicy(Target, _kind.Title, HasAssignments ? Assignments.Summary : Item.TypeText));
    }

    internal void Refresh() => OnPropertyChanged(nameof(DeleteText));
}

/// <summary>A box on the New form.</summary>
public sealed partial class CreateFieldRow(CreateField box) : ObservableObject
{
    public CreateField Field => box;
    public string Label => box.Label + (box.Required ? "" : " (Optional)");
    public string Placeholder => box.Placeholder ?? "";
    public bool IsText => box.Kind is SettingKind.Text or SettingKind.Number;
    public bool IsLong => box.Kind is SettingKind.LongText or SettingKind.Script or SettingKind.List;
    public bool IsChoice => box.Kind == SettingKind.Choice;
    public IReadOnlyList<SettingChoice> Choices { get; } = [.. (box.Choices ?? []).Select(c => new SettingChoice(c.Value, c.Label))];

    [ObservableProperty]
    public partial string Text { get; set; } = box.Kind == SettingKind.Choice ? "" : box.Default ?? "";

    [ObservableProperty]
    public partial SettingChoice? Choice { get; set; } = (box.Choices ?? []).Select(c => new SettingChoice(c.Value, c.Label)).FirstOrDefault(c => c.Value == box.Default)
        ?? (box.Choices ?? []).Select(c => new SettingChoice(c.Value, c.Label)).FirstOrDefault();

    /// <summary>The value for the body, or a problem.</summary>
    public (JsonNode? Value, string? Problem) Read()
    {
        var text = Text.Trim();
        if (box.Kind == SettingKind.Choice)
            return Choice is null ? (null, $"Choose {box.Label}.") : (JsonValue.Create(Choice.Value), null);
        if (text.Length == 0)
            return box.Required ? (null, $"Fill in {box.Label}.") : (null, null);
        return box.Kind switch
        {
            SettingKind.Number => int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) ? (JsonValue.Create(n), null) : (null, $"{box.Label} must be a whole number."),
            SettingKind.Script => (JsonValue.Create(SettingRow.Encode(Text)), null),
            SettingKind.List => (new JsonArray([.. Text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => (JsonNode)l)]), null),
            SettingKind.LongText => (JsonValue.Create(Text), null),
            _ => (JsonValue.Create(text), null),
        };
    }
}

/// <summary>
/// One Intune list (apps, update rings, scripts…) as a page of cards, read live when first shown:
/// a search of the list, New (what the kind can make, with its boxes) and every item's card.
/// </summary>
public sealed partial class IntuneObjectsViewModel : LivePage
{
    readonly IDirectoryIndex _index;
    readonly IIntunePolicyRecords? _filters;
    IReadOnlyList<IntuneObjectCard> _all = [];

    public IntuneObjectsViewModel(IntuneKind kind, IIntuneObjectRecords? records, ChangePipeline? pipeline, Availability canEdit, IDirectoryIndex index, IIntunePolicyRecords? filters)
        : base(pipeline, canEdit)
    {
        Kind = kind;
        Records = records;
        Pipeline = pipeline;
        _index = index;
        _filters = filters;
        CreateOptions = kind.Creates ?? [];
        CreateOption = CreateOptions.FirstOrDefault();
        if (pipeline is not null)
            pipeline.Changed += (_, _) =>
            {
                foreach (var c in _all)
                    c.Refresh();
            };
    }

    public IntuneKind Kind { get; }
    internal IIntuneObjectRecords? Records { get; }
    internal ChangePipeline? Pipeline { get; }
    public string Title => Kind.Title;
    public string About => Kind.About;

    public ObservableCollection<IntuneObjectCard> Cards { get; } = [];
    public bool IsEmpty => Cards.Count == 0 && IsLoaded && !HasError;
    public string EmptyText => _all.Count == 0 ? $"No {Kind.Title.ToLowerInvariant()} yet." : "Nothing matches.";

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    partial void OnFilterTextChanged(string value) => Show();

    void Show()
    {
        var words = FilterText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Cards.Clear();
        foreach (var c in _all.Where(c => words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || c.TypeText.Contains(w, StringComparison.OrdinalIgnoreCase))))
            Cards.Add(c);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    protected override bool Concerns(ChangeOp change) => change is IntuneChange i && (i.Path.StartsWith(Kind.Collection, StringComparison.Ordinal)
        || i is CreateIntuneObject c && c.Collection == Kind.Collection);

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (Records is null)
            return;
        var filters = Kind.Assign == AssignMode.Assign && _filters is not null ? await _filters.GetAssignmentFiltersAsync(cancellationToken) : [];
        var items = await Records.GetObjectsAsync(Kind, cancellationToken);
        var open = _all.Where(c => c.IsExpanded).Select(c => c.Item.Id).ToHashSet();
        _all = [.. items.OrderBy(i => Kind.DefaultWhen?.Invoke(i.Raw) == true).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => new IntuneObjectCard(i, Kind, this, _index, filters) { IsExpanded = open.Contains(i.Id) })];
        Show();
    }

    // ---- New ----------------------------------------------------------------------------------

    public IReadOnlyList<CreateOption> CreateOptions { get; }
    public bool CanCreate => CanEdit && CreateOptions.Count > 0;
    public bool HasManyOptions => CreateOptions.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreateDescription))]
    public partial CreateOption? CreateOption { get; set; }

    partial void OnCreateOptionChanged(CreateOption? value) => Fields = value is null ? [] : [.. value.Fields.Select(f => new CreateFieldRow(f))];

    public string? CreateDescription => CreateOption?.Description;

    [ObservableProperty]
    public partial IReadOnlyList<CreateFieldRow> Fields { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsAddOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddProblem))]
    public partial string? AddProblem { get; private set; }

    public bool HasAddProblem => AddProblem is not null;

    [RelayCommand]
    void ToggleAdd()
    {
        IsAddOpen = !IsAddOpen;
        AddProblem = null;
        OnCreateOptionChanged(CreateOption);
    }

    [RelayCommand]
    void Add()
    {
        if (CreateOption is not { } option || !CanEdit)
            return;
        var body = option.Body.DeepClone().AsObject();
        var then = option.Then?.Body.DeepClone().AsObject();
        foreach (var f in Fields)
        {
            var (value, problem) = f.Read();
            if (problem is not null)
            {
                AddProblem = problem;
                return;
            }
            if (value is null)
                continue;
            if (f.Field.Path.StartsWith("Then.", StringComparison.Ordinal) && then is not null)
                IntuneKinds.Set(then, f.Field.Path[5..], value);
            else
                IntuneKinds.Set(body, f.Field.Path, value);
        }
        var name = (string?)body[Kind.NameProperty] ?? (string?)body["displayName"] ?? "";
        if (_all.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            AddProblem = "One has that name already.";
            return;
        }
        if (then is not null)
            then["subject"] ??= name;
        var summary = string.Join("; ", Fields.Where(f => f.Field.Path is not ("displayName" or "name") && f.Field.Kind is not (SettingKind.Script or SettingKind.LongText))
            .Select(f => $"{f.Field.Label}: {(f.IsChoice ? f.Choice?.Label : f.Text.Trim())}").Where(s => !s.EndsWith(": ", StringComparison.Ordinal)));
        Pipeline!.Stage(new CreateIntuneObject(IntuneChange.TargetFor($"new:{Kind.Key}:{name}", name), Kind.Collection, option.Title, body,
            summary.Length == 0 ? option.Title : $"{option.Title}: {summary}", option.Then?.Path, then));
        AddProblem = null;
        IsAddOpen = false;
    }
}
