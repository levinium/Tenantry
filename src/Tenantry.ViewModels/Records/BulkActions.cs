using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// The actions offered when several users, or several groups and mailboxes, are selected. Each
/// plans one change per row it applies to and names the rows it skips, with the reason.
/// </summary>
public static class BulkActions
{
    /// <summary>Details read live (the list's local copy holds only department and job title) this many at a time.</summary>
    const int ReadsAtOnce = 4;

    static readonly (string Property, string Label)[] Details =
    [
        ("department", "Department"), ("jobTitle", "Job title"), ("companyName", "Company"), ("officeLocation", "Office"),
        ("city", "City"), ("country", "Country or region"), ("usageLocation", "Usage location (country code, e.g. US)"),
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
            new("Block or allow sign-in", "Stop these accounts signing in, or let them sign in again.", "PersonProhibited",
                (a, _) => Task.FromResult(Plan(selected(), u =>
                {
                    var allow = (bool)a.Choice!.Value;
                    return u.AccountEnabled == allow ? (null, $"{u.DisplayName}: already {(allow ? "allowed" : "blocked")}")
                        : (new SetSignInAllowed(GroupAccess.Target(u), u.AccountEnabled, allow), null);
                })),
                choiceLabel: "Sign-in", choices: [new BulkChoice("Block sign-in", false), new BulkChoice("Allow sign-in", true)]),

            new("Sign out everywhere", "End every session; their apps ask them to sign in again within about an hour.", "SignOut",
                (_, _) => Task.FromResult(Plan(selected(), u => (new RevokeSessions(GroupAccess.Target(u)), null))),
                isCaution: true),

            new("Add a license", "Assign the same license to everyone who doesn't have it.", "Add",
                async (a, ct) =>
                {
                    var sku = (LicenseSku)a.Choice!.Value;
                    var plan = Plan(selected(), u => u.Licenses.Contains(sku.Name) ? (null, $"{u.DisplayName}: already has it")
                        : (new SetLicenseAssigned(GroupAccess.Target(u), sku.SkuId, sku.Name, false, true), null));
                    return plan.Changes.Count > sku.Available
                        ? plan with { Skipped = [$"Only {sku.Available:N0} left for {plan.Changes.Count:N0} people: the rest will fail when applied.", .. plan.Skipped] }
                        : plan;
                },
                choiceLabel: "License",
                loadChoices: async ct => (await Catalog(ct)).OrderBy(s => s.Name, StringComparer.CurrentCulture)
                    .Select(s => new BulkChoice($"{s.Name} · {s.Available:N0} left", s)).ToArray()),

            new("Remove a license", "Take a license away from everyone who has it directly.", "Subtract",
                (a, _) =>
                {
                    var sku = (LicenseSku)a.Choice!.Value;
                    return Task.FromResult(Plan(selected(), u => !u.Licenses.Contains(sku.Name) ? (null, $"{u.DisplayName}: doesn't have it")
                        : (new SetLicenseAssigned(GroupAccess.Target(u), sku.SkuId, sku.Name, true, false), null)));
                },
                choiceLabel: "License",
                loadChoices: async ct =>
                {
                    var held = selected().SelectMany(u => u.Licenses).ToHashSet(StringComparer.Ordinal);
                    return (await Catalog(ct)).Where(s => held.Contains(s.Name)).OrderBy(s => s.Name, StringComparer.CurrentCulture)
                        .Select(s => new BulkChoice(s.Name, s)).ToArray();
                },
                isCaution: true),

            new("Automatic replies", "Turn automatic replies on with one message for everyone (inside and outside), or turn them off.", "MailArrowUp",
                (a, ct) => AutoRepliesAsync(selected(), records, (bool)a.Choice!.Value, a.Text.Trim(), ct),
                choiceLabel: "Replies", choices: [new BulkChoice("Turn on", true), new BulkChoice("Turn off", false)],
                textLabel: "Reply (needed when turning on)"),

            new("Turn forwarding off", "Stop forwarding mail for everyone selected who forwards it.", "MailArrowForward",
                (_, _) => Task.FromResult(Plan(selected(), u => u.Mailbox is not { } m ? (null, $"{u.DisplayName}: no mailbox")
                    : m.ForwardingTo is null ? (null, $"{u.DisplayName}: not forwarding")
                    : (new SetForwarding(GroupAccess.Target(u), m.ForwardingTo, m.KeepsCopyWhenForwarding, null, false, false), null)))),

            new("Address list", "Hide everyone from the address list (Outlook's directory), or show them again.", "BookContacts",
                (a, _) =>
                {
                    var hide = (bool)a.Choice!.Value;
                    return Task.FromResult(Plan(selected(), u => u.Mailbox is not { } m ? (null, $"{u.DisplayName}: no mailbox")
                        : m.HiddenFromAddressList == hide ? (null, $"{u.DisplayName}: already {(hide ? "hidden" : "shown")}")
                        : (new SetHiddenFromAddressList(GroupAccess.Target(u), m.HiddenFromAddressList, hide), null)));
                },
                choiceLabel: "Address list", choices: [new BulkChoice("Hide from the address list", true), new BulkChoice("Show in the address list", false)]),

            new("Turn an email app off", "Stop everyone reaching their mail one way, such as POP or IMAP (older apps that often skip multi-factor sign-in).", "PlugDisconnected",
                (a, _) => Task.FromResult(EmailApp(selected(), (string)a.Choice!.Value, false)),
                choiceLabel: "App", choices: AppChoices(), isCaution: true),

            new("Turn an email app on", "Let everyone reach their mail one more way.", "PlugConnected",
                (a, _) => Task.FromResult(EmailApp(selected(), (string)a.Choice!.Value, true)),
                choiceLabel: "App", choices: AppChoices()),

            new("Litigation hold", "Keep every item in their mailboxes, even ones they delete, until the hold is lifted; or lift it.", "Gavel",
                (a, _) =>
                {
                    var on = (bool)a.Choice!.Value;
                    var note = a.Text.Trim() is { Length: > 0 } n ? n : null;
                    return Task.FromResult(Plan(selected(), u => u.Mailbox is not { } m ? (null, $"{u.DisplayName}: no mailbox")
                        : m.LitigationHold == on ? (null, $"{u.DisplayName}: {(on ? "already on hold" : "not on hold")}")
                        // The list doesn't hold a hold's days or note; turning it off leaves them as Exchange has them.
                        : (new SetLitigationHold(GroupAccess.Target(u), m.LitigationHold, null, null, on, null, on ? note : null), null)));
                },
                choiceLabel: "Hold", choices: [new BulkChoice("Turn on (keep items indefinitely)", true), new BulkChoice("Turn off", false)],
                textLabel: "Note for the hold (optional)", isCaution: true),

            new("Add to a group", "Make everyone a member of one group.", "PeopleAdd",
                (a, _) => Task.FromResult(index.Group(a.Picked!.Id) is { } g ? Membership(selected(), g, GroupRole.Member, true)
                    : BulkPlan.Invalid("That group isn't in the local copy any more.")),
                pickPlaceholder: "Choose a group: type its name",
                pickOptions: () => index.AllGroups.Where(g => !g.IsMailbox && !g.IsDynamic).Select(PickOption.Of)),

            new("Remove from a group", "Take everyone out of one group.", "PeopleRemove",
                (a, _) => Task.FromResult(index.Group(a.Picked!.Id) is { } g ? Membership(selected(), g, GroupRole.Member, false)
                    : BulkPlan.Invalid("That group isn't in the local copy any more.")),
                pickPlaceholder: "Choose a group they're in: type its name",
                pickOptions: () =>
                {
                    var ids = Ids();
                    return index.AllGroups.Where(g => !g.IsMailbox && !g.IsDynamic && g.Members.Any(m => ids.Contains(m.Id))).Select(PickOption.Of);
                },
                isCaution: true),

            new("Give access to a shared mailbox", "Give everyone the same right on one shared, room or equipment mailbox.", "MailInbox",
                (a, _) => Task.FromResult(index.Group(a.Picked!.Id) is { } m ? Access(selected(), m, (MailboxRight)a.Choice!.Value, true)
                    : BulkPlan.Invalid("That mailbox isn't in the local copy any more.")),
                pickPlaceholder: "Choose a mailbox: type its name",
                pickOptions: () => index.AllGroups.Where(g => g.IsMailbox).Select(PickOption.Of),
                choiceLabel: "Right", choices: Rights()),

            new("Remove access to a shared mailbox", "Take the same right away from everyone on one mailbox.", "MailDismiss",
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

    static BulkChoice[] Rights() => GroupAccess.Rights.Select(r => new BulkChoice(SetMailboxAccess.RightName(r), r)).ToArray();

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
            new("Add a member", "Add one person to every selected group.", "PersonAdd",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Join(g, p, GroupRole.Member, true))),
                pickPlaceholder: "Choose a person: type a name", pickOptions: () => GroupAccess.People(index)),

