using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// A record page's tab that reads live: once, the first time it's shown, with a wheel while it
/// does. A read that isn't possible here (no access, no role) leaves a note instead.
/// </summary>
public abstract partial class LiveSection : RecordSection
{
    bool _loaded;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why something here can't be shown, or a read that failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; protected set; }

    public bool HasNote => Note is not null;

    /// <summary>A button beside the note that fixes what it says ("Add Yourself as Site Admin"), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoteAction))]
    public partial string? NoteActionText { get; protected set; }

    public bool HasNoteAction => NoteActionText is not null;

    [RelayCommand]
    void NoteAction() => OnNoteAction();

    protected virtual void OnNoteAction()
    {
    }

    /// <summary>The fix to offer for a read that wasn't allowed, or null.</summary>
    protected virtual string? NoteActionFor(UnauthorizedAccessException e) => null;

    /// <summary>Reads again, if it has been shown (after changes to what it shows were applied).</summary>
    public void ReadAgainIfShown()
    {
        if (_loaded || Note is not null || IsLoading)
            Reread();
    }

    /// <summary>What the wheel says while this tab reads.</summary>
    public virtual string LoadingText => $"Reading {Title.ToLowerInvariant()} from Microsoft 365…";

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded || IsLoading)
            return;
        IsLoading = true;
        Note = null;
        NoteActionText = null;
        try
        {
            await ReadAsync(cancellationToken);
            _loaded = true;
        }
        catch (UnauthorizedAccessException e)
        {
            // Already in plain words (no access to a site's contents, an archived site).
            Note = e.Message;
            NoteActionText = NoteActionFor(e);
            Forget();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note = $"Couldn't read this: {Friendly.Message(e)}";
            Forget();
        }
        finally
        {
            IsLoading = false;
            Loaded();
        }
        // Changes applied while it read: what it just read may be from before them.
        if (_again)
        {
            _again = false;
            Reread();
        }
    }

    bool _again;

    /// <summary>After each read, for "nothing here" lines that depend on IsLoading.</summary>
    protected virtual void Loaded()
    {
    }

    /// <summary>
    /// A read failed: drop what an earlier read showed, so the note isn't read as being about
    /// rows that may no longer be true.
    /// </summary>
    protected virtual void Forget()
    {
    }

    /// <summary>Reads again next time it's shown (after changes to it were applied).</summary>
    protected void Reread()
    {
        _loaded = false;
        if (IsLoading)
        {
            _again = true;
            return;
        }
        _ = LoadAsync();
    }

    protected abstract Task ReadAsync(CancellationToken cancellationToken);
}

/// <summary>A tab of the device page.</summary>
public abstract partial class DeviceSection(DeviceDetailViewModel page) : LiveSection
{
    protected DeviceDetailViewModel Page => page;

    protected DeviceSummary Device => page.Device;

    /// <summary>Intune's tabs on a device Intune doesn't manage.</summary>
    protected const string NotManaged = "Intune doesn't manage this device, so it reports none of this. Enroll it in Intune to see it here.";
}

// ---- Overview ---------------------------------------------------------------------------------

/// <summary>What the device is, its hardware and health at a glance, and its actions.</summary>
public sealed partial class DeviceOverviewSection(DeviceDetailViewModel page, BulkEditorViewModel? actions) : DeviceSection(page)
{
    public override string Key => "overview";
    public override string Title => "Overview";
    public override string Icon => "Home";
    public override string LoadingText => "Reading its hardware from Intune…";

    /// <summary>Sync, restart, disable, retire, wipe, delete; null when the admin can't edit.</summary>
    public BulkEditorViewModel? Actions => actions;

    public bool CanEdit => Page.CanEdit;
    public string EditReason => Page.EditReason;
    public bool IsManaged => Device.IsManaged;

    DeviceHardware? _hardware;

