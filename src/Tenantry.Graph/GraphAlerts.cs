using System.Globalization;
using System.Text.Json;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>Alert policies (Security &amp; Compliance endpoint) and fired alerts (Graph alerts_v2).</summary>
public sealed partial class GraphUserRecords : IAlertRecords
{
    public async Task<IReadOnlyList<AlertPolicy>> GetAlertPoliciesAsync(CancellationToken cancellationToken = default)
    {
        if (exchange is null)
            throw new InvalidOperationException("Exchange Online isn't connected for this tenant.");
        var rows = await exchange.InvokeAsync(CmdletEndpoint.Compliance, "Get-ProtectionAlert", new Dictionary<string, object?>(), cancellationToken);
        return [.. rows.Select(ToAlertPolicy).OrderBy(a => a.Category).ThenBy(a => SeverityOrder(a.Severity)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }

    internal static AlertPolicy ToAlertPolicy(JsonElement r) => new(
        (Str(r, "Name") ?? "").Replace("​", "", StringComparison.Ordinal).Trim(),
        Str(r, "Identity") ?? Str(r, "Name") ?? "",
        Str(r, "Severity") ?? "",
        Str(r, "Category") ?? "",
        Str(r, "Comment") is { Length: > 0 } c ? c : null,
        r.TryGetProperty("Disabled", out var d) && d.ValueKind == JsonValueKind.True,
        r.TryGetProperty("NotificationEnabled", out var n) && n.ValueKind == JsonValueKind.True,
        List(r, "NotifyUser").ToArray(),
        r.TryGetProperty("IsSystemRule", out var s) && s.ValueKind == JsonValueKind.True,
        string.Equals(Str(r, "Mode"), "PendingDeletion", StringComparison.OrdinalIgnoreCase));

    internal static int SeverityOrder(string severity) => severity switch { "High" or "high" => 0, "Medium" or "medium" => 1, "Low" or "low" => 2, _ => 3 };

    public async Task<IReadOnlyList<SecurityAlert>> GetAlertsAsync(CancellationToken cancellationToken = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var alerts = new List<SecurityAlert>();
        // No $top: some Microsoft collections answer that many and no next link (service health did).
        await foreach (var a in graph.GetAllAsync($"security/alerts_v2?$filter=createdDateTime ge {since}", cancellationToken))
        {
            alerts.Add(ToSecurityAlert(a));
            if (alerts.Count >= 5000)
                break;
        }
        return [.. alerts.OrderByDescending(a => a.Created)];
    }

    internal static SecurityAlert ToSecurityAlert(JsonElement a)
    {
        // Who or what it's about: the people and mailboxes in its evidence.
        var involved = new List<string>();
        if (a.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array)
            foreach (var e in evidence.EnumerateArray())
            {
                if (e.TryGetProperty("userAccount", out var u) && u.ValueKind == JsonValueKind.Object && (Str(u, "userPrincipalName") ?? Str(u, "accountName")) is { Length: > 0 } who)
                    involved.Add(who);
                else if (Str(e, "primaryAddress") is { Length: > 0 } mailbox)
                    involved.Add(mailbox);
            }
        return new SecurityAlert(Str(a, "id") ?? "", Str(a, "title") ?? "(no title)", Str(a, "severity") ?? "", Str(a, "status") ?? "",
            Str(a, "category"), Str(a, "serviceSource"), Date(a, "createdDateTime"), Date(a, "lastActivityDateTime"), Str(a, "description"),
            Str(a, "assignedTo"), Str(a, "classification") is { Length: > 0 } c && c != "unknown" ? c : null, Str(a, "determination") is { Length: > 0 } d && d != "unknown" ? d : null,
            Str(a, "alertWebUrl"), [.. involved.Distinct(StringComparer.OrdinalIgnoreCase)]);
    }
}
