using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.MailSecurity;
using Tenantry.ViewModels.Organization;

namespace Tenantry.ViewModels;

/// <summary>
/// The Organization area (Phase 15): what the Microsoft 365 admin center covers beyond people,
/// groups and devices. Service health and Message Center (15a), read live when first shown.
/// </summary>
public sealed partial class OrganizationAreaViewModel : TabViewModel
{
    public static IReadOnlyList<MailSecurityPage> Pages { get; } =
    [
        new("servicehealth", "Service Health", "HeartPulse"),
        new("messagecenter", "Message Center", "Megaphone"),
    ];

    /// <summary>The pages in sections, as the admin center groups them.</summary>
    public static IReadOnlyList<SecurityPageGroup> Sections { get; } =
    [
        new("health", "Health", "HeartPulse", Of("servicehealth", "messagecenter")),
    ];

    static IReadOnlyList<MailSecurityPage> Of(params string[] keys) => [.. keys.Select(k => Pages.Single(p => p.Key == k))];

    public static SecurityPageGroup SectionOf(MailSecurityPage page) => Sections.First(s => s.Pages.Contains(page));

    public OrganizationAreaViewModel(IServiceHealthRecords? health, IListStateStore? listState, Func<DateTimeOffset> now)
    {
        Health = new ServiceHealthViewModel(health, listState, now);
        Messages = new MessageCenterViewModel(health, listState, now);
        SelectedSection = Sections[0];
        SelectedPage = Pages[0];
    }

    public override string Title => "Organization";

    public override bool CanClose => false;

    public ServiceHealthViewModel Health { get; }

    public MessageCenterViewModel Messages { get; }

    /// <summary>The area's lists, so they follow the shared preview-pane setting.</summary>
    public IEnumerable<Lists.DataListViewModel> Lists => [Health.Issues, Messages.Posts];

    [ObservableProperty]
    public partial SecurityPageGroup SelectedSection { get; set; }

    partial void OnSelectedSectionChanged(SecurityPageGroup value)
    {
        if (SelectedPage is not null && !value.Pages.Contains(SelectedPage))
            SelectedPage = value.Pages[0];
    }

    bool _movingSection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHealth), nameof(ShowMessages))]
    public partial MailSecurityPage SelectedPage { get; set; }

    /// <summary>A page chosen from anywhere brings its section along; the page row clearing its choice while it swaps keeps the page.</summary>
    partial void OnSelectedPageChanged(MailSecurityPage oldValue, MailSecurityPage newValue)
    {
        if (newValue is null)
        {
            SelectedPage = oldValue ?? SelectedSection.Pages[0];
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

    public bool ShowHealth => SelectedPage?.Key == "servicehealth";
    public bool ShowMessages => SelectedPage?.Key == "messagecenter";

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
            case "servicehealth":
                _ = Health.LoadAsync();
                break;
            case "messagecenter":
                _ = Messages.Load();
                break;
        }
    }
}
