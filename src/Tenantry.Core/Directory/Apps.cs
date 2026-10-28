using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>A client secret or a certificate an app signs in with.</summary>
/// <param name="Id">Its keyId.</param>
/// <param name="Hint">A secret's first characters, or a certificate's thumbprint.</param>
public sealed record AppCredential(string Id, bool IsSecret, string? Name, string? Hint, DateTimeOffset? Start, DateTimeOffset? End)
{
    /// <summary>"Expired", "Expires soon" (within 30 days) or null.</summary>
    public string? Status(DateTimeOffset now) => End is not { } end ? null : end < now ? AppFields.Expired : end < now.AddDays(30) ? AppFields.Soon : null;

    public string KindText => IsSecret ? "Secret" : "Certificate";
}

/// <summary>An app registration: the app's definition in this tenant, as App registrations lists it.</summary>
/// <param name="Audience">Graph's signInAudience ("AzureADMyOrg", …).</param>
/// <param name="RedirectUris">Where sign-ins return, by platform ("Web", "Single-Page App", "Mobile and Desktop").</param>
/// <param name="Requested">The API permissions it asks for: resource app ID → permission IDs and whether each is an application permission.</param>
public sealed record AppRegistration(
    string Id,
    string AppId,
    string Name,
    string Audience,
    DateTimeOffset? Created,
    IReadOnlyList<AppCredential> Credentials,
    IReadOnlyList<RequestedPermission> Requested,
    IReadOnlyList<(string Platform, string Uri)> RedirectUris,
    IReadOnlyList<AppOwner> Owners,
    string? Notes = null,
    string? PublisherDomain = null)
{
    public static string AudienceText(string audience) => audience switch
    {
        "AzureADMyOrg" => "This organization only",
        "AzureADMultipleOrgs" => "Any organization",
        "AzureADandPersonalMicrosoftAccount" => "Any organization and personal accounts",
        "PersonalMicrosoftAccount" => "Personal accounts only",
        _ => audience,
    };

    /// <summary>The soonest end among credentials that haven't ended, else the latest that has.</summary>
    public DateTimeOffset? NextExpiry(DateTimeOffset now) =>
        Credentials.Where(c => c.End >= now).Min(c => c.End) ?? Credentials.Max(c => c.End);

    /// <summary>The worst credential state: Expired only when every credential has ended.</summary>
    public string CredentialState(DateTimeOffset now) =>
        Credentials.Count == 0 ? AppFields.NoCredentials
        : Credentials.All(c => c.Status(now) == AppFields.Expired) ? AppFields.Expired
        : Credentials.Any(c => c.Status(now) == AppFields.Soon) ? AppFields.Soon
        : Credentials.Any(c => c.Status(now) == AppFields.Expired) ? AppFields.SomeExpired
        : AppFields.Current;
}

/// <summary>One API permission an app registration asks for (IDs; named on its page).</summary>
public sealed record RequestedPermission(string ResourceAppId, string PermissionId, bool IsApplication);

/// <summary>An owner of an app: a person (or an app).</summary>
public sealed record AppOwner(string Id, string Name, string? Detail);

/// <summary>Who an enterprise app comes from, as the Enterprise applications list sorts them.</summary>
public enum AppSource
{
    ThisOrganization,
    Microsoft,
    ThirdParty,
}

/// <summary>An enterprise app (service principal): an app's presence in this tenant, with its sign-in settings.</summary>
/// <param name="IsIntegrated">Tagged WindowsAzureActiveDirectoryIntegratedApp, the apps Enterprise applications shows by default.</param>
/// <param name="IsVisible">Shown to people in My Apps and the Office app launcher (no HideApp tag).</param>
/// <param name="SsoMode">preferredSingleSignOnMode: "saml", "password", "oidc" or null.</param>
public sealed record EnterpriseApp(
    string Id,
    string AppId,
    string Name,
    string? Publisher,
    bool IsVerifiedPublisher,
    AppSource Source,
    string Type,
    bool IsEnabled,
    bool AssignmentRequired,
    bool IsVisible,
    bool IsIntegrated,
    DateTimeOffset? Created,
    string? SsoMode = null,
    string? Homepage = null,
    string? Notes = null)
{
    /// <summary>The tenants Microsoft's own apps come from.</summary>
    public static readonly IReadOnlySet<string> MicrosoftTenants = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "f8cdef31-a31e-4b4a-93e4-5f571e91255a",
        "72f988bf-86f1-41af-91ab-2d7cd011db47",
    };

    public static string SourceText(AppSource source) => source switch
    {
        AppSource.ThisOrganization => AppFields.Ours,
        AppSource.Microsoft => AppFields.Microsoft,
        _ => AppFields.ThirdParty,
    };

    public static string? SsoText(string? mode) => mode switch
    {
        "saml" => "SAML",
        "password" => "Password-based",
        "oidc" => "OpenID Connect",
        "notSupported" or null or "" => null,
        var m => m,
    };
}

