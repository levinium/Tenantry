using System.Globalization;

namespace Tenantry.Core.Directory;

/// <summary>
/// Everything about a user that the record page shows but the list doesn't keep: names,
/// contact details, address, manager, guest status. Read live when the page opens.
/// </summary>
/// <param name="Text">Graph text properties by name (givenName, officeLocation, …); missing means not set.</param>
public sealed record UserDetails(
    string Id,
    IReadOnlyDictionary<string, string?> Text,
    string? ManagerId,
    string? ManagerName,
    string? GuestInvitationState,
    DateTimeOffset? GuestInvitationChanged)
{
    public string? this[string property] => Text.GetValueOrDefault(property);

    /// <summary>The properties the Account tab edits, as Graph names them.</summary>
    public static readonly string[] Properties =
    [
        "givenName", "surname", "displayName", "userPrincipalName", "jobTitle", "department", "companyName", "employeeId",
        "officeLocation", "mobilePhone", "businessPhones", "streetAddress", "city", "state", "postalCode", "country", "usageLocation",
    ];
}

/// <summary>Reads what a record page needs live (the list's local copy holds only list fields).</summary>
public interface IUserRecords
{
    Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>The tenant's verified domains, for changing a sign-in name.</summary>
    Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default);

    /// <summary>The user's licenses (and where each comes from), the tenant's licenses with counts, and assigned apps.</summary>
    Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>Countries by ISO code, for usage location (Microsoft requires it before licensing).</summary>
public static class Countries
{
    static readonly Lazy<IReadOnlyList<(string Code, string Name)>> List = new(() =>
        CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(c =>
            {
                try
                {
                    return new RegionInfo(c.Name);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            })
            .OfType<RegionInfo>()
            .Where(r => r.TwoLetterISORegionName.Length == 2 && char.IsLetter(r.TwoLetterISORegionName[0]))
            .GroupBy(r => r.TwoLetterISORegionName)
            .Select(g => (g.Key, g.First().EnglishName))
            .OrderBy(r => r.EnglishName, StringComparer.CurrentCulture)
            .ToArray());

    public static IReadOnlyList<(string Code, string Name)> All => List.Value;

    public static string? NameOf(string? code) =>
        code is null ? null : All.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)).Name ?? code;
}
