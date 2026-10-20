using System.Net;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Reads the directory live from Graph. A stepping stone: Phase 2 puts the local cache in
/// front of this, and list pages read the cache instead.
/// </summary>
public sealed class GraphTenantDirectory(GraphClient graph, string tenantName) : ITenantDirectory
{
    const string BaseSelect = "id,displayName,userPrincipalName,mail,department,jobTitle,accountEnabled,userType,createdDateTime,assignedLicenses";

    public string TenantName => tenantName;

    public async Task<IReadOnlyList<UserSummary>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        var skuNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var sku in graph.GetAllAsync("subscribedSkus?$select=skuId,skuPartNumber", cancellationToken))
            skuNames[sku.GetProperty("skuId").GetString()!] = sku.GetProperty("skuPartNumber").GetString()!;

        // signInActivity needs Entra ID P1 and a reports-reading role. Without them Graph
        // refuses the whole request, so fall back to the list without last sign-in.
        try
        {
            return await ReadAsync($"users?$select={BaseSelect},signInActivity&$top=999", skuNames, cancellationToken);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            return await ReadAsync($"users?$select={BaseSelect}&$top=999", skuNames, cancellationToken);
        }
    }

    async Task<IReadOnlyList<UserSummary>> ReadAsync(string path, Dictionary<string, string> skuNames, CancellationToken cancellationToken)
    {
        var users = new List<UserSummary>();
        await foreach (var u in graph.GetAllAsync(path, cancellationToken))
            users.Add(ToSummary(u, skuNames));
        return users;
    }

    internal static UserSummary ToSummary(JsonElement u, IReadOnlyDictionary<string, string> skuNames) => new(
        Id: u.GetProperty("id").GetString()!,
        DisplayName: Str(u, "displayName") ?? Str(u, "userPrincipalName") ?? "",
        UserPrincipalName: Str(u, "userPrincipalName") ?? "",
        Mail: Str(u, "mail"),
        Department: Str(u, "department"),
        JobTitle: Str(u, "jobTitle"),
        AccountEnabled: u.TryGetProperty("accountEnabled", out var e) && e.ValueKind == JsonValueKind.True,
        Kind: string.Equals(Str(u, "userType"), "Guest", StringComparison.OrdinalIgnoreCase) ? UserKind.Guest : UserKind.Member,
        CreatedAt: Date(u, "createdDateTime") ?? DateTimeOffset.MinValue,
        LastSignIn: u.TryGetProperty("signInActivity", out var activity) && activity.ValueKind == JsonValueKind.Object
            ? Latest(Date(activity, "lastSignInDateTime"), Date(activity, "lastNonInteractiveSignInDateTime"))
            : null,
        Licenses: u.TryGetProperty("assignedLicenses", out var licenses)
            ? licenses.EnumerateArray()
                .Select(l => l.GetProperty("skuId").GetString()!)
                .Select(id => skuNames.GetValueOrDefault(id, id))
                .ToArray()
            : []);

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);
}
