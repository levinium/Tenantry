using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// Everyone attached to a group or shared mailbox, editable: a group's owners and members, or a
/// mailbox's people with their rights. The same editor sits on the group's page and in the
/// preview's Manage popup. Every change is staged in Pending changes and shows here as pending.
/// </summary>
public sealed partial class GroupPeopleEditor : ObservableObject, IDisposable
{
    /// <summary>Rows drawn at once; a big list is narrowed with the filter box instead.</summary>
    const int MaxShown = 100;

    GroupSummary _group;
    readonly IDirectoryIndex _index;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly Action<UserSummary> _openUser;
    readonly Action<GroupSummary> _openGroup;

    public GroupPeopleEditor(GroupSummary group, IDirectoryIndex index, ChangePipeline? pipeline, Availability canEdit,
        Action<UserSummary> openUser, Action<GroupSummary> openGroup)
    {
        _group = group;
        _index = index;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _openUser = openUser;
        _openGroup = openGroup;
        OwnerPicker = new PersonPicker("Add an owner: type a name or address", OwnerOptions, o => Add(o, GroupRole.Owner));
        MemberPicker = new PersonPicker("Add a member: type a name or address", MemberOptions, o => Add(o, GroupRole.Member));
        AccessPicker = new PersonPicker("Give someone access: type a name or address", AccessOptions, o =>
            _pipeline?.Stage(new SetMailboxAccess(GroupAccess.Target(_group), o.Target("user"), MailboxRight.FullAccess, false, true)));
        Rebuild();
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public bool IsMailbox => _group.IsMailbox;

    public bool IsGroup => !_group.IsMailbox;

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    /// <summary>Why members can't be added or removed (a dynamic group), or null.</summary>
    public string? MembersLockedReason => GroupAccess.LockedReason(_group, GroupRole.Member);

    public bool HasMembersLock => MembersLockedReason is not null;

    public bool CanAddMembers => CanEdit && !HasMembersLock;

    /// <summary>Opens Add Members from CSV (set by the group's record, which can open popups).</summary>
    public Action? ImportMembers { get; set; }

    public bool CanImportMembers => CanAddMembers && ImportMembers is not null && !_group.IsMailbox;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void ImportMembersFromCsv() => ImportMembers?.Invoke();

    public PersonPicker OwnerPicker { get; }

    public PersonPicker MemberPicker { get; }

    public PersonPicker AccessPicker { get; }

    public ObservableCollection<PersonRow> Owners { get; } = [];

    public ObservableCollection<PersonRow> Members { get; } = [];

    public ObservableCollection<ToggleRow> Access { get; } = [];

    public string OwnersHeading => $"Owners ({_group.OwnerList.Count:N0})";

    public string MembersHeading => $"Members ({_group.Members.Count:N0})";

    public string AccessHeading => $"People with Access ({_group.Members.Count:N0})";

    public bool HasNoOwners => IsGroup && Owners.Count == 0;

    public string NoOwnersText => "No owners: only administrators can manage this group. Adding an owner lets them manage members themselves.";

    public bool HasNoMembers => Members.Count == 0;

    public bool HasNoAccess => Access.Count == 0;

    /// <summary>Narrows the members list (name or address).</summary>
    [ObservableProperty]
    public partial string MemberFilter { get; set; } = "";

    partial void OnMemberFilterChanged(string value) => RebuildMembers();

    [ObservableProperty]
    public partial string? MoreMembersText { get; private set; }

    public bool ShowFilter => _group.Members.Count > 12;

    /// <summary>Shows the group as it is now (after changes are applied and read back).</summary>
    public void Update(GroupSummary group)
    {
        _group = group;
        Rebuild();
    }

    void OnPendingChanged(object? sender, EventArgs e) => Rebuild();

    void Rebuild()
    {
        RebuildOwners();
        RebuildMembers();
        RebuildAccess();
        foreach (var name in new[] { nameof(OwnersHeading), nameof(MembersHeading), nameof(AccessHeading), nameof(HasNoOwners), nameof(ShowFilter),
                     nameof(IsMailbox), nameof(IsGroup), nameof(MembersLockedReason), nameof(HasMembersLock), nameof(CanAddMembers) })
            OnPropertyChanged(name);
    }

    void RebuildOwners()
    {
        Owners.Clear();
        if (!IsGroup)
            return;
        foreach (var row in Rows(_group.OwnerList, GroupRole.Owner, _ => true))
            Owners.Add(row);
        OnPropertyChanged(nameof(HasNoOwners));
    }

    void RebuildMembers()
    {
        Members.Clear();
        MoreMembersText = null;
        if (!IsGroup)
            return;
        var words = MemberFilter.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(MemberRow r) => words.Length == 0
            || words.All(w => $"{r.Name} {r.Detail}".Contains(w, StringComparison.CurrentCultureIgnoreCase));
        var all = Rows(_group.Members, GroupRole.Member, Matches).ToList();
        foreach (var row in all.Take(MaxShown))
            Members.Add(row);
        if (all.Count > MaxShown)
            MoreMembersText = $"Showing {MaxShown:N0} of {all.Count:N0}. Type in the box above to find someone.";
        OnPropertyChanged(nameof(HasNoMembers));
    }

    /// <summary>
    /// Current owners or members (removals marked "Removing"), then people staged to be added
    /// ("Adding"). People first, then groups, by name.
    /// </summary>
    IEnumerable<PersonRow> Rows(IReadOnlyList<GroupMember> current, GroupRole role, Func<MemberRow, bool> include)
    {
        var lastOwner =role == GroupRole.Owner ? GroupAccess.LastOwnerReason(_group, _pipeline) : null;
        var ids = current.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var rows = new List<(MemberRow Row, PersonRow Person)>();
        foreach (var member in current)
        {
            var row = GroupAccess.Describe(member, _index);
            if (!include(row))
                continue;
            var target = GroupAccess.PersonTarget(member, row);
            var pending = _pipeline?.PendingFor(_group.Id, $"{role}:{member.Id}");
            // Undoing a staged removal is always allowed; a new removal can't leave the group ownerless.
            var locked = pending is null ? GroupAccess.LockedReason(_group, role) ?? lastOwner : null;
            var tip = !CanEdit ? EditReason
                : locked ?? (pending is not null ? "Undo: keep them" : $"Remove this {role.ToString().ToLowerInvariant()} (added to Pending Changes)");
            rows.Add((row, new PersonRow(row, pending is not null ? "Removing" : null, CanEdit && locked is null, tip,
                () => Toggle(pending, () => new SetGroupMembership(GroupAccess.Target(_group), target, member.Type, role, _group.IsExchangeManaged, true, false)),
                Opener(row))));
        }
        foreach (var add in _pipeline?.Pending.OfType<SetGroupMembership>().Where(p => p.Group.Id == _group.Id && p.Role == role && p.New && !ids.Contains(p.Person.Id)) ?? [])
        {
            var row = GroupAccess.Describe(new GroupMember(add.Person.Id, add.PersonType), _index);
            if (include(row))
                rows.Add((row, new PersonRow(row, "Adding", true, "Undo: don't add them", () => _pipeline!.Unstage(add), Opener(row))));
        }
        return rows
            .OrderBy(r => r.Row.TypeText switch { "User" => 0, "Group" => 1, _ => 2 })
            .ThenBy(r => r.Row.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => r.Person);
    }

    /// <summary>A mailbox's people, each with three rights as checkboxes; people staged to be given access follow.</summary>
    void RebuildAccess()
    {
        Access.Clear();
        if (!IsMailbox)
            return;
        var target = GroupAccess.Target(_group);
        var rows = new List<(string Name, ToggleRow Row)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in _group.Members)
        {
            ids.Add(member.Id);
            var row = GroupAccess.Describe(member, _index);
            rows.Add((row.Name, AccessRow(row, GroupAccess.PersonTarget(member, row), GroupAccess.RightsOf(member.Access))));
        }
        var added = _pipeline?.Pending.OfType<SetMailboxAccess>().Where(p => p.Mailbox.Id == _group.Id && !ids.Contains(p.Person.Id)) ?? [];
        foreach (var person in added.GroupBy(p => p.Person.Id))
        {
            var row = GroupAccess.Describe(new GroupMember(person.Key, "user"), _index);
            rows.Add((row.Name, AccessRow(row, person.First().Person, [])));
        }
        foreach (var (_, row) in rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            Access.Add(row);
        OnPropertyChanged(nameof(HasNoAccess));

        ToggleRow AccessRow(MemberRow row, ChangeTarget person, HashSet<MailboxRight> actual) => new(row.Name, row.Detail ?? "", row.TypeText, row.TypeKind,
            GroupAccess.Rights.Select(right =>
            {
                var has = actual.Contains(right);
                var (shown, pending) = GroupAccess.State(_pipeline, _group.Id, $"{right}:{person.Id}", has);
                return new RightToggle(SetMailboxAccess.RightLabel(right), shown, pending, CanEdit, CanEdit ? GroupAccess.RightTip(right) : EditReason,
                    on => _pipeline!.Stage(new SetMailboxAccess(target, person, right, has, on)));
            }).ToArray(),
            null, Opener(row));
    }

    void Toggle(ChangeOp? pending, Func<ChangeOp> change)
    {
        if (!CanEdit)
            return;
        if (pending is not null)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(change());
    }

    void Add(PickOption option, GroupRole role)
    {
        if (!CanEdit || (role == GroupRole.Member && HasMembersLock))
            return;
        _pipeline!.Stage(new SetGroupMembership(GroupAccess.Target(_group), option.Target(option.Type), option.Type, role, _group.IsExchangeManaged, false, true));
    }

    Action? Opener(MemberRow row) =>
        row.User is { } u ? () => _openUser(u)
        : row.Group is { } g ? () => _openGroup(g)
        : null;

    HashSet<string> Taken(GroupRole role) =>
        (role == GroupRole.Owner ? _group.OwnerList : _group.Members).Select(m => m.Id)
            .Concat(_pipeline?.Pending.OfType<SetGroupMembership>().Where(p => p.Group.Id == _group.Id && p.Role == role).Select(p => p.Person.Id) ?? [])
            .ToHashSet(StringComparer.Ordinal);

    IEnumerable<PickOption> OwnerOptions()
    {
        var taken = Taken(GroupRole.Owner);
        return GroupAccess.People(_index).Where(o => !taken.Contains(o.Id));
    }

    IEnumerable<PickOption> MemberOptions()
    {
        var taken = Taken(GroupRole.Member);
        return GroupAccess.MemberCandidates(_group, _index).Where(o => !taken.Contains(o.Id));
    }

    IEnumerable<PickOption> AccessOptions()
    {
        var taken = _group.Members.Select(m => m.Id)
            .Concat(_pipeline?.Pending.OfType<SetMailboxAccess>().Where(p => p.Mailbox.Id == _group.Id).Select(p => p.Person.Id) ?? [])
            .Append(_group.Id)
            .ToHashSet(StringComparer.Ordinal);
        return GroupAccess.People(_index).Where(o => !taken.Contains(o.Id));
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
