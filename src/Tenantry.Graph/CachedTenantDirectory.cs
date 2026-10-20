using System.Globalization;
using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Core.Licensing;

namespace Tenantry.Graph;

/// <summary>
/// The directory as the lists see it: read from the tenant's local copy, which
/// <see cref="DirectorySync"/> keeps current. Objects are stored as Graph returned them, so this
/// is where Graph's shape becomes Tenantry's.
/// </summary>
public sealed class CachedTenantDirectory(IEntityStore store, string tenantName) : ITenantDirectory
{
    public string TenantName => tenantName;

    public Task<IReadOnlyList<UserSummary>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadUsers(cancellationToken), cancellationToken);

    IReadOnlyList<UserSummary> ReadUsers(CancellationToken cancellationToken)
    {
        // Product names ("Microsoft 365 Business Premium"), not Graph's codes ("SPB").
        var skuNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in store.All(DirectorySync.Skus))
        {
            using var sku = JsonDocument.Parse(json);
            var id = sku.RootElement.GetProperty("skuId").GetString()!;
            skuNames[id] = LicenseNames.For(id, Str(sku.RootElement, "skuPartNumber"));
        }

        var stored = store.All(DirectorySync.Users);
        var users = new List<UserSummary>(stored.Count);
        foreach (var json in stored)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var user = JsonDocument.Parse(json);
            users.Add(ToSummary(user.RootElement, skuNames));
        }
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
        Licenses: u.TryGetProperty("assignedLicenses", out var licenses) && licenses.ValueKind == JsonValueKind.Array
            ? licenses.EnumerateArray()
                .Select(l => l.GetProperty("skuId").GetString()!)
                .Select(id => skuNames.GetValueOrDefault(id, id))
                .ToArray()
            : []);

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);
}
