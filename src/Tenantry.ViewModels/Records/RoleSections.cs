using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>One assignment of the role as a row: who, where, how long; Remove or Undo.</summary>
public sealed partial class RoleGrantRow
{
    public required string Name { get; init; }
    public string? Detail { get; init; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public required string TypeText { get; init; }
    public required string KindText { get; init; }
    public BadgeKind KindKind => KindText switch { "Eligible" => BadgeKind.Info, "Activated" => BadgeKind.Success, _ => BadgeKind.Neutral };
    public required string ScopeText { get; init; }
    public required string EndsText { get; init; }

    /// <summary>"Adding" or "Removing" while a change waits in Pending changes.</summary>
    public string? StateText { get; init; }
    public bool HasState => StateText is not null;

    public string? ActionText { get; init; }
    public string? ActionTip { get; init; }
    internal Action? Act { get; init; }
    public bool HasAction => ActionText is not null && Act is not null;

    /// <summary>Why it can't be removed here ("The only Global Administrator").</summary>
    public string? Locked { get; init; }
    public bool IsLocked => Locked is not null;

    internal Action? OpenAction { get; init; }
    public bool CanOpen => OpenAction is not null;

    /// <summary>Moves a time-limited assignment's end a year on (PIM); null when it can't be extended.</summary>
    internal Action? ExtendAction { get; init; }
    public bool CanExtend => ExtendAction is not null;

    [RelayCommand]
    void Extend() => ExtendAction?.Invoke();

    [RelayCommand]
    void Do() => Act?.Invoke();

    [RelayCommand]
    void Open() => OpenAction?.Invoke();
}

/// <summary>Where a role applies: the whole organization or one administrative unit.</summary>
public sealed record RoleScopeOption(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Who holds the role, and a form to give it to someone: for the organization or one administrative
/// unit; with PIM, eligible or active, permanently or until a date, with a reason.
/// </summary>
public sealed partial class RoleAssignmentsSection : RecordSection
{
    readonly RoleDetailViewModel _page;

    public RoleAssignmentsSection(RoleDetailViewModel page)
    {
        _page = page;
        Picker = new PersonPicker("Give the role to: type a name", Candidates, o => Picked = _page.Index.User(o.Id));
        Rebuild();
    }

    public override string Key => "assignments";
    public override string Title => "Assignments";
    public override string Icon => "PeopleCheckmark";

    public bool CanEdit => _page.CanEdit;
    public string EditReason => _page.EditReason;

    public ObservableCollection<RoleGrantRow> Rows { get; } = [];

    public bool HasNoRows => Rows.Count == 0;

    // ---- Giving the role ------------------------------------------------------------------------

    public PersonPicker Picker { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicked), nameof(PickedName))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial UserSummary? Picked { get; set; }

    public bool HasPicked => Picked is not null;
    public string PickedName => Picked?.DisplayName ?? "";

    [RelayCommand]
    void ClearPicked() => Picked = null;

    public IReadOnlyList<RoleScopeOption> Scopes =>
        [new("/", "The Whole Organization"), .. _page.Directory.Units.Select(u => new RoleScopeOption($"/administrativeUnits/{u.Id}", u.Name))];

    public bool HasUnits => _page.Directory.Units.Count > 0;

    [ObservableProperty]
    public partial RoleScopeOption? Scope { get; set; }

    /// <summary>Why only active, permanent assignments can be made (no Entra ID P2), or null.</summary>
    public string? PimNote => _page.Directory.PimUnavailable is null ? null : "Without Entra ID P2 a role is given active and permanently.";
    public bool HasPimNote => PimNote is not null;
    public bool HasPim => _page.Directory.PimUnavailable is null;

    [ObservableProperty]
    public partial bool Eligible { get; set; }

    [ObservableProperty]
    public partial DateTime? Until { get; set; }

    [ObservableProperty]
    public partial string Justification { get; set; } = "";

    IEnumerable<PickOption> Candidates()
    {
        var holding = _page.Role.Grants.Select(g => g.PrincipalId)
            .Concat(_page.Pending.OfType<AddRoleAssignment>().Select(a => a.Principal.Id)).ToHashSet(StringComparer.Ordinal);
        return GroupAccess.People(_page.Index).Where(o => !holding.Contains(o.Id));
    }

    bool CanAdd => CanEdit && Picked is not null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    void Add()
    {
        if (Picked is not { } person)
            return;
        var scope = Scope ?? Scopes[0];
        _page.Pipeline!.Stage(new AddRoleAssignment(GroupAccess.Target(person), RolePrincipalType.User, _page.Role.Role.Id, _page.Role.Role.Name,
            _page.Role.Role.IsPrivileged, scope.Id, scope.Name, HasPim && Eligible ? RoleGrantKind.Eligible : RoleGrantKind.Active,
            HasPim && Until is { } day ? new DateTimeOffset(day.Date.AddDays(1).AddSeconds(-1)) : null, HasPim && Justification.Trim().Length > 0 ? Justification.Trim() : null));
        Picked = null;
        Until = null;
        Justification = "";
    }

    // ---- The rows -------------------------------------------------------------------------------

    /// <summary>
    /// Why taking this role away needs the person's name typed: it's your own privileged role, or
    /// it would leave no active Global Administrator. Null otherwise.
    /// </summary>
    string? RemovalReason(RoleGrant grant, bool privileged, bool lastGlobal)
    {
        if (grant.PrincipalType != RolePrincipalType.User || grant.Kind != RoleGrantKind.Active)
            return null;
        if (lastGlobal)
            return "Nobody else would hold Global Administrator directly, and without one some settings can't be changed by anyone.";
        var address = _page.Index.User(grant.PrincipalId)?.UserPrincipalName ?? grant.PrincipalDetail;
        return privileged && _page.Me is { Length: > 0 } me && string.Equals(address, me, StringComparison.OrdinalIgnoreCase)
            ? "This is your own privileged role: you may not be able to give it back."
            : null;
    }

    internal void Rebuild()
    {
        Rows.Clear();
        var role = _page.Role;
        var pending = _page.Pending.ToArray();
        var isGlobal = role.Role.Id == DirectoryRoles.GlobalAdministrator;
        var activeUsers = role.Grants.Count(g => g.Kind == RoleGrantKind.Active && g.PrincipalType == RolePrincipalType.User);
        var lastGlobal = isGlobal && activeUsers <= 1;
        // Removals already waiting count too: taking two of two Global Administrators away is as bad as one of one.
        var lastAfterRemovals = isGlobal && activeUsers - pending.OfType<RemoveRoleAssignment>().Count(r => r.Kind == RoleGrantKind.Active && r.User.Kind == "user") <= 1;
        foreach (var g in role.Grants)
        {
            var removing = pending.OfType<RemoveRoleAssignment>().FirstOrDefault(r => r.AssignmentId == g.Id);
            var extending = pending.OfType<ExtendRoleAssignment>().FirstOrDefault(x => x.Principal.Id == g.PrincipalId && x.Kind == g.Kind && x.ScopeId == g.ScopeId);
            ChangeOp? waiting = (ChangeOp?)removing ?? extending;
            var locked = g.Activated ? $"Ends on its own{(g.End is { } end ? $" {end.ToLocalTime():d MMM h:mm tt}" : "")}"
                : lastGlobal && g.PrincipalType == RolePrincipalType.User ? "The only Global Administrator"
                : null;
            var target = g.PrincipalType switch
            {
                RolePrincipalType.Group => new ChangeTarget("group", g.PrincipalId, g.PrincipalName, g.PrincipalDetail),
                RolePrincipalType.App => new ChangeTarget("app", g.PrincipalId, g.PrincipalName, null),
                _ => new ChangeTarget("user", g.PrincipalId, g.PrincipalName, g.PrincipalDetail),
            };
            Rows.Add(new RoleGrantRow
            {
                Name = g.PrincipalName,
                Detail = g.PrincipalDetail,
                TypeText = RoleGrant.TypeName(g.PrincipalType),
                KindText = g.Activated ? "Activated" : g.Kind == RoleGrantKind.Eligible ? "Eligible" : "Active",
                ScopeText = g.ScopeId == "/" ? "Whole organization" : g.ScopeName,
                EndsText = (extending?.NewEnd ?? g.End) is { } until ? $"Until {until.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}" : "Permanent",
                StateText = removing is not null ? "Removing" : extending is not null ? "Extending" : null,
                ActionText = !CanEdit || (locked is not null && waiting is null) ? null : waiting is null ? "Remove" : "Undo",
                ActionTip = waiting is null ? "Take the role away (added to Pending Changes)" : "Take the change back out of Pending Changes",
                Act = waiting is not null ? () => _page.Pipeline!.Unstage(waiting)
                    : () => _page.Pipeline!.Stage(new RemoveRoleAssignment(target, g.Id, role.Role.Name, role.Role.Id, g.ScopeId, g.Kind, g.ViaPim)
                    {
                        TypedReason = RemovalReason(g, role.Role.IsPrivileged, lastAfterRemovals),
                    }),
                Locked = CanEdit ? locked : null,
                ExtendAction = CanEdit && g.ViaPim && g.End is { } ends && !g.Activated && waiting is null
                    ? () => _page.Pipeline!.Stage(new ExtendRoleAssignment(target, role.Role.Id, role.Role.Name, g.ScopeId, g.Kind, ends,
                        (ends > DateTimeOffset.UtcNow ? ends : DateTimeOffset.UtcNow).AddYears(1)))
                    : null,
                OpenAction = g.PrincipalType switch
                {
                    RolePrincipalType.User when _page.Index.User(g.PrincipalId) is { } u && _page.OpenUserAction is { } openUser => () => openUser(u),
                    RolePrincipalType.Group when _page.Index.Group(g.PrincipalId) is { } grp && _page.OpenGroupAction is { } openGroup => () => openGroup(grp),
                    _ => null,
                },
            });
        }
        foreach (var add in pending.OfType<AddRoleAssignment>())
            Rows.Add(new RoleGrantRow
            {
                Name = add.Principal.Name,
                Detail = add.Principal.Address,
                TypeText = RoleGrant.TypeName(add.PrincipalType),
                KindText = add.Kind == RoleGrantKind.Eligible ? "Eligible" : "Active",
                ScopeText = add.ScopeId == "/" ? "Whole organization" : add.ScopeName,
                EndsText = add.End is { } until ? $"Until {until.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}" : "Permanent",
                StateText = "Adding",
                ActionText = "Undo",
                ActionTip = "Don't give it (takes the change out of Pending Changes)",
                Act = () => _page.Pipeline!.Unstage(add),
            });
        Summary = role.Grants.Count switch { 0 => "Nobody holds it", 1 => "1 assignment", var n => $"{n:N0} assignments" };
        OnPropertyChanged(nameof(HasNoRows));
        OnPropertyChanged(nameof(Scopes));
        OnPropertyChanged(nameof(HasUnits));
        OnPropertyChanged(nameof(PimNote));
        OnPropertyChanged(nameof(HasPimNote));
        OnPropertyChanged(nameof(HasPim));
    }
}

/// <summary>A role's permissions about one kind of thing ("Users": create, delete, password › update).</summary>
public sealed record PermissionGroup(string Title, IReadOnlyList<string> Items)
{
    public string CountText => Items.Count == 1 ? "1" : $"{Items.Count:N0}";
}

/// <summary>What the role lets holders do, grouped by what it acts on; a custom role can be edited or deleted here.</summary>
public sealed class RolePermissionsSection : RecordSection
{
    readonly RoleDetailViewModel _page;

