using Bogus;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// A generated tenant for UI work and screenshots. Seeded, so the same seed always produces the
/// same people and a screenshot can be retaken exactly. Every address uses a reserved
/// <c>.example</c> domain (RFC 2606), so generated data can never be mistaken for, or collide
/// with, a real organisation.
/// </summary>
public sealed class DemoTenantDirectory : ITenantDirectory
{
    public const string Domain = "northgate.example";
    public const string GuestDomain = "partner.example";

    static readonly string[] Departments =
        ["Finance", "Operations", "Facilities", "Communications", "Education", "IT", "Development", "Programs"];

    static readonly string[] Licenses =
        ["Business Premium", "Business Basic", "Exchange Online (Plan 1)", "E3", "E5"];

    readonly int _userCount;
    readonly int _seed;
    IReadOnlyList<UserSummary>? _users;

    public DemoTenantDirectory(int userCount = 250, int seed = 20261020)
    {
        _userCount = userCount;
        _seed = seed;
    }

    public string TenantName => "Northgate (demo)";

    public Task<IReadOnlyList<UserSummary>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_users ??= Generate());

    IReadOnlyList<UserSummary> Generate()
    {
        var faker = new Faker("en") { Random = new Randomizer(_seed) };
        // Fixed, not DateTimeOffset.Now, so output depends on the seed alone.
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var takenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var users = new List<UserSummary>(_userCount);

        for (var i = 0; i < _userCount; i++)
        {
            var first = faker.Name.FirstName();
            var last = faker.Name.LastName();
            var isGuest = faker.Random.Bool(0.08f);
            var alias = UniqueAlias($"{first}.{last}".ToLowerInvariant().Replace("'", ""), takenNames);
            var created = now.AddDays(-faker.Random.Int(1, 2500));

            string upn, mail;
            if (isGuest)
            {
                mail = $"{alias}@{GuestDomain}";
                upn = $"{alias}_{GuestDomain}#EXT#@{Domain}";
            }
            else
            {
                mail = $"{alias}@{Domain}";
                upn = mail;
            }

            users.Add(new UserSummary(
                Id: faker.Random.Guid().ToString(),
                DisplayName: $"{first} {last}",
                UserPrincipalName: upn,
                Mail: mail,
                Department: isGuest ? null : faker.PickRandom(Departments),
                JobTitle: isGuest ? null : faker.Name.JobTitle(),
                AccountEnabled: faker.Random.Bool(0.93f),
                Kind: isGuest ? UserKind.Guest : UserKind.Member,
                CreatedAt: created,
                LastSignIn: faker.Random.Bool(0.9f) ? now.AddMinutes(-faker.Random.Int(5, 60 * 24 * 120)) : null,
                Licenses: isGuest ? [] : [faker.PickRandom(Licenses)]));
        }

        return users;
    }

    static string UniqueAlias(string alias, HashSet<string> taken)
    {
        var candidate = alias;
        for (var n = 2; !taken.Add(candidate); n++)
            candidate = $"{alias}{n}";
        return candidate;
    }
}
