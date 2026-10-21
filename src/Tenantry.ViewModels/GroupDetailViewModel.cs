using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels;

/// <summary>A group member as the record page shows it, with the name looked up locally.</summary>
/// <param name="Access">For a mailbox: what this person can do ("Full access, Send as").</param>
public sealed record MemberRow(string Name, string? Detail, string TypeText, BadgeKind TypeKind, UserSummary? User, GroupSummary? Group, string? Access = null)
{
    public bool CanOpen => User is not null || Group is not null;

    public bool HasAccess => !string.IsNullOrEmpty(Access);
}

/// <summary>Looks up a directory object by id in the local copy, for naming group members.</summary>
public interface IDirectoryIndex
{
    UserSummary? User(string id);
    GroupSummary? Group(string id);
}

/// <summary>A group's record page. Read-only until the change pipeline exists.</summary>
public sealed partial class GroupDetailViewModel : TabViewModel
{
    const int PreviewMembers = 12;

    readonly Action<UserSummary> _openUser;
    readonly Action<GroupSummary> _openGroup;

    public GroupDetailViewModel(GroupSummary group, IDirectoryIndex index, Action<UserSummary>? openUser = null, Action<GroupSummary>? openGroup = null)
    {
        Group = group;
        _openUser = openUser ?? (_ => { });
        _openGroup = openGroup ?? (_ => { });
        Account = group.IsMailbox ? index.User(group.Id) : null;
        Members = group.Members
            .Select(m => Row(m, index) with { Access = m.Access })
            // People first, then nested groups, then devices, contacts and apps.
            .OrderBy(r => r.TypeText switch { "User" => 0, "Group" => 1, _ => 2 })
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public GroupSummary Group { get; }

    public override string Title => Group.DisplayName;

    public string Initials => Display.Initials(Group.DisplayName);

    public string TypeText => GroupList.TypeBadge(Group.Kind).Text;
    public BadgeKind TypeKind => GroupList.TypeBadge(Group.Kind).Kind;

    public string MembershipText => Group.IsDynamic ? "Dynamic membership" : "Assigned membership";
    public bool ShowMembership => !Group.IsMailbox;
    public bool IsTeam => Group.IsTeam;
    public bool IsMailbox => Group.IsMailbox;

    /// <summary>A mailbox's own account (a user object), for its sign-in and license settings.</summary>
    public UserSummary? Account { get; }

    public bool HasAccount => Account is not null;

    public string MembersHeading => Group.IsMailbox ? "People with access" : "Members";

    [RelayCommand]
    void OpenAccount()
    {
        if (Account is { } account)
            _openUser(account);
    }

    public string Email => Group.Mail ?? "No email address";
    public string Description => string.IsNullOrWhiteSpace(Group.Description) ? "No description" : Group.Description;
    public string Visibility => Group.Visibility ?? "Not set";
    public string CreatedText => Group.CreatedAt?.ToLocalTime().ToString("MMMM d, yyyy") ?? "Unknown";

    public IReadOnlyList<MemberRow> Members { get; }

    /// <summary>The first few members, for the narrow preview pane.</summary>
    public IReadOnlyList<MemberRow> FirstMembers => Members.Take(PreviewMembers).ToArray();

    public bool HasMembers => Members.Count > 0;

    public string MoreMembersText => Members.Count > PreviewMembers ? $"and {Members.Count - PreviewMembers:N0} more; open the group to see everyone" : "";

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

    static string Plural(string type, int count) => count == 1 ? type.ToLowerInvariant() : type.ToLowerInvariant() + "s";

    static MemberRow Row(GroupMember member, IDirectoryIndex index) => member.Type switch
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
}
