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

    public static IReadOnlyList<BulkAction> ForUsers(IReadOnlyList<UserSummary> users, IDirectoryIndex index, IUserRecords? records)
    {
        var ids = users.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        Task<IReadOnlyList<LicenseSku>>? catalog = null;
        Task<IReadOnlyList<LicenseSku>> Catalog(CancellationToken ct) => catalog ??= LoadCatalog(ct);
        async Task<IReadOnlyList<LicenseSku>> LoadCatalog(CancellationToken ct) =>
            records is null ? [] : (await records.GetLicensingAsync(users[0].Id, ct)).Catalog;

        return
        [
            new("Set a detail", "Give everyone the same department, job title, office or other detail. Leave the value empty to clear it.", "Edit",
                (a, ct) => SetDetailAsync(users, records, (string)a.Choice!.Value, a.Text.Trim(), ct),
                choiceLabel: "Detail", choices: Details.Select(d => new BulkChoice(d.Label, d.Property)).ToArray(),
                textLabel: "New value"),

            new("Block or allow sign-in", "Stop these accounts signing in, or let them sign in again.", "PersonProhibited",
                (a, _) => Task.FromResult(Plan(users, u =>
                {
                    var allow = (bool)a.Choice!.Value;
                    return u.AccountEnabled == allow ? (null, $"{u.DisplayName}: already {(allow ? "allowed" : "blocked")}")
                        : (new SetSignInAllowed(GroupAccess.Target(u), u.AccountEnabled, allow), null);
                })),
                choiceLabel: "Sign-in", choices: [new BulkChoice("Block sign-in", false), new BulkChoice("Allow sign-in", true)]),

            new("Sign out everywhere", "End every session; their apps ask them to sign in again within about an hour.", "SignOut",
                (_, _) => Task.FromResult(Plan(users, u => (new RevokeSessions(GroupAccess.Target(u)), null))),
                isCaution: true),

            new("Add a license", "Assign the same license to everyone who doesn't have it.", "Add",
                async (a, ct) =>
                {
                    var sku = (LicenseSku)a.Choice!.Value;
                    var plan = Plan(users, u => u.Licenses.Contains(sku.Name) ? (null, $"{u.DisplayName}: already has it")
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
                    return Task.FromResult(Plan(users, u => !u.Licenses.Contains(sku.Name) ? (null, $"{u.DisplayName}: doesn't have it")
                        : (new SetLicenseAssigned(GroupAccess.Target(u), sku.SkuId, sku.Name, true, false), null)));
                },
                choiceLabel: "License",
                loadChoices: async ct =>
                {
                    var held = users.SelectMany(u => u.Licenses).ToHashSet(StringComparer.Ordinal);
                    return (await Catalog(ct)).Where(s => held.Contains(s.Name)).OrderBy(s => s.Name, StringComparer.CurrentCulture)
                        .Select(s => new BulkChoice(s.Name, s)).ToArray();
                },
                isCaution: true),

            new("Add to a group", "Make everyone a member of one group.", "PeopleAdd",
                (a, _) => Task.FromResult(index.Group(a.Picked!.Id) is { } g ? Membership(users, g, GroupRole.Member, true)
                    : BulkPlan.Invalid("That group isn't in the local copy any more.")),
                pickPlaceholder: "Choose a group: type its name",
                pickOptions: () => index.AllGroups.Where(g => !g.IsMailbox && !g.IsDynamic).Select(PickOption.Of)),

            new("Remove from a group", "Take everyone out of one group.", "PeopleRemove",
                (a, _) => Task.FromResult(index.Group(a.Picked!.Id) is { } g ? Membership(users, g, GroupRole.Member, false)
                    : BulkPlan.Invalid("That group isn't in the local copy any more.")),
                pickPlaceholder: "Choose a group they're in: type its name",
                pickOptions: () => index.AllGroups.Where(g => !g.IsMailbox && !g.IsDynamic && g.Members.Any(m => ids.Contains(m.Id))).Select(PickOption.Of),
                isCaution: true),

            new("Give access to a shared mailbox", "Give everyone the same right on one shared, room or equipment mailbox.", "MailInbox",
                (a, _) => Task.FromResult(index.Group(a.Picked!.Id) is { } m ? Access(users, m, (MailboxRight)a.Choice!.Value, true)
                    : BulkPlan.Invalid("That mailbox isn't in the local copy any more.")),
                pickPlaceholder: "Choose a mailbox: type its name",
                pickOptions: () => index.AllGroups.Where(g => g.IsMailbox).Select(PickOption.Of),
                choiceLabel: "Right", choices: Rights()),

            new("Remove access to a shared mailbox", "Take the same right away from everyone on one mailbox.", "MailDismiss",
                (a, _) => Task.FromResult(index.Group(a.Picked!.Id) is { } m ? Access(users, m, (MailboxRight)a.Choice!.Value, false)
                    : BulkPlan.Invalid("That mailbox isn't in the local copy any more.")),
                pickPlaceholder: "Choose a mailbox they can use: type its name",
                pickOptions: () => index.AllGroups.Where(g => g.IsMailbox && g.Members.Any(m => ids.Contains(m.Id))).Select(PickOption.Of),
                choiceLabel: "Right", choices: Rights(), isCaution: true),
        ];
    }

    static BulkChoice[] Rights() => GroupAccess.Rights.Select(r => new BulkChoice(SetMailboxAccess.RightName(r), r)).ToArray();

    /// <summary>
    /// One detail for everyone. Department and job title are in the local copy; the rest are read
    /// live a few at a time, so each change shows its true "before".
    /// </summary>
    static async Task<BulkPlan> SetDetailAsync(IReadOnlyList<UserSummary> users, IUserRecords? records, string property, string value, CancellationToken ct)
    {
        if (property == "usageLocation" && value.Length > 0)
        {
            value = value.ToUpperInvariant();
            if (!Countries.All.Any(c => c.Code == value))
                return BulkPlan.Invalid($"\"{value}\" isn't a country code. Use two letters, such as US, GB or CA.");
        }
        var label = Details.First(d => d.Property == property).Label.Split(" (")[0];
        var current = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (property is "department" or "jobTitle")
        {
            foreach (var u in users)
                current[u.Id] = property == "department" ? u.Department : u.JobTitle;
        }
        else if (records is not null)
        {
            var read = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(users, new ParallelOptions { MaxDegreeOfParallelism = ReadsAtOnce, CancellationToken = ct },
                async (u, token) => read[u.Id] = (await records.GetDetailsAsync(u.Id, token))[property]);
            foreach (var (id, v) in read)
                current[id] = v;
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

    public static IReadOnlyList<BulkAction> ForGroups(IReadOnlyList<GroupSummary> groups, IDirectoryIndex index)
    {
        var ids = groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        UserSummary? Person(PickOption o) => index.User(o.Id);

        BulkPlan ForEachGroup(PickOption picked, Func<GroupSummary, UserSummary, (ChangeOp?, string?)> each) =>
            Person(picked) is { } person ? Plan(groups, g => each(g, person)) : BulkPlan.Invalid("That person isn't in the local copy any more.");

        return
        [
            new("Add a member", "Add one person to every selected group.", "PersonAdd",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Join(g, p, GroupRole.Member, true))),
                pickPlaceholder: "Choose a person: type a name", pickOptions: () => GroupAccess.People(index)),

            new("Remove a member", "Take one person out of every selected group.", "PersonDelete",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Join(g, p, GroupRole.Member, false))),
                pickPlaceholder: "Choose a member: type a name",
                pickOptions: () => MembersOf(groups, index, g => g.Members), isCaution: true),

            new("Add an owner", "Make one person an owner of every selected group.", "PersonStar",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Join(g, p, GroupRole.Owner, true))),
                pickPlaceholder: "Choose a person: type a name", pickOptions: () => GroupAccess.People(index)),

            new("Remove an owner", "Take one owner off every selected group (a group's last owner stays).", "PersonSubtract",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Join(g, p, GroupRole.Owner, false))),
                pickPlaceholder: "Choose an owner: type a name",
                pickOptions: () => MembersOf(groups, index, g => g.OwnerList), isCaution: true),

            new("Give mailbox access", "Give one person the same right on every selected shared, room or equipment mailbox.", "MailInbox",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Grant(g, p, (MailboxRight)a.Choice!.Value, true))),
                pickPlaceholder: "Choose a person: type a name", pickOptions: () => GroupAccess.People(index),
                choiceLabel: "Right", choices: Rights()),

            new("Remove mailbox access", "Take the same right away from one person on every selected mailbox.", "MailDismiss",
                (a, _) => Task.FromResult(ForEachGroup(a.Picked!, (g, p) => Grant(g, p, (MailboxRight)a.Choice!.Value, false))),
                pickPlaceholder: "Choose a person with access: type a name",
                pickOptions: () => MembersOf(groups.Where(g => g.IsMailbox).ToArray(), index, g => g.Members),
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
