using System.Globalization;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>Mail &amp; security's reads (5a): quarantine and organization-wide message trace, through Exchange.</summary>
public sealed partial class GraphUserRecords : IMailSecurity
{
    /// <summary>Exchange keeps quarantined mail up to 30 days and pages it 1,000 at a time.</summary>
    const int QuarantineDays = 30;
    const int QuarantinePage = 1000;
    const int QuarantineMaxPages = QuarantinedMessage.MaxRead / QuarantinePage;

    public async Task<IReadOnlyList<QuarantinedMessage>> GetQuarantineAsync(int days, CancellationToken cancellationToken = default)
    {
        var end = DateTime.UtcNow;
        var start = end.AddDays(-Math.Clamp(days, 1, QuarantineDays));
        var messages = new List<QuarantinedMessage>();
        for (var page = 1; page <= QuarantineMaxPages; page++)
        {
            var rows = await Run("Get-QuarantineMessage", new()
            {
                ["StartReceivedDate"] = start.ToString("o", CultureInfo.InvariantCulture),
                ["EndReceivedDate"] = end.ToString("o", CultureInfo.InvariantCulture),
                ["PageSize"] = QuarantinePage,
                ["Page"] = page,
            }, cancellationToken);
            messages.AddRange(rows.Select(ToQuarantined));
            if (rows.Count < QuarantinePage)
                break;
        }
        return messages.OrderByDescending(m => m.Received).ToArray();
    }

