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
public sealed record MailboxInfo(
    string Type,
    string? PrimaryAddress,
    IReadOnlyList<string> Aliases,
    ForwardingKind Forwarding,
    string? ForwardingTo,
    bool KeepsCopyWhenForwarding,
    bool LitigationHold,
    bool ArchiveEnabled,
    bool HiddenFromAddressList);
