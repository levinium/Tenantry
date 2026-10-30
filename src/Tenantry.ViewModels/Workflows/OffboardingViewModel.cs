using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels.Workflows;

/// <summary>
/// Offboarding one person or several: a checklist of everything that usually happens when someone
/// leaves, the usual steps already ticked. Confirming reads what each person has (their sign-in
/// methods, roles, licenses, groups…) and puts one change per item into Pending changes, in an order
/// that's safe to apply: the mailbox is converted to shared before its license goes. The account
/// itself is never deleted. Nothing is sent until Pending changes are applied.
/// </summary>
public sealed partial class OffboardingViewModel : ObservableObject
{
    /// <summary>People's details are read this many at a time.</summary>
    const int ReadsAtOnce = 4;

    public const string DefaultReply =
        "Thank you for your message. {name} is no longer with {organization}. For help, please contact {manager}.";

    readonly IReadOnlyList<UserSummary> _people;
    readonly IDirectoryIndex _index;
    readonly IUserRecords? _records;
    readonly ChangePipeline _pipeline;
    readonly string _organization;
    readonly Action _close;

    public OffboardingViewModel(IReadOnlyList<UserSummary> people, IDirectoryIndex index, IUserRecords? records, ChangePipeline pipeline,
        string organization, Action close)
    {
        _people = people;
        _index = index;
        _records = records;
        _pipeline = pipeline;
        _organization = organization;
        _close = close;
        Sections =
        [
            new("Lock the Account",
            [
                Step("block", "Block Sign-In", "They can't sign in from now on. The account stays, with its mail and files.", true),
                Step("signout", "Sign Out Everywhere", "Ends every session and app sign-in they still have.", true),
                Step("password", "Reset the Password", "A new password in this tenant's style, shown to you once after applying.", true),
                Step("methods", "Remove Their Sign-In Methods", "Authenticator, phones, passkeys: nothing left to approve a sign-in with.", true),
                Step("roles", "Remove Admin Roles", "Any directory role they hold directly.", true),
                Step("consents", "Revoke App Consents", "Apps they let read their data stop getting access.", true),
            ]),
            new("Access",
            [
                Step("groups", "Remove from Groups, Lists and Shared Mailboxes",
                    "Every group and distribution list they're in or own, and their access to shared mailboxes. Groups that follow a rule (dynamic) are skipped, and so is a group they're the only owner of.", true),
            ]),
            new("Mail",
            [
                Step("autoreply", "Set an Automatic Reply", "People who write to them get the reply below.", true),
                Step("hide", "Hide from the Address List", "They no longer show in Outlook's directory.", true),
                Step("convert", "Convert the Mailbox to Shared", "Mail, calendar and contacts stay, and needs no license up to 50 GB.", true),
                Step("licenses", "Remove Their Licenses",
                    "Licenses assigned to them directly; after the conversion, so the mailbox is kept. Licenses that come from a group go when they leave the group.", true),
                Step("forward", "Forward Their Mail", "New mail goes to the person you choose below, and a copy stays in the mailbox.", false),
            ]),
            new("Hand Over",
            [
                Step("managermail", "Give Their Manager Full Access to the Mailbox", "The manager can open it in Outlook.", false),
                Step("manageronedrive", "Give Their Manager Access to Their OneDrive", "The manager can open all of their files.", false),
            ]),
            new("Devices",
            [
                Step("retire", "Retire Their Intune Devices", "Removes work data and apps from devices Intune manages for them; their own things stay.", false),
            ]),
        ];
        ForwardPicker = new PersonPicker("Forward to: type a name", () => GroupAccess.People(_index).Where(o => _people.All(p => p.Id != o.Id)),
            o => ForwardTo = o);
    }

    WorkflowStep Step(string key, string title, string detail, bool on) => new(key, title, detail, on);

    public string Heading => _people.Count == 1 ? $"Offboard {_people[0].DisplayName}" : $"Offboard {_people.Count:N0} people";

