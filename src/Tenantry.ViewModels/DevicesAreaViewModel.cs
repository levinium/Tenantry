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
        new("devices", "All Devices", "Laptop"),
        new("autopilot", "Autopilot", "Rocket"),
        new("compliance", "Compliance Policies", "ShieldCheckmark"),
        new("configuration", "Configuration Profiles", "Settings"),
        new("autopilotprofiles", "Autopilot Profiles", "Rocket"),
        new("enrollment", "Enrollment Settings", "PhoneLaptop"),
        new("categories", "Device Categories", "Tag"),
        new("cleanup", "Clean-Up Rules", "Broom"),
        new("notifications", "Notification Messages", "Mail"),
        new("terms", "Terms and Conditions", "DocumentText"),
        new("scripts", "Windows Scripts", "Code"),
        new("remediations", "Remediations", "Wrench"),
        new("shellscripts", "macOS Scripts", "Code"),
        new("rings", "Update Rings", "ArrowSync"),
        new("featureupdates", "Feature Updates", "ArrowUp"),
        new("qualityupdates", "Expedited Quality Updates", "FlashFlow"),
        new("driverupdates", "Driver Updates", "Cube"),
        new("apps", "Apps", "Apps"),
        new("mam-ios", "iOS App Protection", "Phone"),
        new("mam-android", "Android App Protection", "Phone"),
        new("appconfig", "App Configuration", "AppsSettings"),
        new("mdmappconfig", "App Configuration (Managed Devices)", "AppsSettings"),
        new("filters", "Assignment Filters", "Filter"),
        new("scopetags", "Scope Tags", "Tag"),
        new("intuneroles", "Intune Roles", "PeopleTeam"),
    ];

    /// <summary>The pages in sections, as Intune's admin center groups them.</summary>
    public static IReadOnlyList<SecurityPageGroup> Sections { get; } =
    [
        new("devices", "Devices", "Laptop", Of("devices", "autopilot", "autopilotprofiles", "enrollment", "categories", "cleanup")),
        new("compliance", "Compliance", "ShieldCheckmark", Of("compliance", "notifications", "terms")),
        new("configuration", "Configuration", "Settings", Of("configuration", "scripts", "remediations", "shellscripts")),
        new("updates", "Updates", "ArrowSync", Of("rings", "featureupdates", "qualityupdates", "driverupdates")),
        new("apps", "Apps", "Apps", Of("apps", "mam-ios", "mam-android", "appconfig", "mdmappconfig")),
        new("tenantadmin", "Tenant Administration", "Building", Of("filters", "scopetags", "intuneroles")),
    ];

    static IReadOnlyList<MailSecurityPage> Of(params string[] keys) => [.. keys.Select(k => Pages.Single(p => p.Key == k))];

    public static SecurityPageGroup SectionOf(MailSecurityPage page) => Sections.First(s => s.Pages.Contains(page));

    [ObservableProperty]
    public partial SecurityPageGroup SelectedSection { get; set; } = Sections[0];

    /// <summary>
    /// A new section's first page is chosen only after everything watching the section has heard of
    /// it: the page row must have the new section's pages first. Chosen earlier (in the section's
    /// changed hook), the row still holds the old section's pages and keeps showing them.
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(SelectedSection) && SelectedPage is not null && SelectedSection is not null && !SelectedSection.Pages.Contains(SelectedPage))
            SelectedPage = SelectedSection.Pages[0];
    }

    bool _movingSection;

    /// <summary>A page chosen from anywhere brings its section along; the page row clearing its choice while it swaps keeps the page.</summary>
    partial void OnSelectedPageChanged(MailSecurityPage oldValue, MailSecurityPage newValue)
    {
        if (newValue is null)
        {
            SelectedPage = oldValue is not null && SelectedSection.Pages.Contains(oldValue) ? oldValue : SelectedSection.Pages[0];
            return;
        }
        if (_movingSection || SectionOf(newValue) == SelectedSection)
            return;
        _movingSection = true;
        try
        {
            SelectedSection = SectionOf(newValue);
        }
        finally
        {
            _movingSection = false;
        }
        if (SelectedPage != newValue)
            SelectedPage = newValue;
    }

    public Intune.CompliancePoliciesViewModel Compliance { get; }

    public bool ShowCompliance => SelectedPage.Key == "compliance";

    public Intune.ConfigurationProfilesViewModel Configuration { get; }

    public bool ShowConfiguration => SelectedPage.Key == "configuration";

    /// <summary>The generic Intune pages (apps, updates, scripts, enrollment, tenant administration), by page key.</summary>
    public IReadOnlyDictionary<string, Intune.IntuneObjectsViewModel> ObjectPages { get; }

    public Intune.IntuneObjectsViewModel? CurrentObjects => ObjectPages.GetValueOrDefault(SelectedPage.Key);

    public bool ShowObjects => CurrentObjects is not null;

    /// <param name="openDevice">Opens a device's page, for an Autopilot device that has enrolled.</param>
    public DevicesAreaViewModel(DataListViewModel devices, IAutopilotRecords? autopilot, ChangePipeline? pipeline, Availability canEdit,
        IListStateStore? listState, Func<DateTimeOffset> now, Action<string>? openDevice = null, IIntunePolicyRecords? policies = null, IDirectoryIndex? index = null)
    {
        Devices = devices;
        Compliance = new Intune.CompliancePoliciesViewModel(policies, pipeline, canEdit, index ?? EmptyIndex.Instance);
        Configuration = new Intune.ConfigurationProfilesViewModel(policies as IConfigurationProfileRecords, pipeline, canEdit, index ?? EmptyIndex.Instance, policies);
        ObjectPages = IntuneKinds.All.ToDictionary(k => k.Key, k => new Intune.IntuneObjectsViewModel(k, policies as IIntuneObjectRecords, pipeline, canEdit, index ?? EmptyIndex.Instance, policies));
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
    [NotifyPropertyChangedFor(nameof(ShowDevices), nameof(ShowAutopilot), nameof(ShowCompliance), nameof(ShowConfiguration), nameof(CurrentObjects), nameof(ShowObjects))]
    public partial MailSecurityPage SelectedPage { get; set; }

    public bool ShowDevices => SelectedPage.Key == "devices";
    public bool ShowAutopilot => SelectedPage.Key == "autopilot";

    partial void OnSelectedPageChanged(MailSecurityPage value)
    {
        if (value is null)
            return;
        if (value.Key == "compliance" && !Compliance.IsLoaded)
            _ = Compliance.Refresh();
        if (value.Key == "configuration" && !Configuration.IsLoaded)
            _ = Configuration.Refresh();
        if (ObjectPages.GetValueOrDefault(value.Key) is { IsLoaded: false } objects)
            _ = objects.Refresh();
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
