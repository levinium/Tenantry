using System.Globalization;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Mail flow reports from Exchange's reporting cmdlets: Get-MailFlowStatusReport (per day, 90 days),
/// Get-MailTrafficSummaryReport (top lists, already totalled over the range) and
/// Get-MailTrafficATPReport (what caught threats; slow, so kept to short ranges).
/// </summary>
public sealed partial class GraphUserRecords : IMailFlowReports
{
    static Dictionary<string, object?> Range(int days)
    {
        // Whole days, up to the end of today (UTC, as Exchange reports them).
        var end = DateTime.UtcNow.Date.AddDays(1);
        return new()
        {
            ["StartDate"] = end.AddDays(-Math.Clamp(days, 1, MailFlowReport.HistoryDays)).ToString("o", CultureInfo.InvariantCulture),
            ["EndDate"] = end.ToString("o", CultureInfo.InvariantCulture),
        };
    }

    public async Task<IReadOnlyList<MailFlowCount>> GetMailFlowStatusAsync(int days, CancellationToken cancellationToken = default) =>
        [.. (await Run("Get-MailFlowStatusReport", Range(days), cancellationToken)).Select(ToMailFlowCount).OfType<MailFlowCount>()];

    internal static MailFlowCount? ToMailFlowCount(JsonElement r) =>
        DateTime.TryParse(Str(r, "Date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var day) && day.Year > 1
            ? new MailFlowCount(DateOnly.FromDateTime(day), Str(r, "EventType") ?? "", Str(r, "Direction") ?? "", Count(r, "MessageCount"))
            : null;

    public async Task<IReadOnlyList<MailFlowTop>> GetTopTrafficAsync(string category, int days, CancellationToken cancellationToken = default)
    {
        var parameters = Range(days);
        parameters["Category"] = category;
        return MailFlowReport.Combine((await Run("Get-MailTrafficSummaryReport", parameters, cancellationToken)).Select(r => ToMailFlowTop(r, category)), take: 50);
    }

    /// <summary>A top entry: the name in C1 and the count in C2 (rule hits put the rule's audit severity in C2 and the count in C3).</summary>
    internal static MailFlowTop ToMailFlowTop(JsonElement r, string category) =>
        new((Str(r, "C1") ?? "").Trim(), Count(r, category.EndsWith("RuleHits", StringComparison.Ordinal) ? "C3" : "C2"));

    public async Task<IReadOnlyList<ThreatCatch>> GetThreatCatchesAsync(int days, CancellationToken cancellationToken = default) =>
        [.. (await Run("Get-MailTrafficATPReport", Range(days), cancellationToken))
            .Select(r => new ThreatCatch(Str(r, "VerdictSource") ?? "", Str(r, "EventType") ?? "", Str(r, "Direction") ?? "", Count(r, "MessageCount")))
            .GroupBy(c => (c.Verdict, c.Technology, c.Direction)).Select(g => g.First() with { Count = g.Sum(c => c.Count) })
            .OrderByDescending(c => c.Count)];

    /// <summary>A count that may come as a number or as text.</summary>
    static int Count(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.Number ? v.TryGetInt32(out var n) : int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ? n : 0;
}
