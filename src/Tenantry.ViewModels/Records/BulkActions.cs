using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// The actions offered when several users, or several groups and mailboxes, are selected. Each
/// plans one change per row it applies to and names the rows it skips, with the reason.
/// </summary>
public static partial class BulkActions
{
    /// <summary>Details read live (the list's local copy holds only department and job title) this many at a time.</summary>
    const int ReadsAtOnce = 4;

    static readonly (string Property, string Label)[] Details =
    [
        ("department", "Department"), ("jobTitle", "Job Title"), ("companyName", "Company"), ("officeLocation", "Office"),
        ("city", "City"), ("country", "Country or Region"), ("usageLocation", "Usage Location (Country Code, e.g. US)"),
    ];

    // ---- Users ------------------------------------------------------------------------------

    /// <param name="selected">The users checked right now (read each time, as the selection changes).</param>
    public static IReadOnlyList<BulkAction> ForUsers(Func<IReadOnlyList<UserSummary>> selected, IDirectoryIndex index, IUserRecords? records)
    {
        HashSet<string> Ids() => selected().Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        Task<IReadOnlyList<LicenseSku>>? catalog = null;
        Task<IReadOnlyList<LicenseSku>> Catalog(CancellationToken ct) => catalog ??= LoadCatalog(ct);
        async Task<IReadOnlyList<LicenseSku>> LoadCatalog(CancellationToken ct) =>
            records is null || selected().Count == 0 ? [] : (await records.GetLicensingAsync(selected()[0].Id, ct)).Catalog;

        return
        [
            new("Block or Allow Sign-In", "Stop these accounts signing in, or let them sign in again.", "PersonProhibited",
                (a, _) => Task.FromResult(Plan(selected(), u =>
                {
                    var allow = (bool)a.Choice!.Value;
                    return u.AccountEnabled == allow ? (null, $"{u.DisplayName}: already {(allow ? "allowed" : "blocked")}")
                        : (new SetSignInAllowed(GroupAccess.Target(u), u.AccountEnabled, allow), null);
                })),
                choiceLabel: "Sign-In", choices: [new BulkChoice("Block Sign-In", false), new BulkChoice("Allow Sign-In", true)]),

            new("Sign Out Everywhere", "End every session; their apps ask them to sign in again within about an hour.", "SignOut",
                (_, _) => Task.FromResult(Plan(selected(), u => (new RevokeSessions(GroupAccess.Target(u)), null))),
                isCaution: true),

            new("Add a License", "Assign the same license to everyone who doesn't have it.", "Add",
                async (a, ct) =>
                {
                    var sku = (LicenseSku)a.Choice!.Value;
                    // Microsoft refuses a license to someone with no usage location (the country they work in).
                    var plan = Plan(selected(), u => u.Licenses.Contains(sku.Name) ? (null, $"{u.DisplayName}: already has it")
                        : string.IsNullOrEmpty(u.UsageLocation) ? (null, $"{u.DisplayName}: no usage location set (set their Usage Location first)")
                        : (new SetLicenseAssigned(GroupAccess.Target(u), sku.SkuId, sku.Name, false, true), null));
                    return plan.Changes.Count > sku.Available
                        ? plan with { Skipped = [$"Only {sku.Available:N0} left for {plan.Changes.Count:N0} people: the rest will fail when applied.", .. plan.Skipped] }
                        : plan;
                },
                choiceLabel: "License",
                loadChoices: async ct => (await Catalog(ct)).OrderBy(s => s.Name, StringComparer.CurrentCulture)
                    .Select(s => new BulkChoice($"{s.Name} · {s.Available:N0} left", s)).ToArray()),

            new("Remove a License", "Take a license away from everyone who has it directly.", "Subtract",
                async (a, ct) =>
                {
                    var sku = (LicenseSku)a.Choice!.Value;
                    var users = selected();
                    // The list's licenses include ones that come from a group, which only leaving the group takes away.
                    var notDirect = await NotDirectAsync(users.Where(u => u.Licenses.Contains(sku.Name)).ToArray(), records, sku.SkuId, ct);
                    return Plan(users, u => !u.Licenses.Contains(sku.Name) ? (null, $"{u.DisplayName}: doesn't have it")
                        : notDirect.GetValueOrDefault(u.Id) is { } why ? (null, $"{u.DisplayName}: {why}")
                        : (new SetLicenseAssigned(GroupAccess.Target(u), sku.SkuId, sku.Name, true, false), null));
                },
                choiceLabel: "License",
                loadChoices: async ct =>
                {
                    var held = selected().SelectMany(u => u.Licenses).ToHashSet(StringComparer.Ordinal);
                    return (await Catalog(ct)).Where(s => held.Contains(s.Name)).OrderBy(s => s.Name, StringComparer.CurrentCulture)
                        .Select(s => new BulkChoice(s.Name, s)).ToArray();
                },
                isCaution: true),

            new("Automatic Replies", "Turn automatic replies on with one message for everyone (inside and outside), or turn them off.", "MailArrowUp",
                (a, ct) => AutoRepliesAsync(selected(), records, (bool)a.Choice!.Value, a.Text.Trim(), ct),
                choiceLabel: "Replies", choices: [new BulkChoice("Turn On", true), new BulkChoice("Turn Off", false)],
                textLabel: "Reply (Needed When Turning On)"),

            new("Turn Forwarding Off", "Stop forwarding mail for everyone selected who forwards it.", "MailArrowForward",
                (_, _) => Task.FromResult(Plan(selected(), u => u.Mailbox is not { } m ? (null, $"{u.DisplayName}: no mailbox")
                    : m.ForwardingTo is null ? (null, $"{u.DisplayName}: not forwarding")
                    : (new SetForwarding(GroupAccess.Target(u), m.ForwardingTo, m.KeepsCopyWhenForwarding, null, false, false), null)))),

            new("Address List", "Hide everyone from the address list (Outlook's directory), or show them again.", "BookContacts",
                (a, _) =>
                {
                    var hide = (bool)a.Choice!.Value;
                    return Task.FromResult(Plan(selected(), u => u.Mailbox is not { } m ? (null, $"{u.DisplayName}: no mailbox")
                        : m.HiddenFromAddressList == hide ? (null, $"{u.DisplayName}: already {(hide ? "hidden" : "shown")}")
                        : (new SetHiddenFromAddressList(GroupAccess.Target(u), m.HiddenFromAddressList, hide), null)));
                },
                choiceLabel: "Address List", choices: [new BulkChoice("Hide from the Address List", true), new BulkChoice("Show in the Address List", false)]),

            new("Turn an Email App Off", "Stop everyone reaching their mail one way, such as POP or IMAP (older apps that often skip multi-factor sign-in).", "PlugDisconnected",
                (a, _) => Task.FromResult(EmailApp(selected(), (string)a.Choice!.Value, false)),
                choiceLabel: "App", choices: AppChoices(), isCaution: true),

            new("Turn an Email App On", "Let everyone reach their mail one more way.", "PlugConnected",
                (a, _) => Task.FromResult(EmailApp(selected(), (string)a.Choice!.Value, true)),
                choiceLabel: "App", choices: AppChoices()),

            new("Litigation Hold", "Keep every item in their mailboxes, even ones they delete, until the hold is lifted; or lift it.", "Gavel",
                (a, _) =>
                {
                    var on = (bool)a.Choice!.Value;
                    var note = a.Text.Trim() is { Length: > 0 } n ? n : null;
                    return Task.FromResult(Plan(selected(), u => u.Mailbox is not { } m ? (null, $"{u.DisplayName}: no mailbox")
                        : m.LitigationHold == on ? (null, $"{u.DisplayName}: {(on ? "already on hold" : "not on hold")}")
                        // The list doesn't hold a hold's days or note; turning it off leaves them as Exchange has them.
                        : (new SetLitigationHold(GroupAccess.Target(u), m.LitigationHold, null, null, on, null, on ? note : null), null)));
                },
                choiceLabel: "Hold", choices: [new BulkChoice("Turn On (Keep Items Indefinitely)", true), new BulkChoice("Turn Off", false)],
                textLabel: "Note for the Hold (Optional)", isCaution: true),

            new("Add to a Group", "Make everyone a member of one group.", "PeopleAdd",
                (a, _) => Task.FromResult(index.Group(a.Picked!.Id) is { } g ? Membership(selected(), g, GroupRole.Member, true)
                    : BulkPlan.Invalid("That group isn't in the local copy any more.")),
                pickPlaceholder: "Choose a group: type its name",
                pickOptions: () => index.AllGroups.Where(g => !g.IsMailbox && GroupAccess.MembersEditable(g)).Select(PickOption.Of)),

            new("Remove from a Group", "Take everyone out of one group.", "PeopleRemove",
                (a, _) => Task.FromResult(index.Group(a.Picked!.Id) is { } g ? Membership(selected(), g, GroupRole.Member, false)
                    : BulkPlan.Invalid("That group isn't in the local copy any more.")),
                pickPlaceholder: "Choose a group they're in: type its name",
                pickOptions: () =>
                {
                    var ids = Ids();
                    return index.AllGroups.Where(g => !g.IsMailbox && GroupAccess.MembersEditable(g) && g.Members.Any(m => ids.Contains(m.Id))).Select(PickOption.Of);
                },
                isCaution: true),

            new("Give Access to a Shared Mailbox", "Give everyone the same right on one shared, room or equipment mailbox.", "MailInbox",
                (a, _) => Task.FromResult(index.Group(a.Picked!.Id) is { } m ? Access(selected(), m, (MailboxRight)a.Choice!.Value, true)
                    : BulkPlan.Invalid("That mailbox isn't in the local copy any more.")),
                pickPlaceholder: "Choose a mailbox: type its name",
                pickOptions: () => index.AllGroups.Where(g => g.IsMailbox).Select(PickOption.Of),
                choiceLabel: "Right", choices: Rights()),

            new("Remove Access to a Shared Mailbox", "Take the same right away from everyone on one mailbox.", "MailDismiss",
                (a, _) => Task.FromResult(index.Group(a.Picked!.Id) is { } m ? Access(selected(), m, (MailboxRight)a.Choice!.Value, false)
                    : BulkPlan.Invalid("That mailbox isn't in the local copy any more.")),
                pickPlaceholder: "Choose a mailbox they can use: type its name",
                pickOptions: () =>
                {
                    var ids = Ids();
                    return index.AllGroups.Where(g => g.IsMailbox && g.Members.Any(m => ids.Contains(m.Id))).Select(PickOption.Of);
                },
                choiceLabel: "Right", choices: Rights(), isCaution: true),
        ];
    }