            new("Remove a member", "Take one person out of every selected group.", "PersonDelete",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Join(g, p, GroupRole.Member, false))),
                pickPlaceholder: "Choose a member: type a name",
                pickOptions: () => MembersOf(selected(), index, g => g.Members), isCaution: true),

            new("Add an owner", "Make one person an owner of every selected group.", "PersonStar",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Join(g, p, GroupRole.Owner, true))),
                pickPlaceholder: "Choose a person: type a name", pickOptions: () => GroupAccess.People(index)),

            new("Remove an owner", "Take one owner off every selected group (a group's last owner stays).", "PersonSubtract",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Join(g, p, GroupRole.Owner, false))),
                pickPlaceholder: "Choose an owner: type a name",
                pickOptions: () => MembersOf(selected(), index, g => g.OwnerList), isCaution: true),

            new("Give mailbox access", "Give one person the same right on every selected shared, room or equipment mailbox.", "MailInbox",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Grant(g, p, (MailboxRight)a.Choice!.Value, true))),
                pickPlaceholder: "Choose a person: type a name", pickOptions: () => GroupAccess.People(index),
                choiceLabel: "Right", choices: Rights()),

            new("Remove mailbox access", "Take the same right away from one person on every selected mailbox.", "MailDismiss",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Grant(g, p, (MailboxRight)a.Choice!.Value, false))),
                pickPlaceholder: "Choose a person with access: type a name",
                pickOptions: () => MembersOf(selected().Where(g => g.IsMailbox).ToArray(), index, g => g.Members),
                choiceLabel: "Right", choices: Rights(), isCaution: true),
        ];
    }

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