    public string Intro => (_people.Count == 1 ? "Everything below is added to Pending Changes for review. " : $"The same steps for each of the {_people.Count:N0} people, all added to Pending Changes for review. ")
        + "Nothing changes in Microsoft 365 until you apply them, and the account is never deleted.";

    public IReadOnlyList<UserSummary> People => _people;

    public string PeopleText => string.Join(", ", _people.Take(6).Select(p => p.DisplayName)) + (_people.Count > 6 ? $" and {_people.Count - 6:N0} more" : "");

    public IReadOnlyList<WorkflowSection> Sections { get; }

    /// <summary>The checklist in two columns, so the popup fits without scrolling: the account and access, then mail onward.</summary>
    public IReadOnlyList<WorkflowSection> LeftSections => Sections.Take(2).ToArray();

    public IReadOnlyList<WorkflowSection> RightSections => Sections.Skip(2).ToArray();

    /// <summary>Under the popup's title: who, when it's more than one person (one is already in the title).</summary>
    public string? Subtitle => _people.Count == 1 ? null : PeopleText;

    WorkflowStep Get(string key) => Sections.SelectMany(s => s.Steps).First(s => s.Key == key);

    bool On(string key) => Get(key).IsOn;

    // ---- The reply and the forwarding address -------------------------------------------------

    [ObservableProperty]
    public partial string ReplyText { get; set; } = DefaultReply;

    public string ReplyHelp => "{name}, {first name}, {organization} and {manager} are filled in for each person; with no manager set, {manager} becomes \"us\".";