    /// <summary>What it is and when it was last seen: from the local copy, shown at once.</summary>
    public IReadOnlyList<SummaryLine> Facts => Lines(
        ("Last Seen", Device.LastSeen is { } seen ? Display.Exact(seen) : "Never"),
        ("Intune Check-In", Device.LastCheckIn is { } checkIn ? Display.Exact(checkIn) : null),
        ("Join Type", Device.JoinType),
        ("Ownership", Device.Ownership),
        ("Operating System", string.Join(' ', new[] { Device.Os, Device.OsVersion }.Where(s => !string.IsNullOrEmpty(s)))),
        ("Model", string.Join(' ', new[] { Device.Manufacturer, Device.Model }.Where(s => !string.IsNullOrEmpty(s)).Distinct())),
        ("Serial Number", Device.Serial ?? _hardware?.Serial),
        (Device.IsManaged ? "Enrolled" : "Registered", Device.Registered is { } at ? Display.Exact(at) : null));

    /// <summary>Hardware and health as Intune last heard it; empty until read (or for a device Intune doesn't manage).</summary>
    public IReadOnlyList<SummaryLine> Hardware => _hardware is not { } h ? [] : Lines(
        ("Memory", h.MemoryBytes is { } memory ? DeviceText.Size(memory) : null),
        ("Storage", h.StorageTotal is { } total ? $"{DeviceText.Size(h.StorageFree ?? 0)} free of {DeviceText.Size(total)}" : null),
        ("Architecture", h.Processor),
        ("Battery Health", h.BatteryHealthPercent is { } battery ? $"{battery}%" + (h.BatteryCycles is { } cycles ? $" after {cycles:N0} charge cycles" : "") : null),
        ("OS Edition", h.OsEdition),
        ("OS Build", h.OsBuild),
        ("Language", h.OsLanguage),
        ("TPM", h.Tpm),
        ("BIOS", h.Bios),
        ("IP Address", h.IpAddress),
        ("Wi-Fi MAC", h.WifiMac),
        ("Ethernet MAC", h.EthernetMac),
        ("IMEI", h.Imei),
        ("MEID", h.Meid),
        ("Phone Number", h.PhoneNumber),
        ("Carrier", h.Carrier),
        ("Supervised", h.Supervised == true ? "Yes" : null),
        ("Shared Device", h.SharedDevice == true ? "Yes" : null),
        ("Enrollment", string.Join(" · ", new[] { h.EnrollmentType is { } type ? DeviceText.Enrollment(type) : null, h.EnrollmentProfile, h.Autopilot == true ? "Autopilot" : null }
            .Where(s => !string.IsNullOrEmpty(s)))),
        ("Management Certificate", h.CertificateExpires is { } expires ? $"Expires {Display.Exact(expires)}" : null));

    public bool HasHardware => Hardware.Count > 0;

    /// <summary>One-glance warnings: not compliant, jailbroken, a threat, a grace period ending.</summary>
    public IReadOnlyList<HealthChip> Health
    {
        get
        {
            var chips = new List<HealthChip>();
            if (Device.Compliant == false)
                chips.Add(new("Not Compliant", BadgeKind.Danger, "It fails a compliance policy: see the Compliance tab"));
            if (_hardware?.GraceExpires is { } grace)
                chips.Add(new($"Grace Period Ends {grace.ToLocalTime():d MMM}", BadgeKind.Warning, "When its grace period ends it loses access to company resources"));
            if (_hardware?.Jailbroken == true)
                chips.Add(new("Jailbroken", BadgeKind.Danger, "Intune detected the device's protections have been removed"));
            if (_hardware?.ThreatState is { } threat && threat is not ("secured" or "activated"))
                chips.Add(new($"Threat: {DeviceText.Words(threat)}", BadgeKind.Danger, "What Microsoft Defender reports for this device"));
            if (_hardware?.ActiveMalware is > 0)
                chips.Add(new($"{_hardware.ActiveMalware} Active Malware", BadgeKind.Danger, "See the Defender tab"));
            if (Device.Encrypted == false)
                chips.Add(new("Not Encrypted", BadgeKind.Warning, "Intune reports the drive isn't encrypted and no BitLocker key is backed up"));
            if (Device.StorageTotal is { } total && Device.StorageFree is { } free && free < total / 10)
                chips.Add(new("Storage Nearly Full", BadgeKind.Warning, $"{DeviceText.Size(free)} free of {DeviceText.Size(total)}"));
            // A device never used counts once it was registered that long ago (a new one hasn't signed in yet).
            var cutoff = DateTimeOffset.UtcNow.AddDays(-90);
            if (Device.LastSeen is { } seen ? seen < cutoff : Device.Registered is not { } registered || registered < cutoff)
                chips.Add(new("Not Seen in 90+ Days", BadgeKind.Warning, "Nothing has signed in on it or checked in for three months"));
            return chips;
        }
    }

