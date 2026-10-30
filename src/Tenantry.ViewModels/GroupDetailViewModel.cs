using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>A group member as the record page shows it, with the name looked up locally.</summary>
/// <param name="Access">For a mailbox: what this person can do ("Full access, Send as").</param>
public sealed record MemberRow(string Name, string? Detail, string TypeText, BadgeKind TypeKind, UserSummary? User, GroupSummary? Group, string? Access = null)
{
    public bool CanOpen => User is not null || Group is not null;

    public bool HasAccess => !string.IsNullOrEmpty(Access);
}

/// <summary>The local copy by id (for naming group members) and in full (for the people pickers).</summary>
public interface IDirectoryIndex
{
    UserSummary? User(string id);
    GroupSummary? Group(string id);
    IEnumerable<UserSummary> AllUsers { get; }
    IEnumerable<GroupSummary> AllGroups { get; }

    /// <summary>Someone by sign-in name (any case). The app's index looks it up; this default scans.</summary>
    UserSummary? UserByUpn(string upn) => AllUsers.FirstOrDefault(u => string.Equals(u.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An empty local copy, for records shown without one (tests, demos of a single page).</summary>
public sealed class EmptyIndex : IDirectoryIndex
{
    public static readonly EmptyIndex Instance = new();

    public UserSummary? User(string id) => null;
    public GroupSummary? Group(string id) => null;
    public IEnumerable<UserSummary> AllUsers => [];
    public IEnumerable<GroupSummary> AllGroups => [];
}

/// <summary>
/// A group's or shared mailbox's record, as a page or in the preview pane. Owners, members and
/// people with access are edited in <see cref="People"/>: inline on the page, in a popup from
/// the preview's Manage button.
/// </summary>
public sealed partial class GroupDetailViewModel : TabViewModel, IDisposable
{
    const int PreviewMembers = 12;

    readonly IDirectoryIndex _index;
    readonly Action<UserSummary> _openUser;
    readonly Action<GroupSummary> _openGroup;
    readonly Action<DialogViewModel>? _openDialog;
    readonly Action? _closeDialog;

    public GroupDetailViewModel(GroupSummary group, IDirectoryIndex index, Action<UserSummary>? openUser = null, Action<GroupSummary>? openGroup = null,
        ChangePipeline? pipeline = null, Availability? canEdit = null, Action<DialogViewModel>? openDialog = null, Action? closeDialog = null)
    {
        _index = index;
        _openUser = openUser ?? (_ => { });
        _openGroup = openGroup ?? (_ => { });
        _openDialog = openDialog;
        _closeDialog = closeDialog;
        Group = group;
        Members = Rows(group);
        // Opening someone from the popup closes it first, so their page isn't hidden behind it.
        People = new GroupPeopleEditor(group, index, pipeline, canEdit ?? Availability.No("Editing isn't available here"),
            u => { _closeDialog?.Invoke(); _openUser(u); },
            g => { _closeDialog?.Invoke(); _openGroup(g); });
        if (pipeline is not null && openDialog is not null && closeDialog is not null)
            People.ImportMembers = () => openDialog(new DialogViewModel("Add Members from CSV", Group.DisplayName,
                new ImportMembersViewModel(Group, pipeline, index, closeDialog)));
    }

    public GroupSummary Group { get; private set; }

    public override string Title => Group.DisplayName;

    public string Initials => Display.Initials(Group.DisplayName);

    public string TypeText => GroupList.TypeBadge(Group.Kind).Text;
    public BadgeKind TypeKind => GroupList.TypeBadge(Group.Kind).Kind;

    public string MembershipText => Group.IsDynamic ? "Dynamic Membership" : Group.SyncedFromOnPremises ? "Synced from On-Premises AD" : "Assigned Membership";
    public bool ShowMembership => !Group.IsMailbox;
    public bool IsTeam => Group.IsTeam;
    public bool IsMailbox => Group.IsMailbox;

    /// <summary>A mailbox's own account (a user object), for its sign-in and license settings.</summary>
    public UserSummary? Account => Group.IsMailbox ? _index.User(Group.Id) : null;

    public bool HasAccount => Account is not null;

    public string MembersHeading => Group.IsMailbox ? "People with Access" : "Members";

    /// <summary>The owners, members or people with access, editable.</summary>
    public GroupPeopleEditor People { get; }

    public string ManageText => Group.IsMailbox ? "Manage Access" : "Manage Owners and Members";

    public string ManageTip => Group.IsMailbox
        ? "Give or remove Full access, Send as and Send on behalf"
        : "Add or remove owners and members (changes go to Pending changes first)";

    /// <summary>Opens the people editor in a popup (the preview pane's Manage button).</summary>
    [RelayCommand]
    void ManagePeople() => _openDialog?.Invoke(new DialogViewModel(Group.IsMailbox ? "People with Access" : "Owners and Members", Group.DisplayName, People));

    /// <summary>"Owned by Ada Lovelace and 1 other" for the preview, or why there's no owner.</summary>
    public string OwnersText
    {
        get
        {
            var owners = Group.OwnerList.Select(o => GroupAccess.Describe(o, _index).Name).ToArray();
            return owners.Length switch
            {
                0 => "No owners",
                1 => $"Owned by {owners[0]}",
                2 => $"Owned by {owners[0]} and {owners[1]}",
                _ => $"Owned by {owners[0]} and {owners.Length - 1:N0} others",
            };
        }
    }

    [RelayCommand]
    void OpenAccount()
    {
        if (Account is { } account)
            _openUser(account);
    }

    public string Email => Group.Mail ?? "No email address";
    public string Description => string.IsNullOrWhiteSpace(Group.Description) ? "No description" : Group.Description;
    public string Visibility => Group.Visibility ?? "Not Set";
    public string CreatedText => Group.CreatedAt?.ToLocalTime().ToString("MMMM d, yyyy") ?? "Unknown";

    public IReadOnlyList<MemberRow> Members { get; private set; }

    /// <summary>The first few members, for the narrow preview pane.</summary>
    public IReadOnlyList<MemberRow> FirstMembers => Members.Take(PreviewMembers).ToArray();

    public bool HasMembers => Members.Count > 0;

    public string MoreMembersText => Members.Count > PreviewMembers ? $"and {Members.Count - PreviewMembers:N0} more" : "";

    /// <summary>"42 members: 40 users, 2 groups".</summary>
    public string MembersSummary
    {
        get
        {
            if (Members.Count == 0)
                return Group.IsMailbox ? "Nobody has been given access."
                    : Group.IsDynamic ? "No members match the rule yet."
                    : "No members.";
            if (Group.IsMailbox)
                return $"{Members.Count:N0} {(Members.Count == 1 ? "has" : "have")} access. Full access opens the mailbox; Send as and Send on behalf send from it.";
            var parts = Members.GroupBy(m => m.TypeText)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count():N0} {Plural(g.Key, g.Count())}");
            return $"{Members.Count:N0} direct {(Members.Count == 1 ? "member" : "members")}: {string.Join(", ", parts)}";
        }
    }

    [RelayCommand]
    void OpenMember(MemberRow member)
    {
        if (member.User is { } user)
            _openUser(user);
        else if (member.Group is { } group)
            _openGroup(group);
    }

    /// <summary>Whether this page matches Microsoft 365: checked when the page opens (see MainViewModel).</summary>
    public RecordFreshness Freshness { get; } = new();

    /// <summary>Shows the group as it is now (after changes are applied and read back).</summary>
    public void Update(GroupSummary group)
    {
        Group = group;
        Members = Rows(group);
        People.Update(group);
        foreach (var name in new[]
                 {
                     nameof(Group), nameof(Title), nameof(Initials), nameof(TypeText), nameof(TypeKind), nameof(MembershipText), nameof(Account),
                     nameof(HasAccount), nameof(Email), nameof(Description), nameof(Visibility), nameof(Members), nameof(FirstMembers),
                     nameof(HasMembers), nameof(MoreMembersText), nameof(MembersSummary), nameof(OwnersText),
                 })
            OnPropertyChanged(name);
    }

    IReadOnlyList<MemberRow> Rows(GroupSummary group) => group.Members
        .Select(m => GroupAccess.Describe(m, _index) with { Access = m.Access })
        // People first, then nested groups, then devices, contacts and apps.
        .OrderBy(r => r.TypeText switch { "User" => 0, "Group" => 1, _ => 2 })
        .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    static string Plural(string type, int count) => count == 1 ? type.ToLowerInvariant() : type.ToLowerInvariant() + "s";

    public void Dispose() => People.Dispose();
}
