using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>Something to tick in a short list (a label a policy publishes).</summary>
public sealed partial class TickItem(string label, string? detail, bool isChecked) : ObservableObject
{
    public string Label => label;
    public string? Detail => detail;
    public bool HasDetail => !string.IsNullOrEmpty(detail);

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;
}

/// <summary>How long and what then: the part of a policy or label that says what happens to content.</summary>
public sealed partial class RetentionSettingEditor : ObservableObject
{
    public const string KeepForever = "keepforever";
    public const string KeepFor = "keepfor";
    public const string KeepThenDelete = "keepthendelete";
    public const string DeleteAfter = "deleteafter";
    public const string ClassifyOnly = "classify";

    public static IReadOnlyList<EditorChoice> Units { get; } =
    [
        new("365", "Years", ""),
        new("30", "Months", ""),
        new("1", "Days", ""),
    ];

    public RetentionSettingEditor(bool forLabel, string? action, int? days, string basedOn, bool canChangeAction)
    {
        CanChangeAction = canChangeAction;
        Actions =
        [
            new(KeepFor, "Keep for a Period", "Content can't be deleted for good until the period ends; after that nothing happens."),
            new(KeepThenDelete, "Keep for a Period, Then Delete", "Kept for the period, then deleted."),
            new(KeepForever, "Keep Forever", "Content can't be deleted for good, ever."),
            new(DeleteAfter, "Delete After a Period", "Nothing is kept; content older than the period is deleted."),
            .. forLabel ? [new EditorChoice(ClassifyOnly, "Only Classify", "The label marks content; nothing is kept or deleted.")] : Array.Empty<EditorChoice>(),
        ];
        BasedOnChoices =
        [
            new("CreationAgeInDays", "When It Was Created", ""),
            new("ModificationAgeInDays", "When It Was Last Changed", ""),
            .. forLabel ? [new EditorChoice("TaggedAgeInDays", "When It Was Labeled", ""), new EditorChoice("EventAgeInDays", "When an Event Happens", "")] : Array.Empty<EditorChoice>(),
        ];
        Action = Actions.First(a => a.Value == (action switch
        {
            null when forLabel => ClassifyOnly,
            "Delete" => DeleteAfter,
            "KeepAndDelete" => KeepThenDelete,
            _ => days is null ? KeepForever : KeepFor,
        }));
        var whole = days ?? 7 * 365;
        Unit = whole % 365 == 0 ? Units[0] : whole % 30 == 0 ? Units[1] : Units[2];
        Amount = (whole / int.Parse(Unit.Value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
        BasedOn = BasedOnChoices.FirstOrDefault(b => b.Value == basedOn) ?? BasedOnChoices[0];
    }

    public IReadOnlyList<EditorChoice> Actions { get; }
    public IReadOnlyList<EditorChoice> BasedOnChoices { get; }

    /// <summary>A label's action and basis are fixed once it's made (Purview's rule).</summary>
    public bool CanChangeAction { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPeriod))]
    public partial EditorChoice Action { get; set; }

    public bool HasPeriod => Action.Value is KeepFor or KeepThenDelete or DeleteAfter;

    [ObservableProperty]
    public partial string Amount { get; set; }

    [ObservableProperty]
    public partial EditorChoice Unit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEventBased))]
    public partial EditorChoice BasedOn { get; set; }

    public bool IsEventBased => BasedOn.Value == "EventAgeInDays";

    public string? Problem => HasPeriod && (!int.TryParse(Amount, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) || n < 1 || Days > 36500)
        ? "Give the period as a whole number (up to 100 years)." : null;

    public int? Days => HasPeriod && int.TryParse(Amount, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n)
        ? n * int.Parse(Unit.Value, CultureInfo.InvariantCulture) : null;

    /// <summary>Purview's action word, or null for a label that only classifies.</summary>
    public string? ActionValue => Action.Value switch
    {
        ClassifyOnly => null,
        DeleteAfter => "Delete",
        KeepThenDelete => "KeepAndDelete",
        _ => "Keep",
    };

    public RetentionSetting? Setting => ActionValue is { } a ? new RetentionSetting(a, Action.Value == KeepForever ? null : Days, BasedOn.Value) : null;
}

