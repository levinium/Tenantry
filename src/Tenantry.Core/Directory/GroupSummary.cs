namespace Tenantry.Core.Directory;

public enum GroupKind
{
    Microsoft365,
    Security,
    MailEnabledSecurity,
    Distribution,
}

/// <summary>A direct member of a group. <see cref="Type"/> is Graph's: user, group, device, orgContact, servicePrincipal.</summary>
public sealed record GroupMember(string Id, string Type);

/// <summary>The fields a group list row needs, plus its direct members.</summary>
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
    IReadOnlyList<GroupMember> Members)
{
    public static string KindName(GroupKind kind) => kind switch
    {
        GroupKind.Microsoft365 => "Microsoft 365",
        GroupKind.Security => "Security",
        GroupKind.MailEnabledSecurity => "Mail-enabled security",
        _ => "Distribution list",
    };
}
