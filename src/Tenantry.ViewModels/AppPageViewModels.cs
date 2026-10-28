using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>
/// An app's page (8c): a header with its flags and links, then tabs of cards. App registrations and
/// enterprise apps share it; each kind supplies its own tabs. Details are read live once, when a tab
/// that needs them is first shown.
/// </summary>
public abstract partial class AppPageViewModel : TabViewModel
{
    protected AppPageViewModel(IAppRecords? records, IDirectoryIndex? index, Action<UserSummary>? openUser, Func<DateTimeOffset> now)
    {
        Records = records;
        Index = index ?? EmptyIndex.Instance;
        OpenUserAction = openUser;
        Now = now;
    }

    protected IAppRecords? Records { get; }
    protected IDirectoryIndex Index { get; }
    protected Action<UserSummary>? OpenUserAction { get; }
    protected Func<DateTimeOffset> Now { get; }

    public abstract string Subtitle { get; }

    /// <summary>The page's icon: an app registration's or an enterprise app's.</summary>
    public abstract string Icon { get; }

    public abstract IReadOnlyList<HealthChip> Badges { get; }

    public IReadOnlyList<RecordSection> Sections { get; protected init; } = [];

    [ObservableProperty]
    public partial RecordSection? SelectedSection { get; set; }

    partial void OnSelectedSectionChanged(RecordSection? value) => _ = value?.LoadAsync();

    /// <summary>"Open Enterprise App" or "Open App Registration", when this app has the other half here.</summary>
    public abstract string? PartnerText { get; }
    public bool HasPartner => PartnerText is not null;

    [RelayCommand]
    protected abstract void OpenPartner();

    [RelayCommand]
    protected abstract void OpenInAdminCenter();

    /// <summary>Shows the app as read again; returns false when it's gone.</summary>
    public abstract bool Update(AppDirectory directory);

    protected void Refresh(params string[] names)
    {
        foreach (var name in new[] { nameof(Title), nameof(Subtitle), nameof(Badges), nameof(PartnerText), nameof(HasPartner) }.Concat(names))
            OnPropertyChanged(name);
        foreach (var section in Sections.OfType<AppSection>())
            section.Rebuild();
    }

    protected static string Day(DateTimeOffset? when) => when is { } w ? w.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "Unknown";

    protected AppRow OwnerRow(AppOwner o) => new()
    {
        Title = o.Name,
        Detail = o.Detail,
        OpenAction = Index.User(o.Id) is { } u && OpenUserAction is { } open ? () => open(u) : null,
    };

    /// <summary>Delegated (on behalf of whoever signs in) or Application (on its own, with no one signed in).</summary>
    protected static HealthChip KindChip(bool isApplication) => isApplication
        ? new HealthChip("Application", BadgeKind.Warning, "The app acts on its own, with no one signed in")
        : new HealthChip("Delegated", BadgeKind.Neutral, "The app acts for whoever signs in, within what they can do");
}

/// <summary>An app registration's page: Overview, Secrets &amp; Certificates, API Permissions, Owners.</summary>
public sealed partial class AppRegistrationPageViewModel : AppPageViewModel
{
    readonly Action<EnterpriseApp>? _openEnterprise;
    AppDirectory _directory;
    Task<AppRegistrationDetail>? _detail;

    public AppRegistrationPageViewModel(AppRegistration app, AppDirectory directory, IAppRecords? records, IDirectoryIndex? index, Action<UserSummary>? openUser,
        Action<EnterpriseApp>? openEnterprise, Func<DateTimeOffset> now) : base(records, index, openUser, now)
    {
        App = app;
        _directory = directory;
        _openEnterprise = openEnterprise;
        Sections =
        [
            new AppSection("overview", "Overview", "Home", _ => Task.FromResult(OverviewCards())),
            new AppSection("credentials", "Secrets & Certificates", "Key", _ => Task.FromResult(CredentialCards())),
            new AppSection("permissions", "API Permissions", "ShieldKeyhole", PermissionCardsAsync, "Reading which permissions it has and who consented…"),
            new AppSection("owners", "Owners", "People", _ => Task.FromResult<IReadOnlyList<AppCard>>(
                [new AppCard("Owners", "Owners can change the app's settings and credentials in Entra.", Rows: [.. App.Owners.Select(OwnerRow)], EmptyText: "Nobody owns this app.")])),
        ];
        SelectedSection = Sections[0];
    }

