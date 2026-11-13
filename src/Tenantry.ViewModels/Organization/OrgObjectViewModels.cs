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

namespace Tenantry.ViewModels.Organization;

/// <summary>Lines read under an item (a domain's DNS records), with a title.</summary>
public sealed record OrgExtraLines(string Title, IReadOnlyList<string> Lines)
{
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>An action an item offers (Verify, Archive, Restore), staged in Pending Changes.</summary>
public sealed partial class OrgActionButton(OrgAction action, OrgObjectCard card) : ObservableObject
{
    public OrgAction Action { get; } = action;
    public string Description => Action.Done;
    public string Text => card.PendingAction(Action) ? $"Undo {Action.Title}" : Action.Title;

    [RelayCommand]
    void Run() => card.ToggleAction(Action);

    internal void Refresh() => OnPropertyChanged(nameof(Text));
}

/// <summary>
/// One organization item as a card (a settings page, a domain, a team, a policy): its settings
/// edited in place, what it shows but can't change here, what's read under it, its actions,
/// its children (a team's channels) and Delete.
/// </summary>
public sealed partial class OrgObjectCard : ObservableObject
{
    readonly OrgObjectsViewModel _page;
    readonly OrgKind _kind;

    public OrgObjectCard(OrgObject item, OrgKind kind, OrgObjectsViewModel page)
    {
        Item = item;
        _kind = kind;
        _page = page;
        Target = OrgChange.TargetFor(kind.Key, item.Id, item.Name);
        IsReadOnly = kind.IsReadOnly || kind.ReadOnlyWhen?.Invoke(item.Raw) == true;
        Actions = page.CanEdit ? [.. (kind.Actions ?? []).Where(a => a.When?.Invoke(item.Raw) != false).Select(a => new OrgActionButton(a, this))] : [];
        Build();
    }

    public OrgObject Item { get; }
    public ChangeTarget Target { get; }
    public string Name => Item.Name;

    public string? Subtitle => _kind.SubtitleOf is { } of ? of(Item.Raw) : _kind.SubtitleProperty is { } p && OrgPath.Get(Item.Raw, p) is { ValueKind: JsonValueKind.String } s && s.GetString() is { Length: > 0 } text
        ? (p is "status" or "membershipType" or "category" ? Display.TitleCase(SettingsCatalog.Words(text)) : text) : null;

    public bool HasSubtitle => Subtitle is not null;

    public bool IsReadOnly { get; }
    public string? KeptText => IsReadOnly ? _kind.ReadOnlyText is { } t ? t.Length == 0 ? null : t : "Shown as Microsoft has it." : null;
    public bool HasKeptText => KeptText is not null;
    public bool CanEdit => _page.CanEdit && !IsReadOnly;
    public bool CanDelete => _page.CanEdit && _kind.CanDelete && _kind.KeepWhen?.Invoke(Item.Raw) != true;

