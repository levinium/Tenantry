using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// A secret (a BitLocker recovery key, a local admin password): hidden until Show, read then (so
/// Microsoft's audit and Tenantry's change log both record it), hidden again after a minute.
/// </summary>
public sealed partial class SecretRow(string label, string? detail, Func<Task<string>> reveal, Action logged) : ObservableObject
{
    /// <summary>How long a revealed secret stays on screen.</summary>
    public static TimeSpan ShowFor { get; set; } = TimeSpan.FromSeconds(60);

    int _generation;

    public string Label => label;
    public string? Detail => detail;
    public bool HasDetail => detail is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown), nameof(ShowText), nameof(Masked))]
    public partial string? Secret { get; private set; }

    public bool IsShown => Secret is not null;

    public string ShowText => IsShown ? "Hide" : "Show";

    public string Masked => IsShown ? Secret! : "••••••••••••";

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    public partial bool Copied { get; private set; }

    // Pressable again while a shown secret waits to hide itself: that press hides it.
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task Show()
    {
        if (IsShown)
        {
            Hide();
            return;
        }
        IsLoading = true;
        Error = null;
        try
        {
            Secret = await reveal();
            logged();
            var generation = ++_generation;
            await Task.Delay(ShowFor);
            if (generation == _generation)
                Hide();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = Friendly.Message(e);
        }
        finally
        {
            IsLoading = false;
        }
    }

    void Hide()
    {
        _generation++;
        Secret = null;
        Copied = false;
    }

    [RelayCommand]
    void Copy()
    {
        if (Secret is null)
            return;
        Platform.CopyText(Secret);
        Copied = true;
    }
}

/// <summary>One of their devices: a summary row that expands to its details, secrets and actions.</summary>
public sealed partial class DeviceItem : ObservableObject
{
    readonly DevicesSection _section;

    public DeviceItem(UserDevice device, DevicesSection section)
    {
        Device = device;
        _section = section;
    }

    public UserDevice Device { get; }

    public string Name => Device.Name;

    /// <summary>A FluentIcons name for the kind of device.</summary>
    public string Icon => Device.Os switch
    {
        "iPadOS" => "Tablet",
        "iOS" or "Android" or "AndroidForWork" => "Phone",
        _ when Device.Os?.StartsWith("Mac", StringComparison.OrdinalIgnoreCase) == true => "Laptop",
        _ => "Laptop",
    };

    public string Detail => string.Join(" · ", new[]
    {
        string.Join(' ', new[] { Device.Manufacturer, Device.Model }.Where(s => !string.IsNullOrEmpty(s)).Distinct()),
        string.Join(' ', new[] { Device.Os, Device.OsVersion }.Where(s => !string.IsNullOrEmpty(s))),
    }.Where(s => s.Length > 0));

    public string LastText => Device.LastActivity is { } at
        ? $"{(Device.IsManaged ? "Last checked in" : "Last signed in")} {at.ToLocalTime():d MMM yyyy}"
        : "No recent activity";

    public string JoinType => Device.JoinType;
    public bool IsManaged => Device.IsManaged;
    public bool IsDisabled => Device.Enabled == false;
    public string? ComplianceText => Device.Compliant switch { true => "Compliant", false => "Not Compliant", _ => null };
    public BadgeKind ComplianceKind => Device.Compliant == false ? BadgeKind.Danger : BadgeKind.Success;
    public bool HasCompliance => ComplianceText is not null;
    public bool IsUnencrypted => Device.Encrypted == false;

    public IReadOnlyList<(string Label, string Value)> Facts =>
    [
        .. new (string, string?)[]
        {
            ("How It's Theirs", Device.Relation),
            ("Owned By", Device.Ownership),
            ("Serial Number", Device.Serial),
            ("Encrypted", Device.Encrypted switch { true => "Yes", false => "No", _ => null }),
            (Device.IsManaged ? "Enrolled" : "Registered", Device.Registered?.ToLocalTime().ToString("d MMM yyyy")),
        }.Where(f => f.Item2 is not null).Select(f => (f.Item1, f.Item2!)),
    ];

