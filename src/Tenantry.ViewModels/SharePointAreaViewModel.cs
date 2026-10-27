using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.ViewModels;

/// <summary>
/// The SharePoint &amp; OneDrive area: one tab with two pages over the same local copy of every
/// site collection: Sites (team, communication, channel and system sites) and OneDrives.
/// </summary>
public sealed partial class SharePointAreaViewModel : TabViewModel
{
    public static IReadOnlyList<MailSecurityPage> Pages { get; } =
    [
        new("sites", "Sites", "Globe"),
        new("onedrives", "OneDrives", "Cloud"),
    ];

    public SharePointAreaViewModel(DataListViewModel sites, DataListViewModel oneDrives)
    {
        Sites = sites;
        OneDrives = oneDrives;
        SelectedPage = Pages[0];
    }

    public override string Title => "SharePoint & OneDrive";

    public override bool CanClose => false;

    public DataListViewModel Sites { get; }

    public DataListViewModel OneDrives { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSites), nameof(ShowOneDrives))]
    public partial MailSecurityPage SelectedPage { get; set; }

    public bool ShowSites => SelectedPage.Key == "sites";
    public bool ShowOneDrives => SelectedPage.Key == "onedrives";
}
