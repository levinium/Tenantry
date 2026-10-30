using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>
/// An app's page (8c): a header with its flags, links and Delete, then tabs of cards. App
/// registrations and enterprise apps share it; each kind supplies its own tabs. Details are read
/// live once, when a tab that needs them is first shown. Every change goes to Pending changes; rows
/// waiting there show Adding or Removing with Undo.
/// </summary>
public abstract partial class AppPageViewModel : TabViewModel, IDisposable
{
    protected AppPageViewModel(IAppRecords? records, IDirectoryIndex? index, Action<UserSummary>? openUser, Func<DateTimeOffset> now,
        ChangePipeline? pipeline, Availability? canEdit)
    {
        Records = records;
        Index = index ?? EmptyIndex.Instance;
        OpenUserAction = openUser;
        Now = now;
        Pipeline = pipeline;
        CanEdit = pipeline is not null && canEdit?.IsAvailable == true;
        EditReason = pipeline is null ? "This tenant can't be edited" : canEdit?.Reason ?? "";
        if (pipeline is not null)
            pipeline.Changed += OnPendingChanged;
    }

    protected IAppRecords? Records { get; }
    protected IDirectoryIndex Index { get; }
    protected Action<UserSummary>? OpenUserAction { get; }
    protected Func<DateTimeOffset> Now { get; }
    protected ChangePipeline? Pipeline { get; }

    public bool CanEdit { get; }
    public string EditReason { get; }

    /// <summary>The app Tenantry signs in with in this tenant (its client ID), when known.</summary>
    public string? TenantryClientId { get; init; }

    /// <summary>The app's application (client) ID.</summary>
    protected abstract string AppId { get; }

    /// <summary>This is Tenantry itself: taking anything away could lock Tenantry out, so it needs the name typed.</summary>
    public bool IsTenantry => TenantryClientId is { Length: > 0 } id && string.Equals(AppId, id, StringComparison.OrdinalIgnoreCase);

    public const string TenantryReason = "This is the app Tenantry signs in with: it could stop Tenantry working in this tenant.";

    /// <summary>Puts a change in Pending changes; one that takes something away from Tenantry's own app needs its name typed.</summary>
    protected void AddToPending(ChangeOp change) =>
        Pipeline!.Stage(IsTenantry && change.IsCaution ? change with { TypedReason = TenantryReason } : change);

    public abstract string Subtitle { get; }

    /// <summary>The page's icon: an app registration's or an enterprise app's.</summary>
    public abstract string Icon { get; }

    public abstract IReadOnlyList<HealthChip> Badges { get; }

    /// <summary>The app as a change target (judged by its name in Test mode).</summary>
    public abstract ChangeTarget Target { get; }

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

    /// <summary>What's waiting in Pending changes for this app.</summary>
    protected IEnumerable<AppChange> Pending => Pipeline?.Pending.OfType<AppChange>().Where(c => c.App.Id == Target.Id) ?? [];

    public string? PendingText
    {
        get
        {
            var labels = Pending.Select(p => $"{p.Label.ToLowerInvariant()} → {p.After}").ToArray();
            return labels.Length == 0 ? null : "Pending: " + string.Join(", ", labels);
        }
    }

    public bool HasPending => PendingText is not null;

    /// <summary>Delete is offered for apps that can be deleted here (not Microsoft's own).</summary>
    public virtual bool CanDelete => CanEdit;

    protected abstract string DeleteDetail { get; }

    [RelayCommand]
    void Delete()
    {
        if (CanDelete)
            AddToPending(new DeleteApp(Target, DeleteDetail));
    }

    public abstract string DeleteText { get; }