/// <summary>Every app registration and enterprise app, read live.</summary>
public sealed record AppDirectory(IReadOnlyList<AppRegistration> Registrations, IReadOnlyList<EnterpriseApp> EnterpriseApps)
{
    public EnterpriseApp? EnterpriseFor(string appId) => EnterpriseApps.FirstOrDefault(e => string.Equals(e.AppId, appId, StringComparison.OrdinalIgnoreCase));

    public AppRegistration? RegistrationFor(string appId) => Registrations.FirstOrDefault(r => string.Equals(r.AppId, appId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One permission, named: on an API ("Microsoft Graph"), delegated or application, with Microsoft's words for it.</summary>
/// <param name="Consented">Admin consent has been given for it (null when that isn't known).</param>
public sealed record NamedPermission(string ResourceId, string Resource, string Id, string Name, bool IsApplication, string? Description, bool? Consented = null);

/// <summary>Someone (or a group) given access to an enterprise app, and as what.</summary>
public sealed record AppUserAssignment(string Id, string PrincipalId, string PrincipalName, string PrincipalType, string RoleId, string? RoleName, DateTimeOffset? Created)
{
    /// <summary>The app role ID of an assignment that gives plain access (no particular role).</summary>
    public const string DefaultAccess = "00000000-0000-0000-0000-000000000000";
}

/// <summary>Delegated permissions given to an app: by an admin for everyone, or by one person for themselves.</summary>
/// <param name="PrincipalId">The person, when one person consented; null for admin consent.</param>
public sealed record DelegatedGrant(string Id, string ResourceId, string Resource, string? PrincipalId, string? PrincipalName, IReadOnlyList<string> Scopes);

/// <summary>An app role assignment an app holds on an API (an application permission granted).</summary>
public sealed record AppPermissionGrant(string Id, string ResourceId, string Resource, string RoleId, string? RoleName, DateTimeOffset? Created);

/// <summary>An app registration's page data: its requested permissions named, with whether admin consent covers each.</summary>
public sealed record AppRegistrationDetail(IReadOnlyList<NamedPermission> Permissions);

/// <summary>An enterprise app's page data: who's assigned, what it's been granted, its owners and the roles it offers.</summary>
public sealed record EnterpriseAppDetail(
    IReadOnlyList<AppUserAssignment> Assignments,
    IReadOnlyList<DelegatedGrant> DelegatedGrants,
    IReadOnlyList<AppPermissionGrant> AppPermissions,
    IReadOnlyList<AppOwner> Owners,
    IReadOnlyList<(string Id, string Name)> Roles);

/// <summary>App registrations and enterprise apps (8c), read live from Entra.</summary>
public interface IAppRecords
{
    Task<AppDirectory> GetAppsAsync(CancellationToken cancellationToken = default);

    Task<AppRegistrationDetail> GetRegistrationDetailAsync(AppRegistration app, CancellationToken cancellationToken = default);

    Task<EnterpriseAppDetail> GetEnterpriseDetailAsync(EnterpriseApp app, CancellationToken cancellationToken = default);

    /// <summary>When the app last signed anyone in; null when unknown (the report is slow, so it's read on its own).</summary>
    Task<DateTimeOffset?> GetLastSignInAsync(EnterpriseApp app, CancellationToken cancellationToken = default) => Task.FromResult<DateTimeOffset?>(null);
}

/// <summary>The app lists' fields.</summary>
public static class AppFields
{
    public const string Name = "name";
    public const string AppId = "appid";
    public const string Credentials = "credentials";
    public const string NextExpiry = "nextexpiry";
    public const string Secrets = "secrets";
    public const string Certificates = "certificates";
    public const string Audience = "audience";
    public const string Created = "created";
    public const string Permissions = "permissions";
    public const string Owners = "owners";
    public const string OwnerCount = "ownercount";

    public const string Source = "source";
    public const string Publisher = "publisher";
    public const string Enabled = "enabled";
    public const string Assignment = "assignment";
    public const string Visible = "visible";
    public const string Integrated = "integrated";
    public const string Sso = "sso";
    public const string Type = "type";

    public const string Expired = "Expired";
    public const string Soon = "Expires Soon";
    public const string SomeExpired = "Some Expired";
    public const string Current = "Current";
    public const string NoCredentials = "None";

    public const string Ours = "This Organization";
    public const string Microsoft = "Microsoft";
    public const string ThirdParty = "Third Party";

    public const string On = "Enabled";
    public const string Off = "Disabled";
    public const string Required = "Required";
    public const string Shown = "Visible";
    public const string Hidden = "Hidden";
    public const string Yes = "Yes";

    public static IReadOnlyList<Field<AppRegistration>> Registration(Func<DateTimeOffset> now) =>
    [
        new() { Key = Name, Label = "Name", Kind = FieldKind.Text, Searchable = true, Aliases = ["app"], Text = a => a.Name },
        new() { Key = AppId, Label = "Application (Client) ID", Kind = FieldKind.Text, Searchable = true, Aliases = ["id", "clientid"], Text = a => a.AppId },
        new()
        {
            Key = Credentials, Label = "Secrets & Certificates", Kind = FieldKind.Choice, Aliases = ["creds", "secret"],
            ChoiceOrder = [Expired, SomeExpired, Soon, Current, NoCredentials], Text = a => a.CredentialState(now()),
        },
        new() { Key = NextExpiry, Label = "Next Expiry", Kind = FieldKind.Date, Aliases = ["expires"], Date = a => a.NextExpiry(now()) },
        new() { Key = Secrets, Label = "Secrets", Kind = FieldKind.Number, Number = a => a.Credentials.Count(c => c.IsSecret) },
        new() { Key = Certificates, Label = "Certificates", Kind = FieldKind.Number, Aliases = ["certs"], Number = a => a.Credentials.Count(c => !c.IsSecret) },
        new() { Key = Audience, Label = "Who Can Sign In", Kind = FieldKind.Choice, Text = a => AppRegistration.AudienceText(a.Audience) },
        new() { Key = Created, Label = "Created", Kind = FieldKind.Date, Date = a => a.Created },
        new() { Key = Permissions, Label = "API Permissions", Kind = FieldKind.Number, Aliases = ["perms"], Number = a => a.Requested.Count },
        new() { Key = OwnerCount, Label = "Owners", Kind = FieldKind.Number, Number = a => a.Owners.Count },
        new() { Key = Owners, Label = "Owned By", Kind = FieldKind.Text, Searchable = true, Aliases = ["owner"], Text = a => string.Join(", ", a.Owners.Select(o => o.Name)) },
    ];

    public static IReadOnlyList<Field<EnterpriseApp>> Enterprise { get; } =
    [
        new() { Key = Name, Label = "Name", Kind = FieldKind.Text, Searchable = true, Aliases = ["app"], Text = a => a.Name },
        new() { Key = AppId, Label = "Application ID", Kind = FieldKind.Text, Searchable = true, Aliases = ["id"], Text = a => a.AppId },
        new() { Key = Publisher, Label = "Publisher", Kind = FieldKind.Text, Searchable = true, Text = a => a.Publisher },
        new() { Key = Source, Label = "From", Kind = FieldKind.Choice, ChoiceOrder = [Ours, Microsoft, ThirdParty], Text = a => EnterpriseApp.SourceText(a.Source) },
        new() { Key = Enabled, Label = "Sign-In", Kind = FieldKind.Choice, ChoiceOrder = [On, Off], Text = a => a.IsEnabled ? On : Off },
        new() { Key = Assignment, Label = "Assignment Required", Kind = FieldKind.Choice, EmptyLabel = "No", Text = a => a.AssignmentRequired ? Required : null },
        new() { Key = Visible, Label = "Visible to Users", Kind = FieldKind.Choice, ChoiceOrder = [Shown, Hidden], Text = a => a.IsVisible ? Shown : Hidden },
        new() { Key = Integrated, Label = "Enterprise Application", Kind = FieldKind.Choice, EmptyLabel = "No", Text = a => a.IsIntegrated ? Yes : null },
        new() { Key = Sso, Label = "Single Sign-On", Kind = FieldKind.Choice, EmptyLabel = "None", Text = a => EnterpriseApp.SsoText(a.SsoMode) },
        new() { Key = Type, Label = "Type", Kind = FieldKind.Choice, Text = a => a.Type },
        new() { Key = Created, Label = "Created", Kind = FieldKind.Date, Date = a => a.Created },
    ];
}