    /// <summary>
    /// Of these people, the ones who hold a license only through a group (or whose licenses
    /// couldn't be read), with why, read a few at a time. Empty when there's nothing to read with.
    /// </summary>
    static async Task<Dictionary<string, string>> NotDirectAsync(IReadOnlyList<UserSummary> users, IUserRecords? records, string skuId, CancellationToken ct)
    {
        var why = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        if (records is null)
            return [];
        await Parallel.ForEachAsync(users, new ParallelOptions { MaxDegreeOfParallelism = ReadsAtOnce, CancellationToken = ct }, async (u, token) =>
        {
            try
            {
                var held = (await records.GetLicensingAsync(u.Id, token)).Assignments.Where(l => l.SkuId == skuId).ToArray();
                if (held.Length > 0 && !held.Any(l => l.IsDirect))
                    why[u.Id] = $"gets it from {string.Join(", ", held.Select(l => l.AssignedByGroupName ?? "a group"))} (take them out of that group instead)";
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                why[u.Id] = $"couldn't read their licenses ({Friendly.Message(e)})";
            }
        });
        return new Dictionary<string, string>(why, StringComparer.Ordinal);
    }

    static BulkChoice[] Rights() => GroupAccess.Rights.Select(r => new BulkChoice(SetMailboxAccess.RightLabel(r), r)).ToArray();