    public AppRegistration App { get; private set; }

    public override string Title => App.Name;
    public override string Subtitle => $"Application (client) ID {App.AppId}";
    public override string Icon => "AppFolder";

    EnterpriseApp? Enterprise => _directory.EnterpriseFor(App.AppId);

    public override IReadOnlyList<HealthChip> Badges
    {
        get
        {
            var state = App.CredentialState(Now());
            var chips = new List<HealthChip>();
            if (AppList.CredentialBadge(state) is { Text.Length: > 0 } badge)
                chips.Add(new HealthChip(state == AppFields.Current ? "Credentials Current" : $"Credentials {badge.Text}", badge.Kind, "Its client secrets and certificates"));
            if (App.Audience != "AzureADMyOrg")
                chips.Add(new HealthChip(AppRegistration.AudienceText(App.Audience), BadgeKind.Info, "Who can sign in to it"));
            if (App.Owners.Count == 0)
                chips.Add(new HealthChip("No Owners", BadgeKind.Neutral, "Only admins can manage it"));
            return chips;
        }
    }

    public override string? PartnerText => Enterprise is null ? null : "Open Enterprise App";

    protected override void OpenPartner()
    {
        if (Enterprise is { } e)
            _openEnterprise?.Invoke(e);
    }

    protected override void OpenInAdminCenter() => Platform.OpenUrl(
        $"https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/{App.AppId}");

    public override bool Update(AppDirectory directory)
    {
        if (directory.Registrations.FirstOrDefault(r => r.Id == App.Id) is not { } app)
            return false;
        App = app;
        _directory = directory;
        _detail = null;
        Refresh(nameof(App));
        return true;
    }

    IReadOnlyList<AppCard> OverviewCards()
    {
        IReadOnlyList<SummaryLine> facts =
        [
            new("Application ID", App.AppId),
            new("Object ID", App.Id),
            new("Who Can Sign In", AppRegistration.AudienceText(App.Audience)),
            new("Created", Day(App.Created)),
            .. App.PublisherDomain is { } domain ? [new SummaryLine("Publisher Domain", domain)] : Array.Empty<SummaryLine>(),
            new("Enterprise App", Enterprise is { } e ? e.IsEnabled ? "In this organization, sign-in on" : "In this organization, sign-in off" : "None (nobody here has used it yet)"),
            .. App.Notes is { Length: > 0 } notes ? [new SummaryLine("Notes", notes)] : Array.Empty<SummaryLine>(),
        ];
        return
        [
            new AppCard("Details", Facts: facts),
            new AppCard("Redirect URIs", "Where Entra sends people back to after they sign in.",
                Rows: [.. App.RedirectUris.GroupBy(r => r.Platform).Select(g => new AppRow { Title = g.Key, Code = string.Join(Environment.NewLine, g.Select(r => r.Uri)) })],
                EmptyText: "None: the app doesn't sign people in through a browser."),
        ];
    }

