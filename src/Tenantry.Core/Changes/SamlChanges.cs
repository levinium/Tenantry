using Tenantry.Core.Directory;

namespace Tenantry.Core.Changes;

/// <summary>
/// Sets up or changes an enterprise app's SAML single sign-on: SAML as its sign-on mode, its
/// sign-on URL and relay state, and on its app registration the identifiers, reply URLs and
/// logout URL. Changing a working setup can stop sign-in until the app matches, so that's a caution.
/// </summary>
/// <param name="App">The enterprise app (judged by its name in Test mode).</param>
/// <param name="RegistrationId">The app registration's object ID.</param>
public sealed record SetSaml(ChangeTarget App, string RegistrationId, bool WasSaml, SamlSettings Old, SamlSettings New) : AppChange(App)
{
    public override string Key => "saml";
    public override string Label => WasSaml ? "SAML Settings" : "SAML Single Sign-On";
    public override string Before => WasSaml ? Old.Summary : "Not set up";
    public override string After => New.Summary;
    public override bool IsCaution => WasSaml;
    public override bool IsNoChange => WasSaml && Old.SameAs(New);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetSaml e ? this with { Old = e.Old, WasSaml = e.WasSaml } : this;
}

/// <summary>
/// A new app for SAML single sign-on, made the way Entra's "Create your own application" makes a
/// non-gallery app (its application template): an app registration and enterprise app Entra lets
/// take the app's own SAML identifier. SAML is set up on its Single Sign-On tab afterwards.
/// </summary>
/// <param name="App">Kind "enterpriseapp"; Id and Name are the new app's name.</param>
public sealed record CreateSamlApp(ChangeTarget App) : AppChange(App)
{
    /// <summary>Entra's "non-gallery application" template.</summary>
    public const string TemplateId = "8adf8e6e-67b2-4cf2-a259-e3dc5476c621";

    public override string Key => "create";
    public override string Label => $"New SAML App \"{App.Name}\"";
    public override string Before => "None";
    public override string After => "Made, ready for its SAML settings";
    public override bool IsNoChange => false;
}

/// <summary>A new certificate Entra signs SAML tokens with (Entra makes it); the first one becomes the active one.</summary>
public sealed record AddSamlCertificate(ChangeTarget App, int Years, bool MakeActive) : AppChange(App)
{
    public override string Key => "samlcert:new";
    public override string Label => "New SAML Signing Certificate";
    public override string Before => "None";
    public override string After => $"Made by Entra, valid {(Years == 1 ? "a year" : $"{Years} years")}" + (MakeActive ? ", active" : " (not active until you make it so)");
    public override bool IsNoChange => false;
}

/// <summary>Which certificate signs the app's SAML tokens. The app must trust the new one first, or sign-in fails.</summary>
public sealed record SetSamlActiveCertificate(ChangeTarget App, string? OldThumbprint, string NewThumbprint) : AppChange(App)
{
    public override string Key => "samlcert:active";
    public override string Label => "Active SAML Signing Certificate";
    public override string Before => OldThumbprint ?? "None";
    public override string After => NewThumbprint;
    public override bool IsCaution => true;
    public override bool IsNoChange => string.Equals(OldThumbprint, NewThumbprint, StringComparison.OrdinalIgnoreCase);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetSamlActiveCertificate e ? this with { OldThumbprint = e.OldThumbprint } : this;
}

/// <summary>Stops using SAML for the app: people can't sign in to it through Entra that way until it's set up again. The settings are kept.</summary>
public sealed record StopSaml(ChangeTarget App) : AppChange(App)
{
    public override string Key => "saml:stop";
    public override string Label => "SAML Single Sign-On";
    public override string Before => "On";
    public override string After => "Off (settings kept)";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
}