    [ObservableProperty]
    public partial IReadOnlyList<SettingGroup> Groups { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    public partial IReadOnlyList<SummaryLineText> Details { get; private set; } = [];

    public bool HasDetails => Details.Count > 0;

    public IReadOnlyList<OrgActionButton> Actions { get; }
    public bool HasActions => Actions.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExtras))]
    public partial IReadOnlyList<OrgExtraLines> Extras { get; private set; } = [];

    public bool HasExtras => Extras.Count > 0;

    /// <summary>A team's channels, read when the card first opens.</summary>
    public OrgObjectsViewModel? Children { get; private set; }
    public bool HasChildren => Children is not null;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    bool _opened;

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _opened)
            return;
        _opened = true;
        _ = OpenedAsync();
    }

    async Task OpenedAsync()
    {
        if (_kind.Children is { } childKey && _page.Records is { } records)
        {
            Children = new OrgObjectsViewModel(OrgKinds.Get(childKey), records, _page.Pipeline, _page.CanEditAvailability, Item.Url);
            OnPropertyChanged(nameof(Children));
            OnPropertyChanged(nameof(HasChildren));
            await Children.Refresh();
        }
        if (_kind.Extras is { Count: > 0 } extras && _page.Records is { } reader)
            Extras = [.. await Task.WhenAll(extras.Select(async e => new OrgExtraLines(e.Title, await reader.GetOrgExtraAsync(_kind, Item, e))))];
    }

    // ---- Settings --------------------------------------------------------------------------------

    /// <summary>The Teams admin API's housekeeping, never shown.</summary>
    static readonly HashSet<string> Housekeeping = new(StringComparer.OrdinalIgnoreCase)
    {
        "Identity", "Key", "XsAnyElements", "XsAnyAttributes", "Element", "ConfigId", "DataSource", "ScopeClass", "Anchor", "@odata.type", "@odata.context", "id",
    };

    /// <summary>A Teams policy's settings, from what it has: switches, numbers and text become settings; lists and objects are shown.</summary>
    static IReadOnlyList<OrgField> AutoFields(JsonElement raw) => raw.ValueKind != JsonValueKind.Object ? [] :
        [.. raw.EnumerateObject().Where(p => !Housekeeping.Contains(p.Name)).Select(p => new OrgField(p.Name, Display.TitleCase(SettingsCatalog.Words(p.Name)),
            p.Value.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => SettingKind.OnOff,
                JsonValueKind.Number => SettingKind.Number,
                JsonValueKind.String or JsonValueKind.Null => SettingKind.Text,
                _ => SettingKind.List,
            },
            ReadOnly: p.Value.ValueKind is JsonValueKind.Object || p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String),
            Group: p.Name == "Description" ? "About" : "Settings"))
            .OrderBy(f => f.Group == "About" ? 0 : 1).ThenBy(f => f.Label, StringComparer.CurrentCultureIgnoreCase)];

    IReadOnlyList<OrgField> Fields => _kind.AutoFields ? AutoFields(Item.Raw) : _kind.Fields ?? [];

    void Build()
    {
        var flat = new JsonObject();
        var defs = new List<SettingDef>();
        var details = new List<SummaryLineText>();
        foreach (var field in Fields)
        {
            var shown = Shown(field, OrgPath.Get(Item.Raw, field.Path));
            if (!CanEdit || field.ReadOnly)
            {
                if (Words(field, shown) is { Length: > 0 } words)
                    details.Add(new SummaryLineText(field.Label, words));
                continue;
            }
            flat[field.Path] = shown?.DeepClone();
            defs.Add(new SettingDef(field.Group ?? "Settings", field.Path, field.Label, field.Help ?? "", "", "", field.Path, field.Kind, Choices: field.Choices,
                Min: field.Min, Max: field.Max, Unit: field.Unit));
        }
        var row = JsonDocument.Parse(flat.ToJsonString()).RootElement.Clone();
        var context = new SettingContext(_page.Pipeline, CanEdit ? Availability.Yes : Availability.No(_page.EditReason), Make, "Not set");
        var order = Fields.Select(f => f.Group ?? "Settings").Distinct().ToList();
        Groups = SettingCards.Build(defs.OrderBy(d => order.IndexOf(d.Group)), d => d.Read(row), context, Target, null);
        Details = details;
    }

    /// <summary>The value as the editor shows it.</summary>
    internal static JsonNode? Shown(OrgField field, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return field.Kind == SettingKind.List ? new JsonArray() : field.Value == OrgValue.FirstOfList ? JsonValue.Create("") : null;
        switch (field.Value)
        {
            case OrgValue.StringBool:
                return bool.TryParse(value.ToString(), out var b) ? JsonValue.Create(b) : null;
            case OrgValue.FirstOfList:
                return JsonValue.Create(value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(e => e.GetString()).FirstOrDefault() ?? "" : value.ToString());
        }
        return field.Kind switch
        {
            SettingKind.OnOff => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? JsonValue.Create(value.GetBoolean()) : null,
            SettingKind.Number => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? JsonValue.Create(n)
                : int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? JsonValue.Create(parsed) : null,
            SettingKind.List => value.ValueKind == JsonValueKind.Array
                ? new JsonArray([.. value.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : i.ValueKind == JsonValueKind.Object ? NameIn(i) : i.ToString())
                    .OfType<string>().Where(s => s.Length > 0).Select(s => (JsonNode)s)])
                : JsonValue.Create(value.ToString()),
            _ => value.ValueKind == JsonValueKind.String ? JsonValue.Create(value.GetString()) : JsonValue.Create(value.ToString()),
        };
    }

    static string? NameIn(JsonElement o) => o.TryGetProperty("displayName", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()
        : o.TryGetProperty("Id", out var i) ? i.ToString() : null;

    static string Words(OrgField field, JsonNode? value) => value switch
    {
        null => "",
        JsonArray a => a.Count == 0 ? "" : string.Join(", ", a.Select(i => i?.ToString())),
        JsonValue v when field.Kind == SettingKind.OnOff && v.TryGetValue<bool>(out var on) => on ? "Yes" : "No",
        JsonValue v when field.Choices?.FirstOrDefault(c => c.Value.Equals(v.ToString(), StringComparison.OrdinalIgnoreCase)).Label is { Length: > 0 } label => label,
        JsonValue v when field.Path.EndsWith("DateTime", StringComparison.Ordinal) && DateTimeOffset.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            => d.ToLocalTime().ToString("d MMM yyyy, h:mm tt", CultureInfo.CurrentCulture),
        _ => value.ToString() is { Length: > 300 } text ? text[..300] + "…" : value.ToString(),
    };

    /// <summary>What the object is sent for a value as the editor shows it.</summary>
    internal static JsonNode? ToSent(OrgField field, JsonNode? value) => field.Value switch
    {
        OrgValue.StringBool => JsonValue.Create(value is JsonValue v && v.TryGetValue<bool>(out var on) && on ? "true" : "false"),
        OrgValue.FirstOfList => value?.ToString() is { Length: > 0 } one ? new JsonArray(one) : new JsonArray(),
        // An emptied box clears the setting; a name/value pair keeps "" (Graph refuses null there).
        _ when field.Kind is SettingKind.Text or SettingKind.LongText && string.IsNullOrWhiteSpace(value?.ToString()) => field.Path.Contains('[') ? JsonValue.Create("") : null,
        _ => value?.DeepClone(),
    };

    ChangeOp Make(SettingDef def, JsonNode? value, string oldText, string newText, bool caution)
    {
        var field = Fields.First(f => f.Path == def.Key);
        return new SetOrgSetting(Target, _kind.Api, Item.Url, _kind.Write, Display.TitleCase(_kind.Noun), field.Path, field.Label, ToSent(field, value), oldText, newText,
            caution || field.Caution);
    }

    // ---- Actions and Delete --------------------------------------------------------------------

    internal bool PendingAction(OrgAction action) => _page.Pipeline?.PendingFor(Target.Id, "action:" + action.Title) is not null;

    internal void ToggleAction(OrgAction action)
    {
        if (_page.Pipeline is not { } pipeline || !_page.CanEdit)
            return;
        if (pipeline.PendingFor(Target.Id, "action:" + action.Title) is { } pending)
        {
            pipeline.Unstage(pending);
            return;
        }
        var body = action.Body is null ? null
            : JsonNode.Parse(action.Body.ToJsonString().Replace("{month}", DateTimeOffset.UtcNow.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), StringComparison.Ordinal))!.AsObject();
        pipeline.Stage(new RunOrgAction(Target, _kind.Api, action.Path is null ? Item.Url : $"{Item.Url}/{action.Path}", action.Method, action.Title, action.Done, body, action.Caution));
    }

    public string DeleteText => _page.Pipeline?.PendingFor(Target.Id, "delete") is not null ? "Undo Delete" : "Delete";

    [RelayCommand]
    void Delete()
    {
        if (!CanDelete)
            return;
        if (_page.Pipeline!.PendingFor(Target.Id, "delete") is { } pending)
            _page.Pipeline.Unstage(pending);
        else
            _page.Pipeline.Stage(new DeleteOrgObject(Target, _kind.Api, _kind.DeleteUrl?.Replace("{id}", Uri.EscapeDataString(Item.Id), StringComparison.Ordinal) ?? Item.Url,
                Display.TitleCase(_kind.Noun), Subtitle ?? _kind.Title));
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(DeleteText));
        foreach (var a in Actions)
            a.Refresh();
    }
}

