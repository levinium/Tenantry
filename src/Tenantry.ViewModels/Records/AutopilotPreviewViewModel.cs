using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>An Autopilot device in the preview pane: what it is, its profile and enrollment, and its actions.</summary>
public sealed partial class AutopilotPreviewViewModel
{
    readonly Action<string>? _openManaged;

    /// <param name="openManaged">Opens the Intune device it enrolled as (by Intune ID), when it has.</param>
    public AutopilotPreviewViewModel(AutopilotDevice device, ChangePipeline? pipeline, Availability canEdit, Action<string>? openManaged = null)
    {
        Device = device;
        _openManaged = openManaged;
        CanEdit = pipeline is not null && canEdit.IsAvailable;
        EditReason = pipeline is null ? "This tenant can't be edited" : canEdit.Reason ?? "";
        if (CanEdit)
            Actions = new BulkEditorViewModel(device.Serial, BulkActions.ForAutopilot(() => [Device]), pipeline!);
    }

    public AutopilotDevice Device { get; }

    public string Serial => Device.Serial;
    public string Model => string.Join(' ', new[] { Device.Manufacturer, Device.Model }.Where(s => !string.IsNullOrEmpty(s)).Distinct());
    public string ProfileText => AutopilotList.ProfileBadge(Device).Text;
    public BadgeKind ProfileKind => AutopilotList.ProfileBadge(Device).Kind;
    public string EnrollmentText => AutopilotList.EnrollmentBadge(Device).Text;
    public BadgeKind EnrollmentKind => AutopilotList.EnrollmentBadge(Device).Kind;

    public IReadOnlyList<SummaryLine> Facts =>
    [
        .. new (string, string?)[]
        {
            ("Group Tag", Device.GroupTag ?? "None"),
            ("Profile", Device.ProfileName),
            ("Last Contacted", Device.LastContacted is { } at ? Display.Exact(at) : "Never"),
            ("Assigned User", Device.AssignedUser),
            ("Purchase Order", Device.PurchaseOrder),
        }.Where(f => f.Item2 is not null).Select(f => new SummaryLine(f.Item1, f.Item2!)),
    ];

    public bool CanOpenManaged => Device.ManagedDeviceId is not null && _openManaged is not null;

    [RelayCommand]
    void OpenManaged()
    {
        if (Device.ManagedDeviceId is { } id)
            _openManaged?.Invoke(id);
    }

    public bool CanEdit { get; }
    public string EditReason { get; }

    /// <summary>Set the group tag, remove from Autopilot; null when the admin can't edit.</summary>
    public BulkEditorViewModel? Actions { get; }
}