    /// <summary>POP and IMAP first: the ones an admin most often turns off.</summary>
    static BulkChoice[] AppChoices() => EmailApps.All.OrderBy(a => a.Parameter is "PopEnabled" or "ImapEnabled" ? 0 : 1)
        .Select(a => new BulkChoice(a.Name, a.Parameter)).ToArray();

    static BulkPlan EmailApp(IReadOnlyList<UserSummary> users, string parameter, bool on)
    {
        var name = EmailApps.All.First(a => a.Parameter == parameter).Name;
        return Plan(users, u => u.Mailbox is not { } m ? (null, $"{u.DisplayName}: no mailbox")
            : m.Apps is not { } apps ? (null, $"{u.DisplayName}: email apps not read yet (refresh first)")
            : apps.IsOn(parameter) == on ? (null, $"{u.DisplayName}: {name} already {(on ? "on" : "off")}")
            : (new SetEmailApp(GroupAccess.Target(u), parameter, name, !on, on), null));
    }

    /// <summary>The details a bulk edit can set, in the order the selection panel shows them.</summary>
    public static IReadOnlyList<(string Property, string Label)> DetailFields => Details;

    /// <summary>Department and job title are in the list's local copy; the rest must be read live.</summary>
    public static bool IsListDetail(string property) => property is "department" or "jobTitle";

    public static string? ListValue(UserSummary u, string property) => property == "department" ? u.Department : u.JobTitle;

