namespace Tenantry.Core.Changes;

/// <summary>
/// An app registration's name or notes. A rename is guarded by the new name too, so Test mode
/// can't rename a test app out of test scope (or a real one into it).
/// </summary>
/// <param name="Property">"displayName" or "notes".</param>
public sealed record SetAppProperty(ChangeTarget App, string Property, string Old, string New) : AppChange(App)
{
    public override string Key => "property:" + Property;
    public override string Label => Property == "displayName" ? "App Name" : "App Notes";
    public override string Before => Old.Length == 0 ? "None" : Old;
    public override string After => New.Length == 0 ? "None" : New;
    public override bool IsNoChange => Old == New;
    public override IReadOnlyList<ChangeTarget> Guarded => Property == "displayName" ? [App, App with { Name = New }] : [App];
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetAppProperty e ? this with { Old = e.Old } : this;
}

/// <summary>
/// The redirect URIs of one platform ("Web", "Single-Page App", "Mobile and Desktop"): where Entra
/// sends people after they sign in. Removing one can break sign-in wherever it's used.
/// </summary>
public sealed record SetRedirectUris(ChangeTarget App, string Platform, IReadOnlyList<string> Old, IReadOnlyList<string> New) : AppChange(App)
{
    public override string Key => "redirect:" + Platform;
    public override string Label => $"{Platform} Redirect URIs";
    public override string Before => Old.Count == 0 ? "None" : string.Join(", ", Old);
    public override string After => New.Count == 0 ? "None" : string.Join(", ", New);
    public override bool IsCaution => Old.Except(New, StringComparer.Ordinal).Any();
    public override bool IsNoChange => Old.SequenceEqual(New, StringComparer.Ordinal);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetRedirectUris e ? this with { Old = e.Old } : this;

    /// <summary>Graph's property for a platform.</summary>
    public static string GraphProperty(string platform) => platform switch
    {
        "Single-Page App" => "spa",
        "Mobile and Desktop" => "publicClient",
        _ => "web",
    };

    /// <summary>Why a redirect URI can't be used, or null. Web and single-page apps need https (http only for localhost).</summary>
    public static string? Problem(string platform, string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return "That isn't a full address (like https://app.contoso.example/signin).";
        if (uri.Length > 256)
            return "Entra allows up to 256 characters.";
        if (platform == "Mobile and Desktop")
            return null;
        if (parsed.Scheme == Uri.UriSchemeHttps)
            return null;
        return parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback ? null : "Web and single-page apps need https:// (http:// only for localhost).";
    }
}

/// <summary>
/// Adds or removes an API permission the app asks for (requiredResourceAccess). Adding doesn't
/// give it: an admin still grants consent. Removing doesn't take back consent already given.
/// </summary>
/// <param name="ResourceAppId">The API's application ID (Microsoft Graph's is 00000003-0000-0000-c000-000000000000).</param>
public sealed record SetApiPermission(ChangeTarget App, string ResourceAppId, string Resource, string PermissionId, string PermissionName, bool IsApplication, bool Add) : AppChange(App)
{
    public override string Key => $"permission:{ResourceAppId}:{PermissionId}:{IsApplication}";
    public override string Label => $"Permission {Resource}: {PermissionName} ({(IsApplication ? "Application" : "Delegated")})";
    public override string Before => Add ? "Not asked for" : "Asked for";
    public override string After => Add ? "Asked for (consent still needed)" : "Not asked for (consent already given stays until revoked)";
    public override bool IsNoChange => false;
}

/// <summary>
/// Adds a certificate (its public part) the app can sign in with. The private key never leaves
/// wherever it's kept; Entra gets only the certificate.
/// </summary>
/// <param name="Certificate">The certificate, DER bytes in base64.</param>
public sealed record AddAppCertificate(ChangeTarget App, string Subject, string Thumbprint, string Certificate, DateTimeOffset NotBefore, DateTimeOffset NotAfter) : AppChange(App)
{
    public override string Key => "newcert:" + Thumbprint;
    public override string Label => "New Certificate";
    public override string Before => "None";
    public override string After => $"{Subject} (thumbprint {Thumbprint}), expires {NotAfter.ToLocalTime():d MMM yyyy}";
    public override bool IsNoChange => false;
}
