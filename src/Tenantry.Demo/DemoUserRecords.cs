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

    static readonly LicensePlan[] Apps =
    [
        new("p-exchange", "Exchange Online (Plan 1)"), new("p-teams", "Microsoft Teams"), new("p-sharepoint", "SharePoint (Plan 1)"),
        new("p-onedrive", "OneDrive for business"), new("p-office", "Microsoft 365 Apps for business"), new("p-forms", "Microsoft Forms"),
        new("p-planner", "Microsoft Planner"), new("p-yammer", "Viva Engage Core"), new("p-sway", "Sway"), new("p-bookings", "Microsoft Bookings"),
    ];

    static readonly (string Name, int Total, int Apps)[] Skus =
    [
        ("Microsoft 365 Business Premium", 60, 10), ("Microsoft 365 Business Basic", 120, 8), ("Exchange Online (Plan 1)", 40, 1),
        ("Microsoft 365 E3", 25, 10), ("Microsoft 365 E5", 10, 10), ("Microsoft Power Automate Free", 10000, 1),
    ];

    readonly Dictionary<(string User, string Sku), IReadOnlyList<string>> _disabled = [];
    readonly HashSet<string> _removedApps = new(StringComparer.Ordinal);

    static string SkuId(string name) => "sku-" + StableSeed(name).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default)
    {
        var users = await directory.GetUsersAsync(cancellationToken);
        var user = users.First(u => u.Id == userId);
        var catalog = Skus.Select(s => new LicenseSku(SkuId(s.Name), s.Name, s.Total,
            Math.Min(s.Total, users.Count(u => u.Licenses.Contains(s.Name))), Apps.Take(s.Apps).ToArray())).ToArray();
        var groups = await directory.GetGroupsAsync(cancellationToken);
        var allStaff = groups.First(g => g.DisplayName == "All Staff");
        var assignments = user.Licenses.Select(name =>
        {
            // The free add-on comes from a group in the demo, to show group-based licensing.
            var fromGroup = name == "Microsoft Power Automate Free";
            var id = SkuId(name);
            return new LicenseAssignment(id, name, fromGroup ? allStaff.Id : null, fromGroup ? allStaff.DisplayName : null, null,
                _disabled.GetValueOrDefault((userId, id), name == "Microsoft 365 Business Premium" ? ["p-yammer"] : []));
        }).ToArray();
        IReadOnlyList<AppAssignment> apps = user.Kind == UserKind.Guest ? [] :
        [
            new("a-" + userId[..8], "Adobe Acrobat", "app-adobe", user.CreatedAt.AddDays(10)),
            new("b-" + userId[..8], "Zoom", "app-zoom", user.CreatedAt.AddDays(30)),
        ];
        return new UserLicensing("US", assignments, catalog, apps.Where(a => !_removedApps.Contains(a.Id)).ToArray());
    }

    /// <summary>The demo license name for an id (the demo executor assigns by name).</summary>
    internal static string? SkuName(string skuId) => Skus.Select(s => s.Name).FirstOrDefault(n => SkuId(n) == skuId);

    internal void RememberApps(string userId, string skuId, IReadOnlyList<string> disabled) => _disabled[(userId, skuId)] = disabled;

    internal void RememberRemovedApp(string assignmentId) => _removedApps.Add(assignmentId);

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
