using System.Globalization;
using System.Text.Json;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>
/// eDiscovery (8f) through Purview's cmdlets. Listing searches gives no counts (seen live: Items
/// and locations read as empty), so a search's counts and places come from reading it on its own.
/// Purview's times carry no zone; they are UTC.
/// </summary>
public sealed partial class GraphUserRecords : IEDiscoveryRecords
{
    /// <summary>How many cases are read at once for their searches.</summary>
    const int CaseReadsAtOnce = 8;

    ICmdletRunner Purview => exchange ?? throw new InvalidOperationException("eDiscovery needs the Security & Compliance connection, which isn't set up for this tenant.");

    Task<IReadOnlyList<JsonElement>> Compliance(string cmdlet, Dictionary<string, object?>? parameters, CancellationToken cancellationToken) =>
        Purview.InvokeAsync(CmdletEndpoint.Compliance, cmdlet, parameters, cancellationToken);

    public async Task<EdDirectory> GetEDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        var casesRead = Compliance("Get-ComplianceCase", null, cancellationToken);
        var looseRead = Compliance("Get-ComplianceSearch", null, cancellationToken);
        var cases = (await casesRead).Select(ToCase).ToArray();
        using var gate = new SemaphoreSlim(CaseReadsAtOnce);
        var inCases = await Task.WhenAll(cases.Select(async c =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return (await Compliance("Get-ComplianceSearch", new() { ["Case"] = c.Name }, cancellationToken)).Select(s => ToSearch(s, c.Name)).ToArray();
            }
            finally
            {
                gate.Release();
            }
        }));
        // A search can come back from both reads; the case's read says which case it's in.
        var searches = inCases.SelectMany(s => s).ToList();
        var known = searches.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        searches.AddRange((await looseRead).Select(s => ToSearch(s, null)).Where(s => !known.Contains(s.Name)));
        return new EdDirectory(cases, searches);
    }

    public async Task<EdCaseDetail> GetCaseDetailAsync(EdCase eCase, CancellationToken cancellationToken = default)
    {
        var searchesRead = Compliance("Get-ComplianceSearch", new() { ["Case"] = eCase.Name }, cancellationToken);
        var holdsRead = Compliance("Get-CaseHoldPolicy", new() { ["Case"] = eCase.Name }, cancellationToken);
        var membersRead = Compliance("Get-ComplianceCaseMember", new() { ["Case"] = eCase.Name }, cancellationToken);
        var holds = await Task.WhenAll((await holdsRead).Select(async h =>
        {
            var name = EdStr(h, "Name") ?? "";
            var rulesRead = name.Length == 0 ? Task.FromResult<IReadOnlyList<JsonElement>>([]) : Compliance("Get-CaseHoldRule", new() { ["Policy"] = name }, cancellationToken);
            // Listed holds come back with empty places (seen live); one read by name has them.
            if (name.Length > 0 && (await Compliance("Get-CaseHoldPolicy", new() { ["Identity"] = name }, cancellationToken)).FirstOrDefault() is { ValueKind: JsonValueKind.Object } one)
                h = one;
            var rules = await rulesRead;
            return new EdHold(EdStr(h, "Guid") ?? EdStr(h, "Identity") ?? name, name, eCase.Name, EdBool(h, "Enabled") ?? true,
                Places(h, "ExchangeLocation"), Places(h, "SharePointLocation"), Places(h, "PublicFolderLocation").Count > 0,
                rules.Select(r => EdStr(r, "ContentMatchQuery")).FirstOrDefault(q => !string.IsNullOrEmpty(q)) ?? "",
                EdStr(h, "DistributionStatus"), Utc(h, "WhenCreatedUTC"), Utc(h, "WhenChangedUTC"),
                string.Equals(EdStr(h, "Mode"), "PendingDeletion", StringComparison.OrdinalIgnoreCase));
        }));
        return new EdCaseDetail([.. (await searchesRead).Select(s => ToSearch(s, eCase.Name))], holds,
            [.. (await membersRead).Select(m => new EdMember(EdStr(m, "DisplayName") ?? EdStr(m, "Name") ?? "Someone", EdStr(m, "PrimarySmtpAddress") ?? EdStr(m, "WindowsLiveID")))]);
    }

    public async Task<EdSearchDetail> GetEdSearchAsync(string name, CancellationToken cancellationToken = default)
    {
        var rows = await Compliance("Get-ComplianceSearch", new() { ["Identity"] = name }, cancellationToken);
        if (rows.Count == 0)
            throw new InvalidOperationException($"The search \"{name}\" wasn't found; it may have been deleted.");
        var s = rows[0];
        return new EdSearchDetail(ToSearch(s, EdStr(s, "CaseName") is { Length: > 0 } c ? c : null), EdStr(s, "ContentMatchQuery") ?? "",
            Places(s, "ExchangeLocation"), [.. Places(s, "SharePointLocation"), .. Places(s, "OneDriveLocation")], Places(s, "PublicFolderLocation").Count > 0,
            EdLong(s, "Items"), EdLong(s, "Size"), EdLong(s, "UnindexedItems"), EdLong(s, "UnindexedSize"), Hits(EdStr(s, "SearchStatistics")),
            EdStr(s, "Errors") is { Length: > 0 } errors ? errors : null);
    }

    public async Task StartEdSearchAsync(string name, CancellationToken cancellationToken = default) =>
        await Compliance("Start-ComplianceSearch", new() { ["Identity"] = name, ["Confirm"] = false }, cancellationToken);

    static EdCase ToCase(JsonElement c) => new(EdStr(c, "Identity") ?? EdStr(c, "Name") ?? "", EdStr(c, "Name") ?? "A case", EdStr(c, "Description") is { Length: > 0 } d ? d : null,
        string.Equals(EdStr(c, "Status"), "Closed", StringComparison.OrdinalIgnoreCase), Utc(c, "CreatedDateTime"), Utc(c, "LastModifiedDateTime"),
        EdStr(c, "LastModifiedBy") is { Length: > 0 } by ? by : null, Utc(c, "ClosedDateTime"), EdStr(c, "ClosedBy") is { Length: > 0 } closer ? closer : null);

    static EdSearch ToSearch(JsonElement s, string? caseName) => new(EdStr(s, "Name") ?? "A search", caseName,
        EdStr(s, "Description") is { Length: > 0 } d ? d : null, EdStr(s, "Status") ?? "NotStarted", Utc(s, "CreatedTime"), EdStr(s, "CreatedBy"),
        Utc(s, "JobEndTime"), EdStr(s, "RunBy"));

    /// <summary>
    /// Per-place counts from SearchStatistics (seen live): a JSON object per kind of place
    /// ("ExchangeBinding", "SharePointBinding"…), each with Sources [{ Name, ContentItems, ContentSize }].
    /// </summary>
    internal static IReadOnlyList<EdLocationHits> Hits(string? statistics)
    {
        if (string.IsNullOrWhiteSpace(statistics))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(statistics);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return [];
            var hits = new List<EdLocationHits>();
            foreach (var binding in doc.RootElement.EnumerateObject())
                if (binding.Value.ValueKind == JsonValueKind.Object && binding.Value.TryGetProperty("Sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
                    hits.AddRange(sources.EnumerateArray().Where(x => EdStr(x, "Name") is { Length: > 0 })
                        .Select(x => new EdLocationHits(EdStr(x, "Name")!, EdLong(x, "ContentItems"), EdStr(x, "ContentSize"))));
            return hits;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A location list: plain strings (seen live for searches), or objects naming the place.</summary>
    static IReadOnlyList<string> Places(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()
                : x.ValueKind == JsonValueKind.Object ? EdStr(x, "Name") ?? EdStr(x, "DisplayName") ?? EdStr(x, "ImmutableIdentity") : null).OfType<string>().Where(x => x.Length > 0).ToArray()
            : [];

    static string? EdStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool? EdBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    static long EdLong(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
            : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0
            : 0;

    static DateTimeOffset? Utc(JsonElement e, string name) =>
        DateTimeOffset.TryParse(EdStr(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
        && d.Year > 1 ? d : null;
}
