namespace Tenantry.Core.Directory;

/// <summary>
/// An app's basic SAML settings, as Entra's "Basic SAML Configuration" names them: the identifiers
/// (entity IDs, the app registration's identifierUris), the reply URLs (assertion consumer service
/// URLs, its web redirect URIs), the sign-on URL (the enterprise app's loginUrl), relay state and logout URL.
/// </summary>
public sealed record SamlSettings(IReadOnlyList<string> Identifiers, IReadOnlyList<string> ReplyUrls, string? SignOnUrl, string? LogoutUrl, string? RelayState)
{
    public static readonly SamlSettings Empty = new([], [], null, null, null);

    public bool SameAs(SamlSettings other) =>
        Identifiers.SequenceEqual(other.Identifiers, StringComparer.Ordinal) && ReplyUrls.SequenceEqual(other.ReplyUrls, StringComparer.Ordinal)
        && (SignOnUrl ?? "") == (other.SignOnUrl ?? "") && (LogoutUrl ?? "") == (other.LogoutUrl ?? "") && (RelayState ?? "") == (other.RelayState ?? "");

    /// <summary>In a line, for Pending Changes.</summary>
    public string Summary => Identifiers.Count == 0 && ReplyUrls.Count == 0 ? "Not set"
        : $"Identifier {string.Join(", ", Identifiers)}; reply URL {string.Join(", ", ReplyUrls)}"
          + (SignOnUrl is { Length: > 0 } s ? $"; sign-on URL {s}" : "") + (RelayState is { Length: > 0 } r ? $"; relay state {r}" : "")
          + (LogoutUrl is { Length: > 0 } l ? $"; logout URL {l}" : "");
}

/// <summary>A certificate Entra signs the app's SAML tokens with.</summary>
public sealed record SamlCertificate(string Thumbprint, DateTimeOffset? Start, DateTimeOffset? End, bool Active);

/// <summary>
/// An enterprise app's SAML single sign-on as it stands. <see cref="CanConfigure"/> is false for apps
/// registered in another organization (their identifiers and reply URLs live there).
/// </summary>
/// <param name="RegistrationId">The app registration's object ID here, when it's registered here.</param>
public sealed record SamlSetup(bool IsSaml, bool CanConfigure, string? RegistrationId, string TenantId, string AppId, SamlSettings Settings, IReadOnlyList<SamlCertificate> Certificates)
{
    /// <summary>Where the app sends people to sign in (Entra's "Login URL").</summary>
    public string LoginUrl => $"https://login.microsoftonline.com/{TenantId}/saml2";

    /// <summary>Entra's entity ID, as the app should expect it ("Microsoft Entra Identifier").</summary>
    public string EntraIdentifier => $"https://sts.windows.net/{TenantId}/";

    public string EntraLogoutUrl => $"https://login.microsoftonline.com/{TenantId}/saml2";

    /// <summary>Everything above plus the active signing certificate, in one file the app can read.</summary>
    public string MetadataUrl => $"https://login.microsoftonline.com/{TenantId}/federationmetadata/2007-06/federationmetadata.xml?appid={AppId}";
}

/// <summary>Checks on SAML settings before they're staged.</summary>
public static class Saml
{
    /// <summary>Why a reply or sign-on URL can't be used, or null: https, except localhost.</summary>
    public static string? UrlProblem(string url) =>
        !Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"\"{url}\" isn't a full address."
        : u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback) ? null
        : $"\"{url}\" needs https:// (http:// only for localhost).";

    /// <summary>Problems by setting ("identifiers", "reply", "signon", "logout"); empty when it can be staged.</summary>
    public static IReadOnlyDictionary<string, string> Problems(SamlSettings settings)
    {
        var problems = new Dictionary<string, string>();
        if (settings.Identifiers.Count == 0)
            problems["identifiers"] = "Required: the app's entity ID (it's in the app's own SAML settings).";
        else if (settings.Identifiers.FirstOrDefault(i => !Uri.TryCreate(i, UriKind.Absolute, out _)) is { } bad)
            problems["identifiers"] = $"\"{bad}\" isn't a URI (like https://app.contoso.example or urn:contoso:app).";
        if (settings.ReplyUrls.Count == 0)
            problems["reply"] = "Required: where Entra posts the sign-in (the app's assertion consumer service URL).";
        else if (settings.ReplyUrls.Select(UrlProblem).FirstOrDefault(p => p is not null) is { } replyProblem)
            problems["reply"] = replyProblem;
        if (settings.SignOnUrl is { Length: > 0 } signOn && UrlProblem(signOn) is { } signOnProblem)
            problems["signon"] = signOnProblem;
        if (settings.LogoutUrl is { Length: > 0 } logout && UrlProblem(logout) is { } logoutProblem)
            problems["logout"] = logoutProblem;
        return problems;
    }
}
