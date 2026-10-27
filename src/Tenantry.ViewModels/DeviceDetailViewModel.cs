using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>
/// A device's record page (6b): what it is and how healthy, then a tab per area: Overview (hardware
/// and actions), Users, Compliance, Apps, Groups, Defender and Recovery Keys. Each tab reads live
/// from Microsoft 365 when first shown; the device's own details are read once and shared.
/// </summary>
public sealed partial class DeviceDetailViewModel : TabViewModel, IDisposable
{
    readonly IDevicePageRecords? _pages;
    readonly ChangePipeline? _pipeline;
    Task<DeviceLive>? _live;

    public DeviceDetailViewModel(DeviceSummary device, ChangePipeline? pipeline, Availability canEdit, Func<DateTimeOffset> now,
        IDevicePageRecords? pages = null, IDeviceRecords? keys = null, IDirectoryIndex? index = null,
        Action<UserSummary>? openUser = null, Action<GroupSummary>? openGroup = null)
    {
        Device = device;
        _pages = pages;
        _pipeline = pipeline;
        CanEdit = pipeline is not null && canEdit.IsAvailable;
        EditReason = pipeline is null ? "This tenant can't be edited" : canEdit.Reason ?? "";
        index ??= EmptyIndex.Instance;
        Overview = new DeviceOverviewSection(this, CanEdit ? new Editing.BulkEditorViewModel(device.Name, BulkActions.ForDevices(() => [Device], now, one: true), pipeline!) : null);
        Sections =
        [
            Overview,
            new DeviceUsersSection(this, index, openUser),
            new DeviceComplianceSection(this),
            new DeviceAppsSection(this),
            new DeviceGroupsSection(this, index, openGroup),
            new DeviceDefenderSection(this),
            new DeviceKeysSection(this, keys),
        ];
        SelectedSection = Sections[0];
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public DeviceSummary Device { get; private set; }

    public override string Title => Device.Name;

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

    public string LastSeenText => Device.LastSeen is { } seen ? $"Last seen {Display.Exact(seen)}" : "Never seen";

    public bool CanEdit { get; }
    public string EditReason { get; }

    internal ChangePipeline? Pipeline => _pipeline;
    internal IDevicePageRecords? Pages => _pages;

    public DeviceOverviewSection Overview { get; }

    public IReadOnlyList<RecordSection> Sections { get; }

    [ObservableProperty]
    public partial RecordSection? SelectedSection { get; set; }

    /// <summary>Each tab reads what it shows the first time it's opened.</summary>
    partial void OnSelectedSectionChanged(RecordSection? value) => _ = value?.LoadAsync();

    /// <summary>The device's own details, read once for the tabs that show them.</summary>
    internal Task<DeviceLive> LiveAsync() => _live ??= _pages is null
        ? Task.FromResult(new DeviceLive(null, [], [], []))
        : _pages.GetDeviceAsync(Device);

    /// <summary>What's waiting in Pending changes for this device.</summary>
    public string? PendingText
    {
        get
        {
            var labels = _pipeline?.Pending.Where(p => p.Target.Id == Device.ObjectId || p.Target.Id == Device.ManagedId
                    || p is SetGroupMembership m && m.Person.Id == Device.ObjectId)
                .Select(p => p is SetGroupMembership m ? $"{(m.New ? "join" : "leave")} {m.Group.Name}" : p.Label.ToLowerInvariant())
                .ToArray() ?? [];
            return labels.Length == 0 ? null : "Pending: " + string.Join(", ", labels);
        }
    }

    public bool HasPending => PendingText is not null;

    void OnPendingChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(HasPending));
    }

    /// <summary>Shows the device as the local copy has it now (after a sync or applied changes).</summary>
    public void Update(DeviceSummary device)
    {
        Device = device;
        foreach (var name in new[]
                 {
                     nameof(Device), nameof(Title), nameof(Icon), nameof(Detail), nameof(JoinType), nameof(ManagedText), nameof(ManagedKind),
                     nameof(ComplianceText), nameof(ComplianceKind), nameof(HasCompliance), nameof(IsDisabled), nameof(IsUnencrypted), nameof(LastSeenText),
                 })
            OnPropertyChanged(name);
        Overview.Refresh();
    }

    [RelayCommand]
    void OpenInAdminCenter() => Platform.OpenUrl(Device.ManagedId is { } managed
        ? $"https://intune.microsoft.com/#view/Microsoft_Intune_Devices/DeviceSettingsMenuBlade/~/overview/mdmDeviceId/{managed}"
        : $"https://entra.microsoft.com/#view/Microsoft_AAD_Devices/DeviceDetailsMenuBlade/~/Properties/objectId/{Device.ObjectId}");

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
        foreach (var section in Sections.OfType<IDisposable>())
            section.Dispose();
    }
}
