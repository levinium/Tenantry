using System.Globalization;
using System.Net;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>The Activity tab's reads (4d-1): Microsoft's directory audit log and the usage reports.</summary>
public sealed partial class GraphUserRecords : IActivityRecords
{
    const int AuditLimit = 100;

    /// <summary>How long one read of the usage report serves every record page (it changes once a day).</summary>
    static readonly TimeSpan ReportKeep = TimeSpan.FromMinutes(30);

    // The report covers every user at once, so it's read once and looked up by sign-in name.
    Task<Dictionary<string, JsonElement>>? _activityReport;
    DateTimeOffset _activityReadAt;

    public async Task<Available<IReadOnlyList<AuditEvent>>> GetAuditAsync(string userId, bool byThem, CancellationToken cancellationToken = default)
    {
        var filter = byThem ? $"initiatedBy/user/id eq '{userId}'" : $"targetResources/any(t:t/id eq '{userId}')";
        var events = new List<AuditEvent>();
        try
        {
            await foreach (var e in graph.GetAllAsync($"auditLogs/directoryAudits?$filter={filter}&$top={AuditLimit}", cancellationToken))
            {
                events.Add(ToAudit(e));
                if (events.Count >= AuditLimit)
                    break;
            }
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return Available<IReadOnlyList<AuditEvent>>.Because("Reading the audit log needs the Reports Reader, Security Reader or Global Reader role.");
        }
        return new(events.OrderByDescending(e => e.At).ToArray());
    }

    static AuditEvent ToAudit(JsonElement e)
    {
        var by = e.TryGetProperty("initiatedBy", out var i) ? i : default;
        var who = by.ValueKind == JsonValueKind.Object && by.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
            ? Text(u, "displayName") ?? Text(u, "userPrincipalName")
            : null;
        who ??= by.ValueKind == JsonValueKind.Object && by.TryGetProperty("app", out var a) && a.ValueKind == JsonValueKind.Object ? Text(a, "displayName") : null;
        var about = e.TryGetProperty("targetResources", out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0
            ? Text(t[0], "displayName") ?? Text(t[0], "userPrincipalName")
            : null;
        return new AuditEvent(
            When(e, "activityDateTime") ?? DateTimeOffset.MinValue,
            Text(e, "activityDisplayName") ?? "Activity",
            Text(e, "category") ?? "",
            !string.Equals(Text(e, "result"), "failure", StringComparison.OrdinalIgnoreCase),
            who, about,
            Text(e, "resultReason") is { Length: > 0 } reason ? reason : null);
    }

    public async Task<Available<AppActivity>> GetAppActivityAsync(string userPrincipalName, CancellationToken cancellationToken = default)
    {
        Dictionary<string, JsonElement> rows;
        try
        {
            if (_activityReport is null || DateTimeOffset.UtcNow - _activityReadAt > ReportKeep || _activityReport is { IsCompleted: true, IsCompletedSuccessfully: false })
            {
                _activityReadAt = DateTimeOffset.UtcNow;
                // Shared by every page that asks, so one page closing doesn't cancel it for the others.
                _activityReport = ReadActivityReportAsync(CancellationToken.None);
            }
            rows = await _activityReport.WaitAsync(cancellationToken);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return Available<AppActivity>.Because("Usage reports need the Reports Reader or Global Reader role.");
        }
        // With names concealed (the tenant's default since 2021) every row is a scrambled name.
        if (rows.Count > 0 && !rows.Keys.Any(k => k.Contains('@')))
            return Available<AppActivity>.Because(
                "Microsoft 365 hides names in usage reports for this tenant. A global admin can show them in the Microsoft 365 admin center: Settings › Org settings › Reports.");
        if (!rows.TryGetValue(userPrincipalName, out var row))
            return new(new AppActivity(null, []));
        return new(new AppActivity(Day(row, "reportRefreshDate"),
        [
            ("Outlook (mail)", Day(row, "exchangeLastActivityDate")),
            ("OneDrive", Day(row, "oneDriveLastActivityDate")),
            ("SharePoint", Day(row, "sharePointLastActivityDate")),
            ("Teams", Day(row, "teamsLastActivityDate")),
            ("Viva Engage", Day(row, "yammerLastActivityDate")),
        ]));
    }

    /// <summary>The 30-day active-user report (beta: v1.0 only sends CSV), keyed by sign-in name.</summary>
    async Task<Dictionary<string, JsonElement>> ReadActivityReportAsync(CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        await foreach (var row in graph.GetAllAsync("https://graph.microsoft.com/beta/reports/getOffice365ActiveUserDetail(period='D30')?$format=application/json", cancellationToken))
            if (Text(row, "userPrincipalName") is { } upn)
                rows[upn] = row;
        return rows;
    }

    static DateOnly? Day(JsonElement e, string name) =>
        DateOnly.TryParse(Text(e, name), CultureInfo.InvariantCulture, out var d) ? d : null;
}