    IReadOnlyList<AppCard> CredentialCards()
    {
        var now = Now();
        AppRow Row(AppCredential c)
        {
            var status = c.Status(now);
            return new AppRow
            {
                Title = c.Name ?? (c.IsSecret ? "A secret" : "A certificate"),
                Detail = string.Join(" · ", new[]
                {
                    c.IsSecret && c.Hint is { } hint ? $"Starts with {hint}…" : null,
                    $"Added {Day(c.Start)}",
                    c.End is { } end ? $"{(end < now ? "Expired" : "Expires")} {Day(end)}" : "Never expires",
                }.OfType<string>()),
                Code = c.IsSecret ? null : c.Hint is { } thumb ? $"Thumbprint {thumb}" : null,
                Badges = status switch
                {
                    AppFields.Expired => [new HealthChip(AppFields.Expired, BadgeKind.Danger, "Sign-ins with it fail")],
                    AppFields.Soon => [new HealthChip(AppFields.Soon, BadgeKind.Warning, "Within 30 days")],
                    _ => [new HealthChip("Current", BadgeKind.Success, "It works")],
                },
            };
        }
        var secrets = App.Credentials.Where(c => c.IsSecret).OrderBy(c => c.End).Select(Row).ToArray();
        var certificates = App.Credentials.Where(c => !c.IsSecret).OrderBy(c => c.End).Select(Row).ToArray();
        return
        [
            new AppCard("Client Secrets", "Passwords the app signs in with. Entra shows a secret's value only once, when it's made.", Rows: secrets, EmptyText: "No client secrets."),
            new AppCard("Certificates", "Safer than secrets: the app proves who it is with a private key only it holds.", Rows: certificates, EmptyText: "No certificates."),
        ];
    }

    async Task<IReadOnlyList<AppCard>> PermissionCardsAsync(CancellationToken cancellationToken)
    {
        if (App.Requested.Count == 0)
            return [new AppCard("API Permissions", EmptyText: "It doesn't ask for any permissions.")];
        if (Records is null)
            return [new AppCard("API Permissions", EmptyText: "Permissions can't be read for this tenant.")];
        var read = _detail ??= Records.GetRegistrationDetailAsync(App, cancellationToken);
        AppRegistrationDetail detail;
        try
        {
            detail = await read;
        }
        catch
        {
            if (ReferenceEquals(_detail, read))
                _detail = null;
            throw;
        }
        return
        [
            .. detail.Permissions.GroupBy(p => p.Resource).OrderBy(g => g.Key, StringComparer.CurrentCulture).Select(g => new AppCard(g.Key,
                Rows: [.. g.OrderBy(p => p.IsApplication).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => new AppRow
                {
                    Title = p.Name,
                    Detail = p.Description,
                    Badges =
                    [
                        KindChip(p.IsApplication),
                        p.Consented switch
                        {
                            true => new HealthChip("Admin Consent Given", BadgeKind.Success, "An admin has approved it for everyone"),
                            false => new HealthChip("Not Consented", BadgeKind.Warning, p.IsApplication ? "It won't work until an admin approves it" : "Each person is asked, unless an admin approves it for everyone"),
                            null => new HealthChip("Consent Unknown", BadgeKind.Neutral, "Couldn't check"),
                        },
                    ],
                })])),
        ];
    }
}

/// <summary>An enterprise app's page: Overview, Users &amp; Groups, Permissions, Owners.</summary>
public sealed partial class EnterpriseAppPageViewModel : AppPageViewModel
{
    readonly Action<AppRegistration>? _openRegistration;
    readonly Action<GroupSummary>? _openGroup;
    AppDirectory _directory;
    Task<EnterpriseAppDetail>? _detail;
    DateTimeOffset? _lastSignIn;
    bool _lastSignInRead;

    public EnterpriseAppPageViewModel(EnterpriseApp app, AppDirectory directory, IAppRecords? records, IDirectoryIndex? index, Action<UserSummary>? openUser,
        Action<GroupSummary>? openGroup, Action<AppRegistration>? openRegistration, Func<DateTimeOffset> now) : base(records, index, openUser, now)
    {
        App = app;
        _directory = directory;
        _openRegistration = openRegistration;
        _openGroup = openGroup;
        Sections =
        [
            new AppSection("overview", "Overview", "Home", _ => Task.FromResult(OverviewCards())),
            new AppSection("assignments", "Users & Groups", "PeopleCheckmark", AssignmentCardsAsync, "Reading who has access…"),
            new AppSection("permissions", "Permissions", "ShieldKeyhole", PermissionCardsAsync, "Reading what it's been allowed to do…"),
            new AppSection("owners", "Owners", "People", async ct => [new AppCard("Owners", "Owners can change this app's settings in Entra.",
                Rows: [.. (await DetailAsync(ct))?.Owners.Select(OwnerRow) ?? []], EmptyText: "Nobody owns it.")], "Reading its owners…"),
        ];
        SelectedSection = Sections[0];
        _ = ReadLastSignInAsync();
    }

