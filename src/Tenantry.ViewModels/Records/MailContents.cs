using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>The Mail tab's pages: each groups a few cards, so the tab never becomes one long scroll.</summary>
public enum MailPage
{
    Delivery,
    Limits,
    Retention,
    RulesAndSharing,
    Devices,
    Recovery,
}

public sealed record MailPageItem(MailPage Page, string Title, string Icon);

/// <summary>One inbox rule, with its switch (staged at once) and delete.</summary>
public sealed partial class RuleRow(InboxRule rule, bool enabled, string? pendingText, bool deleting, bool canEdit, bool forwardsOutside,
    Action<bool> setEnabled, Action toggleDelete) : ObservableObject
{
    public string Name => rule.Name;
    public string Description => rule.Description;
    public bool CanEdit => canEdit && !deleting;
    public bool CanDelete => canEdit;
    public string? PendingText => pendingText;
    public bool HasPending => pendingText is not null;
    public bool IsDeleting => deleting;
    public bool Forwards => rule.Forwards;
    public bool ForwardsOutside => forwardsOutside;
    public bool ForwardsInside => rule.Forwards && !forwardsOutside;
    public bool Deletes => rule.Deletes;
    public bool InError => rule.InError;
    public string DeleteTip => deleting ? "Keep this rule (take the delete out of Pending changes)" : "Delete this rule (added to Pending changes)";

    public bool IsEnabled
    {
        get => enabled;
        set
        {
            if (value != enabled)
                setEnabled(value);
        }
    }

    [RelayCommand]
    void Delete() => toggleDelete();
}

/// <summary>A folder role offered in a dropdown.</summary>
public sealed record RoleOption(string Role, string Name, string Explanation)
{
    public override string ToString() => Name;
}

/// <summary>Someone's access to the calendar, with a role dropdown (staged at once) and remove.</summary>
public sealed partial class SharingRow(string name, string? detail, string role, bool isBuiltIn, string? pendingText, bool canEdit,
    Action<string> setRole, Action remove) : ObservableObject
{
    public static IReadOnlyList<RoleOption> Roles { get; } = FolderRoles.Calendar.Select(r => new RoleOption(r.Role, r.Name, r.Explanation)).ToArray();

    public string Name => name;
    public string? Detail => detail;
    public bool HasDetail => detail is not null;
    public string? PendingText => pendingText;
    public bool HasPending => pendingText is not null;
    public bool CanEdit => canEdit;
    public bool CanRemove => canEdit && !isBuiltIn;
    public string RemoveTip => HasPending ? "Undo this pending change" : "Take away their access (added to Pending changes)";

    public RoleOption? SelectedRole
    {
        get => Roles.FirstOrDefault(r => string.Equals(r.Role, role, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null && !string.Equals(value.Role, role, StringComparison.OrdinalIgnoreCase))
                setRole(value.Role);
        }
    }

    [RelayCommand]
    void Remove() => remove();
}

/// <summary>A phone or tablet, with block, remove and wipe (each staged; pressing again undoes).</summary>
public sealed partial class DeviceRow(MobileDevice device, bool blocked, string? pendingText, DeviceAction? pendingAction, bool canEdit,
    Action<DeviceAction> act) : ObservableObject
{
    public string Name => device.Name;
    public string Detail => string.Join(" · ", new[] { device.Model, device.Os, device.ClientType }.Where(s => s.Length > 0).Distinct());
    public string LastSyncText => device.LastSync is { } last ? $"Last synced {last.ToLocalTime():d MMM yyyy, h:mm tt}" : "Hasn't synced";
    public string? WipeStatus => device.WipeStatus;
    public bool HasWipeStatus => device.WipeStatus is not null;
    public bool IsBlocked => blocked;
    public bool IsQuarantined => string.Equals(device.AccessState, "Quarantined", StringComparison.OrdinalIgnoreCase);
    public string? PendingText => pendingText;
    public bool HasPending => pendingText is not null;
    public bool CanEdit => canEdit;
    public string BlockText => blocked ? "Unblock" : "Block";
    public string BlockTip => blocked ? "Let it sync their mail again (added to Pending changes)" : "Stop it syncing their mail (added to Pending changes)";
    public bool IsWiping => pendingAction is DeviceAction.Wipe or DeviceAction.WipeAccountOnly;
    public bool IsRemoving => pendingAction == DeviceAction.Remove;

    [RelayCommand]
    void ToggleBlock() => act(blocked ? DeviceAction.Unblock : DeviceAction.Block);

    [RelayCommand]
    void Remove() => act(DeviceAction.Remove);

    [RelayCommand]
    void Wipe() => act(DeviceAction.Wipe);

    [RelayCommand]
    void WipeAccountOnly() => act(DeviceAction.WipeAccountOnly);
}