    public bool HasHealth => Health.Count > 0;

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        if (!Device.IsManaged)
        {
            // Nothing to read, but the summary still says what the warnings are.
            Refresh();
            return;
        }
        var live = await Page.LiveAsync();
        _hardware = live.Hardware;
        Note = live.IntuneProblem;
        Refresh();
    }

    protected override void Forget()
    {
        _hardware = null;
        Refresh();
    }

    public void Refresh()
    {
        foreach (var name in new[] { nameof(Facts), nameof(Hardware), nameof(HasHardware), nameof(Health), nameof(HasHealth) })
            OnPropertyChanged(name);
        Summary = Health.Count == 0 ? "No warnings" : string.Join(", ", Health.Select(h => h.Text));
    }

    static IReadOnlyList<SummaryLine> Lines(params (string Label, string? Value)[] lines) =>
        lines.Where(l => !string.IsNullOrWhiteSpace(l.Value)).Select(l => new SummaryLine(l.Label, l.Value!)).ToArray();
}

// ---- Users ------------------------------------------------------------------------------------

/// <summary>A person on the device page: everything they are to the device, opening their record when they're in the local copy.</summary>
public sealed partial class DevicePersonRow(string name, string? detail, IReadOnlyList<string> roles, UserSummary? user, Action<UserSummary>? open)
{
    public string Name => name;
    public string? Detail => detail;
    public bool HasDetail => !string.IsNullOrEmpty(detail);
    public IReadOnlyList<string> Roles => roles;
    public bool CanOpen => user is not null && open is not null;

    [RelayCommand]
    void Open()
    {
        if (user is not null)
            open?.Invoke(user);
    }
}

/// <summary>Who uses it: Intune's primary user, the people who signed in to it (Windows), and its Entra owner and users.</summary>
public sealed partial class DeviceUsersSection(DeviceDetailViewModel page, IDirectoryIndex index, Action<UserSummary>? openUser) : DeviceSection(page)
{
    public override string Key => "users";
    public override string Title => "Users";
    public override string Icon => "People";

    public ObservableCollection<DevicePersonRow> People { get; } = [];

    public bool HasNoPeople => !IsLoading && People.Count == 0;

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        var live = await Page.LiveAsync();
        // One row per person, in order of how they're tied to it, with every role they have.
        var people = new Dictionary<string, (string? Name, string? Upn, List<string> Roles, DateTimeOffset? SignedIn)>(StringComparer.Ordinal);
        var order = new List<string>();
        void Add(string id, string? name, string? upn, string? role, DateTimeOffset? signedIn = null)
        {
            if (!people.TryGetValue(id, out var p))
            {
                people[id] = p = (name, upn, [], null);
                order.Add(id);
            }
            if (role is not null && !p.Roles.Contains(role))
                p.Roles.Add(role);
            people[id] = (p.Name ?? name, p.Upn ?? upn, p.Roles, signedIn ?? p.SignedIn);
        }
        if (Device.UserId is { } primary)
            Add(primary, Device.UserName, Device.UserPrincipalName, "Primary User");
        foreach (var on in live.LoggedOn)
            Add(on.UserId, null, null, "Signed In", on.LastLogOn);
        foreach (var owner in live.Owners)
            Add(owner.Id, owner.Name, owner.UserPrincipalName, "Registered Owner");
        foreach (var user in live.Users)
            Add(user.Id, user.Name, user.UserPrincipalName, live.Owners.Any(o => o.Id == user.Id) ? null : "Registered User");
        People.Clear();
        foreach (var id in order)
        {
            var (name, upn, roles, signedIn) = people[id];
            var user = index.User(id);
            var detail = string.Join(" · ", new[] { user?.UserPrincipalName ?? upn, signedIn is { } at ? $"Last signed in to it {Display.Exact(at)}" : null }
                .Where(s => !string.IsNullOrEmpty(s)));
            People.Add(new DevicePersonRow(user?.DisplayName ?? name ?? "Someone no longer in the directory", detail, roles, user, openUser));
        }
        Note = live.IntuneProblem;
        Summary = People.Count switch { 0 => "Nobody", 1 => "1 person", var n => $"{n} people" };
        OnPropertyChanged(nameof(HasNoPeople));
    }

    protected override void Forget() => People.Clear();
}

