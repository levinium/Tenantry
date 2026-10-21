using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// What both sides of Groups &amp; access share: how groups, mailboxes and people are named as
/// change targets, how rights read, and who may be added where.
/// </summary>
public static class GroupAccess
{
    public static readonly MailboxRight[] Rights = [MailboxRight.FullAccess, MailboxRight.SendAs, MailboxRight.SendOnBehalf];

    /// <summary>A group, or a shared/room/equipment mailbox (whose id is its account's).</summary>
    public static ChangeTarget Target(GroupSummary g) => new(g.IsMailbox ? "mailbox" : "group", g.Id, g.DisplayName, g.Mail);

    /// <summary>A person; also the target for changes to their own mailbox.</summary>
    public static ChangeTarget Target(UserSummary u) => new("user", u.Id, u.DisplayName, u.UserPrincipalName);

    /// <summary>"Full access, Send as" (as the directory gives a mailbox's people) → the rights.</summary>
    public static HashSet<MailboxRight> RightsOf(string? access)
    {
        var names = (access ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries);
        return Rights.Where(r => names.Contains(SetMailboxAccess.RightName(r))).ToHashSet();
    }

    public static string RightTip(MailboxRight right) => right switch
    {
        MailboxRight.FullAccess => "Full access: open the mailbox and read, edit and delete its mail. It appears in their Outlook automatically.",
        MailboxRight.SendAs => "Send as: send mail that appears to come from the mailbox itself.",
        _ => "Send on behalf: send mail marked \"on behalf of\" the mailbox.",
    };

    /// <summary>Why someone's place in a group can't be changed here, or null when it can.</summary>
    public static string? LockedReason(GroupSummary g, GroupRole role) =>
        role == GroupRole.Member && g.IsDynamic ? "Membership follows the group's rule: change the rule (or the person's details) to change who is in it." : null;

    /// <summary>
    /// Microsoft 365 and security groups that have owners must keep at least one (Graph refuses
    /// the removal). Returns why an owner can't be removed, counting changes already staged, or null.
    /// </summary>
    public static string? LastOwnerReason(GroupSummary g, ChangePipeline? pipeline)
    {
        if (g.IsExchangeManaged || g.IsMailbox)
            return null;
        var staying = g.OwnerList.Count(o => pipeline?.PendingFor(g.Id, $"{GroupRole.Owner}:{o.Id}") is not SetGroupMembership { New: false });
        var adding = pipeline?.Pending.OfType<SetGroupMembership>().Count(p => p.Group.Id == g.Id && p.Role == GroupRole.Owner && p.New) ?? 0;
        return staying + adding <= 1 ? "A group must keep at least one owner: add another owner first, then remove this one." : null;
    }

    /// <summary>A member as a row: named from the local copy where it can be.</summary>
    public static MemberRow Describe(GroupMember member, IDirectoryIndex index) => member.Type switch
    {
        "user" when index.User(member.Id) is { } u =>
            new(u.DisplayName, u.Kind == UserKind.Guest && u.Mail is { Length: > 0 } m ? m : u.UserPrincipalName, "User", BadgeKind.Neutral, u, null),
        "group" when index.Group(member.Id) is { } g =>
            new(g.DisplayName, g.Mail ?? GroupSummary.KindName(g.Kind), "Group", BadgeKind.Info, null, g),
        // An address or name Exchange gave that the local copy can't match (a contact, a
        // deleted account): shown as given.
        "unresolved" => new(member.Name ?? member.Id, "Not found in the directory", "Other", BadgeKind.Neutral, null, null),
        _ => new(Unnamed(member.Type), member.Id, TypeLabel(member.Type), BadgeKind.Neutral, null, null),
    };

    /// <summary>The change target for a member row: what the pipeline and Exchange need to find them.</summary>
    public static ChangeTarget PersonTarget(GroupMember member, MemberRow row) =>
        row.User is { } u ? Target(u)
        : row.Group is { } g ? Target(g)
        // Unresolved trustees are named by the address Exchange gave.
        : new ChangeTarget(member.Type == "unresolved" ? "user" : member.Type, member.Id, row.Name, member.Name);

