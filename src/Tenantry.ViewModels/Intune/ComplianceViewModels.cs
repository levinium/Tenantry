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

/// <summary>One action for noncompliance as a row: what, after how many days, and (for an email) which message.</summary>
public sealed partial class ComplianceActionRow : ObservableObject
{
    readonly CompliancePolicyCard _card;
    bool _loading = true;

    public ComplianceActionRow(ComplianceAction action, CompliancePolicyCard card, IReadOnlyList<EditorChoice> templates)
    {
        Action = action.Action;
        _card = card;
        Templates = templates;
        DaysText = (action.GraceHours / 24.0).ToString("0.##", CultureInfo.CurrentCulture);
        Template = templates.FirstOrDefault(t => t.Value == action.TemplateId) ?? templates.FirstOrDefault();
        CcGroupIds = action.CcGroupIds;
        _loading = false;
    }

    public string Action { get; }
    public string Title => ComplianceAction.ActionText(Action);
    public bool IsEmail => Action == "notification";
    public IReadOnlyList<EditorChoice> Templates { get; }
    public IReadOnlyList<string>? CcGroupIds { get; }

    /// <summary>Marking noncompliant is always there (Intune needs it); the rest can go.</summary>
    public bool CanRemove => _card.CanEdit && Action != "block";

    [ObservableProperty]
    public partial string DaysText { get; set; }

    [ObservableProperty]
    public partial EditorChoice? Template { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    partial void OnTemplateChanged(EditorChoice? value)
    {
        if (!_loading)
            _card.StageActions();
    }

    [RelayCommand]
    void CommitDays() => _card.StageActions();

    [RelayCommand]
    void Remove() => _card.RemoveAction(this);

    /// <summary>The action as typed, or null (with a problem shown) when the days aren't 0 to 365.</summary>
    internal ComplianceAction? Read()
    {
        if (!double.TryParse(DaysText, NumberStyles.Float, CultureInfo.CurrentCulture, out var days) || days < 0 || days > 365)
        {
            Problem = "Days from 0 to 365 (0 is at once).";
            return null;
        }
        Problem = null;
        return new ComplianceAction(Action, (int)Math.Round(days * 24), IsEmail ? Template?.Value : null, CcGroupIds);
    }
}

/// <summary>
/// A compliance policy as a card: its platform, who it reaches and how devices stand; opened, its
/// settings (each staged as changed), what happens to devices that don't comply, its assignments
/// and the devices it reaches, and Delete.
/// </summary>
public sealed partial class CompliancePolicyCard : ObservableObject
{
    readonly CompliancePoliciesViewModel _page;
    readonly IReadOnlyList<EditorChoice> _templates;

    public CompliancePolicyCard(CompliancePolicy policy, CompliancePoliciesViewModel page, IDirectoryIndex index, IReadOnlyList<AssignmentFilter> filters,
        IReadOnlyList<NotificationTemplate> templates)
    {
        Policy = policy;
        _page = page;
        Target = IntuneChange.TargetFor($"deviceManagement/deviceCompliancePolicies/{policy.Id}", policy.Name);
        var context = new SettingContext(page.Pipeline, page.CanEdit ? Availability.Yes : Availability.No(page.EditReason),
            (def, value, oldText, newText, caution) => new SetIntuneSetting(Target, def.SetCmdlet, def.Parameter, def.Label, value, oldText, newText, caution), "Not configured");
        var about = new SettingDef[]
        {
            new("About", "displayName", "Name", "", "intune", policy.ODataType, "displayName", SettingKind.Text),
            new("About", "description", "Description", "", "intune", policy.ODataType, "description", SettingKind.Text),
        };
        var defs = CompliancePlatforms.Settings(policy.ODataType);
        // An unset switch is off in Intune (Graph sends false), so turning one on and off again changes nothing.
        Groups = SettingCards.Build([.. about, .. defs], d => d.Read(policy.Raw) ?? (d.Kind == SettingKind.OnOff ? JsonValue.Create(false) : null), context, Target, null);
        // Settings Intune has that Tenantry doesn't list: shown so nothing is hidden, left as they are.
        var known = defs.Select(d => d.Parameter).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Others = policy.Raw.ValueKind == JsonValueKind.Object
            ? [.. policy.Raw.EnumerateObject().Where(p => !known.Contains(p.Name) && !CompliancePlatforms.Housekeeping.Contains(p.Name) && !p.Name.Contains('@')
                    && p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False) && !(p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() == 0))
                .Select(p => new SummaryLineText(Display.TitleCase(SplitWords(p.Name)), p.Value.ValueKind == JsonValueKind.True ? "Required" : p.Value.ToString()))]
            : [];
        _templates = [.. templates.Select(t => new EditorChoice(t.Id, t.Name))];
        Assignments = new AssignmentEditor(Target, "Compliance Policy", policy.Assignments, page.Pipeline, page.CanEdit, index, filters);
        FillActions();
    }