    public IEnumerable<string> FactLines => Facts.Select(f => $"{f.Label}: {f.Value}");

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    void Toggle() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_secretsRead)
            _ = ReadSecretsAsync();
    }

    bool _secretsRead;

    public ObservableCollection<SecretRow> Secrets { get; } = [];

    [ObservableProperty]
    public partial string? SecretsNote { get; private set; }

    public bool HasSecretsNote => SecretsNote is not null;

    partial void OnSecretsNoteChanged(string? value) => OnPropertyChanged(nameof(HasSecretsNote));

    [ObservableProperty]
    public partial bool SecretsLoading { get; private set; }

    /// <summary>BitLocker keys and the local admin password: their metadata only, the secrets on Show.</summary>
    async Task ReadSecretsAsync()
    {
        _secretsRead = true;
        if (Device.DeviceId is not { } deviceId || _section.Records is not { } records)
        {
            SecretsNote = "Not in Entra, so no BitLocker keys or local admin password are filed for it.";
            return;
        }
        SecretsLoading = true;
        var notes = new List<string>();
        try
        {
            var keys = records.GetRecoveryKeysAsync(deviceId);
            var laps = records.GetLapsAsync(deviceId);
            foreach (var key in await keys)
            {
                var captured = key;
                Secrets.Add(new SecretRow($"BitLocker Recovery Key · {key.Volume}", key.Created is { } at ? $"Saved {at.ToLocalTime():d MMM yyyy}" : null,
                    () => records.RevealRecoveryKeyAsync(captured.Id), () => _section.Logged(this, $"BitLocker Recovery Key ({captured.Volume.ToLowerInvariant()})")));
            }
            if (await laps is { } local)
                Secrets.Add(new SecretRow("Local Admin Password", local.BackedUp is { } at ? $"Backed up {at.ToLocalTime():d MMM yyyy}" : null,
                    async () =>
                    {
                        var (account, password) = await records.RevealLapsAsync(deviceId);
                        return $"{account}: {password}";
                    },
                    () => _section.Logged(this, "Local Admin Password")));
            if (Secrets.Count == 0)
                notes.Add("No BitLocker keys or local admin password are backed up for it.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            notes.Add($"Couldn't read its keys: {Friendly.Message(e)}");
        }
        finally
        {
            SecretsLoading = false;
        }
        SecretsNote = notes.Count == 0 ? null : string.Join(" ", notes);
    }

    // ---- Actions ----------------------------------------------------------------------------

    public bool CanEdit => _section.CanEdit;

    public bool CanSetEnabled => CanEdit && Device.ObjectId is not null;

    public bool IsEnablePending => _section.PendingFor(Device.ObjectId, "enabled") is not null;

    public string EnableText => IsEnablePending ? "Undo" : Device.Enabled == false ? "Enable in Entra" : "Disable in Entra";

    [RelayCommand]
    void ToggleEnabled() => _section.ToggleEnabled(this);

    public bool CanManage => CanEdit && IsManaged;

    public bool CanRotate => CanManage && Device.IsWindows;

    string? Pending(DeviceCommand command) => _section.PendingFor(Device.ManagedId, command is DeviceCommand.Retire or DeviceCommand.Wipe ? "remove" : "action:" + command) is RunDeviceAction a
        && a.Command == command ? "Undo" : null;

    public string SyncText => Pending(DeviceCommand.Sync) ?? "Sync";
    public string RestartText => Pending(DeviceCommand.Restart) ?? "Restart";
    public string LockText => Pending(DeviceCommand.Lock) ?? "Lock";
    public string RotateText => Pending(DeviceCommand.RotateLocalAdminPassword) ?? "Rotate Admin Password";
    public string RetireText => Pending(DeviceCommand.Retire) ?? "Retire";
    public string WipeText => Pending(DeviceCommand.Wipe) ?? "Wipe…";

    [RelayCommand]
    void Run(DeviceCommand command) => _section.Run(this, command);

    /// <summary>What's waiting in Pending changes for this device, for its row.</summary>
    public string? PendingText
    {
        get
        {
            var parts = _section.PendingForDevice(Device).Select(p => p.Label).ToArray();
            return parts.Length == 0 ? null : "Pending: " + string.Join(", ", parts);
        }
    }

    public bool HasPending => PendingText is not null;

    public void Refresh()
    {
        foreach (var name in new[] { nameof(IsEnablePending), nameof(EnableText), nameof(SyncText), nameof(RestartText), nameof(LockText),
                     nameof(RotateText), nameof(RetireText), nameof(WipeText), nameof(PendingText), nameof(HasPending) })
            OnPropertyChanged(name);
    }
}

