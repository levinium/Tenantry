using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's mailbox contents: a few inbox rules (one forwarding outside, for some people), the
/// calendar shared with the organization and a colleague, a phone or two, recently deleted mail
/// and a couple of days of traced messages. Seeded per mailbox; changes are kept for the session.
/// </summary>
public sealed partial class DemoUserRecords : IMailboxContents
{
    readonly Dictionary<string, List<InboxRule>> _rules = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, FolderSharing> _sharing = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<MobileDevice>> _devices = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<DeletedItem>> _deleted = new(StringComparer.OrdinalIgnoreCase);

    static readonly string[] Subjects =
    [
        "Quarterly budget review", "Re: Facilities walkthrough", "Lunch on Thursday?", "Updated board deck", "Your invoice is ready",
        "Re: Holiday schedule", "Parking passes", "Fwd: Vendor contract", "Team offsite agenda", "Weekly newsletter",
    ];

    static Random Seeded(string address, int salt) => new(StableSeed(address.ToLowerInvariant()) + salt);

    public Task<IReadOnlyList<InboxRule>> GetInboxRulesAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!_rules.TryGetValue(address, out var rules))
        {
            var random = Seeded(address, 10);
            rules = [new InboxRule($"{address}\\1", "Newsletters to a folder", true, 1, "If the message:\n  is from newsletters@\nTake the following actions:\n  move the message to folder 'Newsletters'",
                false, [], false, "Newsletters")];
            if (random.NextDouble() < 0.3)
                rules.Add(new InboxRule($"{address}\\2", "Copy everything to my phone", true, 2,
                    "If the message:\n  (all messages)\nTake the following actions:\n  forward the message to 'someone@mail.example'",
                    false, ["someone@mail.example"], false, null));
            if (random.NextDouble() < 0.2)
                rules.Add(new InboxRule($"{address}\\3", ".", true, 3, "If the message:\n  includes 'invoice' in the subject\nTake the following actions:\n  delete the message",
                    false, [], true, null));
            _rules[address] = rules;
        }
        return Task.FromResult<IReadOnlyList<InboxRule>>(rules.ToArray());
    }

    public async Task<FolderSharing> GetCalendarSharingAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!_sharing.TryGetValue(address, out var sharing))
        {
            var users = await directory.GetUsersAsync(cancellationToken);
            var colleague = users[Seeded(address, 11).Next(users.Count)];
            sharing = _sharing[address] = new FolderSharing($"{address}:\\Calendar", "Calendar",
            [
                new FolderPermission("Default", "AvailabilityOnly", false),
                new FolderPermission("Anonymous", "None", false),
                new FolderPermission(colleague.DisplayName, "Editor", true),
            ]);
        }
        return sharing;
    }

    public Task<IReadOnlyList<MobileDevice>> GetMobileDevicesAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!_devices.TryGetValue(address, out var devices))
        {
            var random = Seeded(address, 12);
            var now = DateTimeOffset.UtcNow;
            devices = [new MobileDevice($"{address}\\iPhone", "APPL" + random.Next(100000, 999999), "iPhone 15", "iPhone 15", "iOS 19.1", "Outlook app",
                now.AddDays(-random.Next(60, 400)), now.AddMinutes(-random.Next(5, 600)), "Allowed", null)];
            if (random.NextDouble() < 0.4)
                devices.Add(new MobileDevice($"{address}\\Pixel", "ANDR" + random.Next(100000, 999999), "Pixel 8", "Pixel 8", "Android 16", "Built-in mail app",
                    now.AddDays(-random.Next(200, 700)), now.AddDays(-random.Next(30, 120)), "Allowed", null));
            _devices[address] = devices;
        }
        return Task.FromResult<IReadOnlyList<MobileDevice>>(devices.ToArray());
    }

    public Task<IReadOnlyList<DeletedItem>> GetDeletedItemsAsync(string address, string? subject, CancellationToken cancellationToken = default)
    {
        if (!_deleted.TryGetValue(address, out var items))
        {
            var random = Seeded(address, 13);
            items = Enumerable.Range(0, random.Next(3, 9))
                .Select(i => new DeletedItem($"demo-{i}", Subjects[random.Next(Subjects.Length)], i % 5 == 4 ? "IPM.Appointment" : "IPM.Note",
                    i % 3 == 0 ? "/Inbox" : "/Deleted Items", DateTimeOffset.UtcNow.AddDays(-random.Next(1, 14)).AddHours(-random.Next(24))))
                .ToList();
            _deleted[address] = items;
        }
        IEnumerable<DeletedItem> found = items;
        if (!string.IsNullOrWhiteSpace(subject))
            found = found.Where(i => i.Subject.Contains(subject.Trim(), StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<DeletedItem>>(found.OrderByDescending(i => i.Modified).ToArray());
    }

    public Task<IReadOnlyList<TracedMessage>> TraceAsync(string address, TraceDirection direction, int days, CancellationToken cancellationToken = default)
    {
        var random = Seeded(address, 14 + (int)direction * 7 + days);
        var now = DateTimeOffset.UtcNow;
        string[] statuses = ["Delivered", "Delivered", "Delivered", "Delivered", "FilteredAsSpam", "Quarantined", "Failed"];
        var messages = Enumerable.Range(0, Math.Min(60, days * random.Next(4, 12)))
            .Select(i =>
            {
                var other = $"{(char)('a' + random.Next(26))}.{Subjects[random.Next(Subjects.Length)].Split(' ')[0].ToLowerInvariant().Trim(':')}@{(random.NextDouble() < 0.6 ? DemoTenantDirectory.Domain : "mail.example")}";
                return new TracedMessage($"trace-{i}", now.AddMinutes(-random.Next(days * 24 * 60)),
                    direction == TraceDirection.Received ? other : address, direction == TraceDirection.Received ? address : other,
                    Subjects[random.Next(Subjects.Length)], statuses[random.Next(statuses.Length)], random.Next(4_000, 900_000));
            })
            .OrderByDescending(m => m.Received)
            .ToArray();
        return Task.FromResult<IReadOnlyList<TracedMessage>>(messages);
    }

    public Task<IReadOnlyList<TraceEvent>> TraceDetailAsync(TracedMessage message, CancellationToken cancellationToken = default)
    {
        var t = message.Received;
        IReadOnlyList<TraceEvent> events = message.Status switch
        {
            "FilteredAsSpam" => [new(t, "Receive", "", "Message received by the mail server."), new(t.AddSeconds(2), "Spam", "MoveToJmf", "Spam filtering moved it to Junk Email.")],
            "Quarantined" => [new(t, "Receive", "", "Message received by the mail server."), new(t.AddSeconds(2), "Malware", "Quarantine", "Held in quarantine: suspected phishing.")],
            "Failed" => [new(t, "Receive", "", "Message received by the mail server."), new(t.AddSeconds(5), "Fail", "", "550 5.1.10 Recipient not found.")],
            _ => [new(t, "Receive", "", "Message received by the mail server."), new(t.AddSeconds(1), "Spam", "", "Spam filtering: not spam."),
                  new(t.AddSeconds(3), "Deliver", "", "Delivered to the mailbox.")],
        };
        return Task.FromResult(events);
    }

    /// <summary>Applies a 4b-3 change to the demo's contents; false when it isn't one.</summary>
    public bool ApplyContents(ChangeOp change, string address)
    {
        switch (change)
        {
            case SetInboxRuleEnabled e when _rules.TryGetValue(address, out var rules):
                Replace(rules, r => r.Identity == e.RuleIdentity, r => r with { Enabled = e.New });
                return true;
            case RemoveInboxRule r when _rules.TryGetValue(address, out var rules):
                rules.RemoveAll(x => x.Identity == r.RuleIdentity);
                return true;
            case SetFolderPermission f when _sharing.TryGetValue(address, out var sharing):
            {
                var others = sharing.Permissions.Where(p => !string.Equals(p.User, f.Person, StringComparison.OrdinalIgnoreCase)
                                                            && !string.Equals(p.User, f.PersonName, StringComparison.OrdinalIgnoreCase));
                _sharing[address] = sharing with
                {
                    Permissions = f.New is null ? others.ToArray() : [.. others, new FolderPermission(f.Old is null ? f.PersonName : f.Person, f.New, false)],
                };
                return true;
            }
            case SetMobileDevice d when _devices.TryGetValue(address, out var devices):
                if (d.Action == DeviceAction.Remove)
                    devices.RemoveAll(x => x.Identity == d.DeviceIdentity);
                else
                    Replace(devices, x => x.Identity == d.DeviceIdentity, x => d.Action switch
                    {
                        DeviceAction.Block => x with { AccessState = "Blocked" },
                        DeviceAction.Unblock => x with { AccessState = "Allowed" },
                        _ => x with { WipeStatus = $"Wipe sent {DateTimeOffset.Now:d MMM yyyy}, not yet confirmed by the device" },
                    });
                return true;
            case RestoreDeletedItem r when _deleted.TryGetValue(address, out var items):
                items.RemoveAll(i => i.EntryId == r.EntryId);
                return true;
            default:
                return change is SetInboxRuleEnabled or RemoveInboxRule or SetFolderPermission or SetMobileDevice or RestoreDeletedItem;
        }
    }

    static void Replace<T>(List<T> list, Predicate<T> match, Func<T, T> change)
    {
        var index = list.FindIndex(match);
        if (index >= 0)
            list[index] = change(list[index]);
    }
}