// ---- Compliance -------------------------------------------------------------------------------

/// <summary>A compliance policy or configuration profile on the device; expands to its settings.</summary>
public sealed partial class DevicePolicyRow : ObservableObject
{
    readonly Func<Task<IReadOnlyList<PolicySetting>>> _settings;

    public DevicePolicyRow(PolicyState policy, Func<Task<IReadOnlyList<PolicySetting>>> settings)
    {
        Policy = policy;
        _settings = settings;
        // A policy with a problem opens to show which setting.
        if (policy.IsProblem && policy.SettingCount > 0)
            IsExpanded = true;
    }

    public PolicyState Policy { get; }
    public string Name => Policy.Name;
    public string StateText => DeviceText.State(Policy.State).Text;
    public BadgeKind StateKind => DeviceText.State(Policy.State).Kind;
    public string SettingsText => Policy.SettingCount switch { 0 => "No settings reported", 1 => "1 setting", var n => $"{n} settings" };
    public bool CanExpand => Policy.SettingCount > 0;

    public ObservableCollection<SettingRow> Settings { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    void Toggle() => IsExpanded = !IsExpanded;

    bool _read;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_read)
            _ = ReadAsync();
    }

    async Task ReadAsync()
    {
        _read = true;
        IsLoading = true;
        try
        {
            foreach (var s in await _settings())
                Settings.Add(new SettingRow(s));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = $"Couldn't read its settings: {Friendly.Message(e)}";
            _read = false;
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public sealed class SettingRow(PolicySetting setting)
{
    public string Name => DeviceText.SettingName(setting.Name);
    public string StateText => DeviceText.State(setting.State).Text;
    public BadgeKind StateKind => DeviceText.State(setting.State).Kind;

    /// <summary>The device's value and any error; Intune writes "no error" as 0.</summary>
    public string? Detail => string.Join(" · ", new[]
    {
        setting.Current is { } current ? $"Now: {current}" : null,
        setting.Error is { } error && error != "0" ? error : null,
    }.OfType<string>()) is { Length: > 0 } text ? text : null;

    public bool HasDetail => Detail is not null;
}

/// <summary>Compliance policies (and why it fails one), then configuration profiles, each opening to its settings.</summary>
public sealed partial class DeviceComplianceSection(DeviceDetailViewModel page) : DeviceSection(page)
{
    public override string Key => "compliance";
    public override string Title => "Compliance";
    public override string Icon => "ShieldCheckmark";
    public override string LoadingText => "Reading compliance policies and configuration profiles from Intune…";

    public ObservableCollection<DevicePolicyRow> Policies { get; } = [];
    public ObservableCollection<DevicePolicyRow> Profiles { get; } = [];

    public bool HasNoPolicies => !IsLoading && Policies.Count == 0 && Note is null;
    public bool HasNoProfiles => !IsLoading && Profiles.Count == 0 && Note is null;

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        if (Device.ManagedId is not { } managed || Page.Pages is not { } pages)
        {
            Note = NotManaged;
            return;
        }
        var compliance = pages.GetCompliancePoliciesAsync(managed, cancellationToken);
        var configuration = pages.GetConfigurationProfilesAsync(managed, cancellationToken);
        Policies.Clear();
        Profiles.Clear();
        // Problems first, then by name.
        static IEnumerable<PolicyState> Ordered(IEnumerable<PolicyState> policies) =>
            policies.OrderBy(p => p.IsProblem ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var p in Ordered((await compliance).Value ?? []))
            Policies.Add(new DevicePolicyRow(p, () => pages.GetPolicySettingsAsync(managed, p.Id, compliance: true)));
        foreach (var p in Ordered((await configuration).Value ?? []))
            Profiles.Add(new DevicePolicyRow(p, () => pages.GetPolicySettingsAsync(managed, p.Id, compliance: false)));
        Note = (await compliance).Unavailable ?? (await configuration).Unavailable;
        var problems = Policies.Concat(Profiles).Count(p => p.Policy.IsProblem);
        Summary = problems == 0 ? "No problems" : $"{problems} with a problem";
        OnPropertyChanged(nameof(HasNoPolicies));
        OnPropertyChanged(nameof(HasNoProfiles));
    }
}

// ---- Apps -------------------------------------------------------------------------------------

public sealed class AppStateRow(DeviceAppState app)
{
    public string Name => app.Name;
    public string? Detail => string.Join(" · ", new[] { DeviceText.Intent(app.Intent), app.Version is { } v ? $"Version {v}" : null }
        .Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } text ? text : null;
    public bool HasDetail => Detail is not null;
    public string StateText => DeviceText.InstallState(app.State).Text;
    public BadgeKind StateKind => DeviceText.InstallState(app.State).Kind;
}

public sealed class DetectedAppRow(DetectedApp app)
{
    public DetectedApp App => app;
    public string Name => app.Name;
    public string Detail => string.Join(" · ", new[] { app.Version is { } v ? $"Version {v}" : null, app.Publisher, app.SizeBytes is { } s ? DeviceText.Size(s) : null }
        .Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>The apps Intune assigns (and how each installed), then everything the device reports installed, searchable.</summary>
public sealed partial class DeviceAppsSection(DeviceDetailViewModel page) : DeviceSection(page)
{
    public override string Key => "apps";
    public override string Title => "Apps";
    public override string Icon => "Apps";
    public override string LoadingText => "Reading its apps from Intune…";

    public ObservableCollection<AppStateRow> Assigned { get; } = [];

    IReadOnlyList<DetectedAppRow> _installed = [];

    public ObservableCollection<DetectedAppRow> Installed { get; } = [];

    [ObservableProperty]
    public partial string? AssignedNote { get; private set; }

    public bool HasAssignedNote => AssignedNote is not null;

    partial void OnAssignedNoteChanged(string? value) => OnPropertyChanged(nameof(HasAssignedNote));

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    partial void OnSearchChanged(string value) => Filter();

    public string InstalledHeading => _installed.Count == 0 ? "Installed Apps"
        : Installed.Count == _installed.Count ? $"Installed Apps ({_installed.Count:N0})" : $"Installed Apps ({Installed.Count:N0} of {_installed.Count:N0})";

    public bool HasNoInstalled => !IsLoading && _installed.Count == 0 && Note is null;

    void Filter()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Installed.Clear();
        foreach (var row in _installed.Where(r => words.All(w => r.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                     || r.App.Publisher?.Contains(w, StringComparison.CurrentCultureIgnoreCase) == true)))
            Installed.Add(row);
        OnPropertyChanged(nameof(InstalledHeading));
    }

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        if (Device.ManagedId is not { } managed || Page.Pages is not { } pages)
        {
            Note = NotManaged;
            return;
        }
        var assigned = Device.UserId is { } user ? pages.GetAppStatesAsync(user, managed, cancellationToken) : null;
        var detected = pages.GetDetectedAppsAsync(managed, cancellationToken);
        Assigned.Clear();
        if (assigned is null)
            AssignedNote = "It has no primary user, so Intune lists no apps assigned to someone on it.";
        else
        {
            var read = await assigned;
            // Failed required apps first.
            foreach (var a in (read.Value ?? []).OrderBy(a => a.IsProblem ? 0 : 1).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                Assigned.Add(new AppStateRow(a));
            AssignedNote = read.Unavailable ?? (Assigned.Count == 0 ? "No apps are assigned to its primary user." : null);
        }
        var found = await detected;
        _installed = (found.Value ?? []).Select(a => new DetectedAppRow(a)).ToArray();
        Note = found.Unavailable;
        Filter();
        var failed = Assigned.Count(a => a.StateKind == BadgeKind.Danger);
        Summary = failed > 0 ? $"{failed} app{(failed == 1 ? "" : "s")} failed to install" : $"{_installed.Count:N0} installed";
        OnPropertyChanged(nameof(HasNoInstalled));
    }

    protected override void Forget()
    {
        Assigned.Clear();
        AssignedNote = null;
        _installed = [];
        Filter();
    }
}

// ---- Groups -----------------------------------------------------------------------------------

/// <summary>A group the device is in (or is joining): leave it, or undo the pending change.</summary>
public sealed partial class DeviceGroupRow(string name, bool isDynamic, bool isPending, bool joining, bool canChange, string tip, Action toggle, Action? open)
{
    public string Name => name;
    public bool IsDynamic => isDynamic;
    public string? PendingText => !isPending ? null : joining ? "Joining" : "Leaving";
    public bool HasPending => isPending;
    public string ToggleText => isPending ? "Undo" : "Remove";
    public bool CanChange => canChange;
    public string Tip => tip;
    public bool CanOpen => open is not null;

    [RelayCommand]
    void Toggle() => toggle();

    [RelayCommand]
    void Open() => open?.Invoke();
}

/// <summary>The Entra groups the device is a direct member of; security groups can be joined or left here.</summary>
public sealed partial class DeviceGroupsSection : DeviceSection, IDisposable
{
    readonly IDirectoryIndex _index;
    readonly Action<GroupSummary>? _openGroup;
    IReadOnlyList<DeviceGroup> _groups = [];

    public DeviceGroupsSection(DeviceDetailViewModel page, IDirectoryIndex index, Action<GroupSummary>? openGroup) : base(page)
    {
        _index = index;
        _openGroup = openGroup;
        Picker = new PersonPicker("Add to a security group: type its name", Options, Join);
        if (page.Pipeline is { } pipeline)
        {
            pipeline.Changed += OnPendingChanged;
            pipeline.Applied += OnApplied;
        }
    }

    public override string Key => "groups";
    public override string Title => "Groups";
    public override string Icon => "PeopleTeam";

    public bool CanEdit => Page.CanEdit && Device.ObjectId is not null;

    public string EditReason => Device.ObjectId is null ? "Entra doesn't list this device, so it can't be in groups." : Page.EditReason;

    public PersonPicker Picker { get; }

    public ObservableCollection<DeviceGroupRow> Groups { get; } = [];

    public bool HasNoGroups => !IsLoading && Groups.Count == 0 && Note is null;

    ChangeTarget Me => BulkActions.EntraTarget(Device);

    IEnumerable<SetGroupMembership> Pending => Page.Pipeline?.Pending.OfType<SetGroupMembership>().Where(p => p.Person.Id == Device.ObjectId) ?? [];

    /// <summary>Security groups it isn't in: the only kind a device can join (dynamic ones follow their rule).</summary>
    IEnumerable<PickOption> Options()
    {
        var taken = _groups.Select(g => g.Id).Concat(Pending.Select(p => p.Group.Id)).ToHashSet(StringComparer.Ordinal);
        return _index.AllGroups.Where(g => g.Kind == GroupKind.Security && GroupAccess.MembersEditable(g) && !taken.Contains(g.Id)).Select(PickOption.Of);
    }

    void Join(PickOption option)
    {
        if (!CanEdit || _index.Group(option.Id) is not { } g)
            return;
        Page.Pipeline!.Stage(new SetGroupMembership(GroupAccess.Target(g), Me, "device", GroupRole.Member, false, false, true));
    }

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        if (Device.ObjectId is not { } objectId || Page.Pages is not { } pages)
        {
            Note = "Entra doesn't list this device, so it isn't in any groups.";
            return;
        }
        var read = await pages.GetGroupsAsync(objectId, cancellationToken);
        _groups = read.Value ?? [];
        Note = read.Unavailable;
        _read = true;
        Rebuild();
    }

    protected override void Forget()
    {
        _read = false;
        _groups = [];
        Groups.Clear();
    }

    /// <summary>Its groups have been read: until then a pending change has no list to show in.</summary>
    bool _read;

    void Rebuild()
    {
        Groups.Clear();
        var pending = Pending.ToDictionary(p => p.Group.Id, StringComparer.Ordinal);
        foreach (var g in _groups)
        {
            var summary = _index.Group(g.Id);
            var leaving = pending.GetValueOrDefault(g.Id);
            var locked = g.IsDynamic ? "Dynamic: membership follows the group's rule" : summary?.IsExchangeManaged == true ? "Managed in Exchange: devices can't be members"
                : summary?.SyncedFromOnPremises == true ? "Synced from on-premises Active Directory: change its members there" : null;
            Groups.Add(new DeviceGroupRow(g.Name, g.IsDynamic, leaving is not null, false, CanEdit && locked is null,
                locked ?? (CanEdit ? "Take the device out of this group (added to Pending Changes)" : EditReason),
                // A group outside the local copy is named by what Entra said (devices only join security groups).
                () => Toggle(leaving, summary is null ? new ChangeTarget("group", g.Id, g.Name, null) : GroupAccess.Target(summary), false),
                summary is not null && _openGroup is not null ? () => _openGroup(summary) : null));
        }
        foreach (var join in pending.Values.Where(p => p.New && _groups.All(g => g.Id != p.Group.Id)))
        {
            var summary = _index.Group(join.Group.Id);
            Groups.Add(new DeviceGroupRow(join.Group.Name, false, true, true, CanEdit, "Undo adding it (takes it out of Pending Changes)",
                () => Page.Pipeline!.Unstage(join), summary is not null && _openGroup is not null ? () => _openGroup(summary) : null));
        }
        Summary = _groups.Count switch { 0 => "In no groups", 1 => "In 1 group", var n => $"In {n} groups" };
        OnPropertyChanged(nameof(HasNoGroups));
    }

    void Toggle(SetGroupMembership? pending, ChangeTarget group, bool join)
    {
        if (!CanEdit)
            return;
        if (pending is not null)
            Page.Pipeline!.Unstage(pending);
        else
            Page.Pipeline!.Stage(new SetGroupMembership(group, Me, "device", GroupRole.Member, false, !join, join));
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        if (_read)
            Rebuild();
    }

    void OnApplied(object? sender, IReadOnlyList<ChangeOp> applied)
    {
        if (applied.OfType<SetGroupMembership>().Any(m => m.Person.Id == Device.ObjectId))
            Reread();
    }

    public void Dispose()
    {
        if (Page.Pipeline is { } pipeline)
        {
            pipeline.Changed -= OnPendingChanged;
            pipeline.Applied -= OnApplied;
        }
    }
}

// ---- Defender ---------------------------------------------------------------------------------

public sealed class MalwareRow(MalwareFound found)
{
    public string Name => found.Name;
    public string Detail => string.Join(" · ", new[]
    {
        found.Category is { } c ? DeviceText.Words(c) : null,
        found.State is { } s ? DeviceText.Words(s) : null,
        found.Detected is { } at ? Display.Exact(at) : null,
    }.OfType<string>());
    public string SeverityText => found.Severity is { } s ? DeviceText.Words(s) : "Unknown";
    public BadgeKind SeverityKind => found.Severity is "severe" or "high" ? BadgeKind.Danger : BadgeKind.Warning;
}

/// <summary>Microsoft Defender on the device: protection on or off, scans, and anything it found.</summary>
public sealed partial class DeviceDefenderSection(DeviceDetailViewModel page) : DeviceSection(page)
{
    public override string Key => "defender";
    public override string Title => "Defender";
    public override string Icon => "Shield";
    public override string LoadingText => "Reading Microsoft Defender's state from Intune…";

    ProtectionState? _state;

    public bool HasState => _state is not null;

    public IReadOnlyList<HealthChip> Chips
    {
        get
        {
            if (_state is not { } s)
                return [];
            HealthChip On(string what, bool? on) => on switch
            {
                true => new($"{what} On", BadgeKind.Success, $"{what} is on"),
                false => new($"{what} Off", BadgeKind.Danger, $"{what} is off, or Defender isn't the antivirus here"),
                _ => new($"{what} Unknown", BadgeKind.Neutral, $"The device hasn't reported {what.ToLowerInvariant()}"),
            };
            var chips = new List<HealthChip> { On("Antivirus", s.MalwareProtection), On("Real-Time Protection", s.RealTime), On("Tamper Protection", s.Tamper) };
            if (s.SignatureOverdue)
                chips.Add(new("Definitions Out of Date", BadgeKind.Warning, "Defender's security intelligence needs updating"));
            if (s.QuickScanOverdue || s.FullScanOverdue)
                chips.Add(new("Scan Overdue", BadgeKind.Warning, "A scheduled scan didn't run"));
            if (s.RebootRequired)
                chips.Add(new("Restart Needed", BadgeKind.Warning, "Defender needs a restart to finish"));
            if (s.FullScanRequired)
                chips.Add(new("Full Scan Needed", BadgeKind.Warning, "Defender asks for a full scan"));
            return chips;
        }
    }

    public IReadOnlyList<SummaryLine> Facts => _state is not { } s ? [] : new (string, string?)[]
        {
            ("Device State", s.DeviceState is { } d ? DeviceText.Words(d) : null),
            ("Product Status", s.ProductStatus is { Length: > 0 } p && p != "noStatus" ? DeviceText.Words(p) : null),
            ("Definitions", s.SignatureVersion),
            ("Last Quick Scan", s.LastQuickScan is { } q ? Display.Exact(q) : "Never reported"),
            ("Last Full Scan", s.LastFullScan is { } f ? Display.Exact(f) : "Never reported"),
            ("Last Reported", s.LastReported is { } r ? Display.Exact(r) : null),
        }.Where(l => l.Item2 is not null).Select(l => new SummaryLine(l.Item1, l.Item2!)).ToArray();

    public ObservableCollection<MalwareRow> Malware { get; } = [];

    public bool HasNoMalware => HasState && Malware.Count == 0;

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        if (Device.ManagedId is not { } managed || Page.Pages is not { } pages)
        {
            Note = NotManaged;
            return;
        }
        var read = await pages.GetProtectionAsync(managed, cancellationToken);
        _state = read.Value?.State;
        Malware.Clear();
        foreach (var m in read.Value?.Malware ?? [])
            Malware.Add(new MalwareRow(m));
        Note = read.Unavailable;
        Summary = Malware.Count > 0 ? $"{Malware.Count} threat{(Malware.Count == 1 ? "" : "s")} found" : _state is null ? "Not reported" : "No threats found";
        StateChanged();
    }

    protected override void Forget()
    {
        _state = null;
        Malware.Clear();
        StateChanged();
    }

    void StateChanged()
    {
        foreach (var name in new[] { nameof(HasState), nameof(Chips), nameof(Facts), nameof(HasNoMalware) })
            OnPropertyChanged(name);
    }
}

