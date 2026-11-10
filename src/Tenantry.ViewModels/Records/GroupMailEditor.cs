using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>An on/off mail setting, staged as soon as it's switched (switched back, it leaves Pending Changes).</summary>
public sealed partial class MailToggle(string label, string description, bool isOn, bool isPending, bool canEdit, string tip, Action<bool> set) : ObservableObject
{
    public string Label => label;
    public string Description => description;
    public bool IsPending => isPending;
    public bool CanEdit => canEdit;
    public string Tip => tip;

    public bool IsOn
    {
        get => isOn;
        set
        {
            if (value == isOn || !canEdit)
                return;
            isOn = value;
            set(value);
        }
    }
}

/// <summary>
/// A list of people or groups on a group's mail settings (who may send, moderators, delegates),
/// each with Remove (or Undo), and an Add button. The rows show the list as it will be once pending
/// changes apply.
/// </summary>
public sealed class MailPeopleList(string title, string description, string addText, string emptyText, PersonPicker picker)
{
    public string Title => title;
    public string Description => description;
    public string AddText => addText;
    public string EmptyText => emptyText;
    public PersonPicker Picker => picker;
    public ObservableCollection<PersonRow> Rows { get; } = [];
    public bool IsEmpty => Rows.Count == 0;
}

/// <summary>One of the group's addresses, with Remove and Make Primary.</summary>
public sealed partial class GroupAddressRow(string address, bool isPrimary, string? pendingText, bool canEdit, Action toggle, Action makePrimary) : ObservableObject
{
    public string Address => address;
    public bool IsPrimary => isPrimary;
    public string? PendingText => pendingText;
    public bool HasPending => pendingText is not null;
    public bool CanRemove => canEdit && !isPrimary;
    public bool CanMakePrimary => canEdit && !isPrimary && pendingText is null;

    [RelayCommand]
    void Remove() => toggle();

    [RelayCommand]
    void MakePrimary() => makePrimary();
}

/// <summary>
/// A mail-enabled group's mail settings (G2), read from Exchange when the editor is shown: its
/// addresses, delivery (address list, outside senders, Microsoft 365 subscription options), who can
/// send, message approval, Send As and Send on Behalf, and the MailTip. Every change is staged.
/// </summary>
public sealed partial class GroupMailEditor : ObservableObject, IDisposable
{
    readonly GroupSummary _group;
    readonly IDirectoryIndex _index;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly IGroupRecords? _records;
    readonly IUserRecords? _users;
    bool _started;

