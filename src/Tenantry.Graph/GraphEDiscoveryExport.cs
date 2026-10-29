using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Directory;
using Tenantry.Core.Permissions;

namespace Tenantry.Graph;

/// <summary>
/// Exporting a search's results (8f-2) through Graph's eDiscovery API, the only scripted export
/// left since Purview retired the cmdlet's (May 2025). The case and search are found by name; the
/// export runs as a case operation; each file is downloaded with a Purview eDiscovery token and the
/// X-AllowWithAADToken header (Microsoft's documented way to download exports).
/// </summary>
public sealed partial class GraphUserRecords : IEdExports
{
    const string Cases = "security/cases/ediscoveryCases";

    public async Task<string> StartExportAsync(string caseName, string searchName, EdExportOptions options, CancellationToken cancellationToken = default)
    {
        var caseId = await IdByNameAsync(Cases, caseName, "case", cancellationToken);
        var searchId = await IdByNameAsync($"{Cases}/{caseId}/searches", searchName, "search", cancellationToken);
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        await graph.PostAsync($"{Cases}/{caseId}/searches/{searchId}/exportResult", new JsonObject
        {
            ["displayName"] = $"Tenantry export {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}",
            ["exportCriteria"] = options.PartlyIndexed ? "searchHits, partiallyIndexed" : "searchHits",
            ["exportFormat"] = options.Pst ? "pst" : "msg",
            ["additionalOptions"] = "none",
        }, cancellationToken);
        // Graph answers 202 with no body; the export is the newest exportResult operation in the case.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await foreach (var op in graph.GetAllAsync($"{Cases}/{caseId}/operations?$orderby=createdDateTime desc&$top=10", cancellationToken))
                if (Text(op, "action") == "exportResult" && When(op, "createdDateTime") >= started && Text(op, "id") is { } opId)
                    return $"{caseId}|{opId}";
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
        throw new InvalidOperationException("Purview accepted the export but didn't list it; look for it in Purview's Process manager.");
    }

    public async Task<EdExportProgress> GetExportAsync(string exportId, CancellationToken cancellationToken = default)
    {
        var parts = exportId.Split('|');
        var op = await graph.GetAsync($"{Cases}/{parts[0]}/operations/{parts[1]}", cancellationToken);
        var files = op.TryGetProperty("exportFileMetadata", out var meta) && meta.ValueKind == JsonValueKind.Array
            ? meta.EnumerateArray().Where(f => Text(f, "downloadUrl") is not null)
                .Select(f => new EdExportFile(Text(f, "fileName") ?? "export", Text(f, "downloadUrl")!,
                    f.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0)).ToArray()
            : [];
        var percent = op.TryGetProperty("percentProgress", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
        var problem = op.TryGetProperty("resultInfo", out var info) && info.ValueKind == JsonValueKind.Object ? Text(info, "message") : null;
        return new EdExportProgress(Text(op, "status") ?? "notStarted", percent, files, problem);
    }

    public Task DownloadAsync(EdExportFile file, Stream to, IProgress<long>? progress, CancellationToken cancellationToken = default) =>
        graph.DownloadAsync(file.Url, TenantryPermissions.PurviewEDiscovery.DefaultScope, new Dictionary<string, string> { ["X-AllowWithAADToken"] = "true" },
            to, progress, cancellationToken);

    async Task<string> IdByNameAsync(string collection, string name, string what, CancellationToken cancellationToken)
    {
        await foreach (var item in graph.GetAllAsync($"{collection}?$select=id,displayName", cancellationToken))
            if (string.Equals(Text(item, "displayName"), name, StringComparison.OrdinalIgnoreCase) && Text(item, "id") is { } id)
                return id;
        throw new InvalidOperationException($"Graph doesn't list the {what} \"{name}\", so it can't be exported here; export it in Purview instead.");
    }
}