/// <summary>A deleted item, with Restore (staged; pressing again undoes).</summary>
public sealed partial class DeletedRow(DeletedItem item, bool restoring, bool canEdit, Action toggle) : ObservableObject
{
    public string Subject => item.Subject;
    public string Detail => string.Join(" · ", new[] { item.Kind, item.FolderPath?.Trim('/', '\\'), item.Modified?.ToLocalTime().ToString("d MMM yyyy") }
        .Where(s => !string.IsNullOrEmpty(s)));
    public bool IsRestoring => restoring;
    public bool CanEdit => canEdit;
    public string RestoreText => restoring ? "Undo restore" : "Restore";

    [RelayCommand]
    void Toggle() => toggle();
}

/// <summary>A traced message; expanding it reads the steps it went through.</summary>
public sealed partial class TraceRow(TracedMessage message, TraceDirection direction, Func<TracedMessage, Task<IReadOnlyList<TraceEvent>>> detail)
    : ObservableObject
{
    public string When => message.Received.ToLocalTime().ToString("d MMM, h:mm tt");
    public string Other => direction == TraceDirection.Received ? message.Sender : message.Recipient;
    public string Subject => message.Subject;
    /// <summary>Exchange's status, as words ("FilteredAsSpam" reads "Filtered as spam").</summary>
    public string Status => message.Status switch
    {
        "FilteredAsSpam" => "Filtered as spam",
        "GettingStatus" => "Getting status",
        "Expanded" => "Sent to a group",
        var status => status,
    };

    public BadgeKind StatusKind => message.Status switch
    {
        "Delivered" => BadgeKind.Success,
        "Failed" => BadgeKind.Danger,
        "FilteredAsSpam" or "Quarantined" => BadgeKind.Warning,
        _ => BadgeKind.Neutral,
    };

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<TraceEvent> Events { get; private set; } = [];

    [ObservableProperty]
    public partial string? DetailError { get; private set; }

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
        try
        {
            Events = await detail(message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            DetailError = Friendly.Message(e);
            _read = false;
        }
    }
}

