using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.ViewModels;

/// <summary>
/// The Devices area: one tab with a page per list. Devices come from the local copy; Autopilot is
/// read live from Intune when first shown (and on Refresh).
/// </summary>
public sealed partial class DevicesAreaViewModel : TabViewModel
{
    readonly IAutopilotRecords? _autopilot;
    bool _autopilotRead;

    public static IReadOnlyList<MailSecurityPage> Pages { get; } =
    [
        new("devices", "Devices", "Laptop"),
        new("autopilot", "Autopilot", "Rocket"),
    ];

    /// <param name="openDevice">Opens a device's page, for an Autopilot device that has enrolled.</param>
    public DevicesAreaViewModel(DataListViewModel devices, IAutopilotRecords? autopilot, ChangePipeline? pipeline, Availability canEdit,
        IListStateStore? listState, Func<DateTimeOffset> now, Action<string>? openDevice = null)
    {
        Devices = devices;
        _autopilot = autopilot;
        Autopilot = DataListViewModel.Create(
            AutopilotList.Definition(d => new Records.AutopilotPreviewViewModel(d, pipeline, canEdit, openDevice)),
            LoadAutopilotAsync, row => Autopilot!.PreviewRow(row), listState, now);
        Autopilot.HasRecordPages = false;
        Autopilot.PrimaryAction = null;
        Autopilot.EmptyText = "No devices are registered with Autopilot.";
        Autopilot.SelectionPanel = (rows, uncheck) => Records.SelectionPanelViewModel.ForAutopilot(rows, uncheck, pipeline, canEdit);
        if (pipeline is not null)
            pipeline.Applied += (_, applied) =>
            {
                if (_autopilotRead && applied.Any(c => c is SetAutopilotGroupTag or DeleteAutopilotDevice))
                    _ = Autopilot.ReloadAsync(busyText: "Reading Autopilot again…");
            };
        SelectedPage = Pages[0];
    }

    public override string Title => "Devices";

    public override bool CanClose => false;

    public DataListViewModel Devices { get; }

    public DataListViewModel Autopilot { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDevices), nameof(ShowAutopilot))]
    public partial MailSecurityPage SelectedPage { get; set; }

    public bool ShowDevices => SelectedPage.Key == "devices";
    public bool ShowAutopilot => SelectedPage.Key == "autopilot";

    partial void OnSelectedPageChanged(MailSecurityPage value)
    {
        if (value.Key == "autopilot" && !_autopilotRead)
        {
            _autopilotRead = true;
            _ = Autopilot.LoadAsync();
        }
    }

    /// <summary>Why Autopilot can't be listed (no Intune role or permission), shown above the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutopilotNote))]
    public partial string? AutopilotNote { get; private set; }

    public bool HasAutopilotNote => AutopilotNote is not null;

    async Task<IReadOnlyList<AutopilotDevice>> LoadAutopilotAsync(CancellationToken cancellationToken)
    {
        if (_autopilot is null)
        {
            AutopilotNote = "Autopilot isn't available for this tenant.";
            return [];
        }
        try
        {
            AutopilotNote = null;
            var read = await _autopilot.GetAutopilotDevicesAsync(cancellationToken);
            AutopilotNote = read.Unavailable;
            return read.Value ?? [];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // As Deleted Sites does: the note says why, rather than an empty list saying there's nothing.
            AutopilotNote = $"Couldn't read Autopilot devices: {Friendly.Message(e)}";
            return [];
        }
    }

    [RelayCommand]
    Task RefreshAutopilot() => Autopilot.ReloadAsync(busyText: "Reading Autopilot devices from Intune…");
}
