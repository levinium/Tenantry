namespace Tenantry.Core.Directory;

/// <summary>A deleted Microsoft 365 or security group, restorable for 30 days after it was deleted.</summary>
public sealed record DeletedGroup(string Id, string DisplayName, string? Mail, GroupKind Kind, DateTimeOffset? DeletedAt);

/// <summary>
/// Whether one person would be in a dynamic group under a rule (Entra's own evaluation), or why
/// the rule can't be checked (usually a mistake in it).
/// </summary>
public sealed record RuleTest(string PersonId, string PersonName, bool? Matches, string? Problem);

/// <summary>Group reads that aren't kept in the local copy: deleted groups, and testing a dynamic rule.</summary>
public interface IGroupRecords
{
    Task<IReadOnlyList<DeletedGroup>> GetDeletedGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks one person against a rule that hasn't been saved (Entra's beta evaluateDynamicMembership).</summary>
    Task<RuleTest> TestRuleAsync(string rule, string personId, string personName, CancellationToken cancellationToken = default);
}

/// <summary>What can be changed on a group, by kind (from docs/research/group-settings-catalog.md).</summary>
public static class GroupRules
{
    /// <summary>Graph can write Microsoft 365 and security groups; lists and mail-enabled security groups change only through Exchange.</summary>
    public static bool ThroughExchange(GroupKind kind) => kind is GroupKind.Distribution or GroupKind.MailEnabledSecurity;

    /// <summary>Exchange limits a Microsoft 365 group's display name to 64 characters; Graph and lists allow 256.</summary>
    public static int NameLimit(GroupKind kind) => kind == GroupKind.Microsoft365 ? 64 : 256;

    /// <summary>What to call it in a button or change ("Delete Distribution List", "Restore Group").</summary>
    public static string Noun(GroupKind kind) => kind == GroupKind.Distribution ? "Distribution List" : "Group";

    /// <summary>Dynamic rules are capped at 3,072 characters.</summary>
    public const int RuleLimit = 3072;

    /// <summary>Microsoft 365 and cloud security groups are kept 30 days after deleting; lists are gone at once.</summary>
    public static bool Restorable(GroupKind kind) => kind is GroupKind.Microsoft365 or GroupKind.Security;

    /// <summary>What's wrong with an alias (mailNickname), or null. The stricter Graph character set, as the catalog advises.</summary>
    public static string? AliasProblem(string alias)
    {
        if (alias.Length == 0)
            return "An alias can't be empty.";
        if (alias.Length > 64)
            return "An alias can be at most 64 characters.";
        const string bad = "@()\\[]\";:<>, ";
        if (alias.Any(c => bad.Contains(c) || c > 127))
            return "An alias can use letters, digits, dots, dashes and underscores, with no spaces.";
        if (alias.StartsWith('.') || alias.EndsWith('.') || alias.Contains("..", StringComparison.Ordinal))
            return "An alias can't start or end with a dot, or have two dots together.";
        return null;
    }

    /// <summary>A rough check before Entra's: brackets and quotes must pair up.</summary>
    public static string? RuleShapeProblem(string rule)
    {
        if (rule.Trim().Length == 0)
            return "Write the rule for who belongs, like (user.department -eq \"Finance\").";
        if (rule.Length > RuleLimit)
            return $"A rule can be at most {RuleLimit:N0} characters.";
        var depth = 0;
        var quoted = false;
        foreach (var c in rule)
        {
            if (c == '"')
                quoted = !quoted;
            else if (!quoted && c == '(')
                depth++;
            else if (!quoted && c == ')' && --depth < 0)
                return "A closing bracket has no opening one.";
        }
        return quoted ? "A quote isn't closed." : depth > 0 ? "A bracket isn't closed." : null;
    }
}