    static string TypeLabel(string type) => type switch
    {
        "user" => "User",
        "group" => "Group",
        "device" => "Device",
        "orgContact" => "Contact",
        "servicePrincipal" => "App",
        _ => "Other",
    };

    /// <summary>Members the local copy doesn't hold (devices, contacts, apps, or not synced yet).</summary>
    static string Unnamed(string type) => type switch
    {
        "device" => "A device",
        "orgContact" => "A mail contact",
        "servicePrincipal" => "An application",
        _ => "Not in the local copy yet",
    };

    /// <summary>People who sign in: members and guests, not the accounts behind shared mailboxes.</summary>
    public static IEnumerable<PickOption> People(IDirectoryIndex index) =>
        index.AllUsers.Where(u => UserFields.TypeOf(u) is UserFields.Member or UserFields.Guest).Select(PickOption.Of);

    /// <summary>
    /// Who can join a group as a member: Microsoft 365 groups take people only; security groups
    /// also take other security groups; distribution lists also take mail-enabled groups.
    /// </summary>
    public static IEnumerable<PickOption> MemberCandidates(GroupSummary group, IDirectoryIndex index)
    {
        var people = People(index);
        if (group.Kind == GroupKind.Microsoft365)
            return people;
        var groups = index.AllGroups.Where(g => g.Id != group.Id && !g.IsMailbox && !g.IsDynamic && (group.IsExchangeManaged
            ? g.Mail is not null
            : g.Kind == GroupKind.Security));
        return people.Concat(groups.Select(PickOption.Of));
    }

    /// <summary>The state to show (pending change first, then the directory) and whether a change is waiting.</summary>
    public static (bool Shown, bool Pending) State(ChangePipeline? pipeline, string targetId, string key, bool actual) =>
        pipeline?.PendingFor(targetId, key) switch
        {
            SetGroupMembership m => (m.New, true),
            SetMailboxAccess a => (a.New, true),
            _ => (actual, false),
        };
}

/// <summary>
/// A row of checkboxes: a group with Member and Owner, or a mailbox (or person) with Full
/// access, Send as and Send on behalf.
/// </summary>
public sealed partial class ToggleRow(string name, string detail, string typeText, BadgeKind typeKind, IReadOnlyList<RightToggle> toggles,
    string? note, Action? open) : ObservableObject
{
    public string Name => name;
    public string Detail => detail;
    public string TypeText => typeText;
    public BadgeKind TypeKind => typeKind;
    public IReadOnlyList<RightToggle> Toggles => toggles;

    /// <summary>"Adding" / "Changing" when something is staged for this row.</summary>
    public string? PendingText => toggles.Any(t => t.IsPending) ? "Pending" : null;

    public bool HasPending => PendingText is not null;

    /// <summary>A short reason shown under the name ("Dynamic: follows its rule").</summary>
    public string? Note => note;

    public bool HasNote => !string.IsNullOrEmpty(note);

    public bool CanOpen => open is not null;

    [RelayCommand]
    void Open() => open?.Invoke();
}

/// <summary>A person in a group's Owners or Members list, with remove (or undo).</summary>
public sealed partial class PersonRow(MemberRow row, string? pendingText, bool canRemove, string removeTip, Action toggle, Action? open) : ObservableObject
{
    public string Name => row.Name;
    public string? Detail => row.Detail;
    public string TypeText => row.TypeText;
    public BadgeKind TypeKind => row.TypeKind;

    /// <summary>"Adding" or "Removing" while a change waits in Pending changes.</summary>
    public string? PendingText => pendingText;

    public bool HasPending => pendingText is not null;

    public bool CanRemove => canRemove;

    public string RemoveTip => removeTip;

    public bool CanOpen => open is not null;

    [RelayCommand]
    void Remove() => toggle();

    [RelayCommand]
    void Open() => open?.Invoke();
}