/// <summary>One Microsoft 365 Apps product and where it's activated, against the limit of five computers.</summary>
public sealed class ActivationRow(OfficeActivation activation)
{
    public string Product => activation.ProductName;

    public string ComputersText => activation.Computers == 0 ? "No computers"
        : $"{activation.Computers} of {OfficeActivation.DeviceLimit} computers ({Platforms(("Windows", activation.Windows), ("Mac", activation.Mac))})";

    public string MobileText => activation.Mobile == 0 ? "No phones or tablets"
        : $"{activation.Mobile} phones or tablets ({Platforms(("iPhone and iPad", activation.Ios), ("Android", activation.Android), ("Windows Mobile", activation.Windows10Mobile))})";

    public bool HasComputerLimit => activation.Computers > 0 || activation.Mobile == 0;

    /// <summary>At four of five, the next new computer may be refused.</summary>
    public bool IsNearLimit => activation.Computers >= OfficeActivation.DeviceLimit - 1;

    public string LimitText => activation.Computers >= OfficeActivation.DeviceLimit ? "At the Limit" : "Near the Limit";

    public BadgeKind LimitKind => activation.Computers >= OfficeActivation.DeviceLimit ? BadgeKind.Danger : BadgeKind.Warning;

    public string LastText => (activation.LastActivated is { } d ? $"Last activated {d:d MMM yyyy}" : "")
                              + (activation.SharedComputer ? (activation.LastActivated is null ? "" : " · ") + "Also on a shared computer" : "");

    public bool HasLast => LastText.Length > 0;

    static string Platforms(params (string Name, int Count)[] counts) =>
        string.Join(", ", counts.Where(c => c.Count > 0).Select(c => $"{c.Name} {c.Count}"));
}

/// <summary>A device they signed in from (the sign-in log): what it is, which apps, when, and whether Entra knows it.</summary>
public sealed class SignedInRow(SignedInDevice device, DateTimeOffset now, bool listedAbove)
{
    public string Name => device.Name;

    public string Icon => device.Os switch
    {
        var os when os?.StartsWith("iOS", StringComparison.OrdinalIgnoreCase) == true || os?.StartsWith("Android", StringComparison.OrdinalIgnoreCase) == true => "Phone",
        var os when os?.StartsWith("iPad", StringComparison.OrdinalIgnoreCase) == true => "Tablet",
        _ => "Laptop",
    };

    public string Detail => string.Join(" · ", new[] { device.Os, device.Browser, device.Location }.Where(s => !string.IsNullOrEmpty(s)));

    public string AppsText => device.Apps.Count == 0 ? "" : string.Join(", ", device.Apps);

    public bool HasApps => device.Apps.Count > 0;

    public string LastText => $"Last signed in {Display.Relative(device.LastSeen, now)}";

    public string Trust => device.Trust;

    public BadgeKind TrustKind => device.IsKnown ? BadgeKind.Neutral : BadgeKind.Warning;

    public string? ComplianceText => device.Compliant switch { true => "Compliant", false => "Not Compliant", _ => null };

    public BadgeKind ComplianceKind => device.Compliant == false ? BadgeKind.Danger : BadgeKind.Success;

    public bool HasCompliance => ComplianceText is not null;

    /// <summary>The same device is in Entra or Intune: its actions are in the list above.</summary>
    public bool IsListedAbove => listedAbove;
}

/// <summary>
/// The Devices tab (4c-2): the devices they use, from Entra (registered or joined) and Intune
/// (managed), joined into one list. Each expands to its details, BitLocker recovery keys and local
/// admin password (hidden until Show, each view logged), and actions: enable or disable in Entra;
/// sync, restart, lock, rotate the admin password, retire or wipe through Intune.
/// </summary>
public sealed partial class DevicesSection : RecordSection, IDisposable
{
    UserSummary _user;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    bool _loaded;