    static QuarantinedMessage ToQuarantined(JsonElement r) => new(
        Str(r, "Identity") ?? "",
        Date(r, "ReceivedTime") ?? DateTimeOffset.MinValue,
        Str(r, "SenderAddress") ?? "",
        List(r, "RecipientAddress").ToArray(),
        Str(r, "Subject") is { Length: > 0 } s ? s : "(no subject)",
        r.TryGetProperty("Size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null,
        // QuarantineTypes is the code ("HighConfPhish"); Type is wording ("High Confidence Phish"), seen live 2026-11-10.
        Str(r, "QuarantineTypes") is { Length: > 0 } code ? code : Str(r, "Type") ?? "",
        Str(r, "PolicyName"),
        Date(r, "Expires"),
        Str(r, "ReleaseStatus") ?? "",
        Str(r, "Direction"),
        List(r, "ReleasedUser").ToArray());

    /// <summary>One message read on its own: only then does Exchange say who it was released to (seen live).</summary>
    public async Task<QuarantinedMessage?> GetQuarantinedAsync(string identity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var rows = await Run("Get-QuarantineMessage", new() { ["Identity"] = identity }, cancellationToken);
        return rows.Count == 1 ? ToQuarantined(rows[0]) : null;
    }

    public async Task<string> GetQuarantineHeadersAsync(string identity, CancellationToken cancellationToken = default)
    {
        // A missing identity would read EVERY message's headers (Exchange's null-identity trap).
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var rows = await Run("Get-QuarantineMessageHeader", new() { ["Identity"] = identity }, cancellationToken);
        return rows.Count > 0 ? Str(rows[0], "Header") ?? "" : throw new InvalidOperationException("Exchange returned no headers for it.");
    }

    public async Task<byte[]> ExportQuarantinedAsync(string identity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var rows = await Run("Export-QuarantineMessage", new() { ["Identity"] = identity }, cancellationToken);
        var eml = rows.Count > 0 ? Str(rows[0], "Eml") : null;
        if (string.IsNullOrEmpty(eml))
            throw new InvalidOperationException("Exchange returned nothing to download.");
        // Base64 as Exchange sends it; plain text when it says so.
        return string.Equals(Str(rows[0], "BodyEncoding"), "Base64", StringComparison.OrdinalIgnoreCase) || !eml.Contains(':')
            ? Convert.FromBase64String(eml)
            : System.Text.Encoding.UTF8.GetBytes(eml);
    }

    public async Task<QuarantinePreview> PreviewQuarantinedAsync(string identity, CancellationToken cancellationToken = default)
    {
        var rows = await Run("Preview-QuarantineMessage", new() { ["Identity"] = identity }, cancellationToken);
        var r = rows.Count > 0 ? rows[0] : throw new InvalidOperationException("Exchange returned nothing to preview.");
        var body = Str(r, "Body") ?? "";
        return new QuarantinePreview(
            Str(r, "Subject") ?? "",
            Str(r, "SenderAddress") ?? Str(r, "From") ?? "",
            string.Join(", ", List(r, "RecipientAddress").DefaultIfEmpty(Str(r, "To") ?? "")),
            Date(r, "ReceivedTime") ?? Date(r, "SentTime"),
            body,
            r.TryGetProperty("IsHtml", out var html) ? html.ValueKind == JsonValueKind.True : body.Contains("<html", StringComparison.OrdinalIgnoreCase),
            List(r, "Attachment").ToArray());
    }

    /// <summary>The four list types and the spoofed senders, read together.</summary>
    public async Task<IReadOnlyList<ListEntry>> GetListEntriesAsync(CancellationToken cancellationToken = default)
    {
        ListKind[] kinds = [ListKind.Sender, ListKind.Url, ListKind.File, ListKind.IP];
        var reads = kinds.Select(async kind => (kind, rows: await Run("Get-TenantAllowBlockListItems", new() { ["ListType"] = AllowBlock.ListType(kind) }, cancellationToken))).ToArray();
        var spoof = Run("Get-TenantAllowBlockListSpoofItems", new(), cancellationToken);
        var entries = new List<ListEntry>();
        foreach (var (kind, rows) in await Task.WhenAll(reads))
            entries.AddRange(rows.Select(r => new ListEntry(
                Str(r, "Identity") ?? "", kind, Str(r, "Value") ?? "",
                string.Equals(Str(r, "Action"), "Block", StringComparison.OrdinalIgnoreCase),
                Str(r, "Notes") is { Length: > 0 } notes ? notes : null,
                Date(r, "ExpirationDate"),
                r.TryGetProperty("RemoveAfter", out var after) && after.ValueKind == JsonValueKind.Number ? after.GetInt32() : null,
                Date(r, "LastUsedDate"), Date(r, "CreatedDateTime"), Date(r, "LastModifiedDateTime"),
                Str(r, "ModifiedBy"), Str(r, "ListSubType"))));
        entries.AddRange((await spoof).Select(r => new ListEntry(
            Str(r, "Identity") ?? "", ListKind.Spoof, Str(r, "SpoofedUser") ?? "",
            string.Equals(Str(r, "Action"), "Block", StringComparison.OrdinalIgnoreCase),
            null, null, null, Date(r, "LastUsedDate"), Date(r, "CreatedDateTime"), Date(r, "LastModifiedDateTime"), Str(r, "ModifiedBy"), null,
            Str(r, "SendingInfrastructure"), Str(r, "SpoofType"))));
        return entries.OrderBy(e => e.Kind).ThenBy(e => e.Value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<MailFlowRule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await Run("Get-TransportRule", new() { ["ResultSize"] = "Unlimited" }, cancellationToken);
        return rows.Select(ToMailFlowRule).OrderBy(r => r.Priority).ToArray();
    }

    public async Task<Core.MailFlow.RuleDraft> GetRuleDraftAsync(string identity, CancellationToken cancellationToken = default)
    {
        var rows = await Run("Get-TransportRule", new() { ["Identity"] = identity }, cancellationToken);
        return rows.Count == 1 ? Core.MailFlow.RuleCodec.Read(rows[0]) : throw new InvalidOperationException("That rule isn't there any more.");
    }

    public static MailFlowRule ToMailFlowRule(JsonElement r)
    {
        var (conditions, exceptions, actions) = MailFlowRule.ParseDescription(Str(r, "Description"));
        return new MailFlowRule(
            Str(r, "Guid") ?? Str(r, "Identity") ?? "",
            Str(r, "Name") ?? "",
            r.TryGetProperty("Priority", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
            !string.Equals(Str(r, "State"), "Disabled", StringComparison.OrdinalIgnoreCase),
            MailFlowRule.ParseMode(Str(r, "Mode")),
            conditions, exceptions, actions,
            Str(r, "Comments") is { Length: > 0 } comments ? comments : null,
            Date(r, "ActivationDate"), Date(r, "ExpiryDate"),
            Date(r, "WhenChanged") ?? Date(r, "WhenChangedUTC"),
            Str(r, "LastModifiedBy") is { Length: > 0 } by ? by : null);
    }

    /// <summary>
    /// Exchange searches 10 days per query, so a longer range (up to 90 days) is searched in
    /// 10-day windows, newest first, until the range is covered or the result limit is reached.
    /// </summary>
    public async Task<IReadOnlyList<TracedMessage>> TraceAsync(TraceSearch search, CancellationToken cancellationToken = default)
    {
        var found = new List<TracedMessage>();
        foreach (var (start, end) in TraceSearch.Windows(search.Start, search.End))
        {
            var left = TraceSearch.MaxResults - found.Count;
            if (left <= 0)
                break;
            found.AddRange(await TraceWindowAsync(search, start, end, left, cancellationToken));
        }
        return found.OrderByDescending(m => m.Received).ToArray();
    }

    async Task<IReadOnlyList<TracedMessage>> TraceWindowAsync(TraceSearch search, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["StartDate"] = from.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["EndDate"] = to.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["ResultSize"] = limit,
        };
        if (!string.IsNullOrWhiteSpace(search.Sender))
            parameters["SenderAddress"] = search.Sender.Trim();
        if (!string.IsNullOrWhiteSpace(search.Recipient))
            parameters["RecipientAddress"] = search.Recipient.Trim();
        if (!string.IsNullOrWhiteSpace(search.Subject))
        {
            parameters["Subject"] = search.Subject.Trim();
            parameters["SubjectFilterType"] = "Contains";
        }
        if (!string.IsNullOrWhiteSpace(search.Status))
            parameters["Status"] = search.Status;
        var rows = await Run("Get-MessageTraceV2", parameters, cancellationToken);
        return rows.Select(r => new TracedMessage(
                Str(r, "MessageTraceId") ?? "",
                Date(r, "Received") ?? DateTimeOffset.MinValue,
                Str(r, "SenderAddress") ?? "",
                Str(r, "RecipientAddress") ?? "",
                Str(r, "Subject") is { Length: > 0 } s ? s : "(no subject)",
                Str(r, "Status") ?? "",
                r.TryGetProperty("Size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null))
            .ToArray();
    }

    // ---- Trace reports (historical searches) ---------------------------------------------------

    public async Task<IReadOnlyList<TraceReport>> GetTraceReportsAsync(CancellationToken cancellationToken = default) =>
        [.. (await Run("Get-HistoricalSearch", new(), cancellationToken)).Select(ToTraceReport).OrderByDescending(r => r.Submitted)];

    static TraceReport ToTraceReport(JsonElement r) => new(
        Str(r, "JobId") ?? Str(r, "Identity") ?? "",
        Str(r, "ReportTitle") ?? "",
        Str(r, "ReportType") ?? "",
        Str(r, "Status") ?? "",
        Date(r, "SubmitDate"), Date(r, "CompletionDate"), Date(r, "StartDate"), Date(r, "EndDate"),
        r.TryGetProperty("Rows", out var rows) && rows.ValueKind == JsonValueKind.Number ? rows.GetInt64() : 0,
        Str(r, "FileUrl") is { Length: > 0 } url ? url : null,
        Str(r, "ErrorDescription") is { Length: > 0 } error ? error : null,
        List(r, "SenderAddress").ToArray(), List(r, "RecipientAddress").ToArray());

    internal static Dictionary<string, object?> TraceReportParameters(TraceReportRequest request)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["ReportTitle"] = request.Title.Trim(), ["ReportType"] = request.Type,
            ["StartDate"] = request.Start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["EndDate"] = request.End.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
        };
        if (request.Senders.Count > 0)
            parameters["SenderAddress"] = request.Senders.ToArray();
        if (request.Recipients.Count > 0)
            parameters["RecipientAddress"] = request.Recipients.ToArray();
        if (!string.IsNullOrWhiteSpace(request.NotifyAddress))
            parameters["NotifyAddress"] = new[] { request.NotifyAddress.Trim() };
        return parameters;
    }

    public async Task<TraceReport> StartTraceReportAsync(TraceReportRequest request, CancellationToken cancellationToken = default)
    {
        var rows = await Run("Start-HistoricalSearch", TraceReportParameters(request), cancellationToken);
        return rows.Count > 0 ? ToTraceReport(rows[0]) : throw new InvalidOperationException("Exchange didn't say it had started the report.");
    }

    public Task CancelTraceReportAsync(string jobId, CancellationToken cancellationToken = default) =>
        Run("Stop-HistoricalSearch", new() { ["JobId"] = jobId }, cancellationToken);
}