/// <summary>
/// A new or changed retention policy (8f-3): what kind (keep or delete content, publish labels, or
/// apply a label), where (chosen places of each kind, or adaptive scopes), and what it does.
/// Saving puts it in Pending changes and closes the popup.
/// </summary>
public sealed partial class RetentionPolicyEditorViewModel : ObservableObject
{
    readonly ChangePipeline _pipeline;
    readonly Action _close;
    readonly RetentionPolicy? _policy;
    readonly string[] _taken;

    public static IReadOnlyList<EditorChoice> Kinds { get; } =
    [
        new(nameof(RetentionPolicyKind.Retention), "Keep or Delete Content", "Keep content for a time (or forever), delete it after a time, or both."),
        new(nameof(RetentionPolicyKind.PublishLabels), "Publish Labels", "Let people choose retention labels in Outlook, SharePoint and OneDrive."),
        new(nameof(RetentionPolicyKind.AutoApplyLabel), "Apply a Label Automatically", "Label content that matches a query (Microsoft 365 E5 or E5 Compliance)."),
    ];

    public static IReadOnlyList<EditorChoice> WhereModes { get; } =
    [
        new("places", "Chosen Places", "Pick mailboxes, sites, groups and Teams."),
        new("scopes", "Adaptive Scopes", "Follow queries for users, groups or sites as people come and go (Microsoft 365 E5 or E5 Compliance)."),
    ];

    public RetentionPolicyEditorViewModel(ChangePipeline pipeline, RetentionDirectory directory, IDirectoryIndex index, Func<IReadOnlyList<SiteSummary>> sites,
        Action close, RetentionPolicy? policy = null)
    {
        _pipeline = pipeline;
        _close = close;
        _policy = policy;
        _taken = [.. directory.Policies.Select(p => p.Name)];
        IsNew = policy is null;
        Name = policy?.Name ?? "";
        Description = policy?.Comment ?? "";
        Kind = Kinds.First(k => k.Value == (policy?.Kind ?? RetentionPolicyKind.Retention).ToString());
        WhereMode = WhereModes[policy?.Scopes.Count > 0 ? 1 : 0];
        Setting = new RetentionSettingEditor(false, policy?.Setting?.Action ?? "Keep", policy is null ? 7 * 365 : policy.Setting?.Days, policy?.Setting?.BasedOn ?? "CreationAgeInDays", true);
        var mailboxes = EdPlaces.MailboxOptions(index).ToArray();
        IEnumerable<PickOption> GroupOptions(bool teamsOnly) => index.AllGroups.Where(g => g.Kind == GroupKind.Microsoft365 && g.Mail is not null && (!teamsOnly || g.IsTeam))
            .Select(PickOption.Of);
        IReadOnlyList<ChangeTarget> Current(string kind) => policy?.Locations.GetValueOrDefault(kind) is { } raw
            ? RetentionPlaces.PickKind(kind) == "site" ? EdPlaces.Sites(raw, sites()) : EdPlaces.Mailboxes(raw, index)
            : [];
        Places =
        [
            new PlacesEditor("mailbox", RetentionPlaces.Label(RetentionPlaces.Exchange).ToUpperInvariant(), true, () => mailboxes, Current(RetentionPlaces.Exchange)),
            new PlacesEditor("site", RetentionPlaces.Label(RetentionPlaces.SharePoint).ToUpperInvariant(), true,
                () => sites().Where(s => s.Kind != SiteKind.OneDrive).Select(EdPlaces.Option), Current(RetentionPlaces.SharePoint)),
            new PlacesEditor("site", RetentionPlaces.Label(RetentionPlaces.OneDrive).ToUpperInvariant(), true,
                () => sites().Where(s => s.Kind == SiteKind.OneDrive).Select(EdPlaces.Option), Current(RetentionPlaces.OneDrive)),
            new PlacesEditor("mailbox", RetentionPlaces.Label(RetentionPlaces.Groups).ToUpperInvariant(), true, () => GroupOptions(false), Current(RetentionPlaces.Groups)),
            new PlacesEditor("mailbox", RetentionPlaces.Label(RetentionPlaces.TeamsChannels).ToUpperInvariant(), true, () => GroupOptions(true), Current(RetentionPlaces.TeamsChannels)),
            new PlacesEditor("mailbox", RetentionPlaces.Label(RetentionPlaces.TeamsChats).ToUpperInvariant(), true, () => mailboxes, Current(RetentionPlaces.TeamsChats)),
        ];
        PublicFolders = policy?.Locations.ContainsKey(RetentionPlaces.PublicFolders) == true;
        Scopes = [.. directory.Scopes.Select(s => new TickItem(s.Name, $"{RetentionFields.ScopeType(s.LocationType)} where {s.Query}", policy?.Scopes.Contains(s.Name) == true))];
        Labels = [.. directory.Labels.Where(l => !l.IsPendingDeletion).Select(l => new TickItem(l.Name, l.WhatText, policy?.Labels.Contains(l.Name) == true))];
        AutoLabels = [.. directory.Labels.Where(l => !l.IsPendingDeletion).Select(l => new EditorChoice(l.Name, l.Name, l.WhatText))];
        AutoLabel = AutoLabels.FirstOrDefault(l => policy?.Labels.Contains(l.Value) == true) ?? AutoLabels.FirstOrDefault();
        Query = policy?.Query ?? "";
    }