    public abstract string DeleteTip { get; }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(HasPending));
        PendingChanged();
        foreach (var section in Sections.OfType<AppSection>())
            section.Rebuild();
    }

    /// <summary>Before the tabs rebuild after Pending changes changed (switches follow).</summary>
    protected virtual void PendingChanged()
    {
    }

    protected void Refresh(params string[] names)
    {
        foreach (var name in new[] { nameof(Title), nameof(Subtitle), nameof(Badges), nameof(PartnerText), nameof(HasPartner), nameof(PendingText), nameof(HasPending),
                     nameof(CanDelete) }.Concat(names))
            OnPropertyChanged(name);
        PendingChanged();
        foreach (var section in Sections.OfType<AppSection>())
            section.Rebuild();
    }

    protected static string Day(DateTimeOffset? when) => when is { } w ? w.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "Unknown";

    /// <summary>A row that takes something away: Remove (or another verb), or Undo while that waits.</summary>
    protected AppRow Removable(AppRow row, ChangeOp? waiting, Func<ChangeOp> make, string verb = "Remove", string? tip = null, string state = "Removing") =>
        !CanEdit ? row
        : waiting is not null ? row.With(state, "Undo", "Take the change back out of Pending changes", () => Pipeline!.Unstage(waiting))
        : row.With(null, verb, tip ?? $"{verb} (added to Pending changes)", () => AddToPending(make()));

    /// <summary>A row for something waiting to be added, with Undo.</summary>
    protected AppRow Adding(string title, string? detail, ChangeOp waiting) =>
        new AppRow { Title = title, Detail = detail }.With("Adding", "Undo", "Don't add it (takes the change out of Pending changes)", () => Pipeline!.Unstage(waiting));

    /// <summary>Owners, with Remove, those waiting to be added, and a picker to add one.</summary>
    protected IReadOnlyList<AppCard> OwnerCards(IReadOnlyList<AppOwner> owners, AppPickForm form, string caption)
    {
        var pending = Pending.OfType<SetAppOwner>().ToArray();
        var rows = owners.Select(o => Removable(new AppRow
        {
            Title = o.Name,
            Detail = o.Detail,
            OpenAction = Index.User(o.Id) is { } u && OpenUserAction is { } open ? () => open(u) : null,
        }, pending.FirstOrDefault(p => !p.Add && p.Owner.Id == o.Id), () => new SetAppOwner(Target, new ChangeTarget("user", o.Id, o.Name, o.Detail), false),
            tip: "Stop them managing this app (added to Pending changes)")).ToList();
        rows.AddRange(pending.Where(p => p.Add).Select(p => Adding(p.Owner.Name, p.Owner.Address, p)));
        return [new AppCard("Owners", caption, Rows: rows, EmptyText: "Nobody owns this app.", Form: CanEdit ? form : null)];
    }

    protected AppPickForm OwnerPicker(Func<IEnumerable<string>> current) =>
        new(new PersonPicker("Add an owner: type a name", () =>
        {
            var have = current().Concat(Pending.OfType<SetAppOwner>().Where(p => p.Add).Select(p => p.Owner.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return GroupAccess.People(Index).Where(o => !have.Contains(o.Id));
        }, o => Pipeline?.Stage(new SetAppOwner(Target, o.Target("user"), true))));

    /// <summary>Delegated (on behalf of whoever signs in) or Application (on its own, with no one signed in).</summary>
    protected static HealthChip KindChip(bool isApplication) => isApplication
        ? new HealthChip("Application", BadgeKind.Warning, "The app acts on its own, with no one signed in")
        : new HealthChip("Delegated", BadgeKind.Neutral, "The app acts for whoever signs in, within what they can do");

    public void Dispose()
    {
        if (Pipeline is not null)
            Pipeline.Changed -= OnPendingChanged;
    }
}

/// <summary>An app registration's page: Overview, Secrets &amp; Certificates, API Permissions, Owners.</summary>
public sealed partial class AppRegistrationPageViewModel : AppPageViewModel
{
    readonly Action<EnterpriseApp>? _openEnterprise;
    readonly AddSecretForm _secretForm;
    readonly AppPickForm _ownerForm;
    AppDirectory _directory;
    Task<AppRegistrationDetail>? _detail;

    public AppRegistrationPageViewModel(AppRegistration app, AppDirectory directory, IAppRecords? records, IDirectoryIndex? index, Action<UserSummary>? openUser,
        Action<EnterpriseApp>? openEnterprise, Func<DateTimeOffset> now, ChangePipeline? pipeline = null, Availability? canEdit = null)
        : base(records, index, openUser, now, pipeline, canEdit)
    {
        App = app;
        _directory = directory;
        _openEnterprise = openEnterprise;
        _secretForm = new AddSecretForm((description, months) => Pipeline?.Stage(new AddAppSecret(Target, description, months)));
        _ownerForm = OwnerPicker(() => App.Owners.Select(o => o.Id));
        Sections =
        [
            new AppSection("overview", "Overview", "Home", _ => Task.FromResult(OverviewCards())),
            new AppSection("credentials", "Secrets & Certificates", "Key", _ => Task.FromResult(CredentialCards())),
            new AppSection("permissions", "API Permissions", "ShieldKeyhole", PermissionCardsAsync, "Reading which permissions it has and who consented…"),
            new AppSection("owners", "Owners", "People", _ => Task.FromResult(OwnerCards(App.Owners, _ownerForm,
                "Owners can change the app's settings and credentials in Entra."))),
        ];
        SelectedSection = Sections[0];
    }

    public AppRegistration App { get; private set; }

    public override string Title => App.Name;
    public override string Subtitle => $"Application (client) ID {App.AppId}";
    public override string Icon => "AppFolder";
    public override ChangeTarget Target => new(AppChange.Registration, App.Id, App.Name, null);
    protected override string AppId => App.AppId;

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

    public override string DeleteText => "Delete App";
    public override string DeleteTip => "Delete the app registration and its enterprise app (restorable for 30 days; you type its name to confirm)";

    protected override string DeleteDetail =>
        $"{App.Credentials.Count} secrets and certificates, {App.Requested.Count} API permissions" + (Enterprise is null ? "" : ", with its enterprise app");

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
        var pending = Pending.ToArray();
        AppRow Row(AppCredential c)
        {
            var status = c.Status(now);
            var detail = string.Join(" · ", new[]
            {
                c.IsSecret && c.Hint is { } hint ? $"Starts with {hint}…" : null,
                $"Added {Day(c.Start)}",
                c.End is { } end ? $"{(end < now ? "Expired" : "Expires")} {Day(end)}" : "Never expires",
            }.OfType<string>());
            var row = new AppRow
            {
                Title = c.Name ?? (c.IsSecret ? "A secret" : "A certificate"),
                Detail = detail,
                Code = c.IsSecret ? null : c.Hint is { } thumb ? $"Thumbprint {thumb}" : null,
                Badges = status switch
                {
                    AppFields.Expired => [new HealthChip(AppFields.Expired, BadgeKind.Danger, "Sign-ins with it fail")],
                    AppFields.Soon => [new HealthChip(AppFields.Soon, BadgeKind.Warning, "Within 30 days")],
                    _ => [new HealthChip("Current", BadgeKind.Success, "It works")],
                },
            };
            return Removable(row, pending.OfType<RemoveAppCredential>().FirstOrDefault(r => r.KeyId == c.Id),
                () => new RemoveAppCredential(Target, c.Id, c.IsSecret, row.Title, detail),
                tip: status == AppFields.Expired ? "Remove it (added to Pending changes)" : "Anything still signing in with it stops working (you type the app's name to confirm)");
        }
        var secrets = App.Credentials.Where(c => c.IsSecret).OrderBy(c => c.End).Select(Row).ToList();
        secrets.AddRange(pending.OfType<AddAppSecret>().Select(a => Adding(a.Description, $"Expires in {a.Months} months; shown to you once after applying", a)));
        var certificates = App.Credentials.Where(c => !c.IsSecret).OrderBy(c => c.End).Select(Row).ToArray();
        return
        [
            new AppCard("Client Secrets", "Passwords the app signs in with. Entra shows a secret's value only once, when it's made.", Rows: secrets,
                EmptyText: "No client secrets.", Form: CanEdit ? _secretForm : null),
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
        var cards = new List<AppCard>();
        var missing = detail.Permissions.Where(p => p.Consented == false && p.ResourceId.Length > 0).ToArray();
        var waiting = Pending.OfType<GrantAdminConsent>().FirstOrDefault();
        if (CanEdit && (missing.Length > 0 || waiting is not null))
            cards.Add(new AppCard("Admin Consent", Form: waiting is not null
                ? new AppButtonForm("Undo", $"Admin consent for {waiting.Items.Count} permissions is in Pending changes.", "Take it back out of Pending changes",
                    () => Pipeline!.Unstage(waiting))
                : new AppButtonForm("Grant Admin Consent",
                    $"{missing.Length} {(missing.Length == 1 ? "permission isn't" : "permissions aren't")} consented yet. Granting approves {(missing.Length == 1 ? "it" : "them")} for everyone in the organization.",
                    "Approve them for everyone (added to Pending changes as a caution change)",
                    () => Pipeline!.Stage(new GrantAdminConsent(Target, App.AppId,
                        [.. missing.Select(p => new ConsentItem(p.ResourceId, p.Resource, p.Id, p.Name, p.IsApplication))])))));
        cards.AddRange(detail.Permissions.GroupBy(p => p.Resource).OrderBy(g => g.Key, StringComparer.CurrentCulture).Select(g => new AppCard(g.Key,
            Rows: [.. g.OrderBy(p => p.IsApplication).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => new AppRow
            {
                Title = p.Name,
                Detail = p.Description,
                StateText = waiting?.Items.Any(i => i.PermissionId == p.Id) == true ? "Consenting" : null,
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
            })])));
        return cards;
    }
}

