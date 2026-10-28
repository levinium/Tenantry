using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// The unified audit log (8e) through <c>security/auditLog/queries</c> (AuditLogsQuery.Read.All): a
/// search is created, Purview runs it in its own time (it can sit "notStarted" for minutes, seen
/// live), then its records are paged.
/// </summary>
public sealed partial class GraphUserRecords : IAuditRecords
{
    const string AuditQueries = "security/auditLog/queries";

    static AuditSearch Search(JsonElement q)
    {
        var start = When(q, "filterStartDateTime");
        var end = When(q, "filterEndDateTime");
        return new AuditSearch(Text(q, "id") ?? "", Text(q, "displayName") ?? "A search", start, end, Text(q, "status") ?? "notStarted",
            q.TryGetProperty("approximateReturnedRecordCount", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt64() : null,
            q.TryGetProperty("isRecordCountLimitExceeded", out var x) && x.ValueKind == JsonValueKind.True,
            new AuditSearchRequest(Text(q, "displayName") ?? "", start ?? default, end ?? default, Strings(q, "userPrincipalNameFilters"), Strings(q, "operationFilters"),
                Strings(q, "recordTypeFilters"), Text(q, "keywordFilter"), Strings(q, "ipAddressFilters"), Strings(q, "objectIdFilters")));
    }

    public async Task<IReadOnlyList<AuditSearch>> GetSearchesAsync(CancellationToken cancellationToken = default)
    {
        var searches = new List<AuditSearch>();
        await foreach (var q in graph.GetAllAsync(AuditQueries, cancellationToken))
            searches.Add(Search(q));
        return [.. searches.OrderByDescending(s => s.End)];
    }

    public async Task<AuditSearch> StartSearchAsync(AuditSearchRequest request, CancellationToken cancellationToken = default)
    {
        static JsonArray Array(IEnumerable<string>? values) => new([.. (values ?? []).Select(v => (JsonNode)v)]);
        var body = new JsonObject
        {
            ["displayName"] = request.Name,
            ["filterStartDateTime"] = request.Start.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["filterEndDateTime"] = request.End.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        };
        if (request.Users.Count > 0)
            body["userPrincipalNameFilters"] = Array(request.Users);
        if (request.Operations.Count > 0)
            body["operationFilters"] = Array(request.Operations);
        if (request.RecordTypes.Count > 0)
            body["recordTypeFilters"] = Array(request.RecordTypes);
        if (!string.IsNullOrWhiteSpace(request.Keyword))
            body["keywordFilter"] = request.Keyword.Trim();
        if (request.IpAddresses is { Count: > 0 })
            body["ipAddressFilters"] = Array(request.IpAddresses);
        if (request.ObjectIds is { Count: > 0 })
            body["objectIdFilters"] = Array(request.ObjectIds);
        return Search(await graph.PostAsync(AuditQueries, body, cancellationToken));
    }

    public async Task<AuditSearch> GetSearchAsync(string id, CancellationToken cancellationToken = default) =>
        Search(await graph.GetAsync($"{AuditQueries}/{id}", cancellationToken));

    public async Task<IReadOnlyList<AuditRecord>> GetRecordsAsync(string id, int limit, CancellationToken cancellationToken = default)
    {
        var records = new List<AuditRecord>();
        await foreach (var r in graph.GetAllAsync($"{AuditQueries}/{id}/records?$top=1000", cancellationToken))
        {
            records.Add(new AuditRecord(Text(r, "id") ?? "", When(r, "createdDateTime"), Text(r, "userPrincipalName"), Text(r, "userType"), Text(r, "operation") ?? "",
                Text(r, "service"), Text(r, "auditLogRecordType"), Text(r, "objectId"), Text(r, "clientIp"),
                r.TryGetProperty("auditData", out var data) ? data.GetRawText() : "{}"));
            if (records.Count >= limit)
                break;
        }
        return records;
    }
}