    /// <summary>
    /// One detail for everyone. Department and job title are in the local copy; the rest come
    /// from <paramref name="known"/> (details already read) or are read live a few at a time, so
    /// each change shows its true "before".
    /// </summary>
    public static async Task<BulkPlan> SetDetailAsync(IReadOnlyList<UserSummary> users, IUserRecords? records, string property, string value, CancellationToken ct,
        IReadOnlyDictionary<string, UserDetails>? known = null)
    {
        if (property == "usageLocation" && value.Length > 0)
        {
            value = value.ToUpperInvariant();
            if (!Countries.All.Any(c => c.Code == value))
                return BulkPlan.Invalid($"\"{value}\" isn't a country code. Use two letters, such as US, GB or CA.");
        }
        var label = Details.First(d => d.Property == property).Label.Split(" (")[0];
        var current = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (IsListDetail(property))
        {
            foreach (var u in users)
                current[u.Id] = ListValue(u, property);
        }
        else
        {
            var missing = new List<UserSummary>();
            foreach (var u in users)
            {
                if (known?.TryGetValue(u.Id, out var details) == true)
                    current[u.Id] = details[property];
                else
                    missing.Add(u);
            }
            if (records is not null && missing.Count > 0)
            {
                var read = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
                await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = ReadsAtOnce, CancellationToken = ct },
                    async (u, token) => read[u.Id] = (await records.GetDetailsAsync(u.Id, token))[property]);
                foreach (var (id, v) in read)
                    current[id] = v;
            }
        }
        var wanted = value.Length == 0 ? null : value;
        return Plan(users, u =>
        {
            // Usage location is a cloud setting; the other details of a synced person come from AD.
            if (u.SyncedFromOnPremises && property != "usageLocation")
                return (null, $"{u.DisplayName}: synced from on-premises Active Directory (change it there)");
            var old = current.GetValueOrDefault(u.Id);
            return string.Equals(old ?? "", wanted ?? "", StringComparison.Ordinal)
                ? (null, $"{u.DisplayName}: already {(wanted is null ? "empty" : $"\"{wanted}\"")}")
                : (new SetUserText(GroupAccess.Target(u), property, label, old, wanted), null);
        });
    }

    /// <summary>
    /// Replies on (one message, inside and outside) or off for everyone with a mailbox. Each
    /// mailbox's current replies are read live, a few at a time, so the review shows the real "before".
    /// </summary>
    static async Task<BulkPlan> AutoRepliesAsync(IReadOnlyList<UserSummary> users, IUserRecords? records, bool on, string message, CancellationToken ct)
    {
        if (on && message.Length == 0)
            return BulkPlan.Invalid("Write the reply people will get.");
        if (records is null)
            return BulkPlan.Invalid("Mailboxes can't be read here.");
        var withMailbox = users.Where(u => u.Mailbox is not null).ToArray();
        var current = new System.Collections.Concurrent.ConcurrentDictionary<string, AutoReply>(StringComparer.Ordinal);
        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(withMailbox, new ParallelOptions { MaxDegreeOfParallelism = ReadsAtOnce, CancellationToken = ct }, async (u, token) =>
        {
            try
            {
                current[u.Id] = (await records.GetMailAsync(u.Id, u.Mailbox!.PrimaryAddress ?? u.UserPrincipalName, token)).AutoReply;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed.Add(u.Id);
            }
        });
        return Plan(users, u =>
        {
            if (u.Mailbox is null)
                return (null, $"{u.DisplayName}: no mailbox");
            if (!current.TryGetValue(u.Id, out var old))
                return (null, $"{u.DisplayName}: couldn't read their mailbox");
            var wanted = on ? new AutoReply(AutoReplyState.On, null, null, message, message, AutoReplyAudience.All) : old with { State = AutoReplyState.Off, Start = null, End = null };
            return old == wanted ? (null, $"{u.DisplayName}: already {(on ? "on with that reply" : "off")}")
                : (new SetAutoReply(GroupAccess.Target(u), old, wanted), null);
        });
    }

    static BulkPlan Membership(IReadOnlyList<UserSummary> users, GroupSummary g, GroupRole role, bool add)
    {
        if (GroupAccess.LockedReason(g, role) is { } locked)
            return BulkPlan.Invalid(locked);
        var current = (role == GroupRole.Owner ? g.OwnerList : g.Members).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return Plan(users, u => current.Contains(u.Id) == add
            ? (null, $"{u.DisplayName}: {(add ? "already" : "not")} {(role == GroupRole.Owner ? "an owner" : "a member")}")
            : (new SetGroupMembership(GroupAccess.Target(g), GroupAccess.Target(u), "user", role, g.IsExchangeManaged, !add, add), null));
    }

    static BulkPlan Access(IReadOnlyList<UserSummary> users, GroupSummary mailbox, MailboxRight right, bool grant)
    {
        var holders = mailbox.Members.Where(m => GroupAccess.RightsOf(m.Access).Contains(right)).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var name = SetMailboxAccess.RightName(right);
        return Plan(users, u => u.Id == mailbox.Id ? (null, $"{u.DisplayName}: is the mailbox itself")
            : holders.Contains(u.Id) == grant ? (null, $"{u.DisplayName}: {(grant ? "already has" : "doesn't have")} {name}")
            : (new SetMailboxAccess(GroupAccess.Target(mailbox), GroupAccess.Target(u), right, !grant, grant), null));
    }

    static BulkPlan Plan<T>(IEnumerable<T> rows, Func<T, (ChangeOp? Change, string? Skip)> each)
    {
        var changes = new List<ChangeOp>();
        var skipped = new List<string>();
        foreach (var row in rows)
        {
            var (change, skip) = each(row);
            if (change is not null)
                changes.Add(change);
            else if (skip is not null)
                skipped.Add(skip);
        }
        return new BulkPlan(changes, skipped);
    }

    // ---- Groups and mailboxes ---------------------------------------------------------------

    /// <param name="selected">The groups and mailboxes checked right now (read each time).</param>
    public static IReadOnlyList<BulkAction> ForGroups(Func<IReadOnlyList<GroupSummary>> selected, IDirectoryIndex index)
    {
        UserSummary? Person(PickOption o) => index.User(o.Id);

        BulkPlan ForEachGroup(PickOption picked, Func<GroupSummary, UserSummary, (ChangeOp?, string?)> each) =>
            Person(picked) is { } person ? Plan(selected(), g => each(g, person)) : BulkPlan.Invalid("That person isn't in the local copy any more.");

        return
        [
            new("Add a Member", "Add one person to every selected group.", "PersonAdd",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Join(g, p, GroupRole.Member, true))),
                pickPlaceholder: "Choose a person: type a name", pickOptions: () => GroupAccess.People(index)),

            new("Remove a Member", "Take one person out of every selected group.", "PersonDelete",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Join(g, p, GroupRole.Member, false))),
                pickPlaceholder: "Choose a member: type a name",
                pickOptions: () => MembersOf(selected(), index, g => g.Members), isCaution: true),

            new("Add an Owner", "Make one person an owner of every selected group.", "PersonStar",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Join(g, p, GroupRole.Owner, true))),
                pickPlaceholder: "Choose a person: type a name", pickOptions: () => GroupAccess.People(index)),

            new("Remove an Owner", "Take one owner off every selected group (a group's last owner stays).", "PersonSubtract",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Join(g, p, GroupRole.Owner, false))),
                pickPlaceholder: "Choose an owner: type a name",
                pickOptions: () => MembersOf(selected(), index, g => g.OwnerList), isCaution: true),

            new("Give Mailbox Access", "Give one person the same right on every selected shared, room or equipment mailbox.", "MailInbox",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Grant(g, p, (MailboxRight)a.Choice!.Value, true))),
                pickPlaceholder: "Choose a person: type a name", pickOptions: () => GroupAccess.People(index),
                choiceLabel: "Right", choices: Rights()),

            new("Remove Mailbox Access", "Take the same right away from one person on every selected mailbox.", "MailDismiss",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Grant(g, p, (MailboxRight)a.Choice!.Value, false))),
                pickPlaceholder: "Choose a person with access: type a name",
                pickOptions: () => MembersOf(selected().Where(g => g.IsMailbox).ToArray(), index, g => g.Members),
                choiceLabel: "Right", choices: Rights(), isCaution: true),
        ];
    }

    // ---- Devices ----------------------------------------------------------------------------

    /// <summary>A device's Entra record as a change target.</summary>
    public static ChangeTarget EntraTarget(DeviceSummary d) => new("device", d.ObjectId!, d.Name, null);

    /// <summary>A device's Intune record as a change target.</summary>
    public static ChangeTarget IntuneTarget(DeviceSummary d) => new("manageddevice", d.ManagedId!, d.Name, null);

    /// <summary>How long unseen before "Delete Stale Devices" offers a device, in days.</summary>
    public static readonly int[] StaleDays = [90, 180, 365];

    const string Everyday = "Everyday";
    const string Defender = "Microsoft Defender";
    const string Settings = "Settings";
    const string Remove = "Erase or Remove";

    /// <summary>Windows device names: at most 15 letters, digits and hyphens (what Windows accepts).</summary>
    static readonly System.Text.RegularExpressions.Regex WindowsName = new("^[A-Za-z0-9-]{1,15}$");

    /// <param name="selected">The devices checked right now (read each time).</param>
    /// <param name="one">
    /// One device's own page or preview: it also offers what only makes sense one at a time
    /// (rename, primary user, lost mode), and deleting it is plain "Delete Device", with no staleness test.
    /// </param>
    /// <param name="index">People, for choosing a primary user.</param>
    /// <param name="pending">What's in Pending changes: deleting a device and erasing or retiring it can't wait there together.</param>
    public static IReadOnlyList<BulkAction> ForDevices(Func<IReadOnlyList<DeviceSummary>> selected, Func<DateTimeOffset> now, bool one = false,
        IDirectoryIndex? index = null, Func<IReadOnlyList<ChangeOp>>? pending = null)
    {
        // Deleting a device's Intune record before it has checked in would stop a wipe or retire
        // reaching it; and once deleted there's nothing left to wipe. So each skips the other.
        IReadOnlyList<ChangeOp> Waiting() => pending?.Invoke() ?? [];
        string? RemovalWaiting(DeviceSummary d) => Waiting().OfType<RunDeviceAction>()
            .FirstOrDefault(a => a.Removes && a.Device.Id == d.ManagedId) is { } r ? $"{d.Name}: {r.Label} is waiting in Pending changes; apply it first" : null;
        string? DeleteWaiting(DeviceSummary d) => Waiting().OfType<DeleteDevice>()
            .Any(x => x.Device.Id == d.ObjectId || x.Device.Id == d.ManagedId) ? $"{d.Name}: Delete Device is waiting in Pending changes" : null;

        BulkPlan Intune(DeviceCommand command, bool keepUserData = false) => Plan(selected(), d => d.ManagedId is null
            ? (null, $"{d.Name}: not managed by Intune")
            : (new RunDeviceAction(IntuneTarget(d), command, keepUserData), null));

        // Windows-only actions skip the rest, saying why.
        BulkPlan Windows(DeviceCommand command, bool keepUserData = false) => Plan(selected(), d =>
            d.ManagedId is null ? (null, $"{d.Name}: not managed by Intune")
            : d.Platform != "Windows" ? (null, $"{d.Name}: not Windows")
            : (new RunDeviceAction(IntuneTarget(d), command, keepUserData), null));

        // Retire, wipe, Fresh Start and Autopilot reset. Intune erases only company-owned devices.
        BulkPlan Removal(DeviceCommand command, bool windowsOnly = false, bool keepUserData = false) => Plan(selected(), d =>
            d.ManagedId is null ? (null, $"{d.Name}: not managed by Intune")
            : windowsOnly && d.Platform != "Windows" ? (null, $"{d.Name}: not Windows")
            : command != DeviceCommand.Retire && d.Ownership == DeviceFields.Personal ? (null, $"{d.Name}: a personal device can't be erased; use Retire")
            : DeleteWaiting(d) is { } waiting ? (null, waiting)
            : (new RunDeviceAction(IntuneTarget(d), command, keepUserData), null));

        static DeleteDevice Delete(DeviceSummary d) =>
            new(d.ObjectId is not null ? EntraTarget(d) : IntuneTarget(d), d.ObjectId is not null ? d.ManagedId : null,
                d.LastSeen is { } last ? $"Last seen {Display.Exact(last)}" : "Never seen");

        var delete = one
            ? new BulkAction("Delete Device", "Delete its records from Intune and Entra. If it's still in use, it has to be registered again.", "DeleteDismiss",
                (_, _) => Task.FromResult(Plan(selected(), d => RemovalWaiting(d) is { } waiting ? (null, waiting) : (Delete(d), null))), isCaution: true, group: Remove)
            : new BulkAction("Delete Stale Devices", "Delete the records of devices nobody has used for a while, from Intune and Entra. Devices seen more recently are skipped.", "DeleteDismiss",
                (a, _) =>
                {
                    var days = (int)a.Choice!.Value;
                    var cutoff = now().AddDays(-days);
                    // A device never used is only stale once it was registered before the cutoff: a new
                    // one (or an Autopilot device waiting to be set up) hasn't signed in yet.
                    return Task.FromResult(Plan(selected(), d => RemovalWaiting(d) is { } waiting ? (null, waiting)
                        : d.LastSeen is { } seen
                        ? seen > cutoff ? (null, $"{d.Name}: seen {Display.Exact(seen)}") : (Delete(d), null)
                        : d.Registered is not { } registered ? (null, $"{d.Name}: never used, and when it was registered isn't known")
                        : registered > cutoff ? (null, $"{d.Name}: never used yet, registered {Display.Exact(registered)}")
                        : (Delete(d), null)));
                },
                choiceLabel: "Not Seen For", choices: StaleDays.Select(n => new BulkChoice($"{n} Days or More", n)).ToArray(), isCaution: true, group: Remove);

        var actions = new List<BulkAction>
        {
            new("Sync with Intune", "Ask it to check in now: it picks up new policies and apps, and reports its state.", "ArrowSync",
                (_, _) => Task.FromResult(Intune(DeviceCommand.Sync)), group: Everyday),
            new("Restart", "Restart it now. Whoever is using it may lose unsaved work.", "ArrowClockwise",
                (_, _) => Task.FromResult(Intune(DeviceCommand.Restart)), isCaution: true, group: Everyday),
            new("Lock", "Lock the screen; it's unlocked with its PIN or password.", "LockClosed",
                (_, _) => Task.FromResult(Intune(DeviceCommand.Lock)), isCaution: true, group: Everyday),
            new("Locate", "Ask it where it is (Windows with location on, supervised iPhones and iPads in lost mode, Android Enterprise). The location shows in the Intune admin center.", "Location",
                (_, _) => Task.FromResult(Intune(DeviceCommand.Locate)), isCaution: true, group: Everyday),
            new("Collect Diagnostics", "Windows uploads its logs; download them from the device in the Intune admin center.", "DocumentArrowUp",
                (_, _) => Task.FromResult(Windows(DeviceCommand.CollectDiagnostics)), group: Everyday),

            new("Quick Scan", "Microsoft Defender scans the places malware usually hides (Windows).", "ShieldCheckmark",
                (_, _) => Task.FromResult(Windows(DeviceCommand.QuickScan)), isCaution: true, group: Defender),
            new("Full Scan", "Microsoft Defender scans every file; it can take hours and slow the device (Windows).", "Shield",
                (_, _) => Task.FromResult(Windows(DeviceCommand.FullScan)), isCaution: true, group: Defender),
            new("Update Definitions", "Microsoft Defender downloads its latest security intelligence (Windows).", "ArrowDownload",
                (_, _) => Task.FromResult(Windows(DeviceCommand.UpdateDefinitions)), group: Defender),
        };

        if (one)
        {
            actions.Add(new("Rename", "Give it a new name. Windows takes it after a restart; iPhones and iPads need to be supervised.", "Rename",
                (a, _) =>
                {
                    var name = a.Text.Trim();
                    if (name.Length == 0)
                        return Task.FromResult(BulkPlan.Invalid("Type the new name."));
                    return Task.FromResult(Plan(selected(), d =>
                        d.ManagedId is null ? (null, $"{d.Name}: not managed by Intune")
                        : d.Platform == "Windows" && !WindowsName.IsMatch(name) ? (null, "Windows names are up to 15 letters, digits and hyphens.")
                        : string.Equals(d.Name, name, StringComparison.Ordinal) ? (null, $"{d.Name}: already its name")
                        : (new RenameDevice(IntuneTarget(d), d.Name, name), null)));
                },
                textLabel: "New Name", group: Settings));
            if (index is not null)
                actions.Add(new("Change Primary User", "The person its apps, policies and Company Portal follow.", "PersonSwap",
                    (a, _) =>
                    {
                        if (index.User(a.Picked!.Id) is not { } person)
                            return Task.FromResult(BulkPlan.Invalid("That person isn't in the local copy any more."));
                        return Task.FromResult(Plan(selected(), d =>
                            d.ManagedId is null ? (null, $"{d.Name}: not managed by Intune")
                            : d.UserId == person.Id ? (null, $"{d.Name}: already theirs")
                            : (new SetPrimaryUser(IntuneTarget(d), d.UserName, GroupAccess.Target(person)), null)));
                    },
                    pickPlaceholder: "Choose a person: type a name", pickOptions: () => GroupAccess.People(index), group: Settings));
        }

        actions.Add(new("Ownership", "Company-owned or personal: Intune collects less from personal devices and can't wipe them.", "Building",
            (a, _) =>
            {
                var company = (bool)a.Choice!.Value;
                return Task.FromResult(Plan(selected(), d =>
                    d.ManagedId is null ? (null, $"{d.Name}: not managed by Intune")
                    : d.Ownership == (company ? DeviceFields.Company : DeviceFields.Personal) ? (null, $"{d.Name}: already {(company ? "company-owned" : "personal")}")
                    : (new SetDeviceOwnership(IntuneTarget(d), d.Ownership != DeviceFields.Personal, company), null)));
            },
            choiceLabel: "Owned By", choices: [new BulkChoice("Company", true), new BulkChoice("Personal", false)], group: Settings));
        actions.Add(new("Disable or Enable", "A disabled device can't be used to sign in to Microsoft 365; enable it to allow that again.", "Prohibited",
            (a, _) =>
            {
                var enable = (bool)a.Choice!.Value;
                return Task.FromResult(Plan(selected(), d => d.ObjectId is null ? (null, $"{d.Name}: not in Entra")
                    : d.Enabled == enable ? (null, $"{d.Name}: already {(enable ? "enabled" : "disabled")}")
                    : (new SetDeviceEnabled(EntraTarget(d), d.Enabled != false, enable), null)));
            },
            choiceLabel: "Device", choices: [new BulkChoice("Disable", false), new BulkChoice("Enable", true)], group: Settings));
        actions.Add(new("Rotate BitLocker Keys", "Make new BitLocker recovery keys and back them up, after a key has been shown to someone (Windows).", "Key",
            (_, _) => Task.FromResult(Windows(DeviceCommand.RotateBitLockerKeys)), isCaution: true, group: Settings));
        actions.Add(new("Rotate Local Admin Password", "Give the local admin account a new password, after someone has used it (Windows LAPS).", "Password",
            (_, _) => Task.FromResult(Windows(DeviceCommand.RotateLocalAdminPassword)), isCaution: true, group: Settings));
        if (one)
            actions.Add(new("Lost Mode", "A supervised iPhone or iPad locks and shows your message until lost mode is turned off; then Locate can find it.", "PhoneLock",
                (a, _) =>
                {
                    var on = (bool)a.Choice!.Value;
                    var message = a.Text.Trim();
                    if (on && message.Length == 0)
                        return Task.FromResult(BulkPlan.Invalid("Type the message to show on its screen, such as who to call."));
                    return Task.FromResult(Plan(selected(), d =>
                        d.ManagedId is null ? (null, $"{d.Name}: not managed by Intune")
                        : d.Platform != "iOS/iPadOS" ? (null, $"{d.Name}: lost mode is for supervised iPhones and iPads")
                        : (new SetLostMode(IntuneTarget(d), on, on ? message : null), null)));
                },
                choiceLabel: "Lost Mode", choices: [new BulkChoice("Turn On", true), new BulkChoice("Turn Off", false)],
                textLabel: "Message on Its Screen (Needed When Turning On)", group: Settings));

        actions.Add(new("Retire", "Remove work data, apps and settings and stop managing it. The owner's own things stay.", "PhoneLinkSetup",
            (_, _) => Task.FromResult(Removal(DeviceCommand.Retire)), isCaution: true, group: Remove));
        actions.Add(new("Wipe", "Erase it back to factory settings (company-owned devices only). You type its name to confirm before it's sent.", "Delete",
            (_, _) => Task.FromResult(Removal(DeviceCommand.Wipe)), isCaution: true, group: Remove));
        actions.Add(new("Fresh Start", "Reinstall Windows and remove apps the manufacturer added; keep or remove the user's files. You type its name to confirm.", "ArrowReset",
            (a, _) => Task.FromResult(Removal(DeviceCommand.FreshStart, windowsOnly: true, keepUserData: (bool)a.Choice!.Value)),
            choiceLabel: "Their Files", choices: [new BulkChoice("Keep Their Files", true), new BulkChoice("Remove Their Files", false)], isCaution: true, group: Remove));
        actions.Add(new("Autopilot Reset", "Return Windows to a business-ready state: apps, settings and files removed, still joined and enrolled. You type its name to confirm.", "ArrowCounterclockwise",
            (_, _) => Task.FromResult(Removal(DeviceCommand.AutopilotReset, windowsOnly: true)), isCaution: true, group: Remove));
        actions.Add(delete);
        return actions;
    }

    // ---- Autopilot --------------------------------------------------------------------------

    /// <summary>An Autopilot device as a change target, named by its serial number.</summary>
    public static ChangeTarget AutopilotTarget(AutopilotDevice d) => new("autopilot", d.Id, d.Serial, null);

    /// <param name="selected">The Autopilot devices checked right now (read each time).</param>
    public static IReadOnlyList<BulkAction> ForAutopilot(Func<IReadOnlyList<AutopilotDevice>> selected) =>
    [
        new("Set Group Tag", "The tag Autopilot profiles and dynamic groups match on. Leave it empty to clear it.", "Tag",
            (a, _) =>
            {
                var tag = a.Text.Trim();
                return Task.FromResult(Plan(selected(), d => string.Equals(d.GroupTag ?? "", tag, StringComparison.Ordinal)
                    ? (null, $"{d.Serial}: already {(tag.Length == 0 ? "untagged" : tag)}")
                    : (new SetAutopilotGroupTag(AutopilotTarget(d), d.GroupTag, tag.Length == 0 ? null : tag), null)));
            },
            textLabel: "Group Tag"),

        new("Remove from Autopilot", "It won't be set up by Autopilot again. Do this when a device is sold, scrapped or returned. Intune and Entra keep their records.", "DeleteDismiss",
            (_, _) => Task.FromResult(Plan(selected(), d =>
                (new DeleteAutopilotDevice(AutopilotTarget(d), string.Join(" · ", new[] { d.Model, d.EnrollmentState }.Where(s => !string.IsNullOrEmpty(s)))), null))),
            isCaution: true),
    ];

    /// <summary>People who are in any of the selected groups' lists, for "remove" pickers.</summary>
    static IEnumerable<PickOption> MembersOf(IReadOnlyList<GroupSummary> groups, IDirectoryIndex index, Func<GroupSummary, IReadOnlyList<GroupMember>> list)
    {
        var present = groups.SelectMany(list).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return GroupAccess.People(index).Where(o => present.Contains(o.Id));
    }

    static (ChangeOp?, string?) Join(GroupSummary g, UserSummary person, GroupRole role, bool add)
    {
        if (g.IsMailbox)
            return (null, $"{g.DisplayName}: a mailbox (use mailbox access)");
        if (GroupAccess.LockedReason(g, role) is not null)
            return (null, $"{g.DisplayName}: dynamic, follows its rule");
        var current = role == GroupRole.Owner ? g.OwnerList : g.Members;
        var has = current.Any(m => m.Id == person.Id);
        if (has == add)
            return (null, $"{g.DisplayName}: {(add ? "already" : "not")} {(role == GroupRole.Owner ? "an owner" : "a member")}");
        if (!add && role == GroupRole.Owner && !g.IsExchangeManaged && g.OwnerList.Count <= 1)
            return (null, $"{g.DisplayName}: they're its only owner");
        return (new SetGroupMembership(GroupAccess.Target(g), GroupAccess.Target(person), "user", role, g.IsExchangeManaged, has, add), null);
    }

    static (ChangeOp?, string?) Grant(GroupSummary g, UserSummary person, MailboxRight right, bool grant)
    {
        if (!g.IsMailbox)
            return (null, $"{g.DisplayName}: not a mailbox");
        var has = g.Members.Any(m => m.Id == person.Id && GroupAccess.RightsOf(m.Access).Contains(right));
        return has == grant
            ? (null, $"{g.DisplayName}: {(grant ? "already has" : "doesn't have")} {SetMailboxAccess.RightName(right)}")
            : (new SetMailboxAccess(GroupAccess.Target(g), GroupAccess.Target(person), right, has, grant), null);
    }
}
