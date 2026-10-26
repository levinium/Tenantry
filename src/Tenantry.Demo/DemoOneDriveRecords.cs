using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's OneDrives and activity, seeded per person. Licensed people have a OneDrive (a few
/// nearly full), sometimes shared with a colleague; guests and unlicensed accounts have none yet.
/// Audit entries and app activity are made up.
/// </summary>
public sealed partial class DemoUserRecords : IOneDriveRecords, IActivityRecords
{
    const long Terabyte = 1L << 40;

    readonly Dictionary<string, OneDriveInfo?> _drives = new(StringComparer.Ordinal);
    // By site ID.
    readonly Dictionary<string, List<SiteAdmin>> _driveAdmins = new(StringComparer.Ordinal);

    static string Login(string upn) => SetOneDriveAccess.LoginFor(upn);

    static SiteAdmin AdminOf(UserSummary u) => new(Login(u.UserPrincipalName), u.DisplayName, u.Mail, u.UserPrincipalName);

    public async Task<Available<OneDriveInfo>> GetOneDriveAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!_drives.TryGetValue(userId, out var drive))
        {
            var person = await PersonAsync(userId, cancellationToken);
            drive = person.Kind == UserKind.Guest || person.Licenses.Count == 0 ? null : NewDrive(person);
            _drives[userId] = drive;
            if (drive is not null)
            {
                var admins = new List<SiteAdmin> { AdminOf(person) };
                var random = new Random(DemoUserRecords.StableSeed(userId) + 40);
                if (random.NextDouble() < 0.25)
                {
                    var everyone = await directory.GetUsersAsync(cancellationToken);
                    var colleague = everyone.Where(u => u.Id != userId && u.Kind == UserKind.Member).ElementAtOrDefault(random.Next(0, 20));
                    if (colleague is not null)
                        admins.Add(AdminOf(colleague));
                }
                _driveAdmins[drive.SiteId] = admins;
            }
        }
        return new(drive);
    }

    static OneDriveInfo NewDrive(UserSummary person)
    {
        var random = new Random(DemoUserRecords.StableSeed(person.Id) + 41);
        var full = random.NextDouble() < 0.08;
        var used = full ? (long)(Terabyte * (0.91 + random.NextDouble() * 0.07)) : (long)(Terabyte * random.NextDouble() * 0.3);
        var name = person.UserPrincipalName.Replace('@', '_').Replace('.', '_');
        return new OneDriveInfo($"https://northgate-my.example/personal/{name}", $"site-{person.Id}", used, Terabyte,
            (long)(used * random.NextDouble() * 0.05), full ? "nearing" : "normal", DateTimeOffset.UtcNow.AddHours(-random.Next(1, 200)));
    }

    public Task<IReadOnlyList<SiteAdmin>> GetOneDriveAdminsAsync(OneDriveInfo drive, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SiteAdmin>>(_driveAdmins.TryGetValue(drive.SiteId, out var admins) ? admins.ToArray() : []);

    /// <summary>Applies a OneDrive change; null when it isn't one.</summary>
    public ApplyResult? ApplyOneDrive(ChangeOp change)
    {
        switch (change)
        {
            case SetOneDriveAccess a when _driveAdmins.TryGetValue(a.SiteId, out var admins):
                admins.RemoveAll(x => string.Equals(x.LoginName, a.Login, StringComparison.OrdinalIgnoreCase));
                if (a.New)
                    admins.Add(new SiteAdmin(a.Login, a.Person.Name, a.Person.Address, a.Person.Address));
                return new ApplyResult(ChangeOutcome.Applied);
            case SetOneDriveAccess:
                return new ApplyResult(ChangeOutcome.Failed, "They have no OneDrive.");
            default:
                return null;
        }
    }

    // ---- Activity ---------------------------------------------------------------------------

    static readonly (string Activity, string Category)[] AboutThem =
    [
        ("Update user", "UserManagement"), ("Add member to group", "GroupManagement"), ("Change user license", "UserManagement"),
        ("Reset user password", "UserManagement"), ("User registered security info", "UserManagement"), ("Update StsRefreshTokenValidFrom Timestamp", "UserManagement"),
    ];

    static readonly (string Activity, string Category)[] ByThem =
    [
        ("Update user", "UserManagement"), ("Add member to group", "GroupManagement"), ("Update group", "GroupManagement"), ("Consent to application", "ApplicationManagement"),
    ];

    public async Task<Available<IReadOnlyList<AuditEvent>>> GetAuditAsync(string userId, bool byThem, CancellationToken cancellationToken = default)
    {
        var person = await PersonAsync(userId, cancellationToken);
        var admin = person.Security?.IsAdmin == true;
        var random = new Random(DemoUserRecords.StableSeed(userId) + (byThem ? 51 : 50));
        if (byThem && !admin)
            return new([]);
        var pool = byThem ? ByThem : AboutThem;
        var count = random.Next(byThem ? 3 : 2, byThem ? 14 : 9);
        var everyone = (await directory.GetUsersAsync(cancellationToken)).Where(u => u.Kind == UserKind.Member).Take(30).ToArray();
        var events = Enumerable.Range(0, count).Select(_ =>
        {
            var (activity, category) = pool[random.Next(pool.Length)];
            var other = everyone[random.Next(everyone.Length)].DisplayName;
            var ok = random.NextDouble() > 0.06;
            return new AuditEvent(DateTimeOffset.UtcNow.AddMinutes(-random.Next(30, 60 * 24 * 29)), activity, category, ok,
                byThem ? person.DisplayName : random.NextDouble() < 0.2 ? "Microsoft Substrate Management" : other,
                byThem ? other : person.DisplayName,
                ok ? null : "Insufficient privileges to complete the operation.");
        });
        return new(events.OrderByDescending(e => e.At).ToArray());
    }

    public async Task<Available<AppActivity>> GetAppActivityAsync(string userPrincipalName, CancellationToken cancellationToken = default)
    {
        var person = (await directory.GetUsersAsync(cancellationToken)).FirstOrDefault(u =>
            string.Equals(u.UserPrincipalName, userPrincipalName, StringComparison.OrdinalIgnoreCase));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (person is null || person.Licenses.Count == 0)
            return new(new AppActivity(today.AddDays(-2), []));
        var random = new Random(DemoUserRecords.StableSeed(person.Id) + 52);
        DateOnly? Last(double chance) => random.NextDouble() < chance ? today.AddDays(-random.Next(2, 26)) : null;
        return new(new AppActivity(today.AddDays(-2),
        [
            ("Outlook (mail)", Last(0.95)), ("OneDrive", Last(0.8)), ("SharePoint", Last(0.6)), ("Teams", Last(0.85)), ("Viva Engage", Last(0.1)),
        ]));
    }
}