    static string SplitWords(string name) => string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : i == 0 ? char.ToUpperInvariant(c).ToString() : c.ToString()));

    public CompliancePolicy Policy { get; }
    public ChangeTarget Target { get; }
    public bool CanEdit => _page.CanEdit;

    public string Name => Policy.Name;
    public string Platform => Policy.Platform;
    public string? Description => Policy.Description;
    public bool HasDescription => Policy.Description is not null;

    public IReadOnlyList<SettingGroup> Groups { get; }

    /// <summary>Other settings the policy has, read only.</summary>
    public IReadOnlyList<SummaryLineText> Others { get; }
    public bool HasOthers => Others.Count > 0;

    public AssignmentEditor Assignments { get; }

    public string CountsText => Policy.Counts is not { } c ? "Device counts unavailable"
        : c.Total == 0 ? "No devices report on it yet"
        : string.Join(" · ", new[] { $"{c.Compliant:N0} compliant", c.NonCompliant > 0 ? $"{c.NonCompliant:N0} not compliant" : null, c.Error > 0 ? $"{c.Error:N0} with errors" : null,
            c.Conflict > 0 ? $"{c.Conflict:N0} in conflict" : null, c.Pending > 0 ? $"{c.Pending:N0} not reported yet" : null }.OfType<string>());

    public BadgeKind CountsKind => Policy.Counts is { NonCompliant: > 0 } or { Error: > 0 } ? BadgeKind.Warning : Policy.Counts is { Total: > 0 } ? BadgeKind.Success : BadgeKind.Neutral;

    public string ProblemText => Policy.Counts is not { } c ? ""
        : c.NonCompliant + c.Error > 0 ? $"{c.NonCompliant + c.Error:N0} Need Attention"
        : c.Compliant > 0 && c.Compliant == c.Total ? "All Compliant" : "";
    public bool HasProblemText => ProblemText.Length > 0;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    // ---- Actions for noncompliance -----------------------------------------------------------

    public ObservableCollection<ComplianceActionRow> Actions { get; } = [];

    static readonly string[] Addable = ["notification", "pushNotification", "remoteLock", "retire"];

    public IReadOnlyList<EditorChoice> ActionChoices => [.. Addable.Where(a => Actions.All(r => r.Action != a) && (a != "notification" || _templates.Count > 0)
        && (a != "remoteLock" || Policy.ODataType is CompliancePlatforms.IOS or CompliancePlatforms.AndroidAdmin or CompliancePlatforms.AndroidWorkProfile or CompliancePlatforms.AndroidDeviceOwner))
        .Select(a => new EditorChoice(a, ComplianceAction.ActionText(a)))];

    public bool CanAddAction => CanEdit && ActionChoices.Count > 0;

    [RelayCommand]
    void AddAction(EditorChoice? choice)
    {
        if (choice is null || !CanEdit)
            return;
        Actions.Add(new ComplianceActionRow(new ComplianceAction(choice.Value, choice.Value == "retire" ? 24 * 30 : 24, _templates.FirstOrDefault()?.Value), this, _templates));
        StageActions();
    }

    internal void RemoveAction(ComplianceActionRow row)
    {
        Actions.Remove(row);
        StageActions();
    }

    IReadOnlyList<ComplianceAction> CurrentActions => Policy.Actions.Count > 0 ? Policy.Actions : [new ComplianceAction("block", 0)];

    void FillActions()
    {
        var shown = (_page.Pipeline?.PendingFor(Target.Id, "actions") as SetComplianceActions)?.New ?? CurrentActions;
        Actions.Clear();
        foreach (var a in shown)
            Actions.Add(new ComplianceActionRow(a, this, _templates));
        OnPropertyChanged(nameof(ActionChoices));
        OnPropertyChanged(nameof(CanAddAction));
    }

    internal void StageActions()
    {
        OnPropertyChanged(nameof(ActionChoices));
        OnPropertyChanged(nameof(CanAddAction));
        if (!CanEdit)
            return;
        var read = Actions.Select(r => r.Read()).ToArray();
        if (read.Any(r => r is null))
            return;
        var pipeline = _page.Pipeline!;
        if (pipeline.PendingFor(Target.Id, "actions") is { } pending)
            pipeline.Unstage(pending);
        var change = new SetComplianceActions(Target, Policy.ODataType, CurrentActions, [.. read.OfType<ComplianceAction>()]);
        if (!change.IsNoChange)
            pipeline.Stage(change);
    }

    // ---- Devices -------------------------------------------------------------------------------

    public ObservableCollection<DeviceStatusRow> Devices { get; } = [];

    [ObservableProperty]
    public partial bool DevicesLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevicesNote))]
    public partial string? DevicesNote { get; private set; }

    public bool HasDevicesNote => DevicesNote is not null;

    [ObservableProperty]
    public partial bool DevicesShown { get; private set; }

    [RelayCommand]
    async Task ShowDevices()
    {
        if (DevicesShown)
        {
            DevicesShown = false;
            return;
        }
        DevicesShown = true;
        DevicesLoading = true;
        DevicesNote = null;
        try
        {
            var statuses = await _page.StatusesAsync(Policy.Id);
            Devices.Clear();
            foreach (var s in statuses)
                Devices.Add(new DeviceStatusRow(s));
            if (statuses.Count == 0)
                DevicesNote = "No devices report on this policy yet.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            DevicesNote = $"Couldn't read its devices: {Friendly.Message(e)}";
        }
        finally
        {
            DevicesLoading = false;
        }
    }

    // ---- Delete --------------------------------------------------------------------------------

    public string DeleteText => _page.Pipeline?.PendingFor(Target.Id, "delete") is not null ? "Undo Delete" : "Delete Policy";

    [RelayCommand]
    void Delete()
    {
        if (!CanEdit)
            return;
        if (_page.Pipeline!.PendingFor(Target.Id, "delete") is { } pending)
            _page.Pipeline.Unstage(pending);
        else
            _page.Pipeline.Stage(new DeleteIntunePolicy(Target, "Compliance Policy", $"{Platform}; {Assignments.Summary}"));
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(DeleteText));
        if (_page.Pipeline?.PendingFor(Target.Id, "actions") is null && !Actions.Select(a => a.Read()).SequenceEqual(CurrentActions))
            FillActions();
    }
}

