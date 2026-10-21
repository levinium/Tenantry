namespace Tenantry.Core.Directory;

public enum ForwardingKind
{
    None,

    /// <summary>To a recipient or address in one of the tenant's own domains.</summary>
    Internal,

    /// <summary>To an address outside the tenant's domains: worth a second look.</summary>
    External,
}

/// <summary>A user's Exchange mailbox, as far as the user list needs it.</summary>
/// <param name="Type">"User", "Shared", "Room", "Equipment" or Exchange's own name for anything else.</param>
/// <param name="UsedBytes">Size, from the hourly mailbox-details read; null until it has run.</param>
/// <param name="QuotaBytes">The send-and-receive limit; null when unlimited or not read yet.</param>
/// <param name="AutoReply">Automatic replies' state, from the same read; null until it has run.</param>
/// <param name="Apps">Which email apps can reach it (Get-CASMailbox); null until read.</param>
public sealed record MailboxInfo(
    string Type,
    string? PrimaryAddress,
    IReadOnlyList<string> Aliases,
    ForwardingKind Forwarding,
    string? ForwardingTo,
    bool KeepsCopyWhenForwarding,
    bool LitigationHold,
    bool ArchiveEnabled,
    bool HiddenFromAddressList,
    long? UsedBytes = null,
    long? QuotaBytes = null,
    AutoReplyState? AutoReply = null,
    EmailApps? Apps = null)
{
    /// <summary>Used / quota as a percentage (0–100+), when both are known.</summary>
    public double? UsedPercent => UsedBytes is { } used && QuotaBytes is { } quota && quota > 0 ? 100.0 * used / quota : null;

    /// <summary>90% or more of the limit: mail stops arriving at 100%.</summary>
    public bool IsNearlyFull => UsedPercent >= 90;
}