/// <summary>New: what a kind can make, with its boxes.</summary>
public sealed partial class OrgCreateForm : ObservableObject
{
    readonly OrgObjectsViewModel _page;

    public OrgCreateForm(OrgObjectsViewModel page)
    {
        _page = page;
        Options = page.Kind.Creates ?? [];
        Option = Options.FirstOrDefault();
    }

    public IReadOnlyList<OrgCreate> Options { get; }
    public bool HasManyOptions => Options.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description), nameof(ButtonText))]
    public partial OrgCreate? Option { get; set; }

    partial void OnOptionChanged(OrgCreate? value) => Fields = value is null ? [] : [.. value.Fields.Select(f => new CreateFieldRow(f))];

    public string ButtonText => $"New {Option?.Title ?? ""}".Trim();
    public string? Description => Option?.Description is { Length: > 0 } d ? d : null;

    [ObservableProperty]
    public partial IReadOnlyList<CreateFieldRow> Fields { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    [RelayCommand]
    void Toggle()
    {
        IsOpen = !IsOpen;
        Problem = null;
        OnOptionChanged(Option);
    }

    [RelayCommand]
    void Add()
    {
        if (Option is not { } option || _page.Pipeline is not { } pipeline || !_page.CanEdit)
            return;
        var body = option.Fixed.DeepClone().AsObject();
        foreach (var f in Fields)
        {
            var (value, problem) = f.Read();
            if (problem is not null)
            {
                Problem = problem;
                return;
            }
            if (value is null)
                continue;
            if (value is JsonValue choice && f.Field.Kind == SettingKind.Choice && choice.ToString() is "true" or "false")
                value = JsonValue.Create(choice.ToString() == "true");
            OrgPath.Set(body, f.Field.Path, value);
        }
        var name = option.NameField.Length > 0 ? OrgPath.Get(JsonDocument.Parse(body.ToJsonString()).RootElement, option.NameField).ToString() : _page.Kind.Title;
        if (option.NameField.Length > 0 && _page.Names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Problem = "One has that name already.";
            return;
        }
        var collection = _page.CollectionUrl;
        var url = option.Url ?? (option.Put ? _page.Kind.AddressOf(name, collection) : collection);
        var summary = string.Join("; ", Fields.Where(f => f.Field.Path != option.NameField)
            .Select(f => $"{f.Field.Label}: {(f.IsChoice ? f.Choice?.Label : f.Text.Trim().Replace("\n", ", ", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal))}")
            .Where(s => !s.EndsWith(": ", StringComparison.Ordinal)));
        pipeline.Stage(new CreateOrgObject(OrgChange.TargetFor(_page.Kind.Key, name, name), _page.Kind.Api, url, option.Title, body,
            summary.Length == 0 ? option.Title : summary, option.Put));
        Problem = null;
        IsOpen = false;
    }
}

