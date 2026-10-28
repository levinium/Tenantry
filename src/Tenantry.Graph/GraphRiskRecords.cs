using System.Net;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Identity Protection (8d-2): risky users, risk detections and risky apps, read together. Without
/// Entra ID P2 Graph answers 403 "not licensed"; a missing permission answers 403 "required scopes
/// are missing" (both seen live), so each list says which.
/// </summary>
public sealed partial class GraphUserRecords : IRiskRecords
{
    public async Task<RiskDirectory> GetRiskAsync(CancellationToken cancellationToken = default)
    {
        async Task<(List<JsonElement> Rows, string? Unavailable)> Read(string path, string notLicensed, int limit)
        {
            var rows = new List<JsonElement>();
            try
            {
                await foreach (var row in graph.GetAllAsync(path, cancellationToken))
                {
                    rows.Add(row);
                    if (rows.Count >= limit)
                        break;
                }
                return (rows, null);
            }
            catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                return ([], e.Message.Contains("scopes are missing", StringComparison.OrdinalIgnoreCase)
                    ? "Tenantry needs another permission to read this: use Grant Permissions at the top of the window."
                    : notLicensed);
            }
        }
        var usersRead = Read("identityProtection/riskyUsers?$top=500", Risk.NeedsP2, 5000);
        var detectionsRead = Read("identityProtection/riskDetections?$top=500&$orderby=detectedDateTime desc", Risk.NeedsP2, 2000);
        var appsRead = Read("identityProtection/riskyServicePrincipals?$top=500", Risk.NeedsWorkloadPremium, 5000);
        var (users, usersNote) = await usersRead;
        var (detections, detectionsNote) = await detectionsRead;
        var (apps, appsNote) = await appsRead;

        static string? Place(JsonElement d) => d.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.Object
            ? string.Join(", ", new[] { Text(l, "city"), Text(l, "state"), Text(l, "countryOrRegion") }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } p ? p : null
            : null;
        return new RiskDirectory(
            [.. users.Select(u => new RiskyUser(Text(u, "id") ?? "", Text(u, "userDisplayName") ?? "Someone", Text(u, "userPrincipalName"), Text(u, "riskLevel") ?? "none",
                Text(u, "riskState") ?? "none", Text(u, "riskDetail") is { } d && d != "none" ? Words(d) : null, When(u, "riskLastUpdatedDateTime")))],
            [.. detections.Select(d => new RiskDetection(Text(d, "id") ?? "", Text(d, "userId"), Text(d, "userDisplayName") ?? "Someone", Text(d, "userPrincipalName"),
                Text(d, "riskEventType") ?? "generic", Text(d, "riskLevel") ?? "none", Text(d, "riskState") ?? "none", Text(d, "activity") ?? "signin", Text(d, "ipAddress"),
                Place(d), When(d, "detectedDateTime"), Text(d, "detectionTimingType")))],
            [.. apps.Select(a => new RiskyApp(Text(a, "id") ?? "", Text(a, "appId") ?? "", Text(a, "displayName") ?? "An app", Text(a, "riskLevel") ?? "none",
                Text(a, "riskState") ?? "none", Text(a, "riskDetail") is { } d && d != "none" ? Words(d) : null, When(a, "riskLastUpdatedDateTime")))],
            usersNote, detectionsNote, appsNote);
    }
}