    public EnterpriseApp App { get; private set; }

    public override string Title => App.Name;
    public override string Subtitle => App.Publisher is { } p ? $"{p}{(App.IsVerifiedPublisher ? " (verified publisher)" : "")}" : $"Application ID {App.AppId}";
    public override string Icon => "Apps";

    AppRegistration? Registration => _directory.RegistrationFor(App.AppId);

    public override IReadOnlyList<HealthChip> Badges =>
    [
        .. App.IsEnabled ? Array.Empty<HealthChip>() : [new HealthChip("Sign-In Off", BadgeKind.Warning, "Nobody can sign in to it")],
        new HealthChip(EnterpriseApp.SourceText(App.Source), App.Source == AppSource.ThirdParty ? BadgeKind.Info : BadgeKind.Neutral, "Who makes it"),
        .. App.AssignmentRequired ? [new HealthChip("Assignment Required", BadgeKind.Neutral, "Only the people and groups given access can sign in")] : Array.Empty<HealthChip>(),
        .. App.IsVisible ? Array.Empty<HealthChip>() : [new HealthChip("Hidden", BadgeKind.Neutral, "Not shown in My Apps or the Microsoft 365 app launcher")],
        .. EnterpriseApp.SsoText(App.SsoMode) is { } sso ? [new HealthChip($"{sso} Single Sign-On", BadgeKind.Info, "How people sign in to it")] : Array.Empty<HealthChip>(),
    ];

    public override string? PartnerText => Registration is null ? null : "Open App Registration";

    protected override void OpenPartner()
    {
        if (Registration is { } r)
            _openRegistration?.Invoke(r);
    }

    protected override void OpenInAdminCenter() => Platform.OpenUrl(
        $"https://entra.microsoft.com/#view/Microsoft_AAD_IAM/ManagedAppMenuBlade/~/Overview/objectId/{App.Id}/appId/{App.AppId}");

    public override bool Update(AppDirectory directory)
    {
        if (directory.EnterpriseApps.FirstOrDefault(e => e.Id == App.Id) is not { } app)
            return false;
        App = app;
        _directory = directory;
        _detail = null;
        Refresh(nameof(App));
        return true;
    }

    async Task ReadLastSignInAsync()
    {
        if (Records is null)
            return;
        try
        {
            _lastSignIn = await Records.GetLastSignInAsync(App);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
        }
        _lastSignInRead = true;
        if (Sections[0] is AppSection overview)
            overview.Rebuild();
    }

    /// <summary>The app's details, read once for every tab; a failed read is tried again next time.</summary>
    async Task<EnterpriseAppDetail?> DetailAsync(CancellationToken cancellationToken)
    {
        if (Records is null)
            return null;
        var read = _detail ??= Records.GetEnterpriseDetailAsync(App, cancellationToken);
        try
        {
            return await read;
        }
        catch
        {
            if (ReferenceEquals(_detail, read))
                _detail = null;
            throw;
        }
    }

