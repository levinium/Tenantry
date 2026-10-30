using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// The Groups &amp; access tab: the groups and teams the person is in or owns (Member and Owner
/// as checkboxes), the shared mailboxes they can use (with which rights), and who can use their
/// own mailbox. Groups and shared mailboxes come from the local copy; a person's own mailbox is
/// read live from Exchange. Every change is staged.
/// </summary>
public sealed partial class GroupsAccessSection : RecordSection, IDisposable
{
    UserSummary _user;
    readonly IDirectoryIndex _index;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly IUserRecords? _records;
    readonly Action<GroupSummary> _openGroup;
    readonly Action<UserSummary> _openUser;

    /// <summary>The groups and mailboxes they're in, worked out once per load (not on every pending change).</summary>
    List<(GroupSummary Group, bool Member, bool Owner)> _groups = [];
    List<(GroupSummary Mailbox, HashSet<MailboxRight> Rights)> _mailboxes = [];
    IReadOnlyList<(ChangeTarget Person, MemberRow Row, HashSet<MailboxRight> Rights)>? _delegates;
    bool _loadingDelegates;

    public GroupsAccessSection(UserSummary user, IDirectoryIndex index, ChangePipeline? pipeline, Availability canEdit, IUserRecords? records,
        Action<GroupSummary>? openGroup = null, Action<UserSummary>? openUser = null)
    {
        _user = user;
        _index = index;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _records = records;
        _openGroup = openGroup ?? (_ => { });
        _openUser = openUser ?? (_ => { });
        GroupPicker = new PersonPicker("Add to a group: type its name", GroupOptions, AddToGroup);
        MailboxPicker = new PersonPicker("Give access to a shared mailbox: type its name", MailboxOptions, o =>
            _pipeline?.Stage(new SetMailboxAccess(GroupAccess.Target(_index.Group(o.Id)!), Me, MailboxRight.FullAccess, false, true)));
        DelegatePicker = new PersonPicker("Give someone access to this mailbox: type a name", DelegateOptions, o =>
            _pipeline?.Stage(new SetMailboxAccess(OwnMailbox, o.Target("user"), MailboxRight.FullAccess, false, true)));
        Compute();
        Rebuild();
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public override string Key => "groups";
    public override string Title => "Groups & Access";
    public override string Icon => "People";

    bool CanEditHere => _pipeline is not null && _canEdit.IsAvailable;

    public bool CanEdit => CanEditHere;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    ChangeTarget Me => GroupAccess.Target(_user);

    public PersonPicker GroupPicker { get; }

    public PersonPicker MailboxPicker { get; }

    public PersonPicker DelegatePicker { get; }

    public ObservableCollection<ToggleRow> Groups { get; } = [];

    public ObservableCollection<ToggleRow> Mailboxes { get; } = [];

    public ObservableCollection<ToggleRow> Delegates { get; } = [];

    public bool HasNoGroups => Groups.Count == 0;

    public bool HasNoMailboxes => Mailboxes.Count == 0;

    public bool HasNoDelegates => Delegates.Count == 0 && IsDelegatesReady;

    public string GroupsHeading => $"Groups and Teams ({Groups.Count:N0})";

    public string MailboxesHeading => $"Shared Mailboxes They Can Use ({Mailboxes.Count:N0})";

    // ---- Their own mailbox ----------------------------------------------------------------

    public bool HasMailbox => _user.Mailbox is not null;

    /// <summary>A shared, room or equipment mailbox's people are in the local copy; a person's are read live.</summary>
    GroupSummary? OwnMailboxGroup => _index.Group(_user.Id) is { IsMailbox: true } g ? g : null;

    ChangeTarget OwnMailbox => OwnMailboxGroup is { } g ? GroupAccess.Target(g) : Me;

    public string DelegatesHeading => OwnMailboxGroup is not null ? "Who Can Use This Mailbox" : "Who Can Use Their Mailbox";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDelegatesReady), nameof(HasNoDelegates))]
    public partial bool IsLoadingDelegates { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDelegatesError), nameof(IsDelegatesReady), nameof(HasNoDelegates))]
    public partial string? DelegatesError { get; private set; }

    public bool HasDelegatesError => DelegatesError is not null;

    public bool IsDelegatesReady => HasMailbox && !IsLoadingDelegates && DelegatesError is null && _delegates is not null;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_delegates is not null || _loadingDelegates || !HasMailbox)
            return;
        if (OwnMailboxGroup is { } group)
        {
            _delegates = group.Members.Select(m =>
            {
                var row = GroupAccess.Describe(m, _index);
                return (GroupAccess.PersonTarget(m, row), row, GroupAccess.RightsOf(m.Access));
            }).ToArray();
            RebuildDelegates();
            return;
        }
        if (_records is null)
            return;
        _loadingDelegates = true;
        IsLoadingDelegates = true;
        DelegatesError = null;
        try
        {
            var address = _user.Mailbox!.PrimaryAddress ?? _user.Mail ?? _user.UserPrincipalName;
            var found = await _records.GetMailboxDelegatesAsync(_user.Id, address, cancellationToken);
            _delegates = Resolve(found);
            RebuildDelegates();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            DelegatesError = $"Couldn't read who can use their mailbox: {Friendly.Message(e)}";
        }
        finally
        {
            _loadingDelegates = false;
            IsLoadingDelegates = false;
        }
    }

    /// <summary>Exchange names trustees by address, alias or display name: matched to people where it can.</summary>
    IReadOnlyList<(ChangeTarget, MemberRow, HashSet<MailboxRight>)> Resolve(IReadOnlyList<MailboxDelegate> delegates)
    {
        var byName = new Dictionary<string, UserSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in _index.AllUsers)
        {
            foreach (var key in new[] { u.UserPrincipalName, u.Mail, u.DisplayName, u.UserPrincipalName.Split('@')[0] })
                if (!string.IsNullOrEmpty(key))
                    byName.TryAdd(key, u);
        }
        return delegates.Select(d =>
        {
            var member = byName.TryGetValue(d.Trustee, out var u) ? new GroupMember(u.Id, "user") : new GroupMember(d.Trustee, "unresolved", Name: d.Trustee);
            var row = GroupAccess.Describe(member, _index);
            return (GroupAccess.PersonTarget(member, row), row, d.Rights.ToHashSet());
        }).ToArray();
    }

    // ---- Building rows --------------------------------------------------------------------

    /// <summary>One pass over the local copy: every group and mailbox this person appears in.</summary>
    void Compute()
    {
        _groups = [];
        _mailboxes = [];
        foreach (var g in _index.AllGroups)
        {
            if (g.Id == _user.Id)
                continue;
            if (g.IsMailbox)
            {
                if (g.Members.FirstOrDefault(m => m.Id == _user.Id) is { } entry)
                    _mailboxes.Add((g, GroupAccess.RightsOf(entry.Access)));
                continue;
            }
            var member = g.Members.Any(m => m.Id == _user.Id);
            var owner = g.OwnerList.Any(m => m.Id == _user.Id);
            if (member || owner)
                _groups.Add((g, member, owner));
        }
        Summary = SummaryText();
    }

    string SummaryText()
    {
        var groups = _groups.Count == 1 ? "1 group" : $"{_groups.Count:N0} groups";
        return _mailboxes.Count == 0 ? groups : $"{groups} · {_mailboxes.Count:N0} shared {(_mailboxes.Count == 1 ? "mailbox" : "mailboxes")}";
    }

    void OnPendingChanged(object? sender, EventArgs e) => Rebuild();

    void Rebuild()
    {
        RebuildGroups();
        RebuildMailboxes();
        RebuildDelegates();
    }

    void RebuildGroups()
    {
        Groups.Clear();
        var rows = _groups.Select(x => (x.Group, x.Member, x.Owner)).ToList();
        var listed = rows.Select(r => r.Group.Id).ToHashSet(StringComparer.Ordinal);
        // Groups they're staged to join: shown at once.
        foreach (var add in PendingFor<SetGroupMembership>(p => p.Person.Id == _user.Id && p.New && !listed.Contains(p.Group.Id)))
        {
            if (_index.Group(add.Group.Id) is { } g && listed.Add(g.Id))
                rows.Add((g, false, false));
        }
        foreach (var (g, member, owner) in rows.OrderBy(r => r.Group.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            Groups.Add(new ToggleRow(g.DisplayName, GroupSummary.KindName(g.Kind) + (g.IsTeam ? " · Team" : ""), g.IsTeam ? "Team" : "Group",
                g.IsTeam ? BadgeKind.Success : BadgeKind.Info,
                [Membership(g, GroupRole.Member, member), Membership(g, GroupRole.Owner, owner)],
                g.IsDynamic ? "Dynamic: membership follows the group's rule" : g.SyncedFromOnPremises ? "Synced from on-premises Active Directory" : null,
                () => _openGroup(g)));
        OnPropertyChanged(nameof(HasNoGroups));
        OnPropertyChanged(nameof(GroupsHeading));
    }

    RightToggle Membership(GroupSummary g, GroupRole role, bool actual)
    {
        var (shown, pending) = GroupAccess.State(_pipeline, g.Id, $"{role}:{_user.Id}", actual);
        var locked = GroupAccess.LockedReason(g, role)
            // The last owner can't untick themselves (Graph refuses); undoing a staged change always works.
            ?? (role == GroupRole.Owner && actual && !pending ? GroupAccess.LastOwnerReason(g, _pipeline) : null);
        var tip = !CanEditHere ? EditReason
            : locked ?? (role == GroupRole.Owner ? "Owners manage the group: its members and settings" : "Members get the group's mail, files and access");
        return new RightToggle(role.ToString(), shown, pending, CanEditHere && locked is null, tip,
            on => _pipeline!.Stage(new SetGroupMembership(GroupAccess.Target(g), Me, "user", role, g.IsExchangeManaged, actual, on)));
    }

    void RebuildMailboxes()
    {
        Mailboxes.Clear();
        var rows = _mailboxes.Select(x => (x.Mailbox, x.Rights)).ToList();
        var listed = rows.Select(r => r.Mailbox.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var add in PendingFor<SetMailboxAccess>(p => p.Person.Id == _user.Id && !listed.Contains(p.Mailbox.Id)))
        {
            if (_index.Group(add.Mailbox.Id) is { IsMailbox: true } g && listed.Add(g.Id))
                rows.Add((g, []));
        }
        foreach (var (mailbox, rights) in rows.OrderBy(r => r.Mailbox.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            Mailboxes.Add(new ToggleRow(mailbox.DisplayName, mailbox.Mail ?? GroupSummary.KindName(mailbox.Kind), GroupSummary.KindName(mailbox.Kind), BadgeKind.Neutral,
                Toggles(GroupAccess.Target(mailbox), Me, rights), null, () => _openGroup(mailbox)));
        OnPropertyChanged(nameof(HasNoMailboxes));
        OnPropertyChanged(nameof(MailboxesHeading));
    }

    void RebuildDelegates()
    {
        Delegates.Clear();
        if (_delegates is null)
            return;
        var mailbox = OwnMailbox;
        var rows = _delegates.Select(d => (d.Person, d.Row, d.Rights)).ToList();
        var listed = rows.Select(r => r.Person.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var add in PendingFor<SetMailboxAccess>(p => p.Mailbox.Id == _user.Id && !listed.Contains(p.Person.Id)))
        {
            if (listed.Add(add.Person.Id))
                rows.Add((add.Person, GroupAccess.Describe(new GroupMember(add.Person.Id, "user"), _index), []));
        }
        foreach (var (person, row, rights) in rows.OrderBy(r => r.Row.Name, StringComparer.CurrentCultureIgnoreCase))
            Delegates.Add(new ToggleRow(row.Name, row.Detail ?? "", row.TypeText, row.TypeKind, Toggles(mailbox, person, rights), null,
                row.User is { } u ? () => _openUser(u) : null));
        OnPropertyChanged(nameof(HasNoDelegates));
        OnPropertyChanged(nameof(IsDelegatesReady));
    }

    IReadOnlyList<RightToggle> Toggles(ChangeTarget mailbox, ChangeTarget person, HashSet<MailboxRight> actual) =>
        GroupAccess.Rights.Select(right =>
        {
            var has = actual.Contains(right);
            var (shown, pending) = GroupAccess.State(_pipeline, mailbox.Id, $"{right}:{person.Id}", has);
            return new RightToggle(SetMailboxAccess.RightLabel(right), shown, pending, CanEditHere, CanEditHere ? GroupAccess.RightTip(right) : EditReason,
                on => _pipeline!.Stage(new SetMailboxAccess(mailbox, person, right, has, on)));
        }).ToArray();

    IEnumerable<T> PendingFor<T>(Func<T, bool> match) where T : ChangeOp => _pipeline?.Pending.OfType<T>().Where(match) ?? [];

    // ---- Pickers --------------------------------------------------------------------------

    IEnumerable<PickOption> GroupOptions()
    {
        var ids = _groups.Select(g => g.Group.Id)
            .Concat(PendingFor<SetGroupMembership>(p => p.Person.Id == _user.Id).Select(p => p.Group.Id))
            .ToHashSet(StringComparer.Ordinal);
        return _index.AllGroups.Where(g => !g.IsMailbox && GroupAccess.MembersEditable(g) && !ids.Contains(g.Id)).Select(PickOption.Of);
    }

    void AddToGroup(PickOption option)
    {
        if (!CanEditHere || _index.Group(option.Id) is not { } g)
            return;
        _pipeline!.Stage(new SetGroupMembership(GroupAccess.Target(g), Me, "user", GroupRole.Member, g.IsExchangeManaged, false, true));
    }

    IEnumerable<PickOption> MailboxOptions()
    {
        var ids = _mailboxes.Select(m => m.Mailbox.Id)
            .Concat(PendingFor<SetMailboxAccess>(p => p.Person.Id == _user.Id).Select(p => p.Mailbox.Id))
            .Append(_user.Id)
            .ToHashSet(StringComparer.Ordinal);
        return _index.AllGroups.Where(g => g.IsMailbox && !ids.Contains(g.Id)).Select(PickOption.Of);
    }

    IEnumerable<PickOption> DelegateOptions()
    {
        var ids = (_delegates?.Select(d => d.Person.Id) ?? [])
            .Concat(PendingFor<SetMailboxAccess>(p => p.Mailbox.Id == _user.Id).Select(p => p.Person.Id))
            .Append(_user.Id)
            .ToHashSet(StringComparer.Ordinal);
        return GroupAccess.People(_index).Where(o => !ids.Contains(o.Id));
    }

    /// <summary>After changes apply: the local copy has the new groups; their mailbox is read again.</summary>
    public override void RecordChanged(UserSummary user)
    {
        _user = user;
        Compute();
        var hadDelegates = _delegates is not null;
        _delegates = null;
        Rebuild();
        if (hadDelegates)
            _ = LoadAsync();
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
