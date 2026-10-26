namespace Tenantry.Core.Directory;

/// <summary>A mail flow (transport) rule's mode: whether it acts on mail or only records matches.</summary>
public enum RuleMode
{
    Enforce,

    /// <summary>Exchange's "Audit": records matches, acts on nothing.</summary>
    Test,

    /// <summary>Exchange's "AuditAndNotify": records matches and shows senders Policy Tips.</summary>
    TestWithPolicyTips,
}

/// <summary>
/// One mail flow rule. <see cref="Conditions"/>, <see cref="Exceptions"/> and <see cref="Actions"/>
/// are Exchange's own plain-English description of it, split into its parts.
/// </summary>
/// <param name="Priority">0 runs first.</param>
public sealed record MailFlowRule(
    string Identity,
    string Name,
    int Priority,
    bool Enabled,
    RuleMode Mode,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<string> Exceptions,
    IReadOnlyList<string> Actions,
    string? Comments,
    DateTimeOffset? ActivationDate,
    DateTimeOffset? ExpiryDate,
    DateTimeOffset? Changed,
    string? ChangedBy)
{
    public static string ModeName(RuleMode mode) => mode switch
    {
        RuleMode.Enforce => "Enforce",
        RuleMode.Test => "Test",
        _ => "Test with Policy Tips",
    };

    public static RuleMode ParseMode(string? mode) => mode switch
    {
        "Audit" => RuleMode.Test,
        "AuditAndNotify" => RuleMode.TestWithPolicyTips,
        _ => RuleMode.Enforce,
    };

    public static string ExchangeMode(RuleMode mode) => mode switch
    {
        RuleMode.Test => "Audit",
        RuleMode.TestWithPolicyTips => "AuditAndNotify",
        _ => "Enforce",
    };

    /// <summary>
    /// Splits Exchange's description ("If the message:" / "Except if the message:" / "Take the
    /// following actions:", each followed by indented lines) into its parts.
    /// </summary>
    public static (IReadOnlyList<string> Conditions, IReadOnlyList<string> Exceptions, IReadOnlyList<string> Actions) ParseDescription(string? description)
    {
        var conditions = new List<string>();
        var exceptions = new List<string>();
        var actions = new List<string>();
        var current = conditions;
        foreach (var raw in (description ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var text = line.Trim();
            if (text.Length == 0)
                continue;
            if (text.StartsWith("If the message", StringComparison.OrdinalIgnoreCase))
                current = conditions;
            else if (text.StartsWith("Except if", StringComparison.OrdinalIgnoreCase))
                current = exceptions;
            else if (text.StartsWith("Take the following action", StringComparison.OrdinalIgnoreCase))
                current = actions;
            else if (char.IsWhiteSpace(line[0]) || current.Count > 0)
            {
                // "and" on its own line joins two parts of one condition.
                if (text.Equals("and", StringComparison.OrdinalIgnoreCase) && current.Count > 0)
                    continue;
                current.Add(text);
            }
            else
                current.Add(text);
        }
        return (conditions, exceptions, actions);
    }
}
