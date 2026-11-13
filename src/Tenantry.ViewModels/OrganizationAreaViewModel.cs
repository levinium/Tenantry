using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.MailSecurity;
using Tenantry.ViewModels.Organization;

namespace Tenantry.ViewModels;

/// <summary>
/// The Organization area (Phase 15): what the Microsoft 365, Entra and Teams admin centers cover
/// beyond people, groups and devices: billing and usage (15b),
/// organization settings (15c), identity settings (15d), Teams (15e) and the rest (15f). Each page
/// is read live when first shown.
/// </summary>
public sealed partial class OrganizationAreaViewModel : TabViewModel
{
    public static IReadOnlyList<MailSecurityPage> Pages { get; } =
    [
        new("licenses", "Licenses", "Key"),
        new("subscriptions", "Subscriptions", "Receipt"),
        new("usage", "Usage Reports", "DataBarVertical"),
        new("profile", "Organization Profile", "Building"),
        new("apps", "Apps and Services", "Apps"),
        new("spsettings", "SharePoint and OneDrive", "Globe"),
        new("reportprivacy", "Report Privacy", "EyeOff"),
        new("rooms", "Rooms", "ConferenceRoom"),
        new("iemode", "Edge IE Mode", "Globe"),
        new("autopatch", "Windows Autopatch", "ArrowSync"),
        new("usersettings", "User Settings", "PersonSettings"),
        new("crosstenant", "Cross-Tenant Access", "BuildingMultiple"),
        new("groupsettings", "Group Settings", "PeopleSettings"),
        new("adminunits", "Administrative Units", "Organization"),
        new("domains", "Domains", "GlobeAdd"),
        new("dirsync", "Entra Connect", "ArrowSync"),
        new("attributes", "Custom Attributes", "Tag"),
        new("deletedusers", "Deleted Users", "ArrowUndo"),
        new("consent", "App Consent", "ShieldKeyhole"),
        new("signinpolicies", "Sign-In Policies", "LockClosed"),
        new("recommendations", "Recommendations", "Lightbulb"),
        new("governance", "Identity Governance", "PeopleCheckmark"),
        new("teams", "Teams", "PeopleTeam"),
        new("teamsapps", "Teams Apps", "Apps"),
        new("teamsmeetings", "Meetings and Events", "CalendarClock"),
        new("teamsmessaging", "Messaging and Channels", "Chat"),
        new("teamscalling", "Calling", "Call"),
        new("teamsorg", "Teams Settings", "Settings"),
    ];

    /// <summary>The pages in sections, as the admin centers group them.</summary>
    public static IReadOnlyList<SecurityPageGroup> Sections { get; } =
    [
        new("billing", "Billing and Usage", "Receipt", Of("licenses", "subscriptions", "usage")),
        new("settings", "Settings", "Settings", Of("profile", "apps", "spsettings", "reportprivacy", "rooms", "iemode", "autopatch")),
        new("identity", "Identity", "PersonKey", Of("usersettings", "crosstenant", "groupsettings", "adminunits", "domains", "dirsync", "attributes", "deletedusers",
            "consent", "signinpolicies", "recommendations", "governance")),
        new("teamsadmin", "Teams", "PeopleTeam", Of("teams", "teamsapps", "teamsmeetings", "teamsmessaging", "teamscalling", "teamsorg")),
    ];

    static IReadOnlyList<MailSecurityPage> Of(params string[] keys) => [.. keys.Select(k => Pages.Single(p => p.Key == k))];

    public static SecurityPageGroup SectionOf(MailSecurityPage page) => Sections.First(s => s.Pages.Contains(page));

    public OrganizationAreaViewModel(IListStateStore? listState, Func<DateTimeOffset> now,
        IOrgObjectRecords? org = null, ChangePipeline? pipeline = null, Availability? canEdit = null, IUsageReportRecords? usage = null)
    {
        Usage = new UsageReportsViewModel(usage, listState, now);
        var editing = canEdit ?? Availability.No("Tenantry can't change organization settings here.");
        OrgPages = OrgKinds.Pages.ToDictionary(p => p.Key, p => new OrgPageViewModel([.. p.Value.Select(k => new OrgObjectsViewModel(k, org, pipeline, editing))]));
        SelectedSection = Sections[0];
        SelectedPage = Pages[0];
    }

    public override string Title => "Organization";

    public override bool CanClose => false;

    public UsageReportsViewModel Usage { get; }

    /// <summary>The card pages (billing, settings, identity, Teams), by page key.</summary>
    public IReadOnlyDictionary<string, OrgPageViewModel> OrgPages { get; }

    public OrgPageViewModel? CurrentOrgPage => SelectedPage is null ? null : OrgPages.GetValueOrDefault(SelectedPage.Key);

    [ObservableProperty]
    public partial SecurityPageGroup SelectedSection { get; set; }

    /// <summary>
    /// A new section's first page is chosen only after everything watching the section has heard of
    /// it: the page row must have the new section's pages first. Chosen earlier, the row (still
    /// holding the old pages) picks its own first page, which pulls the section back.
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(SelectedSection) && SelectedPage is not null && SelectedSection is not null && !SelectedSection.Pages.Contains(SelectedPage))
            SelectedPage = SelectedSection.Pages[0];
    }

    bool _movingSection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUsage), nameof(CurrentOrgPage), nameof(ShowOrgPage))]
    public partial MailSecurityPage SelectedPage { get; set; }

    /// <summary>A page chosen from anywhere brings its section along; the page row clearing its choice while it swaps keeps the page.</summary>
    partial void OnSelectedPageChanged(MailSecurityPage oldValue, MailSecurityPage newValue)
    {
        if (newValue is null)
        {
            // The page row clears its choice while its pages swap for another section's: keep a page
            // of the section now chosen, never the old section's (which would show the wrong page).
            SelectedPage = oldValue is not null && SelectedSection.Pages.Contains(oldValue) ? oldValue : SelectedSection.Pages[0];
            return;
        }
        if (!_movingSection && SectionOf(newValue) != SelectedSection)
        {
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
        if (_shown)
            LoadPage(newValue);
    }

    public bool ShowUsage => SelectedPage?.Key == "usage";
    public bool ShowOrgPage => CurrentOrgPage is not null;

    bool _shown;

    /// <summary>The area came into view: its page is read now (not at sign-in, so the start isn't slowed).</summary>
    public void Shown()
    {
        _shown = true;
        LoadPage(SelectedPage);
    }

    void LoadPage(MailSecurityPage page)
    {
        switch (page.Key)
        {
            case "usage":
                _ = Usage.Load();
                break;
            default:
                OrgPages.GetValueOrDefault(page.Key)?.Shown();
                break;
        }
    }
}
