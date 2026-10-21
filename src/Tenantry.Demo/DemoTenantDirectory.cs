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

    // Product names as the admin center shows them (and as Tenantry shows real tenants' licenses).
    static readonly string[] Licenses =
    [
        "Microsoft 365 Business Premium", "Microsoft 365 Business Basic", "Exchange Online (Plan 1)",
        "Microsoft 365 E3", "Microsoft 365 E5",
    ];

    const string FreeAddOn = "Microsoft Power Automate Free";

    readonly int _userCount;
    readonly int _seed;
    IReadOnlyList<UserSummary>? _users;
    IReadOnlyList<GroupSummary>? _groups;

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
                Licenses: isGuest ? []
                    : faker.Random.Bool(0.05f) ? []
                    : faker.Random.Bool(0.25f) ? [faker.PickRandom(Licenses), FreeAddOn]
                    : [faker.PickRandom(Licenses)]));
        }

        return AddMailboxes(users);
    }

    /// <summary>
    /// Mailboxes from their own random stream, so adding them left the generated people (and
    /// every count the tests and screenshots rely on) exactly as they were.
    /// </summary>
    IReadOnlyList<UserSummary> AddMailboxes(List<UserSummary> users)
    {
        var random = new Randomizer(_seed + 1);
        return users.Select(u =>
        {
            if (u.Kind == UserKind.Guest)
                return u;
            var alias = u.Mail![..u.Mail!.IndexOf('@')];
            // Unlicensed members stand in for shared, room and equipment mailboxes, which need no license.
            var kindRoll = random.Double();
            var type = u.Licenses.Count > 0 ? "User" : kindRoll < 0.2 ? "Room" : kindRoll < 0.3 ? "Equipment" : "Shared";
            var roll = random.Double();
            var (forwarding, to) = roll < 0.03 ? (ForwardingKind.External, $"{alias}@personal.example")
                : roll < 0.06 ? (ForwardingKind.Internal, $"assistant@{Domain}")
                : (ForwardingKind.None, (string?)null);
            return u with
            {
                Mailbox = new MailboxInfo(type, u.Mail, random.Bool(0.3f) ? [$"{alias[0]}{alias[(alias.IndexOf('.') + 1)..]}@{Domain}"] : [],
                    forwarding, to, forwarding != ForwardingKind.None && random.Bool(), random.Bool(0.1f), random.Bool(0.3f), type == "Shared" && random.Bool()),
            };
        }).ToArray();
    }

    public async Task<IReadOnlyList<GroupSummary>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        var users = await GetUsersAsync(cancellationToken);
        return _groups ??= GenerateGroups(users);
    }

    IReadOnlyList<GroupSummary> GenerateGroups(IReadOnlyList<UserSummary> users)
    {
        var random = new Randomizer(_seed + 2);
        var created = new DateTimeOffset(2023, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var members = users.Where(u => u.Kind == UserKind.Member).ToArray();
        var groups = new List<GroupSummary>();

        static GroupMember[] Of(IEnumerable<UserSummary> people) => people.Select(p => new GroupMember(p.Id, "user")).ToArray();
        string Slug(string name) => name.ToLowerInvariant().Replace(' ', '-').Replace("&", "and");
        string Id() => random.Guid().ToString();

        foreach (var department in Departments)
        {
            var staff = members.Where(u => u.Department == department).ToArray();
            groups.Add(new GroupSummary(Id(), department, $"{Slug(department)}@{Domain}", $"The {department} team's group, files and calendar.",
                GroupKind.Microsoft365, false, random.Bool(0.7f), random.Bool(0.75f) ? "Private" : "Public", created.AddDays(random.Int(0, 600)), Of(staff)));
            groups.Add(new GroupSummary(Id(), $"SG {department}", null, $"Access to {department} shared resources.",
                GroupKind.Security, false, false, null, created.AddDays(random.Int(0, 600)), Of(staff.Where(_ => random.Bool(0.8f)))));
        }

        var everyone = new GroupSummary(Id(), "All Staff", $"allstaff@{Domain}", "Every member of staff.",
            GroupKind.Distribution, false, false, null, created, Of(members));
        groups.Add(everyone);
        groups.Add(new GroupSummary(Id(), "Leadership", $"leadership@{Domain}", "Department heads.",
            GroupKind.Distribution, false, false, null, created.AddDays(40), Of(random.ListItems(members, 9))));
        groups.Add(new GroupSummary(Id(), "Announcements", $"announcements@{Domain}", "Organisation-wide news; nests All Staff.",
            GroupKind.Distribution, false, false, null, created.AddDays(41), [new GroupMember(everyone.Id, "group")]));
        groups.Add(new GroupSummary(Id(), "IT Admins", $"itadmins@{Domain}", "Mail-enabled security group for IT administrators.",
            GroupKind.MailEnabledSecurity, false, false, null, created.AddDays(12), Of(members.Where(u => u.Department == "IT").Take(4))));
        groups.Add(new GroupSummary(Id(), "All Guests (dynamic)", null, "Rule: user.userType -eq \"Guest\"",
            GroupKind.Security, true, false, null, created.AddDays(90), Of(users.Where(u => u.Kind == UserKind.Guest))));
        groups.Add(new GroupSummary(Id(), "Licensed users (dynamic)", null, "Rule: user.assignedPlans -any (assignedPlan.capabilityStatus -eq \"Enabled\")",
            GroupKind.Security, true, false, null, created.AddDays(91), Of(members.Where(u => u.Licenses.Count > 0))));
        groups.Add(new GroupSummary(Id(), "Summer Gala 2025", $"gala2025@{Domain}", "Event planning (finished).",
            GroupKind.Microsoft365, false, true, "Private", created.AddDays(700), []));
        groups.Add(new GroupSummary(Id(), "Old Printer Access", null, null,
            GroupKind.Security, false, false, null, created.AddDays(-300), []));
        groups.Add(new GroupSummary(Id(), "Front Desk Devices", null, "Kiosk tablets in reception.",
            GroupKind.Security, false, false, null, created.AddDays(200),
            Enumerable.Range(0, 3).Select(_ => new GroupMember(random.Guid().ToString(), "device")).ToArray()));

        // Shared, room and equipment mailboxes, with the people who can use them. Rooms are
        // mostly booked rather than opened, so they get fewer delegates.
        string[] rights = ["Full access", "Full access, Send as", "Send as", "Send on behalf"];
        foreach (var account in users.Where(u => GroupSummary.MailboxKind(u.Mailbox?.Type) is not null))
        {
            var kind = GroupSummary.MailboxKind(account.Mailbox!.Type)!.Value;
            var count = kind == GroupKind.SharedMailbox ? random.Int(0, 5) : random.Int(0, 2);
            var people = random.ListItems(members.Where(m => m.Id != account.Id).ToList(), count)
                .Select(p => new GroupMember(p.Id, "user", random.ArrayElement(rights)))
                .ToArray();
            groups.Add(new GroupSummary(account.Id, account.DisplayName, account.Mail, null, kind, false, false, null, account.CreatedAt, people));
        }
        return groups;
    }

    static string UniqueAlias(string alias, HashSet<string> taken)
    {
        var candidate = alias;
        for (var n = 2; !taken.Add(candidate); n++)
            candidate = $"{alias}{n}";
        return candidate;
    }
}