    public GroupMailEditor(GroupSummary group, IDirectoryIndex index, ChangePipeline? pipeline, Availability canEdit, IGroupRecords? records, IUserRecords? users)
    {
        _group = group;
        _index = index;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _records = records;
        _users = users;
        PersonPicker Picker(string parameter) => new("Name or address", () => Candidates(parameter), o => AddToList(parameter, o));
        AcceptList = new("Only These Can Send", "Leave it empty to let anyone send (inside the organization, and outside if allowed above).",
            "Add a Sender", "Anyone can send.", Picker(AcceptParameter));
        RejectList = new("Blocked Senders", "Mail from these is refused.", "Block a Sender", "Nobody is blocked.", Picker(RejectParameter));
        Moderators = new("Moderators", "They approve each message before it reaches the group.", "Add a Moderator",
            IsList ? "None: the group's owners approve." : "None yet.", Picker(ModeratorsParameter));
        Bypass = new("Don't Need Approval", "Their messages reach the group straight away.", "Add Someone", "Everyone needs approval.", Picker(BypassParameter));
        SendOnBehalf = new("Send on Behalf", "They send as \"on behalf of\" the group; replies come to the group.", "Add Someone", "Nobody.", Picker(OnBehalfParameter));
        SendAs = new("Send As", "Their mail looks as if the group sent it.", "Add Someone", "Nobody.",
            new PersonPicker("Name or address", SendAsCandidates, o => _pipeline?.Stage(new SetGroupSendAs(Target, _group.Kind, Person(o), false, true))));
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    const string AcceptParameter = "AcceptMessagesOnlyFromSendersOrMembers";
    const string RejectParameter = "RejectMessagesFromSendersOrMembers";
    const string ModeratorsParameter = "ModeratedBy";
    const string BypassParameter = "BypassModerationFromSendersOrMembers";
    const string OnBehalfParameter = "GrantSendOnBehalfTo";

    ChangeTarget Target => GroupAccess.Target(_group);

    public bool IsUnified => _group.Kind == GroupKind.Microsoft365;

    /// <summary>Lists and mail-enabled security groups (Set-DistributionGroup), which have a few more settings.</summary>
    public bool IsList => _group.Kind is GroupKind.Distribution or GroupKind.MailEnabledSecurity;

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable && !_group.SyncedFromOnPremises;

    string EditReason => _pipeline is null ? "This tenant can't be edited"
        : _group.SyncedFromOnPremises ? "Synced from on-premises Active Directory: change it there."
        : _canEdit.Reason ?? "";

    // ---- Reading -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? LoadError { get; private set; }

    public bool HasError => LoadError is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial GroupMail? Mail { get; private set; }

    public bool IsReady => Mail is not null && !IsLoading;

    IReadOnlyList<string> _domains = [];

    /// <summary>Reads the settings the first time the editor is shown (a preview doesn't read them).</summary>
    public async Task LoadAsync(bool again = false)
    {
        if ((_started && !again) || _records is null)
            return;
        _started = true;
        IsLoading = true;
        LoadError = null;
        try
        {
            var domains = _users?.GetDomainsAsync() ?? Task.FromResult<IReadOnlyList<string>>([]);
            Mail = await _records.GetGroupMailAsync(_group);
            _domains = await domains;
            NewDomain ??= _domains.FirstOrDefault();
            OnPropertyChanged(nameof(Domains));
            BuildFields();
            Rebuild();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read the mail settings: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    Task Reload() => LoadAsync(again: true);

    // ---- Addresses ---------------------------------------------------------------------

    public ObservableCollection<GroupAddressRow> Addresses { get; } = [];

    public IReadOnlyList<string> Domains => _domains;

    [ObservableProperty]
    public partial bool IsAddingAddress { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddressProblem))]
    [NotifyCanExecuteChangedFor(nameof(AddAddressCommand))]
    public partial string NewAlias { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddressProblem))]
    [NotifyCanExecuteChangedFor(nameof(AddAddressCommand))]
    public partial string? NewDomain { get; set; }

    string? NewAddress => NewAlias.Trim().Length == 0 || NewDomain is null ? null : $"{NewAlias.Trim()}@{NewDomain}";

    public string? AddressProblem => NewAlias.Trim().Length == 0 ? null
        : GroupRules.AliasProblem(NewAlias.Trim()) is { } bad ? bad.Replace("An alias", "The part before the @", StringComparison.Ordinal)
        : NewAddress is { } a && AddressInUse(a) ? $"{a} is already in use."
        : null;

    bool AddressInUse(string address) =>
        _index.AllUsers.Any(u => string.Equals(u.Mail, address, StringComparison.OrdinalIgnoreCase) || string.Equals(u.UserPrincipalName, address, StringComparison.OrdinalIgnoreCase)
                                 || (u.Mailbox?.Aliases.Contains(address, StringComparer.OrdinalIgnoreCase) ?? false))
        || _index.AllGroups.Any(g => string.Equals(g.Mail, address, StringComparison.OrdinalIgnoreCase) || g.Aliases.Contains(address, StringComparer.OrdinalIgnoreCase))
        || (Mail?.Addresses.Any(a => string.Equals(a[(a.IndexOf(':') + 1)..], address, StringComparison.OrdinalIgnoreCase)) ?? false);

    [RelayCommand]
    void StartAddAddress()
    {
        NewAlias = "";
        IsAddingAddress = true;
    }

    [RelayCommand]
    void CancelAddAddress() => IsAddingAddress = false;

    bool CanAddAddress => CanEdit && NewAddress is not null && AddressProblem is null;

    [RelayCommand(CanExecute = nameof(CanAddAddress))]
    void AddAddress()
    {
        _pipeline!.Stage(new SetGroupAddress(Target, _group.Kind, NewAddress!, false, true));
        IsAddingAddress = false;
    }

    // ---- Settings as switches ----------------------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<MailToggle> Delivery { get; private set; } = [];

    [ObservableProperty]
    public partial MailToggle? Moderation { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<EditableField> TextSettings { get; private set; } = [];

    [ObservableProperty]
    public partial EditableField? Notices { get; private set; }

    /// <summary>Distribution lists only: whether people can join or leave by themselves (mail-enabled security groups are always closed).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<EditableField> Membership { get; private set; } = [];

    public bool HasMembership => Membership.Count > 0;

    partial void OnMembershipChanged(IReadOnlyList<EditableField> value) => OnPropertyChanged(nameof(HasMembership));

    public MailPeopleList AcceptList { get; }
    public MailPeopleList RejectList { get; }
    public MailPeopleList Moderators { get; }
    public MailPeopleList Bypass { get; }
    public MailPeopleList SendOnBehalf { get; }
    public MailPeopleList SendAs { get; }

    SetGroupMailValue? PendingValue(string parameter) => _pipeline?.PendingFor(_group.Id, "mail:" + parameter) as SetGroupMailValue;

    /// <summary>A switch over one boolean parameter (<paramref name="inverted"/>: on means the parameter is false).</summary>
    MailToggle Toggle(string label, string description, string parameter, bool actual, bool inverted = false, bool caution = false, string? blocked = null)
    {
        var pending = PendingValue(parameter);
        var shown = pending?.Value is JsonValue v ? v.GetValue<bool>() ^ inverted : actual;
        var editable = CanEdit && blocked is null;
        return new MailToggle(label, description, shown, pending is not null, editable, !CanEdit ? EditReason : blocked ?? description, on =>
        {
            if (PendingValue(parameter) is { } p)
                _pipeline!.Unstage(p);
            if (on != actual)
                _pipeline!.Stage(new SetGroupMailValue(Target, _group.Kind, parameter, label, actual ? "On" : "Off", on ? "On" : "Off",
                    JsonValue.Create(on ^ inverted), Caution: caution && on));
        });
    }

    void Rebuild()
    {
        if (Mail is not { } m)
            return;
        RebuildAddresses(m);
        List<MailToggle> delivery =
        [
            Toggle("Hide from the Address List", "People won't find it in Outlook's address book; mail typed to it still arrives.", "HiddenFromAddressListsEnabled", m.Hidden),
            Toggle("Accept Mail from Outside", "People outside the organization can email it.", "RequireSenderAuthenticationEnabled", m.OutsideSenders, inverted: true, caution: true),
        ];
        if (IsUnified)
        {
            delivery.Add(Toggle("Hide from Outlook", "It doesn't appear among members' groups in Outlook (and is hidden from the address list too).",
                "HiddenFromExchangeClientsEnabled", m.HiddenFromOutlook == true));
            delivery.Add(Toggle("Send Copies to Members' Inboxes", "New members get the group's conversations and events in their own inbox. Members already in it keep what they chose.",
                "AutoSubscribeNewMembers", m.AutoSubscribe == true));
            delivery.Add(Toggle("Send Calendar Events to Members", "New members get the group's calendar invitations.", "AlwaysSubscribeMembersToCalendarEvents", m.CalendarEvents == true));
            delivery.Add(Toggle("Welcome Message", "New members get a welcome email.", "UnifiedGroupWelcomeMessageEnabled", m.WelcomeMessage == true));
        }
        Delivery = delivery;

        var moderators = ListShown(ModeratorsParameter, m.Moderators);
        Moderation = Toggle("Require Approval", "A moderator approves each message before it reaches the group.", "ModerationEnabled", m.Moderated,
            blocked: !m.Moderated && moderators.Count == 0 && !IsListWithOwners ? "Add a moderator first: this kind of group needs one before approval can be turned on." : null);

        FillList(AcceptList, AcceptParameter, m.AcceptFrom);
        FillList(RejectList, RejectParameter, m.RejectFrom);
        FillList(Moderators, ModeratorsParameter, m.Moderators);
        FillList(Bypass, BypassParameter, m.BypassModeration);
        FillList(SendOnBehalf, OnBehalfParameter, m.SendOnBehalf);
        FillSendAs(m.SendAs);
        foreach (var field in TextSettings.Append(Notices).Concat(Membership).OfType<EditableField>())
            field.Refresh();
        OnPropertyChanged(nameof(CanEdit));
    }

    /// <summary>The MailTip and notices fields, made once per read.</summary>
    void BuildFields()
    {
        if (Mail is not { } m)
            return;
        var target = Target;
        var editable = CanEdit ? _canEdit : Availability.No(EditReason);
        TextSettings =
        [
            new EditableField("MailTip", "MailTip", m.MailTip, target, _pipeline, editable,
                validate: v => v.Length > 175 ? "Outlook shows at most 175 characters." : null,
                hint: "Shown in Outlook to anyone writing to the group, before they send.",
                change: (old, next) => new SetGroupMailValue(target, _group.Kind, "MailTip", "MailTip", old ?? "(none)", next ?? "(none)", next is null ? null : JsonValue.Create(next)),
                key: "mail:MailTip"),
        ];
        Notices = IsList
            ? new EditableField("Tell Senders When Not Approved", "SendModerationNotifications", m.ModerationNotices, target, _pipeline, editable,
                choices: [new FieldChoice("Always", "Always"), new FieldChoice("Internal", "Only senders in the organization"), new FieldChoice("Never", "Never")],
                change: (old, next) => new SetGroupMailValue(target, _group.Kind, "SendModerationNotifications", "Tell Senders When Not Approved", old ?? "Always",
                    next ?? "Always", JsonValue.Create(next ?? "Always")),
                key: "mail:SendModerationNotifications")
            : null;
        EditableField Restriction(string label, string parameter, string? current, IReadOnlyList<FieldChoice> choices, string hint) =>
            new(label, parameter, current ?? "Closed", target, _pipeline, editable, choices: choices, hint: hint,
                change: (old, next) => new SetGroupMailValue(target, _group.Kind, parameter, label, old ?? "Closed", next ?? "Closed", JsonValue.Create(next ?? "Closed"),
                    Caution: next == "Open"),
                key: "mail:" + parameter);
        Membership = _group.Kind == GroupKind.Distribution
            ?
            [
                Restriction("Joining", "MemberJoinRestriction", m.JoinRestriction,
                    [new FieldChoice("Closed", "Closed: only owners add people"), new FieldChoice("ApprovalRequired", "Owners approve requests to join"),
                        new FieldChoice("Open", "Open: anyone in the organization can join")],
                    "How people get on the list, from Outlook."),
                Restriction("Leaving", "MemberDepartRestriction", m.DepartRestriction,
                    [new FieldChoice("Open", "Open: members can leave by themselves"), new FieldChoice("Closed", "Closed: only owners take people off")],
                    "Whether members can take themselves off, from Outlook."),
            ]
            : [];
    }

    /// <summary>A list or mail-enabled security group falls back to its owners as moderators (a distribution list does; EAC says owners approve).</summary>
    bool IsListWithOwners => _group.Kind == GroupKind.Distribution;

    // ---- Address rows ------------------------------------------------------------------

    void RebuildAddresses(GroupMail m)
    {
        Addresses.Clear();
        var primaryPending = _pipeline?.PendingFor(_group.Id, "primary") as SetGroupPrimaryAddress;
        var primary = primaryPending?.New ?? m.PrimaryAddress;
        var shown = new List<string>();
        if (m.PrimaryAddress is { } p)
            shown.Add(p);
        shown.AddRange(m.Aliases);
        foreach (var add in _pipeline?.Pending.OfType<SetGroupAddress>().Where(a => a.Group.Id == _group.Id && a.New) ?? [])
            if (!shown.Contains(add.Address, StringComparer.OrdinalIgnoreCase))
                shown.Add(add.Address);
        foreach (var address in shown)
        {
            var pending = _pipeline?.PendingFor(_group.Id, "address:" + address.ToLowerInvariant()) as SetGroupAddress;
            var text = pending is null ? primaryPending?.New == address ? "Becoming primary" : null : pending.New ? "Adding" : "Removing";
            var isPrimary = string.Equals(address, primary, StringComparison.OrdinalIgnoreCase);
            Addresses.Add(new GroupAddressRow(address, isPrimary, text, CanEdit,
                () =>
                {
                    if (pending is not null)
                        _pipeline!.Unstage(pending);
                    else
                        _pipeline!.Stage(new SetGroupAddress(Target, _group.Kind, address, true, false));
                },
                () => _pipeline!.Stage(new SetGroupPrimaryAddress(Target, _group.Kind, m.PrimaryAddress ?? "", address))));
        }
    }

    [RelayCommand]
    void UndoPrimary()
    {
        if (_pipeline?.PendingFor(_group.Id, "primary") is { } p)
            _pipeline.Unstage(p);
    }

    public bool HasPendingPrimary => _pipeline?.PendingFor(_group.Id, "primary") is not null;

    // ---- People lists ------------------------------------------------------------------

    /// <summary>The list as it will be once pending changes apply.</summary>
    List<string> ListShown(string parameter, IReadOnlyList<string> actual) =>
        PendingValue(parameter)?.Value is JsonArray pending ? [.. pending.Select(x => x!.GetValue<string>())] : [.. actual];

    IReadOnlyList<string> Actual(string parameter) => Mail is not { } m ? [] : parameter switch
    {
        AcceptParameter => m.AcceptFrom,
        RejectParameter => m.RejectFrom,
        ModeratorsParameter => m.Moderators,
        BypassParameter => m.BypassModeration,
        _ => m.SendOnBehalf,
    };

    string Label(string parameter) => parameter switch
    {
        AcceptParameter => "Who Can Send",
        RejectParameter => "Blocked Senders",
        ModeratorsParameter => "Moderators",
        BypassParameter => "Don't Need Approval",
        _ => "Send on Behalf",
    };

    /// <summary>Someone in a list, named from the local copy (Exchange lists them by Entra ID).</summary>
    MemberRow Describe(string id) =>
        _index.User(id) is { } u ? new MemberRow(u.DisplayName, u.Mail ?? u.UserPrincipalName, "User", BadgeKind.Neutral, u, null)
        : _index.Group(id) is { } g ? new MemberRow(g.DisplayName, g.Mail ?? GroupSummary.KindName(g.Kind), "Group", BadgeKind.Info, null, g)
        : _index.Contact(id) is { } c ? new MemberRow(c.DisplayName, c.Email, "Contact", BadgeKind.Neutral, null, null, Contact: c)
        : _index.UserByUpn(id) is { } byName ? new MemberRow(byName.DisplayName, byName.UserPrincipalName, "User", BadgeKind.Neutral, byName, null)
        : new MemberRow(id, "Not in the local copy", "Other", BadgeKind.Neutral, null, null);

    string Names(IEnumerable<string> ids) => ids.Select(id => Describe(id).Name).ToArray() is { Length: > 0 } names ? string.Join(", ", names) : "(nobody)";

    void StageList(string parameter, List<string> next)
    {
        var actual = Actual(parameter);
        if (PendingValue(parameter) is { } p)
            _pipeline!.Unstage(p);
        if (!next.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(actual))
            _pipeline!.Stage(new SetGroupMailValue(Target, _group.Kind, parameter, Label(parameter), Names(actual), Names(next),
                new JsonArray([.. next.Select(id => (JsonNode)JsonValue.Create(id)!)]), Caution: parameter is RejectParameter or AcceptParameter));
    }

    void AddToList(string parameter, PickOption option)
    {
        if (!CanEdit)
            return;
        var shown = ListShown(parameter, Actual(parameter));
        if (!shown.Contains(option.Id, StringComparer.OrdinalIgnoreCase))
            StageList(parameter, [.. shown, option.Id]);
    }

    void FillList(MailPeopleList list, string parameter, IReadOnlyList<string> actual)
    {
        list.Rows.Clear();
        var shown = ListShown(parameter, actual);
        foreach (var id in shown.Union(actual, StringComparer.OrdinalIgnoreCase))
        {
            var row = Describe(id);
            var adding = !actual.Contains(id, StringComparer.OrdinalIgnoreCase);
            var removing = !shown.Contains(id, StringComparer.OrdinalIgnoreCase);
            list.Rows.Add(new PersonRow(row, adding ? "Adding" : removing ? "Removing" : null, CanEdit, CanEdit ? adding || removing ? "Undo" : "Remove" : EditReason,
                () => StageList(parameter, removing ? [.. shown, id] : [.. shown.Where(x => !string.Equals(x, id, StringComparison.OrdinalIgnoreCase))]),
                null, showRemove: CanEdit));
        }
    }

    /// <summary>
    /// People Exchange can name: those with a mailbox or an email address (seen live: someone without
    /// one is refused as "isn't a group or an individual recipient").
    /// </summary>
    IEnumerable<PickOption> MailPeople() =>
        GroupAccess.People(_index).Where(o => _index.User(o.Id) is { } u && (u.Mailbox is not null || !string.IsNullOrEmpty(u.Mail)));

    IEnumerable<PickOption> Candidates(string parameter)
    {
        var taken = ListShown(parameter, Actual(parameter)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = _index.AllGroups.Where(g => g.Mail is not null && g.Id != _group.Id).Select(PickOption.Of);
        return MailPeople().Concat(groups).Concat(_index.AllContacts.Select(PickOption.Of)).Where(o => !taken.Contains(o.Id));
    }

    // ---- Send As -----------------------------------------------------------------------

    static ChangeTarget Person(PickOption o) => o.Target(o.TargetKind);

    void FillSendAs(IReadOnlyList<string> trustees)
    {
        SendAs.Rows.Clear();
        foreach (var trustee in trustees)
        {
            var row = Describe(trustee);
            var person = row.User is { } u ? GroupAccess.Target(u) : new ChangeTarget("user", trustee, row.Name, trustee);
            var pending = _pipeline?.PendingFor(_group.Id, "sendas:" + person.Id);
            SendAs.Rows.Add(new PersonRow(row, pending is not null ? "Removing" : null, CanEdit, CanEdit ? pending is null ? "Remove" : "Undo" : EditReason,
                () =>
                {
                    if (pending is not null)
                        _pipeline!.Unstage(pending);
                    else
                        _pipeline!.Stage(new SetGroupSendAs(Target, _group.Kind, person, true, false));
                }, null, showRemove: CanEdit));
        }
        foreach (var add in _pipeline?.Pending.OfType<SetGroupSendAs>().Where(s => s.Group.Id == _group.Id && s.New) ?? [])
            SendAs.Rows.Add(new PersonRow(Describe(add.Person.Id), "Adding", true, "Undo", () => _pipeline!.Unstage(add), null));
    }

    IEnumerable<PickOption> SendAsCandidates()
    {
        var taken = (Mail?.SendAs ?? []).Select(t => _index.UserByUpn(t)?.Id ?? t)
            .Concat(_pipeline?.Pending.OfType<SetGroupSendAs>().Where(s => s.Group.Id == _group.Id).Select(s => s.Person.Id) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return MailPeople().Where(o => !taken.Contains(o.Id));
    }

    // ---- Following the tray ------------------------------------------------------------

    void OnPendingChanged(object? sender, EventArgs e)
    {
        Rebuild();
        OnPropertyChanged(nameof(HasPendingPrimary));
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