/// <summary>
/// One organization kind as cards, read live when its page is first shown: what it is, New, a
/// search, and every item's card (a settings page shows its one card open).
/// </summary>
public sealed partial class OrgObjectsViewModel : LivePage
{
    IReadOnlyList<OrgObjectCard> _all = [];

    public OrgObjectsViewModel(OrgKind kind, IOrgObjectRecords? records, ChangePipeline? pipeline, Availability canEdit, string? parentUrl = null) : base(pipeline, canEdit)
    {
        Kind = kind;
        Records = records;
        Pipeline = pipeline;
        ParentUrl = parentUrl;
        CanEditAvailability = canEdit;
        Create = kind.Creates is { Count: > 0 } ? new OrgCreateForm(this) : null;
        if (pipeline is not null)
            pipeline.Changed += (_, _) =>
            {
                foreach (var c in _all)
                    c.Refresh();
            };
    }

    public OrgKind Kind { get; }
    internal IOrgObjectRecords? Records { get; }
    internal ChangePipeline? Pipeline { get; }
    internal Availability CanEditAvailability { get; }
    internal new string EditReason => base.EditReason;

    /// <summary>For a child kind (a team's channels): the parent's address.</summary>
    public string? ParentUrl { get; }

    internal string CollectionUrl => ParentUrl is null ? Kind.Collection : Kind.Collection.Replace("{parent}", ParentUrl, StringComparison.Ordinal);

    internal IEnumerable<string> Names => _all.Select(c => c.Name);

    public string Title => Kind.Title;
    public string About => Kind.About;
    public OrgCreateForm? Create { get; }
    public bool CanCreate => CanEdit && Create is not null && (!Kind.OneOnly || _all.Count == 0) && IsLoaded;
    public bool IsChild => ParentUrl is not null;

    public ObservableCollection<OrgObjectCard> Cards { get; } = [];
    public bool IsEmpty => Cards.Count == 0 && IsLoaded && !HasError && !HasNote;
    public string EmptyText => _all.Count == 0 ? $"No {Kind.Title.ToLowerInvariant()} yet." : "Nothing matches.";
    public bool ShowFilter => !Kind.Single && _all.Count > 5;

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
        OnPropertyChanged(nameof(ShowFilter));
        OnPropertyChanged(nameof(CanCreate));
    }

    protected override bool Concerns(ChangeOp change) => change is OrgChange o && o.Item.Id.StartsWith(Kind.Key + ":", StringComparison.Ordinal);

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (Records is null)
            return;
        var read = await Records.GetOrgObjectsAsync(Kind, ParentUrl, cancellationToken);
        Note = read.Note;
        var open = _all.Where(c => c.IsExpanded).Select(c => c.Item.Id).ToHashSet();
        _all = [.. read.Items.Select(i => new OrgObjectCard(i, Kind, this) { IsExpanded = open.Contains(i.Id) || Kind.Single || read.Items.Count == 1 && Kind.OneOnly })];
        Show();
    }
}

/// <summary>An Organization page of kinds, top to bottom (most hold one).</summary>
public sealed class OrgPageViewModel(IReadOnlyList<OrgObjectsViewModel> kinds)
{
    public IReadOnlyList<OrgObjectsViewModel> Kinds { get; } = kinds;

    public void Shown()
    {
        foreach (var k in Kinds.Where(k => !k.IsLoaded && !k.IsLoading))
            _ = k.Refresh();
    }
}