    public bool IsNew { get; }
    public string SaveText => IsNew ? "Add to Pending Changes" : "Save to Pending Changes";

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRetention), nameof(IsPublish), nameof(IsAutoApply))]
    public partial EditorChoice Kind { get; set; }

    public bool IsRetention => Kind.Value == nameof(RetentionPolicyKind.Retention);
    public bool IsPublish => Kind.Value == nameof(RetentionPolicyKind.PublishLabels);
    public bool IsAutoApply => Kind.Value == nameof(RetentionPolicyKind.AutoApplyLabel);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesPlaces), nameof(UsesScopes))]
    public partial EditorChoice WhereMode { get; set; }

    public bool UsesPlaces => WhereMode.Value == "places";
    public bool UsesScopes => !UsesPlaces;

    public IReadOnlyList<PlacesEditor> Places { get; }

    [ObservableProperty]
    public partial bool PublicFolders { get; set; }

    public IReadOnlyList<TickItem> Scopes { get; }
    public bool HasScopes => Scopes.Count > 0;

    public RetentionSettingEditor Setting { get; }

    public IReadOnlyList<TickItem> Labels { get; }
    public IReadOnlyList<EditorChoice> AutoLabels { get; }
    public bool HasLabels => Labels.Count > 0;

    [ObservableProperty]
    public partial EditorChoice? AutoLabel { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    static readonly string[] PlaceKinds =
        [RetentionPlaces.Exchange, RetentionPlaces.SharePoint, RetentionPlaces.OneDrive, RetentionPlaces.Groups, RetentionPlaces.TeamsChannels, RetentionPlaces.TeamsChats];

    public RetentionPolicyDraft Draft()
    {
        var places = new Dictionary<string, IReadOnlyList<ChangeTarget>>(StringComparer.Ordinal);
        if (UsesPlaces)
        {
            for (var i = 0; i < PlaceKinds.Length; i++)
                if (Places[i].Value is { Count: > 0 } chosen)
                    places[PlaceKinds[i]] = chosen;
            if (PublicFolders)
                places[RetentionPlaces.PublicFolders] = [EdChange.Everywhere("mailbox")];
        }
        var kind = Enum.Parse<RetentionPolicyKind>(Kind.Value);
        return new RetentionPolicyDraft(kind, Description.Trim() is { Length: > 0 } d ? d : null, places,
            UsesScopes ? [.. Scopes.Where(s => s.IsChecked).Select(s => s.Label)] : [],
            kind == RetentionPolicyKind.Retention ? Setting.Setting : null,
            kind switch
            {
                RetentionPolicyKind.PublishLabels => [.. Labels.Where(l => l.IsChecked).Select(l => l.Label)],
                RetentionPolicyKind.AutoApplyLabel => AutoLabel is { } l ? [l.Value] : [],
                _ => [],
            },
            kind == RetentionPolicyKind.AutoApplyLabel ? Query.Trim() : null);
    }

    /// <summary>What Purview would refuse.</summary>
    public IReadOnlyList<string> Check()
    {
        var problems = new List<string>();
        if (IsNew && EDiscovery.NameProblem(Name, _taken) is { } name)
            problems.Add(name);
        var draft = Draft();
        if (draft.Places.Count == 0 && draft.Scopes.Count == 0)
            problems.Add("Choose where the policy applies.");
        if (draft.Places.Keys.Any(RetentionPlaces.IsTeams) && draft.Places.Keys.Any(k => !RetentionPlaces.IsTeams(k)))
            problems.Add("Teams messages need a policy of their own: choose either Teams places or the others, not both.");
        if (draft.Kind != RetentionPolicyKind.Retention && draft.Places.Keys.Any(RetentionPlaces.IsTeams))
            problems.Add("Labels can't be published to or applied in Teams messages.");
        if (draft.Kind == RetentionPolicyKind.Retention && Setting.Problem is { } period)
            problems.Add(period);
        if (draft.Kind == RetentionPolicyKind.PublishLabels && draft.Labels.Count == 0)
            problems.Add("Tick at least one label to publish.");
        if (draft.Kind == RetentionPolicyKind.AutoApplyLabel && draft.Labels.Count == 0)
            problems.Add("Choose the label to apply.");
        if (draft.Kind == RetentionPolicyKind.AutoApplyLabel && EDiscovery.QueryProblem(draft.Query ?? "") is { } query)
            problems.Add(query);
        if (Places.Any(p => p.IsChosen && p.Chips.Count == 0))
            problems.Add("A place set to Chosen has nothing picked; pick some or choose None.");
        return problems;
    }

    [RelayCommand]
    void Save()
    {
        Problems = Check();
        if (Problems.Count > 0)
            return;
        var draft = Draft();
        if (_policy is null)
            _pipeline.Stage(new CreateRetentionPolicy(RetentionPages.PolicyTarget(Name.Trim()), draft));
        else
        {
            var change = new UpdateRetentionPolicy(RetentionPages.PolicyTarget(_policy.Name), RetentionPages.DraftOf(_policy), draft);
            if (!change.IsNoChange)
                _pipeline.Stage(change);
        }
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}

/// <summary>A new or changed retention label. Once made, Purview lets only its words, period and reviewers change.</summary>
public sealed partial class RetentionLabelEditorViewModel : ObservableObject
{
    readonly ChangePipeline _pipeline;
    readonly Action _close;
    readonly RetentionLabel? _label;
    readonly string[] _taken;

    public RetentionLabelEditorViewModel(ChangePipeline pipeline, RetentionDirectory directory, IDirectoryIndex index, Action close, RetentionLabel? label = null)
    {
        _pipeline = pipeline;
        _close = close;
        _label = label;
        _taken = [.. directory.Labels.Select(l => l.Name)];
        IsNew = label is null;
        Name = label?.Name ?? "";
        Description = label?.Comment ?? "";
        Notes = label?.Notes ?? "";
        IsRecord = label?.IsRecord ?? false;
        Setting = new RetentionSettingEditor(true, label is null ? "KeepAndDelete" : label.Action, label is null ? 7 * 365 : label.Days, label?.BasedOn ?? "CreationAgeInDays", IsNew);
        EventTypes = [.. directory.EventTypes.Select(t => new EditorChoice(t.Name, t.Name, t.Comment ?? ""))];
        EventType = EventTypes.FirstOrDefault(t => t.Value == label?.EventType) ?? EventTypes.FirstOrDefault();
        foreach (var r in label?.Reviewers ?? [])
            Reviewers.Add(new PlaceChip(new ChangeTarget("user", r, r, r), c => Reviewers.Remove(c)));
        ReviewerPicker = new PersonPicker("Add a reviewer: type a name", () =>
        {
            var have = Reviewers.Select(c => c.Place.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return GroupAccess.People(index).Where(o => o.Address is not null && !have.Contains(o.Address));
        }, o => Reviewers.Add(new PlaceChip(o.Target("user"), c => Reviewers.Remove(c))));
    }

    public bool IsNew { get; }
    public string SaveText => IsNew ? "Add to Pending Changes" : "Save to Pending Changes";

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }

    [ObservableProperty]
    public partial bool IsRecord { get; set; }

    public RetentionSettingEditor Setting { get; }

    public IReadOnlyList<EditorChoice> EventTypes { get; }

    [ObservableProperty]
    public partial EditorChoice? EventType { get; set; }

    public System.Collections.ObjectModel.ObservableCollection<PlaceChip> Reviewers { get; } = [];

    public PersonPicker ReviewerPicker { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    public RetentionLabelDraft Draft() => new(Description.Trim() is { Length: > 0 } d ? d : null, Notes.Trim() is { Length: > 0 } n ? n : null,
        Setting.ActionValue, Setting.Action.Value == RetentionSettingEditor.KeepForever ? null : Setting.Days, Setting.BasedOn.Value,
        Setting.ActionValue is not null && IsRecord, Setting.IsEventBased ? EventType?.Value : null,
        [.. Reviewers.Select(r => r.Place.Address ?? r.Place.Name)]);

    public IReadOnlyList<string> Check()
    {
        var problems = new List<string>();
        if (IsNew && EDiscovery.NameProblem(Name, _taken) is { } name)
            problems.Add(name);
        if (Setting.Problem is { } period)
            problems.Add(period);
        if (Setting.IsEventBased && EventType is null)
            problems.Add("Choose the kind of event that starts the period (add one under Retention Events).");
        if (Reviewers.Count > 0 && Setting.ActionValue is not ("Delete" or "KeepAndDelete"))
            problems.Add("Reviewers only apply to labels that delete content.");
        return problems;
    }

    [RelayCommand]
    void Save()
    {
        Problems = Check();
        if (Problems.Count > 0)
            return;
        if (_label is null)
            _pipeline.Stage(new CreateRetentionLabel(RetentionPages.LabelTarget(Name.Trim()), Draft()));
        else
        {
            var change = new UpdateRetentionLabel(RetentionPages.LabelTarget(_label.Name), RetentionPages.DraftOf(_label), Draft());
            if (!change.IsNoChange)
                _pipeline.Stage(change);
        }
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}

/// <summary>A new or changed adaptive scope: who or what it covers, by query.</summary>
public sealed partial class AdaptiveScopeEditorViewModel : ObservableObject
{
    readonly ChangePipeline _pipeline;
    readonly Action _close;
    readonly AdaptiveScope? _scope;
    readonly string[] _taken;

    public static IReadOnlyList<EditorChoice> Types { get; } =
    [
        new("User", "Users", "People (and their mailboxes and OneDrives), by their properties."),
        new("Group", "Groups", "Microsoft 365 groups, by their properties."),
        new("Site", "Sites", "SharePoint sites, by their properties."),
    ];

    public AdaptiveScopeEditorViewModel(ChangePipeline pipeline, RetentionDirectory directory, Action close, AdaptiveScope? scope = null)
    {
        _pipeline = pipeline;
        _close = close;
        _scope = scope;
        _taken = [.. directory.Scopes.Select(s => s.Name)];
        IsNew = scope is null;
        Name = scope?.Name ?? "";
        Type = Types.FirstOrDefault(t => t.Value == scope?.LocationType) ?? Types[0];
        Query = scope?.Query ?? "";
        Description = scope?.Comment ?? "";
    }

    public bool IsNew { get; }
    public string SaveText => IsNew ? "Add to Pending Changes" : "Save to Pending Changes";

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueryHint))]
    public partial EditorChoice Type { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    public string QueryHint => Type.Value == "Site"
        ? "A keyword query on site properties, for example: SiteTemplate=GROUP AND RefinableString00=Finance"
        : "A filter on their properties, for example: Department -eq 'Finance' -and CountryOrRegion -eq 'US'";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    [RelayCommand]
    void Save()
    {
        var problems = new List<string>();
        if (IsNew && EDiscovery.NameProblem(Name, _taken) is { } name)
            problems.Add(name);
        if (Query.Trim().Length == 0)
            problems.Add("Give the query that picks who or what it covers.");
        Problems = problems;
        if (problems.Count > 0)
            return;
        var comment = Description.Trim() is { Length: > 0 } d ? d : null;
        if (_scope is null)
            _pipeline.Stage(new CreateAdaptiveScope(RetentionPages.ScopeTarget(Name.Trim()), Type.Value, Query.Trim(), comment));
        else
        {
            var change = new UpdateAdaptiveScope(RetentionPages.ScopeTarget(_scope.Name), _scope.Query ?? "", Query.Trim(), _scope.Comment, comment);
            if (!change.IsNoChange)
                _pipeline.Stage(change);
        }
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}

/// <summary>A new event type: a name and what kind of event it is.</summary>
public sealed partial class NewEventTypeViewModel(ChangePipeline pipeline, IEnumerable<string> taken, Action close) : ObservableObject
{
    readonly string[] _taken = [.. taken];

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    [RelayCommand]
    void Add()
    {
        Problems = EDiscovery.NameProblem(Name, _taken) is { } problem ? [problem] : [];
        if (Problems.Count > 0)
            return;
        pipeline.Stage(new CreateRetentionEventType(RetentionPages.EventTypeTarget(Name.Trim()), Description.Trim() is { Length: > 0 } d ? d : null));
        close();
    }

    [RelayCommand]
    void Cancel() => close();
}

/// <summary>
/// A new event: something happened (a contract ended, someone left) on a date, for content with
/// these asset IDs. Labeled content waiting for this kind of event starts its retention period.
/// </summary>
public sealed partial class NewRetentionEventViewModel : ObservableObject
{
    readonly ChangePipeline _pipeline;
    readonly Action _close;
    readonly string[] _taken;

    public NewRetentionEventViewModel(ChangePipeline pipeline, RetentionDirectory directory, Func<DateTimeOffset> now, Action close)
    {
        _pipeline = pipeline;
        _close = close;
        _taken = [.. directory.Events.Select(e => e.Name)];
        EventTypes = [.. directory.EventTypes.Select(t => new EditorChoice(t.Name, t.Name, t.Comment ?? ""))];
        EventType = EventTypes.FirstOrDefault();
        Date = now().LocalDateTime.Date;
    }

    public IReadOnlyList<EditorChoice> EventTypes { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial EditorChoice? EventType { get; set; }

    [ObservableProperty]
    public partial DateTime? Date { get; set; }

    [ObservableProperty]
    public partial string SharePointQuery { get; set; } = "";

    [ObservableProperty]
    public partial string ExchangeQuery { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    [RelayCommand]
    void Add()
    {
        var problems = new List<string>();
        if (EDiscovery.NameProblem(Name, _taken) is { } name)
            problems.Add(name);
        if (EventType is null)
            problems.Add("Choose the kind of event.");
        if (Date is null)
            problems.Add("Choose when it happened.");
        Problems = problems;
        if (problems.Count > 0)
            return;
        _pipeline.Stage(new CreateRetentionEvent(RetentionPages.EventTarget(Name.Trim()), EventType!.Value, new DateTimeOffset(Date!.Value.Date),
            ExchangeQuery.Trim() is { Length: > 0 } x ? x : null, SharePointQuery.Trim() is { Length: > 0 } s ? s : null,
            Description.Trim() is { Length: > 0 } d ? d : null));
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}