/// <summary>
/// The Mail tab's pages and its mailbox-contents part (4b-3): inbox rules, calendar sharing,
/// mobile devices, deleted items and message trace. Each page reads its own data the first time
/// it's shown, and again after changes to this mailbox apply.
/// </summary>
public sealed partial class MailSection
{
    public static IReadOnlyList<MailPageItem> Pages { get; } =
    [
        new(MailPage.Delivery, "Addresses & delivery", "MailInbox"),
        new(MailPage.Limits, "Limits & apps", "Gauge"),
        new(MailPage.Retention, "Archive & holds", "Archive"),
        new(MailPage.RulesAndSharing, "Rules & sharing", "Filter"),
        new(MailPage.Devices, "Mobile devices", "Phone"),
        new(MailPage.Recovery, "Recovery & trace", "History"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDelivery), nameof(ShowLimits), nameof(ShowRetention), nameof(ShowRulesAndSharing), nameof(ShowDevices),
        nameof(ShowRecovery), nameof(SelectedPageItem))]
    public partial MailPage SelectedPage { get; set; }

    public MailPageItem SelectedPageItem
    {
        get => Pages.First(p => p.Page == SelectedPage);
        set => SelectedPage = value.Page;
    }

    public bool ShowDelivery => SelectedPage == MailPage.Delivery;
    public bool ShowLimits => SelectedPage == MailPage.Limits;
    public bool ShowRetention => SelectedPage == MailPage.Retention;
    public bool ShowRulesAndSharing => SelectedPage == MailPage.RulesAndSharing;
    public bool ShowDevices => SelectedPage == MailPage.Devices;
    public bool ShowRecovery => SelectedPage == MailPage.Recovery;

    partial void OnSelectedPageChanged(MailPage value) => _ = LoadPageAsync(value);

    IMailboxContents? Contents => _records as IMailboxContents;

    string Address => _user.Mailbox?.PrimaryAddress ?? _user.Mail ?? _user.UserPrincipalName;

    async Task LoadPageAsync(MailPage page)
    {
        switch (page)
        {
            case MailPage.RulesAndSharing:
                await Task.WhenAll(LoadRulesAsync(), LoadSharingAsync());
                break;
            case MailPage.Devices:
                await LoadDevicesAsync();
                break;
            case MailPage.Recovery:
                await Task.WhenAll(LoadDeletedAsync(), LoadTraceAsync());
                break;
        }
    }

    /// <summary>After changes to this mailbox apply: everything shown is read again.</summary>
    void OnApplied(object? sender, IReadOnlyList<ChangeOp> applied)
    {
        if (!applied.Any(c => c.Target.Id == _user.Id))
            return;
        _rules = null;
        _sharing = null;
        _devices = null;
        _deleted = null;
        _data = null;
        _ = LoadAsync();
        _ = LoadPageAsync(SelectedPage);
    }

    /// <summary>Reads one page's data once (unless <paramref name="again"/>), reporting progress and errors on that page.</summary>
    async Task<T?> ReadAsync<T>(T? current, Func<IMailboxContents, Task<T>> read, Action<bool> loading, Action<string?> error, bool again = false)
        where T : class
    {
        if ((current is not null && !again) || Contents is not { } contents || !HasMailbox)
            return current;
        loading(true);
        error(null);
        try
        {
            return await read(contents);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error(Friendly.Message(e));
            return current;
        }
        finally
        {
            loading(false);
        }
    }

    void RebuildContents()
    {
        RebuildRules();
        RebuildSharing();
        RebuildDevices();
        RebuildDeleted();
    }

    // ---- Inbox rules ------------------------------------------------------------------------

    IReadOnlyList<InboxRule>? _rules;

    public ObservableCollection<RuleRow> Rules { get; } = [];

    [ObservableProperty]
    public partial bool RulesLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRulesError))]
    public partial string? RulesError { get; private set; }

    public bool HasRulesError => RulesError is not null;

    public bool HasNoRules => _rules is { Count: 0 };

    /// <summary>A rule sends mail outside the organization: a health chip worth a look.</summary>
    public bool HasForwardingRule => Rules.Any(r => r.ForwardsOutside);

    async Task LoadRulesAsync()
    {
        _rules = await ReadAsync(_rules, c => c.GetInboxRulesAsync(Address), v => RulesLoading = v, v => RulesError = v);
        RebuildRules();
    }

    void RebuildRules()
    {
        Rules.Clear();
        foreach (var rule in _rules ?? [])
        {
            var pending = PendingFor("rule:" + rule.Identity);
            var enabled = pending is SetInboxRuleEnabled e ? e.New : rule.Enabled;
            var captured = rule;
            Rules.Add(new RuleRow(rule, enabled, pending switch
                {
                    RemoveInboxRule => "Deleting",
                    SetInboxRuleEnabled p => p.New ? "Turning on" : "Turning off",
                    _ => null,
                }, pending is RemoveInboxRule, CanEdit, rule.ForwardsTo.Any(a => !IsInternal(StripName(a))),
                on => _pipeline?.Stage(new SetInboxRuleEnabled(Target, captured.Identity, captured.Name, captured.Enabled, on)),
                () => ToggleRuleDelete(captured)));
        }
        OnPropertyChanged(nameof(HasNoRules));
        OnPropertyChanged(nameof(HasForwardingRule));
    }

    void ToggleRuleDelete(InboxRule rule)
    {
        if (!CanEdit)
            return;
        if (PendingFor("rule:" + rule.Identity) is RemoveInboxRule pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new RemoveInboxRule(Target, rule.Identity, rule.Name,
                (rule.Enabled ? "On" : "Off") + (rule.Forwards ? ", forwards mail" : rule.Deletes ? ", deletes mail" : "")));
    }

    /// <summary>Exchange writes a forward target as "Name [SMTP:address]" or just the address.</summary>
    static string StripName(string target)
    {
        var at = target.IndexOf("SMTP:", StringComparison.OrdinalIgnoreCase);
        return (at >= 0 ? target[(at + 5)..] : target).Trim('[', ']', ' ', '"');
    }

    // ---- Calendar sharing -------------------------------------------------------------------

    FolderSharing? _sharing;

    public ObservableCollection<SharingRow> CalendarSharing { get; } = [];

    [ObservableProperty]
    public partial bool SharingLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharingError))]
    public partial string? SharingError { get; private set; }

    public bool HasSharingError => SharingError is not null;

    public PersonPicker SharingPicker { get; private set; } = null!;

    void CreateSharingPicker() =>
        SharingPicker = new PersonPicker("Share their calendar with: type a name", SharingOptions, AddSharing);

    IEnumerable<PickOption> SharingOptions()
    {
        var listed = CalendarSharing.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GroupAccess.People(_index).Where(o => o.Id != _user.Id && !listed.Contains(o.Name));
    }

    async Task LoadSharingAsync()
    {
        _sharing = await ReadAsync(_sharing, c => c.GetCalendarSharingAsync(Address), v => SharingLoading = v, v => SharingError = v);
        RebuildSharing();
    }

    static string Describe(FolderPermission p) => p.User switch
    {
        "Default" => "Everyone in the organization",
        "Anonymous" => "People outside the organization",
        _ => p.User,
    };

    void RebuildSharing()
    {
        CalendarSharing.Clear();
        if (_sharing is not { } sharing)
            return;
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var permission in sharing.Permissions)
        {
            shown.Add(permission.User);
            var pending = PendingFor($"folder:{sharing.FolderName.ToLowerInvariant()}:{permission.User.ToLowerInvariant()}") as SetFolderPermission;
            var captured = permission;
            CalendarSharing.Add(new SharingRow(Describe(permission), permission.IsDelegate ? "Delegate: also gets meeting requests" : null,
                pending is null ? permission.AccessRight : pending.New ?? permission.AccessRight, permission.IsBuiltIn,
                pending is null ? null : pending.New is null ? "Removing" : "Changing", CanEdit,
                role => SetSharing(captured.User, Describe(captured), captured.AccessRight, role),
                () => ToggleSharingRemove(captured.User, Describe(captured), captured.AccessRight)));
        }
        foreach (var add in _pipeline?.Pending.OfType<SetFolderPermission>()
                     .Where(p => p.User.Id == _user.Id && p.Old is null && p.FolderIdentity == sharing.FolderIdentity && shown.Add(p.Person)) ?? [])
        {
            var captured = add;
            CalendarSharing.Add(new SharingRow(add.PersonName, add.Person, add.New ?? "Reviewer", false, "Adding", CanEdit,
                role => SetSharing(captured.Person, captured.PersonName, null, role),
                () => _pipeline!.Unstage(captured)));
        }
    }

    void SetSharing(string person, string name, string? old, string? role)
    {
        if (CanEdit && _sharing is { } sharing)
            _pipeline!.Stage(new SetFolderPermission(Target, sharing.FolderIdentity, sharing.FolderName, person, name, old, role));
    }

    void ToggleSharingRemove(string person, string name, string old)
    {
        if (!CanEdit || _sharing is not { } sharing)
            return;
        if (PendingFor($"folder:{sharing.FolderName.ToLowerInvariant()}:{person.ToLowerInvariant()}") is { } pending)
            _pipeline!.Unstage(pending);
        else
            SetSharing(person, name, old, null);
    }

    /// <summary>New people get "Can view all details", the usual choice; the dropdown changes it.</summary>
    void AddSharing(PickOption option) =>
        SetSharing(_index.User(option.Id)?.Mail is { Length: > 0 } mail ? mail : option.Address ?? option.Id, option.Name, null, "Reviewer");

    // ---- Mobile devices ---------------------------------------------------------------------

    IReadOnlyList<MobileDevice>? _devices;

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    [ObservableProperty]
    public partial bool DevicesLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevicesError))]
    public partial string? DevicesError { get; private set; }

    public bool HasDevicesError => DevicesError is not null;

    public bool HasNoDevices => _devices is { Count: 0 };

    async Task LoadDevicesAsync()
    {
        _devices = await ReadAsync(_devices, c => c.GetMobileDevicesAsync(Address), v => DevicesLoading = v, v => DevicesError = v);
        RebuildDevices();
    }

    void RebuildDevices()
    {
        Devices.Clear();
        foreach (var device in _devices ?? [])
        {
            var access = PendingFor("deviceaccess:" + device.Identity) as SetMobileDevice;
            var action = PendingFor("device:" + device.Identity) as SetMobileDevice;
            var blocked = access is null ? device.IsBlocked : access.Action == DeviceAction.Block;
            var pendingText = string.Join(", ", new[] { access, action }.OfType<SetMobileDevice>().Select(p => p.Action switch
            {
                DeviceAction.Wipe => "Wiping",
                DeviceAction.WipeAccountOnly => "Removing work data",
                DeviceAction.Block => "Blocking",
                DeviceAction.Unblock => "Unblocking",
                _ => "Removing",
            }));
            var captured = device;
            Devices.Add(new DeviceRow(device, blocked, pendingText.Length > 0 ? pendingText : null, action?.Action, CanEdit, a => ActOnDevice(captured, a)));
        }
        OnPropertyChanged(nameof(HasNoDevices));
    }

    /// <summary>Stages the action; the same action again takes it back out.</summary>
    void ActOnDevice(MobileDevice device, DeviceAction action)
    {
        if (!CanEdit)
            return;
        var change = new SetMobileDevice(Target, device.Identity, device.DeviceId, device.Name, action);
        if (PendingFor(change.Key) is SetMobileDevice pending)
        {
            _pipeline!.Unstage(pending);
            // Unblock after a staged Block (or the reverse) is just the undo.
            if (pending.Action == action || change.Key.StartsWith("deviceaccess:", StringComparison.Ordinal))
                return;
        }
        _pipeline!.Stage(change);
    }

    // ---- Deleted items ----------------------------------------------------------------------

    IReadOnlyList<DeletedItem>? _deleted;

    public ObservableCollection<DeletedRow> DeletedItems { get; } = [];

    [ObservableProperty]
    public partial bool DeletedLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeletedError))]
    public partial string? DeletedError { get; private set; }

    public bool HasDeletedError => DeletedError is not null;

    public bool HasNoDeleted => _deleted is { Count: 0 };

    /// <summary>Narrows the deleted items to subjects containing this (searched again as it changes).</summary>
    [ObservableProperty]
    public partial string DeletedSearch { get; set; } = "";

    partial void OnDeletedSearchChanged(string value) => _ = LoadDeletedAsync(again: true);

    async Task LoadDeletedAsync(bool again = false)
    {
        _deleted = await ReadAsync(_deleted, c => c.GetDeletedItemsAsync(Address, DeletedSearch), v => DeletedLoading = v, v => DeletedError = v, again);
        RebuildDeleted();
    }

    void RebuildDeleted()
    {
        DeletedItems.Clear();
        foreach (var item in _deleted ?? [])
        {
            var captured = item;
            DeletedItems.Add(new DeletedRow(item, PendingFor("restore:" + item.EntryId) is not null, CanEdit, () =>
            {
                if (!CanEdit)
                    return;
                if (PendingFor("restore:" + captured.EntryId) is { } pending)
                    _pipeline!.Unstage(pending);
                else
                    _pipeline!.Stage(new RestoreDeletedItem(Target, captured.EntryId, captured.Subject));
            }));
        }
        OnPropertyChanged(nameof(HasNoDeleted));
    }

    // ---- Message trace ----------------------------------------------------------------------

    public static IReadOnlyList<int> TraceDayOptions { get; } = [1, 2, 7, 10];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TraceSent), nameof(TraceReceived))]
    public partial TraceDirection TraceDirection { get; set; } = TraceDirection.Received;

    public bool TraceReceived { get => TraceDirection == TraceDirection.Received; set { if (value) TraceDirection = TraceDirection.Received; } }

    public bool TraceSent { get => TraceDirection == TraceDirection.Sent; set { if (value) TraceDirection = TraceDirection.Sent; } }

    [ObservableProperty]
    public partial int TraceDays { get; set; } = 2;

    partial void OnTraceDirectionChanged(TraceDirection value) => _ = LoadTraceAsync(again: true);

    partial void OnTraceDaysChanged(int value) => _ = LoadTraceAsync(again: true);

    IReadOnlyList<TracedMessage>? _trace;

    public ObservableCollection<TraceRow> Trace { get; } = [];

    [ObservableProperty]
    public partial bool TraceLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTraceError))]
    public partial string? TraceError { get; private set; }

    public bool HasTraceError => TraceError is not null;

    public bool HasNoTrace => _trace is { Count: 0 };

    public string TraceSummary => _trace is { } t ? $"{t.Count:N0} message{(t.Count == 1 ? "" : "s")} {(TraceDirection == TraceDirection.Received ? "to" : "from")} this mailbox in the last {(TraceDays == 1 ? "day" : $"{TraceDays} days")}" : "";

    async Task LoadTraceAsync(bool again = false)
    {
        var direction = TraceDirection;
        _trace = await ReadAsync(_trace, c => c.TraceAsync(Address, direction, TraceDays), v => TraceLoading = v, v => TraceError = v, again);
        Trace.Clear();
        foreach (var message in _trace ?? [])
            Trace.Add(new TraceRow(message, direction, m => Contents!.TraceDetailAsync(m)));
        OnPropertyChanged(nameof(HasNoTrace));
        OnPropertyChanged(nameof(TraceSummary));
    }

    // ---- Mailbox type -----------------------------------------------------------------------

    public bool IsSharedMailbox => _user.Mailbox?.Type == "Shared";

    public string MailboxTypeText => IsSharedMailbox
        ? "Shared mailbox: people get access from Groups & access, and it needs no license up to 50 GB."
        : "User mailbox: this person's own mail.";

    public string ConvertText => IsSharedMailbox ? "Convert to a user mailbox…" : "Convert to a shared mailbox…";

    public string? MailboxTypePendingText => PendingFor("mailboxtype") is { } p ? $"Pending: {p.After.ToLowerInvariant()}" : null;

    public bool HasMailboxTypePending => MailboxTypePendingText is not null;

    public bool CanConvert => CanEdit && _convert is not null;

    [RelayCommand]
    void Convert() => _convert?.Invoke();
}
