using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// A device in the preview pane: what it is, who uses it, how healthy it is, and the same actions
/// the selection panel offers for many (each staged to Pending changes).
/// </summary>
public sealed partial class DevicePreviewViewModel
{
    readonly Action<UserSummary>? _openUser;
    readonly IDirectoryIndex? _index;

    public DevicePreviewViewModel(DeviceSummary device, ChangePipeline? pipeline, Availability canEdit, Func<DateTimeOffset> now,
        IDirectoryIndex? index = null, Action<UserSummary>? openUser = null)
    {
        Device = device;
        _index = index;
        _openUser = openUser;
        CanEdit = pipeline is not null && canEdit.IsAvailable;
        EditReason = pipeline is null ? "This tenant can't be edited" : canEdit.Reason ?? "";
        if (CanEdit)
            Bulk = new BulkEditorViewModel(device.Name, BulkActions.ForDevices(() => [Device], now, one: true), pipeline!);
    }

    public DeviceSummary Device { get; }

    public string Name => Device.Name;

    /// <summary>A FluentIcons name for the kind of device.</summary>
    public string Icon => DeviceList.IconFor(Device);

    public string Detail => DeviceList.Describe(Device);

    public string JoinType => Device.JoinType;

    public string ManagedText => Device.IsManaged ? DeviceFields.ManagedYes : DeviceFields.ManagedNo;
    public BadgeKind ManagedKind => Device.IsManaged ? BadgeKind.Info : BadgeKind.Neutral;

    public string? ComplianceText => Device.Compliant switch { true => DeviceFields.Compliant, false => DeviceFields.NotCompliant, _ => null };
    public BadgeKind ComplianceKind => Device.Compliant == false ? BadgeKind.Danger : BadgeKind.Success;
    public bool HasCompliance => ComplianceText is not null;

    public bool IsDisabled => Device.Enabled == false;
    public bool IsUnencrypted => Device.Encrypted == false;

    public string? UserName => Device.UserName;
    public bool HasUser => Device.UserName is not null;
    public string UserDetail => Device.UserPrincipalName ?? "";

    /// <summary>The primary user opens as their record, when they're in the local copy.</summary>
    public bool CanOpenUser => _openUser is not null && Device.UserId is { } id && _index?.User(id) is not null;

    [RelayCommand]
    void OpenUser()
    {
        if (Device.UserId is { } id && _index?.User(id) is { } user)
            _openUser?.Invoke(user);
    }

    public IReadOnlyList<SummaryLine> Facts =>
    [
        .. new (string, string?)[]
        {
            ("Last Seen", Device.LastSeen is { } seen ? Display.Exact(seen) : "Never"),
            ("Intune Check-In", Device.LastCheckIn is { } checkIn ? Display.Exact(checkIn) : null),
            ("Ownership", Device.Ownership),
            ("Serial Number", Device.Serial),
            ("Encryption", Device.Encrypted switch
            {
                true => Device.KeyBackedUp ? $"{DeviceFields.Encrypted} (BitLocker Key Backed Up)" : DeviceFields.Encrypted,
                false => DeviceFields.NotEncrypted,
                _ => null,
            }),
            ("Storage", Device.StorageTotal is { } total && Device.StorageFree is { } free
                ? $"{free / (double)(1L << 30):0.#} GB free of {total / (double)(1L << 30):0} GB" : null),
            (Device.IsManaged ? "Enrolled" : "Registered", Device.Registered is { } at ? Display.Exact(at) : null),
        }.Where(f => f.Item2 is not null).Select(f => new SummaryLine(f.Item1, f.Item2!)),
    ];

    public bool CanEdit { get; }

    public string EditReason { get; }

    /// <summary>Sync, restart, disable, retire, wipe, delete; null when the admin can't edit.</summary>
    public BulkEditorViewModel? Bulk { get; }
}