/// <summary>A label and value, as a plain line.</summary>
public sealed record SummaryLineText(string Label, string Value);

/// <summary>A device under a compliance policy.</summary>
public sealed record DeviceStatusRow(DeviceComplianceStatus Status)
{
    public string Name => Status.DeviceName;
    public string Detail => string.Join(" · ", new[] { Status.UserName, Status.LastReported is { } at ? $"reported {at.ToLocalTime():d MMM, HH:mm}" : null }.OfType<string>());
    public string StateText => DeviceComplianceStatus.StatusText(Status.Status);
    public BadgeKind StateKind => Status.Status switch
    {
        "compliant" or "remediated" => BadgeKind.Success,
        "nonCompliant" or "error" => BadgeKind.Danger,
        "inGracePeriod" or "conflict" => BadgeKind.Warning,
        _ => BadgeKind.Neutral,
    };
}

/// <summary>
/// Compliance Policies: every policy as a card, the organization's compliance settings, and a new
/// policy. Read live from Intune when first shown.
/// </summary>
public sealed partial class CompliancePoliciesViewModel : LivePage
{
    readonly IIntunePolicyRecords? _records;
    readonly IDirectoryIndex _index;

    public CompliancePoliciesViewModel(IIntunePolicyRecords? records, ChangePipeline? pipeline, Availability canEdit, IDirectoryIndex index) : base(pipeline, canEdit)
    {
        _records = records;
        _index = index;
        Pipeline = pipeline;
        if (pipeline is not null)
            pipeline.Changed += (_, _) =>
            {
                foreach (var c in Cards)
                    c.Refresh();
            };
    }

    internal ChangePipeline? Pipeline { get; }

    public ObservableCollection<CompliancePolicyCard> Cards { get; } = [];

    public ObservableCollection<SettingGroup> OrgSettings { get; } = [];