    public PersonPicker ForwardPicker { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForwardTo), nameof(ForwardToText))]
    public partial PickOption? ForwardTo { get; private set; }

    public bool HasForwardTo => ForwardTo is not null;

    public string ForwardToText => ForwardTo is { } o ? $"Forward to {o.Name} ({o.Address})" : "";

    [RelayCommand]
    void ClearForwardTo() => ForwardTo = null;

    /// <summary>The reply for one person, with the placeholders filled in.</summary>
    public string ReplyFor(UserSummary person, string? managerName) => ReplyText
        .Replace("{name}", person.DisplayName, StringComparison.OrdinalIgnoreCase)
        .Replace("{first name}", person.DisplayName.Split(' ')[0], StringComparison.OrdinalIgnoreCase)
        .Replace("{organization}", _organization, StringComparison.OrdinalIgnoreCase)
        .Replace("{manager}", managerName ?? "us", StringComparison.OrdinalIgnoreCase);

    // ---- Planning -----------------------------------------------------------------------------

    /// <summary>What's read about one person before planning; each part is read only when a ticked step needs it.</summary>
    sealed class Facts
    {
        public IReadOnlyList<AuthMethod> Methods = [];
        public IReadOnlyList<RoleHeld> Roles = [];
        public IReadOnlyList<AppConsent> Consents = [];
        public IReadOnlyList<LicenseAssignment> Licenses = [];
        public AutoReply? Reply;
        public UserSummary? Manager;
        public string? ManagerName;
        public OneDriveInfo? Drive;
        public IReadOnlyList<UserDevice> Devices = [];
        public readonly List<string> Problems = [];
    }

    async Task<Facts> ReadAsync(UserSummary person, CancellationToken ct)
    {
        var facts = new Facts();
        if (_records is null)
            return facts;
        async Task Try(string what, Func<Task> read)
        {
            try
            {
                await read();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lock (facts.Problems)
                    facts.Problems.Add($"{person.DisplayName}: couldn't read {what} ({Friendly.Message(e)}), so that step is left out for them.");
            }
        }
        var security = _records as ISecurityRecords;
        var tasks = new List<Task>();
        if (On("methods") && security is not null)
            tasks.Add(Try("their sign-in methods", async () => facts.Methods = (await security.GetAuthMethodsAsync(person.Id, ct)).Where(m => m.Removable).ToArray()));
        if (On("roles") && security is not null)
            tasks.Add(Try("their roles", async () => facts.Roles = (await security.GetRolesAsync(person.Id, ct)).Active));
        if (On("consents") && security is not null)
            tasks.Add(Try("their app consents", async () => facts.Consents = await security.GetAppConsentsAsync(person.Id, ct)));
        if (On("licenses"))
            tasks.Add(Try("their licenses", async () => facts.Licenses = (await _records.GetLicensingAsync(person.Id, ct)).Assignments.Where(a => a.IsDirect).ToArray()));
        if (On("autoreply") && person.Mailbox is not null)
            tasks.Add(Try("their mailbox", async () => facts.Reply = (await _records.GetMailAsync(person.Id, person.Mailbox.PrimaryAddress ?? person.UserPrincipalName, ct)).AutoReply));
        if (On("autoreply") || On("managermail") || On("manageronedrive"))
            tasks.Add(Try("their manager", async () =>
            {
                var details = await _records.GetDetailsAsync(person.Id, ct);
                facts.ManagerName = details.ManagerName;
                facts.Manager = details.ManagerId is { } id ? _index.User(id) : null;
            }));
        if (On("manageronedrive") && _records is IOneDriveRecords oneDrive)
            tasks.Add(Try("their OneDrive", async () => facts.Drive = (await oneDrive.GetOneDriveAsync(person.Id, ct)).Value));
        if (On("retire") && _records is IDeviceRecords devices)
            tasks.Add(Try("their devices", async () => facts.Devices = (await devices.GetDevicesAsync(person.Id, ct)).Value ?? []));
        await Task.WhenAll(tasks);
        return facts;
    }

    /// <summary>Who's in which group, owns which, and has which rights on which shared mailbox: one pass over the groups.</summary>
    Dictionary<string, List<(GroupSummary Group, bool Member, bool Owner, string? Access)>> Memberships()
    {
        var ids = _people.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var map = new Dictionary<string, List<(GroupSummary, bool, bool, string?)>>(StringComparer.Ordinal);
        foreach (var g in _index.AllGroups)
        {
            var found = new Dictionary<string, (bool Member, bool Owner, string? Access)>(StringComparer.Ordinal);
            foreach (var m in g.Members)
                if (ids.Contains(m.Id))
                    found[m.Id] = (true, false, m.Access);
            foreach (var o in g.OwnerList)
                if (ids.Contains(o.Id))
                    found[o.Id] = found.TryGetValue(o.Id, out var f) ? f with { Owner = true } : (false, true, null);
            foreach (var (id, f) in found)
            {
                if (!map.TryGetValue(id, out var list))
                    map[id] = list = [];
                list.Add((g, f.Member, f.Owner, f.Access));
            }
        }
        return map;
    }

    /// <summary>Reads what each person has and lists the changes, person by person, in a safe order.</summary>
    public async Task<WorkflowPlan> PlanAsync(CancellationToken ct = default)
    {
        var facts = await Throttled.ReadAllAsync(_people, ReadsAtOnce, ReadAsync,
            done => Progress = $"Reading what they have: {done:N0} of {_people.Count:N0}…", ct);
        var memberships = On("groups") ? Memberships() : new Dictionary<string, List<(GroupSummary, bool, bool, string?)>>();
        var changes = new List<ChangeOp>();
        var notes = new List<string>();
        // Owners taken off earlier in this plan: two people leaving can be a group's only two owners.
        var ownersLeaving = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var person in _people)
            PlanPerson(person, facts[person.Id], memberships.GetValueOrDefault(person.Id) ?? [], changes, notes, ownersLeaving);
        if (On("forward") && ForwardTo is null)
            notes.Insert(0, "Forwarding is ticked but no one is chosen to forward to, so it's left out.");
        return new WorkflowPlan(changes, notes);
    }

    void PlanPerson(UserSummary person, Facts facts, List<(GroupSummary Group, bool Member, bool Owner, string? Access)> groups, List<ChangeOp> changes,
        List<string> notes, Dictionary<string, int> ownersLeaving)
    {
        var target = GroupAccess.Target(person);
        notes.AddRange(facts.Problems);

        if (On("block") && person.AccountEnabled)
            changes.Add(new SetSignInAllowed(target, true, false));
        if (On("signout"))
            changes.Add(new RevokeSessions(target));
        if (On("password") && person.Kind == UserKind.Guest)
            notes.Add($"{person.DisplayName} is a guest: their password belongs to their own organization, so it isn't reset (blocking sign-in still keeps them out).");
        else if (On("password"))
            changes.Add(new ResetPassword(target, false));
        if (On("methods"))
            changes.AddRange(facts.Methods.Select(m => new RemoveAuthMethod(target, m.Id, m.Segment, m.Kind)));
        if (On("roles"))
            changes.AddRange(facts.Roles.Select(r => new RemoveRoleAssignment(target, r.AssignmentId, r.RoleName)));
        if (On("consents"))
            changes.AddRange(facts.Consents.Select(c => new RevokeAppConsent(target, c.GrantId, c.AppName)));

        if (On("groups"))
        {
            foreach (var (g, member, owner, access) in groups.OrderBy(x => x.Group.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                if (g.IsMailbox)
                {
                    foreach (var right in GroupAccess.RightsOf(access))
                        changes.Add(new SetMailboxAccess(GroupAccess.Target(g), target, right, true, false));
                    continue;
                }
                if (member && g.IsDynamic)
                    notes.Add($"{person.DisplayName}: stays in {g.DisplayName}, which follows a rule (change their details to take them out).");
                else if (member && g.SyncedFromOnPremises)
                    notes.Add($"{person.DisplayName}: stays in {g.DisplayName}, which is synced from on-premises Active Directory (take them out there).");
                else if (member)
                    changes.Add(new SetGroupMembership(GroupAccess.Target(g), target, "user", GroupRole.Member, g.IsExchangeManaged, true, false));
                if (owner && GroupAccess.LastOwnerReason(g, _pipeline, ownersLeaving.GetValueOrDefault(g.Id)) is not null)
                    notes.Add($"{person.DisplayName}: stays an owner of {g.DisplayName}, its last owner (add another owner first).");
                else if (owner)
                {
                    changes.Add(new SetGroupMembership(GroupAccess.Target(g), target, "user", GroupRole.Owner, g.IsExchangeManaged, true, false));
                    ownersLeaving[g.Id] = ownersLeaving.GetValueOrDefault(g.Id) + 1;
                }
            }
        }

        var mailbox = person.Mailbox;
        var isUserMailbox = mailbox is not null && mailbox.Type is not ("Shared" or "Room" or "Equipment");
        if (On("autoreply") && mailbox is not null && facts.Reply is { } old)
        {
            var text = ReplyFor(person, facts.ManagerName);
            var wanted = new AutoReply(AutoReplyState.On, null, null, text, text, AutoReplyAudience.All);
            if (old != wanted)
                changes.Add(new SetAutoReply(target, old, wanted));
        }
        if (On("forward") && mailbox is not null && ForwardTo is { } to)
        {
            var address = _index.User(to.Id)?.Mail is { Length: > 0 } mail ? mail : to.Address;
            if (address is not null)
                changes.Add(new SetForwarding(target, mailbox.ForwardingTo, mailbox.KeepsCopyWhenForwarding, address, true, false));
        }
        if (On("hide") && mailbox is not null && !mailbox.HiddenFromAddressList)
            changes.Add(new SetHiddenFromAddressList(target, false, true));
        if (On("managermail") && mailbox is not null)
        {
            if (facts.Manager is { } manager)
                changes.Add(new SetMailboxAccess(target, GroupAccess.Target(manager), MailboxRight.FullAccess, false, true));
            else
                notes.Add($"{person.DisplayName}: no manager set, so no one is given their mailbox.");
        }
        if (On("manageronedrive"))
        {
            if (facts.Manager is not { } manager)
                notes.Add($"{person.DisplayName}: no manager set, so no one is given their OneDrive.");
            else if (facts.Drive is not { } drive)
                notes.Add($"{person.DisplayName}: has no OneDrive to hand over.");
            else
                changes.Add(new SetOneDriveAccess(target, drive.Url, drive.SiteId, GroupAccess.Target(manager), SetOneDriveAccess.LoginFor(manager.UserPrincipalName), false, true));
        }

        string? convertKey = null;
        if (On("convert") && isUserMailbox)
        {
            var convert = new ConvertMailbox(target, false, true);
            convertKey = convert.Key;
            changes.Add(convert);
        }
        // A shared mailbox still needs a license over 50 GB, on hold or with an archive.
        var stillNeeds = mailbox is not null && (convertKey is not null || !isUserMailbox) ? SharedNeedsLicense(mailbox) : null;
        if (On("licenses") && facts.Licenses.Count > 0 && stillNeeds is not null)
            notes.Add($"{person.DisplayName}: keeps their licenses, because a shared mailbox {stillNeeds} still needs one.");
        else if (On("licenses") && facts.Licenses.Count > 0)
        {
            if (isUserMailbox && convertKey is null)
                notes.Add($"{person.DisplayName}: their mailbox isn't converted to shared, so it's deleted 30 days after its license goes.");
            // Only once it's shared: a user mailbox without a license is deleted after 30 days.
            changes.AddRange(facts.Licenses.Select(l => new SetLicenseAssigned(target, l.SkuId, l.Name, true, false) { DependsOn = convertKey }));
        }

        if (On("retire"))
            changes.AddRange(facts.Devices.Where(d => d.ManagedId is not null)
                .Select(d => new RunDeviceAction(new ChangeTarget("manageddevice", d.ManagedId!, d.Name, null), DeviceCommand.Retire)));
    }

    /// <summary>Why a shared mailbox still needs a license (Microsoft's rules), or null when it doesn't.</summary>
    static string? SharedNeedsLicense(MailboxInfo mailbox) =>
        mailbox.UsedBytes > 50L * 1024 * 1024 * 1024 ? "over 50 GB"
        : mailbox.LitigationHold ? "on litigation hold"
        : mailbox.ArchiveEnabled ? "with an archive"
        : null;

    // ---- Staging ------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    public partial bool IsPlanning { get; private set; }

    [ObservableProperty]
    public partial string? Progress { get; private set; }

    /// <summary>After staging: what went to Pending changes, and the notes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    public partial string? Result { get; private set; }

    public bool IsDone => Result is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial IReadOnlyList<string> Notes { get; private set; } = [];

    public bool HasNotes => Notes.Count > 0;

    bool CanStage => !IsPlanning && !IsDone;

    [RelayCommand(CanExecute = nameof(CanStage))]
    async Task Stage()
    {
        IsPlanning = true;
        Progress = "Reading what they have…";
        try
        {
            var plan = await PlanAsync();
            _pipeline.StageAll(plan.Changes);
            Notes = plan.Notes;
            Result = plan.Changes.Count == 0
                ? "Nothing to change: the ticked steps are already done."
                : $"{plan.Changes.Count:N0} changes added to Pending Changes{(_people.Count > 1 ? $" for {_people.Count:N0} people" : "")}. Review them there, then apply.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Progress = null;
            Notes = [$"Couldn't plan the offboarding: {Friendly.Message(e)}"];
            return;
        }
        finally
        {
            IsPlanning = false;
        }
        Progress = null;
        if (Notes.Count == 0)
            _close();
    }

    [RelayCommand]
    void Close() => _close();
}
