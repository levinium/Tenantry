using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>What to search the unified audit log for. Empty lists mean "any".</summary>
public sealed record AuditSearchRequest(
    string Name,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<string> Users,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> RecordTypes,
    string? Keyword = null,
    IReadOnlyList<string>? IpAddresses = null,
    IReadOnlyList<string>? ObjectIds = null);

/// <summary>
/// A search of the unified audit log. Purview runs it in the background: it can wait queued for
/// many minutes before it starts (seen live), so the page shows its state and checks back.
/// </summary>
/// <param name="Status">"notStarted", "running", "succeeded", "failed" or "cancelled".</param>
public sealed record AuditSearch(string Id, string Name, DateTimeOffset? Start, DateTimeOffset? End, string Status, long? ApproximateCount, bool LimitExceeded,
    AuditSearchRequest Filters)
{
    public bool IsFinished => Status is "succeeded" or "failed" or "cancelled";
    public bool IsDone => Status == "succeeded";

    public string StatusText => Status switch
    {
        "notStarted" => "Queued",
        "running" => "Running",
        "succeeded" => "Done",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        _ => Status,
    };
}

/// <summary>One audited event: who did what, in which service, to what, from where; the whole record as JSON.</summary>
public sealed record AuditRecord(string Id, DateTimeOffset? When, string? User, string? UserType, string Operation, string? Service, string? RecordType,
    string? ObjectId, string? ClientIp, string Json);

/// <summary>The unified audit log (8e), searched through Graph's audit log queries.</summary>
public interface IAuditRecords
{
    /// <summary>Searches already run (Purview keeps them), newest first.</summary>
    Task<IReadOnlyList<AuditSearch>> GetSearchesAsync(CancellationToken cancellationToken = default);

    Task<AuditSearch> StartSearchAsync(AuditSearchRequest request, CancellationToken cancellationToken = default);

    Task<AuditSearch> GetSearchAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>A finished search's records, up to <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<AuditRecord>> GetRecordsAsync(string id, int limit, CancellationToken cancellationToken = default);
}

/// <summary>The kinds of record most searches want, in words (Graph's auditLogRecordType values).</summary>
public static class AuditRecordTypes
{
    public static IReadOnlyList<(string Value, string Label)> Common { get; } =
    [
        ("azureActiveDirectory", "Entra ID: Directory Changes"),
        ("azureActiveDirectoryStsLogon", "Entra ID: Sign-Ins"),
        ("exchangeAdmin", "Exchange: Admin Changes"),
        ("exchangeItem", "Exchange: Mailbox Items"),
        ("exchangeItemGroup", "Exchange: Many Mailbox Items"),
        ("sharePointFileOperation", "SharePoint and OneDrive: Files"),
        ("sharePointSharingOperation", "SharePoint and OneDrive: Sharing"),
        ("sharePoint", "SharePoint: Sites"),
        ("microsoftTeams", "Teams"),
        ("securityComplianceCenterEOPCmdlet", "Purview and Defender: Admin Changes"),
        ("threatIntelligence", "Defender: Email Threats"),
        ("dataGovernance", "Retention"),
        ("discovery", "eDiscovery"),
        ("powerBIAudit", "Power BI"),
        ("microsoftFlow", "Power Automate"),
        ("copilotInteraction", "Copilot"),
    ];

    public static string Label(string? value) =>
        value is null ? "" : Common.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)).Label ?? value;
}

/// <summary>The audit results list's fields.</summary>
public static class AuditFields
{
    public const string When = "when";
    public const string User = "user";
    public const string Operation = "operation";
    public const string Service = "service";
    public const string RecordType = "recordtype";
    public const string Object = "object";
    public const string Ip = "ip";

    public static IReadOnlyList<Field<AuditRecord>> All { get; } =
    [
        new() { Key = When, Label = "When", Kind = FieldKind.Date, Date = r => r.When },
        new() { Key = User, Label = "Who", Kind = FieldKind.Text, Searchable = true, Aliases = ["user", "person"], Text = r => r.User },
        new() { Key = Operation, Label = "Activity", Kind = FieldKind.Choice, Searchable = true, Aliases = ["op", "activity"], Text = r => r.Operation },
        new() { Key = Service, Label = "Service", Kind = FieldKind.Choice, Text = r => r.Service },
        new() { Key = RecordType, Label = "Record Type", Kind = FieldKind.Choice, Aliases = ["type"], Text = r => AuditRecordTypes.Label(r.RecordType) },
        new() { Key = Object, Label = "Object", Kind = FieldKind.Text, Searchable = true, Aliases = ["item", "target"], Text = r => r.ObjectId },
        new() { Key = Ip, Label = "IP Address", Kind = FieldKind.Text, Searchable = true, Text = r => r.ClientIp },
    ];
}
