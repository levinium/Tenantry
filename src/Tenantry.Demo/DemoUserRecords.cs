using Bogus;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// Record-page details for the demo's generated people: names split from the display name,
/// plus a generated office, phone and address, seeded per person so they never change.
/// Edits made in the demo are kept here for the session.
/// </summary>
public sealed class DemoUserRecords(DemoTenantDirectory directory) : IUserRecords
{
    readonly Dictionary<string, Dictionary<string, string?>> _edited = new(StringComparer.Ordinal);

    public async Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var users = await directory.GetUsersAsync(cancellationToken);
        var user = users.First(u => u.Id == userId);
        var faker = new Faker("en") { Random = new Randomizer(StableSeed(userId)) };
        var names = user.DisplayName.Split(' ', 2);
        var text = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["givenName"] = names[0],
            ["surname"] = names.Length > 1 ? names[1] : null,
            ["displayName"] = user.DisplayName,
            ["userPrincipalName"] = user.UserPrincipalName,
            ["jobTitle"] = user.JobTitle,
            ["department"] = user.Department,
            ["companyName"] = user.Kind == UserKind.Guest ? "Partner Co." : "Northgate Partners",
            ["employeeId"] = user.Kind == UserKind.Guest ? null : faker.Random.Number(10000, 99999).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["officeLocation"] = user.Kind == UserKind.Guest ? null : $"Floor {faker.Random.Number(1, 6)}",
            ["mobilePhone"] = faker.Random.Bool(0.6f) ? $"+1 555 01{faker.Random.Number(10, 99)}" : null,
            ["businessPhones"] = user.Kind == UserKind.Guest ? null : $"+1 555 02{faker.Random.Number(10, 99)}",
            ["streetAddress"] = "1 Example Plaza",
            ["city"] = "Springfield",
            ["state"] = "NY",
            ["postalCode"] = "10001",
            ["country"] = "United States",
            ["usageLocation"] = "US",
        };
        if (_edited.TryGetValue(userId, out var edits))
        {
            foreach (var (property, value) in edits)
                text[property] = value;
        }
        var manager = users.FirstOrDefault(u => u.Department == user.Department && u.Id != user.Id && u.Kind == UserKind.Member);
        return new UserDetails(userId, text, manager?.Id, manager?.DisplayName,
            user.Kind == UserKind.Guest ? (faker.Random.Bool(0.7f) ? "Accepted" : "PendingAcceptance") : null,
            user.Kind == UserKind.Guest ? user.CreatedAt.AddDays(2) : null);
    }

    public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([DemoTenantDirectory.Domain, "northgate.onmicrosoft.example"]);

    /// <summary>A seed from the id that is the same on every run (string.GetHashCode isn't).</summary>
    static int StableSeed(string id)
    {
        var hash = 2166136261u;
        foreach (var c in id)
            hash = (hash ^ c) * 16777619u;
        return (int)(hash & 0x7FFFFFFF);
    }

    /// <summary>Keeps an edit made in the demo, for the details it shows next.</summary>
    internal void Remember(string userId, string property, string? value)
    {
        if (!_edited.TryGetValue(userId, out var edits))
            _edited[userId] = edits = new Dictionary<string, string?>(StringComparer.Ordinal);
        edits[property] = value;
    }
}