    protected override bool Concerns(ChangeOp change) => change is IntuneChange i && (i.Path.StartsWith("deviceManagement/deviceCompliancePolicies", StringComparison.Ordinal)
        || i.Path == "deviceManagement" || i is CreateCompliancePolicy);

    internal Task<IReadOnlyList<DeviceComplianceStatus>> StatusesAsync(string id) => _records!.GetComplianceStatusesAsync(id);

    static readonly IReadOnlyList<SettingDef> OrgDefs =
    [
        new("Organization", "secureByDefault", "Devices with No Compliance Policy Are Not Compliant",
            "Off: a device no policy reaches counts as compliant. On: it doesn't, so Conditional Access can block it.", "intune", "", "settings.secureByDefault", CautionWhen: true),
        new("Organization", "deviceComplianceCheckinThresholdDays", "Compliance Status Lasts",
            "A device that hasn't checked in for this long becomes noncompliant.", "intune", "", "settings.deviceComplianceCheckinThresholdDays", SettingKind.Number, Min: 1, Max: 120, Unit: "days"),
        new("Organization", "enhancedJailBreak", "Enhanced Jailbreak Detection", "iOS devices check more often, using location services.", "intune", "", "settings.enhancedJailBreak"),
    ];

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_records is null)
            return;
        var filters = _records.GetAssignmentFiltersAsync(cancellationToken);
        var templates = _records.GetNotificationTemplatesAsync(cancellationToken);
        var settings = _records.GetComplianceSettingsAsync(cancellationToken);
        var policies = await _records.GetCompliancePoliciesAsync(cancellationToken);
        var open = Cards.Where(c => c.IsExpanded).Select(c => c.Policy.Id).ToHashSet();
        Cards.Clear();
        foreach (var p in policies.OrderBy(p => p.Platform).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Cards.Add(new CompliancePolicyCard(p, this, _index, await filters, await templates) { IsExpanded = open.Contains(p.Id) });
        var org = await settings;
        OrgSettings.Clear();
        foreach (var g in SettingCards.Build(OrgDefs, d => d.Read(org.ValueKind == JsonValueKind.Object ? Rename(org, d.Key) : default),
                     new SettingContext(Pipeline, CanEdit ? Availability.Yes : Availability.No(EditReason),
                         (def, value, oldText, newText, caution) => new SetIntuneSetting(IntuneChange.Organization("Compliance Settings"), "", def.Parameter, def.Label, value, oldText, newText, caution)),
                     IntuneChange.Organization("Compliance Settings"), null))
            OrgSettings.Add(g);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>A settings field under the name its definition reads ("settings.secureByDefault" reads "secureByDefault").</summary>
    static JsonElement Rename(JsonElement settings, string key) =>
        JsonDocument.Parse(new JsonObject { [$"settings.{key}"] = settings.TryGetProperty(key, out var v) ? JsonNode.Parse(v.GetRawText()) : null }.ToJsonString()).RootElement.Clone();

    public bool IsEmpty => Cards.Count == 0;

    // ---- A new policy ------------------------------------------------------------------------

    public static IReadOnlyList<EditorChoice> Platforms { get; } = [.. CompliancePlatforms.All.Select(p => new EditorChoice(p.ODataType, p.Title))];

    [ObservableProperty]
    public partial bool IsAddOpen { get; set; }

    [ObservableProperty]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    public partial EditorChoice NewPlatform { get; set; } = Platforms[0];

    [ObservableProperty]
    public partial string NewGraceDays { get; set; } = "1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddProblem))]
    public partial string? AddProblem { get; private set; }

    public bool HasAddProblem => AddProblem is not null;

    [RelayCommand]
    void ToggleAdd() => IsAddOpen = !IsAddOpen;

    [RelayCommand]
    void Add()
    {
        var name = NewName.Trim();
        AddProblem = name.Length == 0 ? "Give the policy a name."
            : Cards.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? "A policy has that name already."
            : !int.TryParse(NewGraceDays, NumberStyles.Integer, CultureInfo.CurrentCulture, out var days) || days < 0 || days > 365 ? "Days from 0 to 365."
            : null;
        if (AddProblem is not null || !CanEdit)
            return;
        Pipeline!.Stage(new CreateCompliancePolicy(IntuneChange.TargetFor($"new:{name}", name), NewPlatform.Value, null, int.Parse(NewGraceDays, CultureInfo.CurrentCulture), []));
        NewName = "";
        NewGraceDays = "1";
        IsAddOpen = false;
    }
}