    public RolePermissionsSection(RoleDetailViewModel page)
    {
        _page = page;
        Rebuild();
    }

    public override string Key => "permissions";
    public override string Title => "Permissions";
    public override string Icon => "ShieldKeyhole";

    public ObservableCollection<PermissionGroup> Groups { get; } = [];

    public bool HasNone => Groups.Count == 0;

    public RoleDetailViewModel Page => _page;

    /// <summary>
    /// "microsoft.directory/conditionalAccessPolicies/allProperties/read" →
    /// ("Conditional Access Policies", "all properties › read"): Microsoft's run-together words split.
    /// </summary>
    public static (string Group, string Item) Split(string action)
    {
        var parts = action.Split('/');
        if (parts.Length < 2)
            return ("Other", action);
        static IEnumerable<string> Words(string s) => System.Text.RegularExpressions.Regex.Replace(s.Replace('.', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        static string Title(string s) => string.Join(' ', Words(s).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        static string Lower(string s) => string.Join(' ', Words(s).Select(w => w.ToLowerInvariant()));
        var service = parts[0] == "microsoft.directory" ? "" : Title(parts[0].Replace("microsoft.", "", StringComparison.Ordinal)) + " › ";
        return (service + Title(parts[1]), parts.Length > 2 ? string.Join(" › ", parts[2..].Select(Lower)) : "everything");
    }

    internal void Rebuild()
    {
        Groups.Clear();
        foreach (var g in _page.Role.Role.AllowedActions.Select(Split).GroupBy(x => x.Group).OrderBy(g => g.Key, StringComparer.CurrentCulture))
            Groups.Add(new PermissionGroup(g.Key, [.. g.Select(x => x.Item).Distinct().Order(StringComparer.Ordinal)]));
        Summary = _page.Role.Role.AllowedActions.Count == 1 ? "1 permission" : $"{_page.Role.Role.AllowedActions.Count:N0} permissions";
        OnPropertyChanged(nameof(HasNone));
    }
}

/// <summary>A duration offered in a settings choice ("8 hours", "1 year").</summary>
public sealed record DurationChoice(TimeSpan? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The role's PIM settings, as the Entra admin center's Role settings page has them: how long an
/// activation lasts and what it needs, who approves, and how long assignments last. Each change goes
/// to Pending changes as it's made (one change per rule, so editing a rule again replaces it).
/// </summary>
public sealed partial class RoleSettingsSection : LiveSection
{
    readonly RoleDetailViewModel _page;
    RoleSettings? _original;
    bool _filling;

    public RoleSettingsSection(RoleDetailViewModel page)
    {
        _page = page;
        ApproverPicker = new PersonPicker("Add an approver: type a name",
            () => GroupAccess.People(_page.Index).Where(o => Approvers.All(a => a.Id != o.Id)),
            o =>
            {
                Approvers.Add(new RoleApprover(o.Id, o.Name));
                Stage(RolePolicyRules.Approval);
            });
    }

    public override string Key => "settings";
    public override string Title => "Settings";
    public override string Icon => "Settings";
    public override string LoadingText => "Reading the role's PIM settings…";

    public bool CanEdit => _page.CanEdit && _original is not null;
    public bool IsLoaded => _original is not null;

    public static IReadOnlyList<DurationChoice> ActivationChoices { get; } =
        [.. new[] { 1, 2, 4, 6, 8, 10, 12, 24 }.Select(h => new DurationChoice(TimeSpan.FromHours(h), RolePolicyRules.Words(TimeSpan.FromHours(h))))];

    public static IReadOnlyList<DurationChoice> AssignmentChoices { get; } =
    [
        new(null, "Permanent Allowed"),
        .. new[] { 15, 30, 90, 180, 365 }.Select(d => new DurationChoice(TimeSpan.FromDays(d), $"Up to {RolePolicyRules.Words(TimeSpan.FromDays(d))}")),
    ];

    [ObservableProperty] public partial DurationChoice? MaxActivation { get; set; }
    [ObservableProperty] public partial bool ActivationMfa { get; set; }
    [ObservableProperty] public partial bool ActivationJustification { get; set; }
    [ObservableProperty] public partial bool ActivationTicket { get; set; }
    [ObservableProperty] public partial bool ApprovalRequired { get; set; }
    [ObservableProperty] public partial DurationChoice? EligibleMax { get; set; }
    [ObservableProperty] public partial DurationChoice? ActiveMax { get; set; }
    [ObservableProperty] public partial bool ActiveMfa { get; set; }
    [ObservableProperty] public partial bool ActiveJustification { get; set; }

    public ObservableCollection<RoleApprover> Approvers { get; } = [];

    public PersonPicker ApproverPicker { get; }

    [RelayCommand]
    void RemoveApprover(RoleApprover approver)
    {
        Approvers.Remove(approver);
        Stage(RolePolicyRules.Approval);
    }

    partial void OnMaxActivationChanged(DurationChoice? value) => Stage(RolePolicyRules.ActivationExpiry);
    partial void OnActivationMfaChanged(bool value) => Stage(RolePolicyRules.ActivationRequirements);
    partial void OnActivationJustificationChanged(bool value) => Stage(RolePolicyRules.ActivationRequirements);
    partial void OnActivationTicketChanged(bool value) => Stage(RolePolicyRules.ActivationRequirements);
    partial void OnApprovalRequiredChanged(bool value) => Stage(RolePolicyRules.Approval);
    partial void OnEligibleMaxChanged(DurationChoice? value) => Stage(RolePolicyRules.EligibleExpiry);
    partial void OnActiveMaxChanged(DurationChoice? value) => Stage(RolePolicyRules.ActiveExpiry);
    partial void OnActiveMfaChanged(bool value) => Stage(RolePolicyRules.ActiveRequirements);
    partial void OnActiveJustificationChanged(bool value) => Stage(RolePolicyRules.ActiveRequirements);

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        if (_page.Records is not { } records)
        {
            Note = "Role settings aren't available for this tenant.";
            return;
        }
        var read = await records.GetRoleSettingsAsync(_page.Role.Role.Id, cancellationToken);
        Note = read.Unavailable;
        _original = read.Value;
        if (_original is { } s)
            Fill(s);
        Summary = _original is { } o ? $"Activations up to {RolePolicyRules.Words(o.MaxActivation)}" : "";
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsLoaded));
    }

    void Fill(RoleSettings s)
    {
        _filling = true;
        MaxActivation = ActivationChoices.FirstOrDefault(c => c.Value == s.MaxActivation) ?? new DurationChoice(s.MaxActivation, RolePolicyRules.Words(s.MaxActivation));
        ActivationMfa = s.ActivationMfa;
        ActivationJustification = s.ActivationJustification;
        ActivationTicket = s.ActivationTicket;
        ApprovalRequired = s.ApprovalRequired;
        Approvers.Clear();
        foreach (var a in s.Approvers)
            Approvers.Add(a);
        EligibleMax = Choice(s.EligibleMax);
        ActiveMax = Choice(s.ActiveMax);
        ActiveMfa = s.ActiveMfa;
        ActiveJustification = s.ActiveJustification;
        _filling = false;
    }

    static DurationChoice Choice(TimeSpan? max) =>
        AssignmentChoices.FirstOrDefault(c => c.Value == max) ?? new DurationChoice(max, max is { } m ? $"Up to {RolePolicyRules.Words(m)}" : "Permanent Allowed");

    static string Requirements(bool mfa, bool justification, bool ticket)
    {
        var parts = new[] { mfa ? "MFA" : null, justification ? "a reason" : null, ticket ? "a ticket number" : null }.OfType<string>().ToArray();
        return parts.Length == 0 ? "Nothing" : string.Join(", ", parts);
    }

    static string Approval(bool required, IEnumerable<RoleApprover> approvers) =>
        !required ? "Not needed" : approvers.Any() ? "Needed from " + string.Join(", ", approvers.Select(a => a.Name)) : "Needed (from the Privileged Role Administrators)";

    static string Expiry(TimeSpan? max) => max is { } m ? $"Up to {RolePolicyRules.Words(m)}" : "Permanent allowed";

    /// <summary>Stages the rule as it now stands; staging it back to how it was drops the change.</summary>
    void Stage(string ruleId)
    {
        if (_filling || _original is not { } o || !_page.CanEdit || !o.Rules.TryGetValue(ruleId, out var rule))
            return;
        var (label, old, @new, json, loosens) = ruleId switch
        {
            RolePolicyRules.ActivationExpiry => ("Activation Length", RolePolicyRules.Words(o.MaxActivation), RolePolicyRules.Words(MaxActivation?.Value ?? o.MaxActivation),
                RolePolicyRules.WithExpiration(rule, MaxActivation?.Value ?? o.MaxActivation), (MaxActivation?.Value ?? o.MaxActivation) > o.MaxActivation),
            RolePolicyRules.ActivationRequirements => ("Activation Needs", Requirements(o.ActivationMfa, o.ActivationJustification, o.ActivationTicket),
                Requirements(ActivationMfa, ActivationJustification, ActivationTicket),
                RolePolicyRules.WithRequirements(rule, Needed(ActivationMfa, ActivationJustification, ActivationTicket)),
                (o.ActivationMfa && !ActivationMfa) || (o.ActivationJustification && !ActivationJustification) || (o.ActivationTicket && !ActivationTicket)),
            RolePolicyRules.Approval => ("Approval", Approval(o.ApprovalRequired, o.Approvers), Approval(ApprovalRequired, Approvers),
                RolePolicyRules.WithApproval(rule, ApprovalRequired, [.. Approvers]), o.ApprovalRequired && !ApprovalRequired),
            RolePolicyRules.EligibleExpiry => ("Eligible Assignments", Expiry(o.EligibleMax), Expiry(EligibleMax?.Value),
                RolePolicyRules.WithExpiration(rule, EligibleMax?.Value), Looser(o.EligibleMax, EligibleMax?.Value)),
            RolePolicyRules.ActiveExpiry => ("Active Assignments", Expiry(o.ActiveMax), Expiry(ActiveMax?.Value),
                RolePolicyRules.WithExpiration(rule, ActiveMax?.Value), Looser(o.ActiveMax, ActiveMax?.Value)),
            _ => ("Active Assignments Need", Requirements(o.ActiveMfa, o.ActiveJustification, false), Requirements(ActiveMfa, ActiveJustification, false),
                RolePolicyRules.WithRequirements(rule, Needed(ActiveMfa, ActiveJustification, false)),
                (o.ActiveMfa && !ActiveMfa) || (o.ActiveJustification && !ActiveJustification)),
        };
        _page.Pipeline!.Stage(new SetRolePolicyRule(new ChangeTarget("role", _page.Role.Role.Id, _page.Role.Role.Name, null), o.PolicyId, ruleId, label, old, @new, json,
            loosens));
    }

    static IEnumerable<string> Needed(bool mfa, bool justification, bool ticket) =>
        new[] { mfa ? RolePolicyRules.Mfa : null, justification ? RolePolicyRules.Justification : null, ticket ? RolePolicyRules.Ticket : null }.OfType<string>();

    /// <summary>Allowing longer (or permanent) assignments than before loosens the rule.</summary>
    static bool Looser(TimeSpan? before, TimeSpan? after) => before is { } b && (after is null || after > b);
}
