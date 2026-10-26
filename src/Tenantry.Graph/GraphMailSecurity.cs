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
    const int QuarantineMaxPages = 10;

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
        Str(r, "Type") ?? Str(r, "QuarantineTypes") ?? "",
        Str(r, "PolicyName"),
        Date(r, "Expires"),
        Str(r, "ReleaseStatus") ?? "",
        Str(r, "Direction"));

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

    public async Task<IReadOnlyList<TracedMessage>> TraceAsync(TraceSearch search, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["StartDate"] = search.Start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["EndDate"] = search.End.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["ResultSize"] = TraceSearch.MaxResults,
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
            .OrderByDescending(m => m.Received)
            .ToArray();
    }
}