/// <summary>An enterprise app's page: Overview (with its settings), Users &amp; Groups, Permissions, Owners.</summary>
public sealed partial class EnterpriseAppPageViewModel : AppPageViewModel
{
    readonly Action<AppRegistration>? _openRegistration;
    readonly Action<GroupSummary>? _openGroup;
    readonly AppPickForm _ownerForm;
    readonly AppPickForm _assignForm;
    readonly AppToggle _enabled;
    readonly AppToggle _assignment;
    readonly AppToggle _visible;
    AppDirectory _directory;
    Task<EnterpriseAppDetail>? _detail;
    DateTimeOffset? _lastSignIn;
    bool _lastSignInRead;
    IReadOnlyList<AppOwner> _owners = [];
    IReadOnlyList<AppUserAssignment> _assignments = [];

    public EnterpriseAppPageViewModel(EnterpriseApp app, AppDirectory directory, IAppRecords? records, IDirectoryIndex? index, Action<UserSummary>? openUser,
        Action<GroupSummary>? openGroup, Action<AppRegistration>? openRegistration, Func<DateTimeOffset> now, ChangePipeline? pipeline = null, Availability? canEdit = null)
        : base(records, index, openUser, now, pipeline, canEdit)
    {
        App = app;
        _directory = directory;
        _openRegistration = openRegistration;
        _openGroup = openGroup;
        _ownerForm = OwnerPicker(() => _owners.Select(o => o.Id));
        _assignForm = new AppPickForm(new PersonPicker("Give access to: type a person or group", () =>
        {
            var role = _assignForm!.Role?.Value ?? AppUserAssignment.DefaultAccess;
            var have = _assignments.Where(a => a.RoleId == role).Select(a => a.PrincipalId)
                .Concat(Pending.OfType<AddAppUserAssignment>().Where(a => a.RoleId == role).Select(a => a.Principal.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return GroupAccess.People(Index).Concat(Index.AllGroups.Where(g => g.Kind == GroupKind.Security || g.Kind == GroupKind.Microsoft365).Select(PickOption.Of))
                .Where(o => !have.Contains(o.Id));
        }, o =>
        {
            var role = _assignForm!.Role;
            Pipeline?.Stage(new AddAppUserAssignment(Target, o.Target(o.Type), o.Type == "group" ? "Group" : "User", role?.Value ?? AppUserAssignment.DefaultAccess,
                role?.Label ?? "Default Access"));
        }), [new EditorChoice(AppUserAssignment.DefaultAccess, "Default Access", "Plain access")]);
        _enabled = new AppToggle("Sign-In On", "Off stops everyone signing in to it (and it signing in on its own).", app.IsEnabled, CanEdit, v => SetSetting("enabled", App.IsEnabled, v));
        _assignment = new AppToggle("Assignment Required", "Only the people and groups on Users & Groups can sign in.", app.AssignmentRequired, CanEdit,
            v => SetSetting("assignment", App.AssignmentRequired, v));
        _visible = new AppToggle("Visible to People", "Shown in My Apps and the Microsoft 365 app launcher to those with access.", app.IsVisible, CanEdit,
            v => SetSetting("visible", App.IsVisible, v));
        Sections =
        [
            new AppSection("overview", "Overview", "Home", _ => Task.FromResult(OverviewCards())),
            new AppSection("assignments", "Users & Groups", "PeopleCheckmark", AssignmentCardsAsync, "Reading who has access…"),
            new AppSection("permissions", "Permissions", "ShieldKeyhole", PermissionCardsAsync, "Reading what it's been allowed to do…"),
            new AppSection("owners", "Owners", "People", async ct =>
            {
                _owners = (await DetailAsync(ct))?.Owners ?? [];
                return OwnerCards(_owners, _ownerForm, "Owners can change this app's settings in Entra.");
            }, "Reading its owners…"),
        ];
        SelectedSection = Sections[0];
        PendingChanged();
        _ = ReadLastSignInAsync();
    }

    public EnterpriseApp App { get; private set; }

    public override string Title => App.Name;
    public override string Subtitle => App.Publisher is { } p ? $"{p}{(App.IsVerifiedPublisher ? " (verified publisher)" : "")}" : $"Application ID {App.AppId}";
    public override string Icon => "Apps";
    public override ChangeTarget Target => new(AppChange.Enterprise, App.Id, App.Name, null);
    protected override string AppId => App.AppId;

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

    /// <summary>Microsoft's own apps aren't deleted here: Microsoft 365 needs them.</summary>
    public override bool CanDelete => CanEdit && App.Source != AppSource.Microsoft;

    public override string DeleteText => "Delete Enterprise App";
    public override string DeleteTip => Registration is null
        ? "Remove it from this organization, with its consents and assignments (you type its name to confirm)"
        : "Remove its enterprise app, with its consents and assignments; the app registration stays (you type its name to confirm)";

    protected override string DeleteDetail => $"{EnterpriseApp.SourceText(App.Source)} app{(App.IsEnabled ? "" : ", sign-in off")}";

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

    void SetSetting(string setting, bool current, bool value)
    {
        if (!CanEdit)
            return;
        var waiting = Pending.OfType<SetEnterpriseAppSetting>().FirstOrDefault(s => s.Setting == setting);
        if (value == current)
        {
            if (waiting is not null)
                Pipeline!.Unstage(waiting);
        }
        else
            AddToPending(new SetEnterpriseAppSetting(Target, setting, current, value));
    }

    protected override void PendingChanged()
    {
        if (_enabled is null)
            return;
        foreach (var (toggle, setting, value) in new[] { (_enabled, "enabled", App.IsEnabled), (_assignment, "assignment", App.AssignmentRequired), (_visible, "visible", App.IsVisible) })
        {
            var waiting = Pending.OfType<SetEnterpriseAppSetting>().FirstOrDefault(s => s.Setting == setting);
            toggle.Show(waiting?.New ?? value, waiting is not null);
        }
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
            new("Single Sign-On", EnterpriseApp.SsoText(App.SsoMode) ?? "None set up"),
            .. App.Homepage is { Length: > 0 } home ? [new SummaryLine("Home Page", home)] : Array.Empty<SummaryLine>(),
            new("Created", Day(App.Created)),
            new("Last Sign-In", !_lastSignInRead ? "Reading…" : _lastSignIn is { } last ? Day(last) : "None recorded (or not available)"),
            .. App.Notes is { Length: > 0 } notes ? [new SummaryLine("Notes", notes)] : Array.Empty<SummaryLine>(),
        ];
        return
        [
            new AppCard("Settings", CanEdit ? "Changes go to Pending changes." : EditReason, Form: new AppToggleForm([_enabled, _assignment, _visible])),
            new AppCard("Details", Facts: facts),
        ];
    }

    async Task<IReadOnlyList<AppCard>> AssignmentCardsAsync(CancellationToken cancellationToken)
    {
        var detail = await DetailAsync(cancellationToken);
        if (detail is null)
            return [new AppCard("Users & Groups", EmptyText: "Who has access can't be read for this tenant.")];
        _assignments = detail.Assignments;
        _assignForm.SetRoles([.. detail.Roles.Select(r => new EditorChoice(r.Id, r.Name, ""))]);
        var pending = Pending.ToArray();
        var rows = detail.Assignments.OrderBy(a => a.PrincipalName, StringComparer.CurrentCulture).Select(a => Removable(new AppRow
        {
            Title = a.PrincipalName,
            Detail = string.Join(" · ", new[] { a.PrincipalType == "ServicePrincipal" ? "App" : a.PrincipalType == "Group" ? "Group" : "Person", a.RoleName ?? "A role", $"Given {Day(a.Created)}" }),
            OpenAction = a.PrincipalType switch
            {
                "User" when Index.User(a.PrincipalId) is { } u && OpenUserAction is { } open => () => open(u),
                "Group" when Index.Group(a.PrincipalId) is { } g && _openGroup is { } open => () => open(g),
                _ => null,
            },
        }, pending.OfType<RemoveAppUserAssignment>().FirstOrDefault(r => r.AssignmentId == a.Id),
            () => new RemoveAppUserAssignment(Target, a.Id, a.PrincipalName, a.RoleName ?? "Access"), tip: "Take their access away (added to Pending changes)")).ToList();
        rows.AddRange(pending.OfType<AddAppUserAssignment>().Select(a => Adding(a.Principal.Name, $"{(a.PrincipalType == "Group" ? "Group" : "Person")} · {a.RoleName}", a)));
        return
        [
            new AppCard("Users & Groups",
                App.AssignmentRequired ? "Only these people and groups (and members of the groups) can sign in." : "Assignment isn't required, so anyone in the organization can sign in; these are shown the app in My Apps.",
                Rows: rows, EmptyText: "Nobody has been given access.", Form: CanEdit ? _assignForm : null),
        ];
    }

    async Task<IReadOnlyList<AppCard>> PermissionCardsAsync(CancellationToken cancellationToken)
    {
        var detail = await DetailAsync(cancellationToken);
        if (detail is null)
            return [new AppCard("Permissions", EmptyText: "Permissions can't be read for this tenant.")];
        var pending = Pending.OfType<RevokeAppGrant>().ToArray();
        AppRow Revocable(AppRow row, string grantId, bool application, string what, string who) =>
            Removable(row, pending.FirstOrDefault(p => p.GrantId == grantId), () => new RevokeAppGrant(Target, grantId, application, what, who), "Revoke",
                "Take the permission back (added to Pending changes)", "Revoking");
        var admin = detail.DelegatedGrants.Where(g => g.PrincipalId is null).ToArray();
        var personal = detail.DelegatedGrants.Where(g => g.PrincipalId is not null).OrderBy(g => g.PrincipalName, StringComparer.CurrentCulture).ToArray();
        return
        [
            new AppCard("Admin Consent", "Delegated permissions an admin approved for everyone: the app can use them for whoever signs in.",
                Rows: [.. admin.Select(g => Revocable(new AppRow { Title = g.Resource, Chips = g.Scopes, Badges = [KindChip(false)] }, g.Id, false,
                    $"{g.Resource}: {string.Join(", ", g.Scopes)}", "everyone (admin consent)"))], EmptyText: "None."),
            new AppCard("Application Permissions", "What the app can do on its own, with nobody signed in. Review these closely.",
                Rows: [.. detail.AppPermissions.OrderBy(p => p.Resource).ThenBy(p => p.RoleName).Select(p => Revocable(new AppRow
                {
                    Title = p.RoleName ?? p.RoleId,
                    Detail = $"{p.Resource} · Granted {Day(p.Created)}",
                    Badges = [KindChip(true)],
                }, p.Id, true, $"{p.Resource}: {p.RoleName ?? p.RoleId}", "the app itself"))], EmptyText: "None."),
            new AppCard("Consented by People", "People who approved it for themselves; it can act for each of them, within what they can do.",
                Rows: [.. personal.Select(g => Revocable(new AppRow
                {
                    Title = g.PrincipalName ?? "Someone",
                    Detail = g.Resource,
                    Chips = g.Scopes,
                    OpenAction = g.PrincipalId is { } pid && Index.User(pid) is { } u && OpenUserAction is { } open ? () => open(u) : null,
                }, g.Id, false, $"{g.Resource}: {string.Join(", ", g.Scopes)}", g.PrincipalName ?? "someone"))], EmptyText: "Nobody has consented for themselves."),
        ];
    }
}