// ---- Recovery keys ----------------------------------------------------------------------------

/// <summary>Its BitLocker recovery keys and local admin password: hidden until Show, each view logged.</summary>
public sealed partial class DeviceKeysSection(DeviceDetailViewModel page, IDeviceRecords? records) : DeviceSection(page)
{
    public override string Key => "keys";
    public override string Title => "Keys and Passwords";
    public override string Icon => "Key";
    public override string LoadingText => "Reading which keys are backed up (not the keys themselves)…";

    public ObservableCollection<SecretRow> Secrets { get; } = [];

    public bool HasNoSecrets => !IsLoading && Secrets.Count == 0 && Note is null;

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        if (Device.DeviceId is not { } deviceId || records is null || Device.ObjectId is null)
        {
            Note = "Entra doesn't list this device, so no BitLocker keys or local admin password are filed for it.";
            return;
        }
        Secrets.Clear();
        var keys = records.GetRecoveryKeysAsync(deviceId, cancellationToken);
        var laps = records.GetLapsAsync(deviceId, cancellationToken);
        foreach (var key in await keys)
        {
            var captured = key;
            Secrets.Add(new SecretRow($"BitLocker Recovery Key · {key.Volume}", key.Created is { } at ? $"Backed up {Display.Exact(at)}" : null,
                () => records.RevealRecoveryKeyAsync(captured.Id), () => Logged($"BitLocker Recovery Key ({captured.Volume.ToLowerInvariant()})")));
        }
        if (await laps is { } local)
            Secrets.Add(new SecretRow("Local Admin Password", local.BackedUp is { } at ? $"Backed up {Display.Exact(at)}" : null,
                async () =>
                {
                    var (account, password) = await records.RevealLapsAsync(deviceId);
                    return $"{account}: {password}";
                },
                () => Logged("Local Admin Password")));
        Summary = Secrets.Count switch { 0 => "None backed up", 1 => "1 backed up", var n => $"{n} backed up" };
        OnPropertyChanged(nameof(HasNoSecrets));
    }

    /// <summary>A secret was shown: Tenantry's change log records who saw what, beside Microsoft's audit.</summary>
    void Logged(string what) =>
        Page.Pipeline?.Record(new SecretViewed(BulkActions.EntraTarget(Device), what), "Viewed from the device's page");
}
