using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>One person checked against a dynamic rule, as the editor shows it.</summary>
public sealed record RuleTestRow(string Name, string Result, BadgeKind Kind, string? Problem)
{
    public bool HasProblem => Problem is not null;
}

/// <summary>
/// A group's own settings, editable: name, description, alias and privacy (as its kind has them),
/// how it gets its members (chosen, or by a rule that can be tested on people first), and Delete.
/// The same editor sits on the group's page and in the preview's Manage popup; every change goes
/// to Pending Changes first. A group synced from on-premises AD is changed there.
/// </summary>
public sealed partial class GroupSettingsEditor : ObservableObject, IDisposable
{
    GroupSummary _group;
    readonly IDirectoryIndex _index;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly IGroupRecords? _records;

    public GroupSettingsEditor(GroupSummary group, IDirectoryIndex index, ChangePipeline? pipeline, Availability canEdit, IGroupRecords? records,
        IUserRecords? users = null)
    {
        if (group.Kind is GroupKind.Microsoft365 or GroupKind.Distribution or GroupKind.MailEnabledSecurity)
            Mail = new GroupMailEditor(group, index, pipeline, canEdit, records, users);
        _group = group;
        _index = index;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _records = records;
        TestPicker = new PersonPicker("Person's name", () => GroupAccess.People(_index).Where(o => TestRows.All(r => r.Name != o.Name)), o => _ = TestAsync(o));
        Build();
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    ChangeTarget Target => GroupAccess.Target(_group);

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable && !_group.SyncedFromOnPremises;

    public string EditReason => _pipeline is null ? "This tenant can't be edited"
        : _group.SyncedFromOnPremises ? "Synced from on-premises Active Directory: change it there, and Entra Connect copies it up."
        : _canEdit.Reason ?? "";

    public bool IsSynced => _group.SyncedFromOnPremises;

    /// <summary>Its mail settings, read from Exchange when shown; null for a security group (no mail).</summary>
    public GroupMailEditor? Mail { get; }

    public bool HasMail => Mail is not null;

    // ---- Name, description, alias, privacy ---------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<FieldGroup> Details { get; private set; } = [];

    bool MailKind => _group.Kind is GroupKind.Microsoft365 or GroupKind.Distribution or GroupKind.MailEnabledSecurity;

    void Build()
    {
        var target = Target;
        var editable = CanEdit ? _canEdit : Availability.No(EditReason);
        EditableField Field(string label, string setting, string? current, bool required = false, Func<string, string?>? validate = null, string? hint = null,
            IReadOnlyList<FieldChoice>? choices = null) =>
            new(label, setting, current, target, _pipeline, editable, required, choices: choices, validate: validate, hint: hint,
                change: (old, next) => new SetGroupSetting(target, _group.Kind, setting, label, old, next), key: "setting:" + setting);
        var limit = GroupRules.NameLimit(_group.Kind);
        List<EditableField> about =
        [
            Field("Name", SetGroupSetting.DisplayName, _group.DisplayName, required: true,
                validate: v => v.Length > limit ? $"A name can be at most {limit} characters here." : null,
                hint: "How it appears in Outlook, Teams and the address list."),
            Field("Description", SetGroupSetting.Description, _group.Description, hint: "What it's for, shown to people who find it."),
        ];
        if (MailKind)
            about.Add(Field("Alias", SetGroupSetting.Alias, _group.MailNickname ?? _group.Mail?.Split('@')[0], required: true, validate: GroupRules.AliasProblem,
                hint: "The name it's known by inside Microsoft 365. Its email address doesn't change with it."));
        if (_group.Kind == GroupKind.Microsoft365)
            about.Add(Field("Privacy", SetGroupSetting.Visibility, _group.Visibility ?? "Public",
                choices: [new FieldChoice("Private", "Private: only members see what's inside"), new FieldChoice("Public", "Public: anyone in the organization can join and see it")],
                hint: "Members' access doesn't change; who else can join and read does."));
        Details = [new("Name and Description", "Info", about)];
        ResetRuleDraft();
    }

    // ---- Membership ------------------------------------------------------------------

    /// <summary>Microsoft 365 and security groups can be chosen by hand or by rule (lists can't).</summary>
    public bool HasMembership => _group.Kind is GroupKind.Microsoft365 or GroupKind.Security;

    SetMembershipRule? PendingRule => _pipeline?.PendingFor(_group.Id, "rule") as SetMembershipRule;

    /// <summary>The pending rule the draft was last reset from.</summary>
    SetMembershipRule? _lastPendingRule;

    public bool HasPendingRule => PendingRule is not null;

    /// <summary>The choice being made: true for by rule.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAssignedDraft), nameof(RuleChanged), nameof(RuleProblem), nameof(HasRuleProblem), nameof(ConvertNote), nameof(HasConvertNote))]
    [NotifyCanExecuteChangedFor(nameof(StageRuleCommand))]
    public partial bool IsDynamicDraft { get; set; }

    public bool IsAssignedDraft
    {
        get => !IsDynamicDraft;
        set => IsDynamicDraft = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuleChanged), nameof(RuleProblem), nameof(HasRuleProblem))]
    [NotifyCanExecuteChangedFor(nameof(StageRuleCommand))]
    public partial string RuleDraft { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuleChanged))]
    [NotifyCanExecuteChangedFor(nameof(StageRuleCommand))]
    public partial bool PausedDraft { get; set; }

    partial void OnRuleDraftChanged(string value) => TestRows.Clear();

    /// <summary>The draft starts from what's pending, else the group as it is.</summary>
    void ResetRuleDraft()
    {
        _lastPendingRule = PendingRule;
        if (PendingRule is { } p)
            (IsDynamicDraft, RuleDraft, PausedDraft) = (p.NewDynamic, p.NewRule ?? "", p.NewPaused);
        else
            (IsDynamicDraft, RuleDraft, PausedDraft) = (_group.IsDynamic, _group.MembershipRule ?? "", _group.RulePaused);
        OnPropertyChanged(nameof(IsAssignedDraft));
        OnPropertyChanged(nameof(HasPendingRule));
        OnPropertyChanged(nameof(CurrentMembershipText));
    }

    public string CurrentMembershipText => _group.IsDynamic
        ? $"By rule{(_group.RulePaused ? " (paused: members stay as they are)" : "")}. Entra keeps the members matching it."
        : "Assigned: owners and admins choose the members.";

    SetMembershipRule DraftChange() => new(Target, _group.Kind, _group.IsDynamic, _group.MembershipRule, _group.RulePaused,
        IsDynamicDraft, IsDynamicDraft ? RuleDraft.Trim() : _group.MembershipRule, IsDynamicDraft && PausedDraft);

    /// <summary>The draft differs from what's pending (or, with nothing pending, from the group).</summary>
    public bool RuleChanged => PendingRule is { } p
        ? p.NewDynamic != IsDynamicDraft || (IsDynamicDraft && (p.NewRule?.Trim() != RuleDraft.Trim() || p.NewPaused != PausedDraft))
        : !DraftChange().IsNoChange;

    public string? RuleProblem => IsDynamicDraft ? GroupRules.RuleShapeProblem(RuleDraft) : null;

    public bool HasRuleProblem => RuleProblem is not null && RuleDraft.Length > 0;

    public string? ConvertNote => IsDynamicDraft == _group.IsDynamic ? null
        : IsDynamicDraft ? "Members who don't match the rule will be removed, and people who match are added. Test it on a few people first."
        : "The current members stay; from then on owners and admins choose them.";

    public bool HasConvertNote => ConvertNote is not null;

    bool CanStageRule => CanEdit && RuleChanged && RuleProblem is null;

    [RelayCommand(CanExecute = nameof(CanStageRule))]
    void StageRule()
    {
        if (PendingRule is { } p)
            _pipeline!.Unstage(p);
        var change = DraftChange();
        if (!change.IsNoChange)
            _pipeline!.Stage(change);
    }

    [RelayCommand]
    void UndoRule()
    {
        if (PendingRule is { } p)
            _pipeline!.Unstage(p);
        ResetRuleDraft();
    }

    // ---- Testing a rule --------------------------------------------------------------

    public bool CanTest => _records is not null && IsDynamicDraft;

    public PersonPicker TestPicker { get; }

    public ObservableCollection<RuleTestRow> TestRows { get; } = [];

    [ObservableProperty]
    public partial bool IsTesting { get; private set; }

    async Task TestAsync(PickOption person)
    {
        if (_records is null)
            return;
        if (RuleProblem is { } problem)
        {
            TestRows.Add(new RuleTestRow(person.Name, "Can't check", BadgeKind.Warning, problem));
            return;
        }
        IsTesting = true;
        try
        {
            var result = await _records.TestRuleAsync(RuleDraft.Trim(), person.Id, person.Name);
            TestRows.Add(result.Matches switch
            {
                true => new RuleTestRow(person.Name, "Would be a member", BadgeKind.Success, null),
                false => new RuleTestRow(person.Name, "Wouldn't be a member", BadgeKind.Neutral, null),
                null => new RuleTestRow(person.Name, "Can't check", BadgeKind.Warning, result.Problem),
            });
        }
        finally
        {
            IsTesting = false;
        }
    }

    // ---- Delete ----------------------------------------------------------------------

    DeleteGroup? PendingDelete => _pipeline?.PendingFor(_group.Id, "delete") as DeleteGroup;

    public bool HasPendingDelete => PendingDelete is not null;

    public string DeleteText => $"Delete {GroupRules.Noun(_group.Kind)}";

    public string DeleteTip => !CanEdit ? EditReason
        : GroupRules.Restorable(_group.Kind)
            ? "Delete it (you type its name to confirm). It can be restored for 30 days from Deleted Groups."
            : "Delete it for good (you type its name to confirm). Distribution lists can't be restored.";

    [RelayCommand]
    void Delete()
    {
        if (!CanEdit)
            return;
        if (PendingDelete is { } p)
            _pipeline!.Unstage(p);
        else
            _pipeline!.Stage(new DeleteGroup(Target, _group.Kind,
                $"{GroupSummary.KindName(_group.Kind)} · {_group.Members.Count:N0} {(_group.Members.Count == 1 ? "member" : "members")}"));
    }

    // ---- Following the group and the tray --------------------------------------------

    public void Update(GroupSummary group)
    {
        _group = group;
        Build();
        Refresh();
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        foreach (var field in Details.SelectMany(g => g.Fields))
            field.Refresh();
        // Only a change to this group's membership slot moves the draft: a rule being typed stays.
        if (!ReferenceEquals(PendingRule, _lastPendingRule))
            ResetRuleDraft();
        Refresh();
    }

    void Refresh()
    {
        foreach (var name in new[]
                 {
                     nameof(CanEdit), nameof(EditReason), nameof(IsSynced), nameof(HasMembership), nameof(HasPendingRule), nameof(RuleChanged),
                     nameof(CurrentMembershipText), nameof(CanTest), nameof(HasPendingDelete), nameof(DeleteText), nameof(DeleteTip),
                 })
            OnPropertyChanged(name);
        StageRuleCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDynamicDraftChanged(bool value) => OnPropertyChanged(nameof(CanTest));

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
        Mail?.Dispose();
    }
}
