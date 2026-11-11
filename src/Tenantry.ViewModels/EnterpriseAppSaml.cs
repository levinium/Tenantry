using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>
/// An enterprise app's Single Sign-On tab: set up SAML, its basic settings (staged when a box is
/// left), its signing certificates, and what to give the app. Apps registered elsewhere are shown, not changed.
/// </summary>
public sealed partial class EnterpriseAppPageViewModel
{
    Task<SamlSetup?>? _saml;
    bool _settingUp;
    AppField? _identifiers;
    AppField? _replyUrls;
    AppField? _signOnUrl;
    AppField? _relayState;
    AppField? _logoutUrl;
    SamlSettings? _shownSaml;

    AppSection? SsoSection => Sections.OfType<AppSection>().FirstOrDefault(s => s.Key == "sso");

    void RebuildSso() => SsoSection?.Rebuild();

    async Task<SamlSetup?> SamlAsync(CancellationToken cancellationToken)
    {
        if (Records is null)
            return null;
        var read = _saml ??= Records.GetSamlAsync(App, cancellationToken);
        try
        {
            return await read;
        }
        catch
        {
            if (ReferenceEquals(_saml, read))
                _saml = null;
            throw;
        }
    }

    static IReadOnlyList<string> Lines(string text) =>
        [.. text.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    void EnsureSamlFields(SamlSetup setup)
    {
        if (_identifiers is null)
        {
            var edit = CanEdit && setup.CanConfigure;
            _identifiers = new AppField("Identifier (Entity ID)", "One per line, from the app's SAML settings", true, edit, _ => StageSaml());
            _replyUrls = new AppField("Reply URL (Assertion Consumer Service URL)", "One per line: https://app.contoso.example/saml/acs", true, edit, _ => StageSaml());
            _signOnUrl = new AppField("Sign-On URL (Optional)", "Where people start signing in, for My Apps", false, edit, _ => StageSaml());
            _relayState = new AppField("Relay State (Optional)", "Where the app sends people after signing in", false, edit, _ => StageSaml());
            _logoutUrl = new AppField("Logout URL (Optional)", "Where Entra sends people after signing out", false, edit, _ => StageSaml());
        }
        var shown = Pending.OfType<SetSaml>().FirstOrDefault()?.New ?? setup.Settings;
        if (_shownSaml is not null && _shownSaml.SameAs(shown))
            return;
        _shownSaml = shown;
        _identifiers.Text = string.Join(Environment.NewLine, shown.Identifiers);
        _replyUrls!.Text = string.Join(Environment.NewLine, shown.ReplyUrls);
        _signOnUrl!.Text = shown.SignOnUrl ?? "";
        _relayState!.Text = shown.RelayState ?? "";
        _logoutUrl!.Text = shown.LogoutUrl ?? "";
    }

    /// <summary>The boxes as typed: checked, then staged as one change (with a first signing certificate when there's none).</summary>
    void StageSaml()
    {
        if (!CanEdit || _saml is not { IsCompletedSuccessfully: true } read || read.Result is not { CanConfigure: true, RegistrationId: { } registration } setup)
            return;
        var settings = new SamlSettings(Lines(_identifiers!.Text), Lines(_replyUrls!.Text), Blank(_signOnUrl!.Text), Blank(_logoutUrl!.Text), Blank(_relayState!.Text));
        var problems = Saml.Problems(settings);
        _identifiers.Problem = problems.GetValueOrDefault("identifiers");
        _replyUrls.Problem = problems.GetValueOrDefault("reply");
        _signOnUrl.Problem = problems.GetValueOrDefault("signon");
        _logoutUrl.Problem = problems.GetValueOrDefault("logout");
        if (problems.Count > 0)
            return;
        if (Pending.OfType<SetSaml>().FirstOrDefault() is { } pending)
            Pipeline!.Unstage(pending);
        _shownSaml = settings;
        if (setup.IsSaml && setup.Settings.SameAs(settings))
            return;
        AddToPending(new SetSaml(Target, registration, setup.IsSaml, setup.Settings, settings));
        if (!setup.IsSaml && setup.Certificates.Count == 0 && !Pending.OfType<AddSamlCertificate>().Any())
            AddToPending(new AddSamlCertificate(Target, 3, MakeActive: true));
    }

    async Task<IReadOnlyList<AppCard>> SsoCardsAsync(CancellationToken cancellationToken)
    {
        if (await SamlAsync(cancellationToken) is not { } setup)
            return [new AppCard("Single Sign-On", EmptyText: "Single sign-on can't be read for this tenant.")];
        if (!setup.CanConfigure)
            return
            [
                new AppCard("Single Sign-On",
                    "This app is registered in its publisher's organization, so its identifier and reply URLs are set up there (or through Entra's app gallery). Open it in the admin center to change them.",
                    Facts: [new SummaryLine("Uses", setup.IsSaml ? "SAML" : EnterpriseApp.SsoText(App.SsoMode) ?? "No single sign-on")]),
                .. setup.IsSaml ? [CertificateCard(setup)] : Array.Empty<AppCard>(),
            ];
        var pendingSetup = Pending.OfType<SetSaml>().Any();
        if (!setup.IsSaml && !_settingUp && !pendingSetup)
            return
            [
                new AppCard("SAML Single Sign-On", "With SAML, people sign in to the app with their work account, through Entra.",
                    Form: CanEdit
                        ? new AppButtonForm("Set Up SAML", "This app doesn't use single sign-on yet. You'll need its SAML details: its identifier and reply URL.",
                            "Show the SAML settings to fill in (nothing changes until you apply)", () =>
                            {
                                _settingUp = true;
                                RebuildSso();
                            })
                        : null,
                    EmptyText: CanEdit ? null : "No single sign-on is set up."),
            ];
        EnsureSamlFields(setup);
        var stopping = Pending.OfType<StopSaml>().FirstOrDefault();
        return
        [
            new AppCard("Basic SAML Configuration", CanEdit ? "From the app's own SAML settings. Changes go to Pending Changes when you leave a box." : null,
                Form: new AppFieldsForm([_identifiers!, _replyUrls!, _signOnUrl!, _relayState!, _logoutUrl!])),
            CertificateCard(setup),
            new AppCard("Give These to the App", "Enter them in the app's SAML settings, or give it the metadata URL, which holds all of them and the active certificate.",
                Rows:
                [
                    new AppRow { Title = "Login URL", Code = setup.LoginUrl },
                    new AppRow { Title = "Microsoft Entra Identifier", Code = setup.EntraIdentifier },
                    new AppRow { Title = "Logout URL", Code = setup.EntraLogoutUrl },
                    new AppRow { Title = "App Federation Metadata URL", Code = setup.MetadataUrl },
                ]),
            .. CanEdit && setup.IsSaml
                ? [new AppCard("Stop Using SAML", Form: stopping is not null
                    ? new AppButtonForm("Undo", "Stopping SAML is in Pending Changes.", "Take it back out of Pending Changes", () => Pipeline!.Unstage(stopping))
                    : new AppButtonForm("Stop Using SAML", "People can't sign in to the app through Entra until SAML is set up again. Its settings are kept.",
                        "Turn SAML off for this app (added to Pending Changes as a caution change)", () => AddToPending(new StopSaml(Target))))]
                : Array.Empty<AppCard>(),
        ];
    }

    AppCard CertificateCard(SamlSetup setup)
    {
        var now = Now();
        var activating = Pending.OfType<SetSamlActiveCertificate>().FirstOrDefault();
        var active = setup.Certificates.FirstOrDefault(c => c.Active)?.Thumbprint;
        var rows = setup.Certificates.Select(c =>
        {
            var row = new AppRow
            {
                Title = c.Active ? "Active Certificate" : "Certificate",
                Detail = $"Expires {Day(c.End)}",
                Code = $"Thumbprint {c.Thumbprint}",
                Badges =
                [
                    .. c.Active ? [new HealthChip("Active", BadgeKind.Success, "Entra signs sign-ins with it")] : Array.Empty<HealthChip>(),
                    .. c.End < now ? [new HealthChip("Expired", BadgeKind.Danger, "Sign-ins signed with it fail")]
                        : c.End < now.AddDays(60) ? [new HealthChip("Expires Soon", BadgeKind.Warning, "Within 60 days: make a new one and give it to the app")]
                        : Array.Empty<HealthChip>(),
                ],
            };
            if (!CanEdit || !setup.CanConfigure || c.Active)
                return row;
            return activating is not null && string.Equals(activating.NewThumbprint, c.Thumbprint, StringComparison.OrdinalIgnoreCase)
                ? row.With("Activating", "Undo", "Keep the current one (takes the change out of Pending Changes)", () => Pipeline!.Unstage(activating))
                : row.With(null, "Make Active", "Sign with this one instead; the app must trust it first or sign-in fails (added to Pending Changes)",
                    () =>
                    {
                        if (activating is not null)
                            Pipeline!.Unstage(activating);
                        AddToPending(new SetSamlActiveCertificate(Target, active, c.Thumbprint));
                    });
        }).ToList();
        rows.AddRange(Pending.OfType<AddSamlCertificate>().Select(a => Adding("New Certificate", $"Made by Entra, valid {a.Years} years{(a.MakeActive ? ", active" : "")}", a)));
        var adding = Pending.OfType<AddSamlCertificate>().Any();
        return new AppCard("SAML Signing Certificates", "Entra signs each sign-in with the active one; the app checks the signature.", Rows: rows,
            EmptyText: "No signing certificate yet.",
            Form: CanEdit && setup.CanConfigure && !adding
                ? new AppButtonForm("New Certificate", "Make another for when the active one nears its end; make it active once the app trusts it.",
                    "Entra makes a new signing certificate, valid 3 years (added to Pending Changes)",
                    () => AddToPending(new AddSamlCertificate(Target, 3, MakeActive: setup.Certificates.Count == 0)))
                : null);
    }
}
