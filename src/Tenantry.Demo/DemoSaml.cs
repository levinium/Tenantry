using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's SAML: the old reporting tool signs in with SAML; Acme's is set up by its publisher; the rest have none.</summary>
public sealed partial class DemoUserRecords
{
    const string DemoTenantId = "00000000-0000-4000-8000-00000000demo";

    readonly Dictionary<string, SamlSetup> _saml = [];

    SamlSetup SamlFor(EnterpriseApp app)
    {
        if (_saml.TryGetValue(app.Id, out var known))
            return known;
        var registration = _registrations?.FirstOrDefault(r => r.AppId == app.AppId);
        var now = DateTimeOffset.UtcNow;
        SamlSetup setup = app.Id switch
        {
            "sp-reports" => new SamlSetup(true, true, registration?.Id, DemoTenantId, app.AppId,
                new SamlSettings(["https://reports.northgate.example/saml"], ["https://reports.northgate.example/saml/acs"], "https://reports.northgate.example/login", null, null),
                [new SamlCertificate("3F2A9C0D1E4B5A6978C1D2E3F4A5B6C7D8E9F0A1", now.AddDays(-1000), now.AddDays(95), true)]),
            "sp-acme" => new SamlSetup(true, false, null, DemoTenantId, app.AppId, SamlSettings.Empty,
                [new SamlCertificate("A1B2C3D4E5F60718293A4B5C6D7E8F9012345678", now.AddDays(-380), now.AddDays(715), true)]),
            _ => new SamlSetup(false, registration is not null, registration?.Id, DemoTenantId, app.AppId, SamlSettings.Empty, []),
        };
        return _saml[app.Id] = setup;
    }

    public async Task<SamlSetup?> GetSamlAsync(EnterpriseApp app, CancellationToken cancellationToken = default)
    {
        await EnsureAppsAsync(cancellationToken);
        return SamlFor(app);
    }

    void ApplySaml(AppChange change)
    {
        if (_enterprise!.FirstOrDefault(e => e.Id == change.App.Id) is not { } app)
            return;
        var setup = SamlFor(app);
        _saml[app.Id] = change switch
        {
            SetSaml s => setup with { IsSaml = true, Settings = s.New },
            AddSamlCertificate a => setup with
            {
                Certificates =
                [
                    .. setup.Certificates.Select(c => a.MakeActive ? c with { Active = false } : c),
                    new SamlCertificate(Convert.ToHexString(Guid.NewGuid().ToByteArray()) + "00000000", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(a.Years), a.MakeActive),
                ],
            },
            SetSamlActiveCertificate c => setup with { Certificates = [.. setup.Certificates.Select(x => x with { Active = x.Thumbprint == c.NewThumbprint })] },
            StopSaml => setup with { IsSaml = false },
            _ => setup,
        };
        EditEnterprise(app.Id, e => e with { SsoMode = _saml[app.Id].IsSaml ? "saml" : null });
    }
}
