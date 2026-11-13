using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.MailSecurity;
using Tenantry.ViewModels.Organization;

namespace Tenantry.ViewModels;

/// <summary>
/// The Health area: Microsoft's service health (incidents and advisories) and Message Center, on
/// their own in the sidebar so they're one click away. Each page is read live when first shown.
/// </summary>
public sealed partial class HealthAreaViewModel : TabViewModel
{
    public static IReadOnlyList<MailSecurityPage> Pages { get; } =
    [
        new("servicehealth", "Service Health", "HeartPulse"),
        new("messagecenter", "Message Center", "Megaphone"),
    ];

    public HealthAreaViewModel(IServiceHealthRecords? health, IListStateStore? listState, Func<DateTimeOffset> now)
    {
        Health = new ServiceHealthViewModel(health, listState, now);
        Messages = new MessageCenterViewModel(health, listState, now);
        SelectedPage = Pages[0];
    }

    public override string Title => "Health";

    public override bool CanClose => false;

    public ServiceHealthViewModel Health { get; }

    public MessageCenterViewModel Messages { get; }

    /// <summary>The area's lists, so they follow the shared preview-pane setting.</summary>
    public IEnumerable<Lists.DataListViewModel> Lists => [Health.Issues, Messages.Posts];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHealth), nameof(ShowMessages))]
    public partial MailSecurityPage SelectedPage { get; set; }

    partial void OnSelectedPageChanged(MailSecurityPage oldValue, MailSecurityPage newValue)
    {
        // The page row clears its choice while it rebuilds: keep the page that was showing.
        if (newValue is null)
        {
            SelectedPage = oldValue ?? Pages[0];
            return;
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
        if (page.Key == "servicehealth")
            _ = Health.LoadAsync();
        else
            _ = Messages.Load();
    }
}
