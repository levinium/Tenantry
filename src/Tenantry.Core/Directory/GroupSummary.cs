namespace Tenantry.Core.Directory;

public enum GroupKind
{
    Microsoft365,
    Security,
    MailEnabledSecurity,
    Distribution,
    SharedMailbox,
    RoomMailbox,
    EquipmentMailbox,
}

/// <summary>
/// A direct member of a group, or someone with access to a shared mailbox. <see cref="Type"/> is
/// Graph's (user, group, device, orgContact, servicePrincipal), or "unresolved" for an address
/// the local copy can't match, with the address in <see cref="Name"/>.
/// </summary>
/// <param name="Access">For mailboxes: what the person can do ("Full access, Send as").</param>
public sealed record GroupMember(string Id, string Type, string? Access = null, string? Name = null);

/// <summary>
/// A row in Groups &amp; shared mailboxes: a group with its direct members, or a shared, room or
/// equipment mailbox with the people who have access. A mailbox's <see cref="Id"/> is its
/// account's user id.
/// </summary>
public sealed record GroupSummary(
    string Id,
    string DisplayName,
    string? Mail,
    string? Description,
    GroupKind Kind,
    bool IsDynamic,
    bool IsTeam,
    string? Visibility,
    DateTimeOffset? CreatedAt,
    IReadOnlyList<GroupMember> Members,
    IReadOnlyList<GroupMember>? Owners = null)
{
    /// <summary>Who owns the group (manages it); shared mailboxes have none.</summary>
    public IReadOnlyList<GroupMember> OwnerList => Owners ?? [];

    /// <summary>
    /// Microsoft 365 and security groups change through Graph; distribution lists and
    /// mail-enabled security groups through Exchange. Dynamic groups follow their rule.
    /// </summary>
    public bool IsExchangeManaged => Kind is GroupKind.Distribution or GroupKind.MailEnabledSecurity;

    public bool IsMailbox => Kind is GroupKind.SharedMailbox or GroupKind.RoomMailbox or GroupKind.EquipmentMailbox;

    public static string KindName(GroupKind kind) => kind switch
    {
        GroupKind.Microsoft365 => "Microsoft 365",
        GroupKind.Security => "Security",
        GroupKind.MailEnabledSecurity => "Mail-enabled security",
        GroupKind.Distribution => "Distribution list",
        GroupKind.SharedMailbox => UserFields.SharedMailboxType,
        GroupKind.RoomMailbox => UserFields.RoomType,
        _ => UserFields.EquipmentType,
    };

    /// <summary>The kind for an Exchange mailbox type ("Shared", "Room", "Equipment"), or null for a person's mailbox.</summary>
    public static GroupKind? MailboxKind(string? mailboxType) => mailboxType switch
    {
        "Shared" => GroupKind.SharedMailbox,
        "Room" => GroupKind.RoomMailbox,
        "Equipment" => GroupKind.EquipmentMailbox,
        _ => null,
    };
}
