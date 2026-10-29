using Tenantry.Core.Cache;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's sign-in and audit history: 45 days of made-up sign-ins (a few failing, from
/// documentation-range addresses), directory audits and Exchange admin actions, the same every run.
/// </summary>
public sealed partial class DemoUserRecords : ILogArchive
{
    MemoryEntityStore? _history;

    MemoryEntityStore History()
    {
        if (_history is not null)
            return _history;
        var store = new MemoryEntityStore();
        var people = directory.GetUsersAsync().GetAwaiter().GetResult().Where(u => u.Kind == UserKind.Member && u.AccountEnabled).Take(30).ToArray();
        var random = new Random(7);
        var now = DateTimeOffset.UtcNow;
        string[] apps = ["Microsoft Outlook", "Microsoft Teams", "Office 365 SharePoint Online", "Microsoft Edge", "OneDrive SyncEngine", "Azure Portal"];
        (string Place, string Ip)[] places = [("Toronto, Ontario, CA", "192.0.2.10"), ("Chicago, Illinois, US", "198.51.100.40"), ("Dublin, IE", "203.0.113.4")];
        var signIns = new List<ArchivedEvent>();
        var audits = new List<ArchivedEvent>();
        var exchange = new List<ArchivedEvent>();
        for (var day = 0; day < 45; day++)
        {
            for (var n = 0; n < 40; n++)
            {
                var p = people[random.Next(people.Length)];
                var at = now.AddDays(-day).AddMinutes(-random.Next(0, 1440));
                var failed = random.Next(25) == 0;
                var (place, ip) = places[random.Next(10) == 0 ? 2 : random.Next(2)];
                var id = $"si-{day}-{n}";
                signIns.Add(new ArchivedEvent(id, at, LogKinds.Write(new SignInEntry(id, at, p.DisplayName, p.UserPrincipalName, p.Id, apps[random.Next(apps.Length)], null, ip,
                    place, random.Next(15) == 0 ? "IMAP4" : "Browser", failed ? "Invalid username or password." : null, failed ? "notApplied" : "success",
                    random.Next(4) == 0 ? "Single-factor" : "Multifactor", random.Next(2) == 0 ? $"NG-{random.Next(100, 999)}" : null, "Windows 11", "Edge 141.0.0", null, true))));
            }
            for (var n = 0; n < 8; n++)
            {
                var by = people[random.Next(people.Length)];
                var target = people[random.Next(people.Length)];
                var at = now.AddDays(-day).AddMinutes(-random.Next(0, 1440));
                var (activity, category) = (random.Next(5)) switch
                {
                    0 => ("Update user", "UserManagement"),
                    1 => ("Add member to group", "GroupManagement"),
                    2 => ("Reset user password", "UserManagement"),
                    3 => ("Add member to role", "RoleManagement"),
                    _ => ("Consent to application", "ApplicationManagement"),
                };
                var id = $"au-{day}-{n}";
                audits.Add(new ArchivedEvent(id, at, LogKinds.Write(new DirectoryAuditEntry(id, at, activity, category, random.Next(20) != 0, null, by.UserPrincipalName,
                    [target.UserPrincipalName], "Core Directory"))));
            }
            if (day % 3 == 0)
            {
                var at = now.AddDays(-day).AddHours(-3);
                var target = people[random.Next(people.Length)];
                var id = $"ex-{day}";
                exchange.Add(new ArchivedEvent(id, at, LogKinds.Write(new ExchangeAdminEntry(id, at, "Set-Mailbox", Account, target.UserPrincipalName,
                    "-Identity " + target.UserPrincipalName + "; -HiddenFromAddressListsEnabled False", true))));
            }
        }
        store.AddEvents(LogKinds.SignIns, signIns);
        store.AddEvents(LogKinds.Audits, audits);
        store.AddEvents(LogKinds.ExchangeAdmin, exchange);
        return _history = store;
    }

    public int AddEvents(string kind, IReadOnlyCollection<ArchivedEvent> events) => History().AddEvents(kind, events);

    public IReadOnlyList<ArchivedEvent> ReadEvents(string kind, DateTimeOffset from, DateTimeOffset to, int limit) => History().ReadEvents(kind, from, to, limit);

    public ArchiveSpan Span(string kind) => History().Span(kind);

    public int PruneEvents(string kind, DateTimeOffset before) => History().PruneEvents(kind, before);
}