    IReadOnlyList<AppCard> OverviewCards()
    {
        IReadOnlyList<SummaryLine> facts =
        [
            new("Application ID", App.AppId),
            new("Object ID", App.Id),
            new("Publisher", App.Publisher is { } p ? $"{p}{(App.IsVerifiedPublisher ? " (verified by Microsoft)" : "")}" : "Not given"),
            new("From", EnterpriseApp.SourceText(App.Source)),
            new("Type", App.Type),
            new("Sign-In", App.IsEnabled ? "On: people can sign in" : "Off: nobody can sign in"),
            new("Assignment", App.AssignmentRequired ? "Required: only people and groups given access" : "Not required: anyone in the organization"),
            new("Visible to People", App.IsVisible ? "Yes, in My Apps and the app launcher" : "No"),
            new("Single Sign-On", EnterpriseApp.SsoText(App.SsoMode) ?? "None set up"),
            .. App.Homepage is { Length: > 0 } home ? [new SummaryLine("Home Page", home)] : Array.Empty<SummaryLine>(),
            new("Created", Day(App.Created)),
            new("Last Sign-In", !_lastSignInRead ? "Reading…" : _lastSignIn is { } last ? Day(last) : "None recorded (or not available)"),
            .. App.Notes is { Length: > 0 } notes ? [new SummaryLine("Notes", notes)] : Array.Empty<SummaryLine>(),
        ];
        return [new AppCard("Details", Facts: facts)];
    }

    async Task<IReadOnlyList<AppCard>> AssignmentCardsAsync(CancellationToken cancellationToken)
    {
        var detail = await DetailAsync(cancellationToken);
        if (detail is null)
            return [new AppCard("Users & Groups", EmptyText: "Who has access can't be read for this tenant.")];
        var rows = detail.Assignments.OrderBy(a => a.PrincipalName, StringComparer.CurrentCulture).Select(a => new AppRow
        {
            Title = a.PrincipalName,
            Detail = string.Join(" · ", new[] { a.PrincipalType == "ServicePrincipal" ? "App" : a.PrincipalType == "Group" ? "Group" : "Person", a.RoleName ?? "A role", $"Given {Day(a.Created)}" }),
            OpenAction = a.PrincipalType switch
            {
                "User" when Index.User(a.PrincipalId) is { } u && OpenUserAction is { } open => () => open(u),
                "Group" when Index.Group(a.PrincipalId) is { } g && _openGroup is { } open => () => open(g),
                _ => null,
            },
        }).ToArray();
        return
        [
            new AppCard("Users & Groups",
                App.AssignmentRequired ? "Only these people and groups (and members of the groups) can sign in." : "Assignment isn't required, so anyone in the organization can sign in; these are shown the app in My Apps.",
                Rows: rows, EmptyText: "Nobody has been given access."),
        ];
    }

    async Task<IReadOnlyList<AppCard>> PermissionCardsAsync(CancellationToken cancellationToken)
    {
        var detail = await DetailAsync(cancellationToken);
        if (detail is null)
            return [new AppCard("Permissions", EmptyText: "Permissions can't be read for this tenant.")];
        var admin = detail.DelegatedGrants.Where(g => g.PrincipalId is null).ToArray();
        var personal = detail.DelegatedGrants.Where(g => g.PrincipalId is not null).OrderBy(g => g.PrincipalName, StringComparer.CurrentCulture).ToArray();
        return
        [
            new AppCard("Admin Consent", "Delegated permissions an admin approved for everyone: the app can use them for whoever signs in.",
                Rows: [.. admin.Select(g => new AppRow { Title = g.Resource, Chips = g.Scopes, Badges = [KindChip(false)] })], EmptyText: "None."),
            new AppCard("Application Permissions", "What the app can do on its own, with nobody signed in. Review these closely.",
                Rows: [.. detail.AppPermissions.OrderBy(p => p.Resource).ThenBy(p => p.RoleName).Select(p => new AppRow
                {
                    Title = p.RoleName ?? p.RoleId,
                    Detail = $"{p.Resource} · Granted {Day(p.Created)}",
                    Badges = [KindChip(true)],
                })], EmptyText: "None."),
            new AppCard("Consented by People", "People who approved it for themselves; it can act for each of them, within what they can do.",
                Rows: [.. personal.Select(g => new AppRow
                {
                    Title = g.PrincipalName ?? "Someone",
                    Detail = g.Resource,
                    Chips = g.Scopes,
                    OpenAction = g.PrincipalId is { } pid && Index.User(pid) is { } u && OpenUserAction is { } open ? () => open(u) : null,
                })], EmptyText: "Nobody has consented for themselves."),
        ];
    }
}
