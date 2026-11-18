using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.ViewModels.MailSecurity;
using Tenantry.ViewModels.Organization;

namespace Tenantry.ViewModels;

/// <summary>
/// The Apps area: app registrations, enterprise apps, deleted apps, app consent and risky apps on
/// one row of pages, as Entra keeps applications apart from roles. The lists and their reads stay
/// with <see cref="SecurityAreaViewModel"/> (one read of the apps serves the lists and the app
/// pages, and its local copy and refreshes are kept there); App Consent is Organization's page.
/// </summary>
public sealed partial class AppsAreaViewModel : TabViewModel
{
    public static IReadOnlyList<MailSecurityPage> Pages { get; } =
    [
        new("appregs", "App Registrations", "AppFolder"),
        new("enterprise", "Enterprise Apps", "Apps"),
        new("deletedapps", "Deleted Apps", "Delete"),
        new("consent", "App Consent", "ShieldKeyhole"),
        new("riskyapps", "Risky Apps", "AppsListDetail"),
    ];

    public AppsAreaViewModel(SecurityAreaViewModel security, OrgPageViewModel? consent)
    {
        Security = security;
        Consent = consent;
        security.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SecurityAreaViewModel.RiskyAppsNote))
                OnPropertyChanged(nameof(RiskyAppsNote));
        };
        SelectedPage = Pages[0];
    }

    public override string Title => "Apps";

    public override bool CanClose => false;

    /// <summary>Where the app lists, their notes and Refresh live.</summary>
    public SecurityAreaViewModel Security { get; }

    /// <summary>App Consent: whether people can ask an admin to approve an app, and the apps they asked for.</summary>
    public OrgPageViewModel? Consent { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRegistrations), nameof(ShowEnterpriseApps), nameof(ShowDeletedApps), nameof(ShowConsent), nameof(ShowRiskyApps))]
    public partial MailSecurityPage SelectedPage { get; set; }

    public bool ShowRegistrations => SelectedPage.Key == "appregs";
    public bool ShowEnterpriseApps => SelectedPage.Key == "enterprise";
    public bool ShowDeletedApps => SelectedPage.Key == "deletedapps";
    public bool ShowConsent => SelectedPage.Key == "consent" && Consent is not null;
    public bool ShowRiskyApps => SelectedPage.Key == "riskyapps";

    /// <summary>Why risky apps can't be listed (no Entra ID P2, a missing permission), above the list.</summary>
    public string? RiskyAppsNote => Security.RiskyAppsNote;

    /// <summary>The page row clearing its choice while it redraws keeps the page.</summary>
    partial void OnSelectedPageChanged(MailSecurityPage oldValue, MailSecurityPage newValue)
    {
        if (newValue is null)
        {
            SelectedPage = oldValue ?? Pages[0];
            return;
        }
        // Nothing is read until the area is first shown (it's made when the tenant opens).
        if (_visible)
            Shown();
    }

    bool _visible;

    /// <summary>The area was chosen in the sidebar: read what the page shows, the first time it shows.</summary>
    public void Shown()
    {
        _visible = true;
        if (ShowConsent)
            Consent!.Shown();
        else
            Security.AppPageShown(SelectedPage.Key);
    }

    /// <summary>Opens a page (from elsewhere: a report, a check); it's read once the area shows.</summary>
    public void Show(string pageKey) => SelectedPage = Pages.Single(p => p.Key == pageKey);
}