    readonly IAppActivationRecords? _apps;
    readonly Func<DateTimeOffset> _now;

    public DevicesSection(UserSummary user, IDeviceRecords? records, ChangePipeline? pipeline, Availability canEdit, IAppActivationRecords? apps = null,
        Func<DateTimeOffset>? now = null)
    {
        _user = user;
        _apps = apps;
        _now = now ?? (() => DateTimeOffset.Now);
        Records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
        Summary = "Devices they use, and their recovery keys";
        if (_pipeline is not null)
        {
            _pipeline.Changed += OnPendingChanged;
            _pipeline.Applied += OnApplied;
        }
    }

    public override string Key => "devices";
    public override string Title => "Devices";
    public override string Icon => "Laptop";

    internal IDeviceRecords? Records { get; }

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? LoadError { get; private set; }

    public bool HasError => LoadError is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnavailable))]
    public partial string? Unavailable { get; private set; }

    public bool HasUnavailable => Unavailable is not null;

    public bool HasNoDevices => _loaded && Devices.Count == 0 && Unavailable is null && LoadError is null;

    // ---- Microsoft 365 Apps -------------------------------------------------------------------

    public bool HasAppsCard => _apps is not null;

    public ObservableCollection<ActivationRow> Activations { get; } = [];

    [ObservableProperty]
    public partial bool ActivationsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivationsNote))]
    public partial string? ActivationsNote { get; private set; }

    public bool HasActivationsNote => ActivationsNote is not null;

    public ObservableCollection<SignedInRow> SignedInFrom { get; } = [];

    [ObservableProperty]
    public partial bool SignedInLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignedInNote))]
    public partial string? SignedInNote { get; private set; }

    public bool HasSignedInNote => SignedInNote is not null;

    bool _appsLoaded;

    /// <summary>Activations and the devices they signed in from, each shown as soon as it arrives.</summary>
    async Task LoadAppsAsync(CancellationToken cancellationToken)
    {
        if (_apps is null || _appsLoaded)
            return;
        _appsLoaded = true;
        ActivationsLoading = SignedInLoading = true;
        async Task Activated()
        {
            try
            {
                var read = await _apps.GetActivationsAsync(_user.UserPrincipalName, cancellationToken);
                Activations.Clear();
                foreach (var a in (read.Value ?? []).OrderByDescending(a => a.Computers).ThenBy(a => a.ProductName, StringComparer.CurrentCulture))
                    Activations.Add(new ActivationRow(a));
                ActivationsNote = read.Unavailable ?? (Activations.Count == 0 ? "They haven't activated Microsoft 365 Apps anywhere." : null);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ActivationsNote = $"Couldn't read the activation report: {Friendly.Message(e)}";
            }
            finally
            {
                ActivationsLoading = false;
            }
        }
        async Task SignedIn()
        {
            try
            {
                var read = await _apps.GetSignedInDevicesAsync(_user.Id, cancellationToken);
                SignedInFrom.Clear();
                var known = Devices.Select(d => d.Device.DeviceId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var d in read.Value ?? [])
                    SignedInFrom.Add(new SignedInRow(d, _now(), d.DeviceId is { } id && known.Contains(id)));
                SignedInNote = read.Unavailable ?? (SignedInFrom.Count == 0 ? "No sign-ins in the last 30 days." : null);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                SignedInNote = $"Couldn't read the sign-in log: {Friendly.Message(e)}";
            }
            finally
            {
                SignedInLoading = false;
            }
        }
        await Task.WhenAll(Activated(), SignedIn());
    }

    /// <summary>
    /// Microsoft offers other apps no way to deactivate one installation; the admin center can.
    /// Opens their page there (Account › Microsoft 365 activations).
    /// </summary>
    [RelayCommand]
    void OpenActivations() => Platform.OpenUrl($"https://admin.microsoft.com/Adminportal/Home#/users/:/UserDetails/{_user.Id}");

    ChangeTarget UserTarget => GroupAccess.Target(_user);

    public bool IsSignOutPending => _pipeline?.PendingFor(_user.Id, "sessions") is not null;

    public string SignOutText => IsSignOutPending ? "Undo Sign Out Everywhere" : "Sign Out Everywhere";

    /// <summary>Signs them out of every app and browser (Entra's Revoke sessions); Office asks them to sign in again.</summary>
    [RelayCommand]
    void ToggleSignOut()
    {
        if (!CanEdit)
            return;
        if (_pipeline!.PendingFor(_user.Id, "sessions") is { } pending)
            _pipeline.Unstage(pending);
        else
            _pipeline.Stage(new RevokeSessions(UserTarget));
    }

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await LoadDevicesAsync(cancellationToken);
        await LoadAppsAsync(cancellationToken);
    }

    async Task LoadDevicesAsync(CancellationToken cancellationToken)
    {
        if (_loaded || IsLoading || Records is null)
            return;
        IsLoading = true;
        LoadError = null;
        try
        {
            var read = await Records.GetDevicesAsync(_user.Id, cancellationToken);
            Unavailable = read.Unavailable;
            Devices.Clear();
            foreach (var device in read.Value ?? [])
                Devices.Add(new DeviceItem(device, this));
            _loaded = true;
            Summary = Devices.Count switch
            {
                0 => "No devices",
                1 => "1 device",
                var n => $"{n} devices",
            } + (Devices.Any(d => d.Device.Compliant == false) ? " · one not compliant" : "");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read their devices: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasNoDevices));
        }
    }

    public override void RecordChanged(UserSummary user) => _user = user;

    void OnApplied(object? sender, IReadOnlyList<ChangeOp> applied)
    {
        if (!applied.Any(c => Devices.Any(d => c.Target.Id == d.Device.ObjectId || c.Target.Id == d.Device.ManagedId)))
            return;
        _loaded = false;
        _ = LoadAsync();
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        foreach (var device in Devices)
            device.Refresh();
        OnPropertyChanged(nameof(IsSignOutPending));
        OnPropertyChanged(nameof(SignOutText));
    }

    internal ChangeOp? PendingFor(string? targetId, string key) => targetId is null ? null : _pipeline?.PendingFor(targetId, key);

    internal IEnumerable<ChangeOp> PendingForDevice(UserDevice device) =>
        _pipeline?.Pending.Where(p => p.Target.Id == device.ObjectId || p.Target.Id == device.ManagedId) ?? [];

    static ChangeTarget Entra(UserDevice d) => new("device", d.ObjectId!, d.Name, null);

    static ChangeTarget Intune(UserDevice d) => new("manageddevice", d.ManagedId!, d.Name, null);

    internal void ToggleEnabled(DeviceItem item)
    {
        if (!CanEdit || item.Device.ObjectId is null)
            return;
        if (PendingFor(item.Device.ObjectId, "enabled") is { } pending)
            _pipeline!.Unstage(pending);
        else
        {
            var enabled = item.Device.Enabled != false;
            _pipeline!.Stage(new SetDeviceEnabled(Entra(item.Device), enabled, !enabled));
        }
    }

    /// <summary>Stages an Intune action; the same action again takes it back out (retire and wipe share a slot).</summary>
    internal void Run(DeviceItem item, DeviceCommand command)
    {
        if (!CanEdit || item.Device.ManagedId is null)
            return;
        var change = new RunDeviceAction(Intune(item.Device), command);
        if (PendingFor(item.Device.ManagedId, change.Key) is RunDeviceAction pending)
        {
            _pipeline!.Unstage(pending);
            if (pending.Command == command)
                return;
        }
        _pipeline!.Stage(change);
    }

    /// <summary>A secret was shown: Tenantry's change log records who saw what, beside Microsoft's audit.</summary>
    internal void Logged(DeviceItem item, string what) =>
        _pipeline?.Record(new SecretViewed(new ChangeTarget("device", item.Device.ObjectId ?? item.Device.DeviceId ?? item.Name, item.Name, null), what),
            $"Viewed for {_user.DisplayName}");

    public void Dispose()
    {
        if (_pipeline is not null)
        {
            _pipeline.Changed -= OnPendingChanged;
            _pipeline.Applied -= OnApplied;
        }
    }
}
