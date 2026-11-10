using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// A group's lifecycle (G3), read when shown: when a Microsoft 365 group expires (with Renew), its
/// sensitivity label, the licenses it gives its members, and upgrading a distribution list to a
/// Microsoft 365 group. Every change goes to Pending Changes.
/// </summary>
public sealed partial class GroupLifecycleEditor : ObservableObject, IDisposable
{
    readonly GroupSummary _group;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly IGroupLifecycleRecords? _records;
    readonly Func<DateTimeOffset> _now;
    bool _started;

    public GroupLifecycleEditor(GroupSummary group, ChangePipeline? pipeline, Availability canEdit, IGroupLifecycleRecords? records, Func<DateTimeOffset>? now = null)
    {
        _group = group;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _records = records;
        _now = now ?? (() => DateTimeOffset.Now);
        LicensePicker = new PersonPicker("License name", LicenseOptions, o =>
        {
            if (CanEdit)
                _pipeline!.Stage(new SetGroupLicense(Target, _group.Kind, o.Id, o.Name, false, true));
        });
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    /// <summary>Which kinds have something here: Microsoft 365 and security groups (licenses, expiry), and lists (upgrade).</summary>
    public static bool Applies(GroupSummary g) => g.Kind is GroupKind.Microsoft365 or GroupKind.Security or GroupKind.Distribution;

    ChangeTarget Target => GroupAccess.Target(_group);

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable && !_group.SyncedFromOnPremises;

    string EditReason => _pipeline is null ? "This tenant can't be edited"
        : _group.SyncedFromOnPremises ? "Synced from on-premises Active Directory: change it there."
        : _canEdit.Reason ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? LoadError { get; private set; }

    public bool HasError => LoadError is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial GroupLifecycle? Lifecycle { get; private set; }

    public bool IsReady => Lifecycle is not null && !IsLoading;

    public async Task LoadAsync(bool again = false)
    {
        if ((_started && !again) || _records is null)
            return;
        _started = true;
        IsLoading = true;
        LoadError = null;
        try
        {
            Lifecycle = await _records.GetGroupLifecycleAsync(_group);
            Rebuild();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read the group's lifecycle: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    Task Reload() => LoadAsync(again: true);

    // ---- Expiration ------------------------------------------------------------------

    public bool IsUnified => _group.Kind == GroupKind.Microsoft365;

    public string ExpiryText => Lifecycle is not { } l ? ""
        : l.PolicyDays is not { } days ? "Doesn't expire: the organization's expiration policy doesn't cover it."
        : l.ExpiresAt is { } at
            ? $"Expires {at.ToLocalTime():d MMM yyyy} ({Display.Relative(at, _now())}), unless renewed. Groups in use renew themselves; owners are emailed before. Lifetime: {days} days."
            : $"Covered by a {days}-day expiration policy.";

    RenewGroup? PendingRenew => _pipeline?.PendingFor(_group.Id, "renew") as RenewGroup;

    public bool HasPendingRenew => PendingRenew is not null;

    public bool CanRenew => CanEdit && Lifecycle?.PolicyDays is not null;

    [RelayCommand]
    void Renew()
    {
        if (PendingRenew is { } p)
            _pipeline!.Unstage(p);
        else if (CanRenew)
            _pipeline!.Stage(new RenewGroup(Target, _group.Kind, Lifecycle?.ExpiresAt is { } at ? $"Expires {at.ToLocalTime():d MMM yyyy}" : "Expiring"));
    }

    public string LabelText => Lifecycle?.Label is { } label ? label : "None";

    // ---- Licenses ----------------------------------------------------------------------

    /// <summary>Microsoft 365 and security groups can give their members licenses.</summary>
    public bool HasLicenses => _group.Kind is GroupKind.Microsoft365 or GroupKind.Security;

    public PersonPicker LicensePicker { get; }

    public ObservableCollection<PersonRow> Licenses { get; } = [];

    public bool HasNoLicenses => Licenses.Count == 0;

    public string? LicenseStateText => Lifecycle switch
    {
        null => null,
        { Licenses.Count: 0 } => null,
        { LicenseState: not (null or "ProcessingComplete") } => "Microsoft is still giving members these licenses.",
        { MembersWithErrors: > 0 and var n } => $"{n} {(n == 1 ? "member" : "members")} couldn't get them (not enough licenses, or a usage location missing). Their own Licenses tab says why.",
        _ => "Every member has them.",
    };

    IEnumerable<PickOption> LicenseOptions()
    {
        var taken = (Lifecycle?.Licenses.Select(l => l.SkuId) ?? [])
            .Concat(_pipeline?.Pending.OfType<SetGroupLicense>().Where(l => l.Group.Id == _group.Id && l.New).Select(l => l.SkuId) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (Lifecycle?.Catalog ?? []).Where(s => !taken.Contains(s.SkuId))
            .Select(s => new PickOption(s.SkuId, s.Name, $"{s.Available:N0} of {s.Total:N0} available", "license", null, s.Name.ToLowerInvariant()));
    }

    void RebuildLicenses()
    {
        Licenses.Clear();
        if (Lifecycle is not { } l)
            return;
        foreach (var license in l.Licenses)
        {
            var pending = _pipeline?.PendingFor(_group.Id, "license:" + license.SkuId);
            var row = new MemberRow(license.Name, "Given to every direct member", "License", BadgeKind.Neutral, null, null);
            Licenses.Add(new PersonRow(row, pending is not null ? "Removing" : null, CanEdit, CanEdit ? pending is null ? "Take it away from the members (added to Pending Changes)" : "Undo" : EditReason,
                () =>
                {
                    if (pending is not null)
                        _pipeline!.Unstage(pending);
                    else
                        _pipeline!.Stage(new SetGroupLicense(Target, _group.Kind, license.SkuId, license.Name, true, false));
                }, null, showRemove: CanEdit));
        }
        foreach (var add in _pipeline?.Pending.OfType<SetGroupLicense>().Where(x => x.Group.Id == _group.Id && x.New) ?? [])
            Licenses.Add(new PersonRow(new MemberRow(add.SkuName, "Will be given to every direct member", "License", BadgeKind.Neutral, null, null), "Adding", true, "Undo",
                () => _pipeline!.Unstage(add), null));
        OnPropertyChanged(nameof(HasNoLicenses));
    }

    // ---- Upgrade ---------------------------------------------------------------------

    public bool IsList => _group.Kind == GroupKind.Distribution;

    UpgradeList? PendingUpgrade => _pipeline?.PendingFor(_group.Id, "upgrade") as UpgradeList;

    public bool HasPendingUpgrade => PendingUpgrade is not null;

    public bool CanUpgrade => CanEdit && Lifecycle?.CanUpgrade == true;

    public string UpgradeText => Lifecycle?.UpgradeBlocked
        ?? "A Microsoft 365 group keeps this list's address and members, and adds a shared inbox, calendar and files. It can't be turned back into a list.";

    [RelayCommand]
    void Upgrade()
    {
        if (PendingUpgrade is { } p)
            _pipeline!.Unstage(p);
        else if (CanUpgrade)
            _pipeline!.Stage(new UpgradeList(Target, _group.Kind));
    }

    // ---- Following the tray ------------------------------------------------------------

    void Rebuild()
    {
        RebuildLicenses();
        foreach (var name in new[]
                 {
                     nameof(ExpiryText), nameof(HasPendingRenew), nameof(CanRenew), nameof(LabelText), nameof(LicenseStateText), nameof(HasPendingUpgrade),
                     nameof(CanUpgrade), nameof(UpgradeText), nameof(CanEdit),
                 })
            OnPropertyChanged(name);
    }

    void OnPendingChanged(object? sender, EventArgs e) => Rebuild();

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
