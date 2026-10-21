using System.Globalization;
using System.Text.Json;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>The Mail tab's mailbox-contents pages (4b-3), read live from Exchange.</summary>
public sealed partial class GraphUserRecords : IMailboxContents
{
    ICmdletRunner Exchange => exchange ?? throw new InvalidOperationException("Exchange Online isn't connected for this tenant.");

    Task<IReadOnlyList<JsonElement>> Run(string cmdlet, Dictionary<string, object?> parameters, CancellationToken cancellationToken, string[]? select = null) =>
        Exchange.InvokeAsync(CmdletEndpoint.Exchange, cmdlet, parameters, cancellationToken, select);

    public async Task<IReadOnlyList<InboxRule>> GetInboxRulesAsync(string address, CancellationToken cancellationToken = default)
    {
        var rows = await Run("Get-InboxRule", new() { ["Mailbox"] = address }, cancellationToken,
            ["Name", "RuleIdentity", "Enabled", "Priority", "Description", "InError", "ForwardTo", "ForwardAsAttachmentTo", "RedirectTo", "DeleteMessage",
             "MoveToFolder"]);
        return rows.Select(r => ToRule(r, address)).OrderBy(r => r.Priority).ToArray();
    }

    /// <summary>A rule as Get-InboxRule returns it; its identity is "mailbox\ruleId" (the ID kept as written, it's a 64-bit number).</summary>
    public static InboxRule ToRule(JsonElement r, string address) => new(
        Identity: $"{address}\\{(r.TryGetProperty("RuleIdentity", out var id) ? id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText() : "")}",
        Name: Str(r, "Name") ?? "(no name)",
        Enabled: Bool(r, "Enabled"),
        Priority: r.TryGetProperty("Priority", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
        Description: (Str(r, "Description") ?? "").Replace("\r", "").Replace("\t", "  ").Trim(),
        InError: Bool(r, "InError"),
        ForwardsTo: new[] { "ForwardTo", "ForwardAsAttachmentTo", "RedirectTo" }.SelectMany(n => List(r, n)).ToArray(),
        Deletes: Bool(r, "DeleteMessage"),
        MovesTo: Str(r, "MoveToFolder"));

    public async Task<FolderSharing> GetCalendarSharingAsync(string address, CancellationToken cancellationToken = default)
    {
        // The calendar's name follows the mailbox's language ("Calendar", "Calendrier"…): find it by type.
        var folders = await Run("Get-MailboxFolderStatistics", new() { ["Identity"] = address, ["FolderScope"] = "Calendar" }, cancellationToken,
            ["Name", "FolderPath", "FolderType"]);
        var calendar = folders.FirstOrDefault(f => Str(f, "FolderType") == "Calendar");
        var path = calendar.ValueKind == JsonValueKind.Object ? Str(calendar, "FolderPath") ?? "/Calendar" : "/Calendar";
        var name = calendar.ValueKind == JsonValueKind.Object ? Str(calendar, "Name") ?? "Calendar" : "Calendar";
        var identity = $"{address}:{path.Replace('/', '\\')}";
        var rows = await Run("Get-MailboxFolderPermission", new() { ["Identity"] = identity }, cancellationToken, ["User", "AccessRights", "SharingPermissionFlags"]);
        var permissions = rows.Select(r => new FolderPermission(
                Str(r, "User") ?? "",
                List(r, "AccessRights").FirstOrDefault() ?? "None",
                (Str(r, "SharingPermissionFlags") ?? "").Contains("Delegate", StringComparison.OrdinalIgnoreCase)))
            .Where(p => p.User.Length > 0)
            // Default and Anonymous first, as Outlook shows them.
            .OrderBy(p => p.User switch { "Default" => 0, "Anonymous" => 1, _ => 2 })
            .ThenBy(p => p.User, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new FolderSharing(identity, name, permissions);
    }

    public async Task<IReadOnlyList<MobileDevice>> GetMobileDevicesAsync(string address, CancellationToken cancellationToken = default)
    {
        var devices = Run("Get-MobileDevice", new() { ["Mailbox"] = address }, cancellationToken,
            ["Identity", "Guid", "DeviceId", "FriendlyName", "DeviceModel", "DeviceOS", "DeviceType", "ClientType", "FirstSyncTime", "DeviceAccessState"]);
        var statistics = Run("Get-MobileDeviceStatistics", new() { ["Mailbox"] = address }, cancellationToken,
            ["Guid", "Identity", "LastSuccessSync", "DeviceWipeSentTime", "DeviceWipeAckTime", "Status"]);
        var byGuid = (await statistics).Where(s => Str(s, "Guid") is not null).GroupBy(s => Str(s, "Guid")!).ToDictionary(g => g.Key, g => g.First());
        return (await devices).Select(d =>
            {
                var stats = Str(d, "Guid") is { } guid && byGuid.TryGetValue(guid, out var s) ? s : default;
                var model = Str(d, "DeviceModel") ?? Str(d, "DeviceType") ?? "Device";
                var wipeAck = stats.ValueKind == JsonValueKind.Object ? Date(stats, "DeviceWipeAckTime") : null;
                var wipeSent = stats.ValueKind == JsonValueKind.Object ? Date(stats, "DeviceWipeSentTime") : null;
                return new MobileDevice(
                    Identity: Str(d, "Identity") ?? Str(d, "Guid") ?? "",
                    DeviceId: Str(d, "DeviceId") ?? "",
                    Name: Str(d, "FriendlyName") is { Length: > 0 } friendly ? friendly : model,
                    Model: model,
                    Os: Str(d, "DeviceOS") ?? "",
                    ClientType: Str(d, "ClientType") is "Outlook" ? "Outlook app" : "Built-in mail app",
                    FirstSync: Date(d, "FirstSyncTime"),
                    LastSync: stats.ValueKind == JsonValueKind.Object ? Date(stats, "LastSuccessSync") : null,
                    AccessState: Str(d, "DeviceAccessState") ?? "Allowed",
                    WipeStatus: wipeAck is { } ack ? $"Wiped {ack.ToLocalTime():d MMM yyyy}"
                        : wipeSent is { } sent ? $"Wipe sent {sent.ToLocalTime():d MMM yyyy}, not yet confirmed by the device"
                        : null);
            })
            .OrderByDescending(d => d.LastSync ?? d.FirstSync ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    public async Task<IReadOnlyList<DeletedItem>> GetDeletedItemsAsync(string address, string? subject, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = address, ["ResultSize"] = 200 };
        if (!string.IsNullOrWhiteSpace(subject))
            parameters["SubjectContains"] = subject.Trim();
        var rows = await Run("Get-RecoverableItems", parameters, cancellationToken);
        return rows.Select(r => new DeletedItem(
                Str(r, "EntryID") ?? Str(r, "EntryId") ?? "",
                Str(r, "Subject") is { Length: > 0 } s ? s : "(no subject)",
                Str(r, "ItemClass") ?? "",
                Str(r, "LastParentPath") ?? Str(r, "SourceFolder"),
                Date(r, "LastModifiedTime")))
            .Where(i => i.EntryId.Length > 0)
            .OrderByDescending(i => i.Modified ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    /// <summary>Message trace keeps 90 days; one search covers at most 10.</summary>
    public const int MaxTraceDays = 10;

    public async Task<IReadOnlyList<TracedMessage>> TraceAsync(string address, TraceDirection direction, int days, CancellationToken cancellationToken = default)
    {
        var end = DateTime.UtcNow;
        var parameters = new Dictionary<string, object?>
        {
            [direction == TraceDirection.Sent ? "SenderAddress" : "RecipientAddress"] = address,
            ["StartDate"] = end.AddDays(-Math.Clamp(days, 1, MaxTraceDays)).ToString("o", CultureInfo.InvariantCulture),
            ["EndDate"] = end.ToString("o", CultureInfo.InvariantCulture),
            ["ResultSize"] = 1000,
        };
        var rows = await Run("Get-MessageTraceV2", parameters, cancellationToken);
        return rows.Select(r => new TracedMessage(
                Str(r, "MessageTraceId") ?? "",
                Date(r, "Received") ?? DateTimeOffset.MinValue,
                Str(r, "SenderAddress") ?? "",
                Str(r, "RecipientAddress") ?? "",
                Str(r, "Subject") is { Length: > 0 } s ? s : "(no subject)",
                Str(r, "Status") ?? "",
                r.TryGetProperty("Size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null))
            .OrderByDescending(m => m.Received)
            .ToArray();
    }

    public async Task<IReadOnlyList<TraceEvent>> TraceDetailAsync(TracedMessage message, CancellationToken cancellationToken = default)
    {
        var rows = await Run("Get-MessageTraceDetailV2", new() { ["MessageTraceId"] = message.TraceId, ["RecipientAddress"] = message.Recipient },
            cancellationToken);
        return rows.Select(r => new TraceEvent(Date(r, "Date") ?? DateTimeOffset.MinValue, Str(r, "Event") ?? "", Str(r, "Action") ?? "", Str(r, "Detail") ?? ""))
            .OrderBy(e => e.Date)
            .ToArray();
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.GetRawText(),
        } : null;

    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static IEnumerable<string> List(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()).OfType<string>(),
            JsonValueKind.String => [v.GetString()!],
            _ => [],
        } : [];

    static DateTimeOffset? Date(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
