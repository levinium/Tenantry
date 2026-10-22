namespace Tenantry.Core.Directory;

/// <summary>
/// A user's MFA registration, from Entra's registration report (synced with the directory; the
/// report needs Entra ID P1). Absent when the tenant has no P1 or the report hasn't been read.
/// </summary>
/// <param name="IsAdmin">Holds an admin role (as Entra's report judges it).</param>
/// <param name="Methods">Entra's names for what they registered ("microsoftAuthenticatorPush", "mobilePhone").</param>
public sealed record UserSecurity(bool MfaRegistered, bool IsAdmin, IReadOnlyList<string> Methods, string? DefaultMethod)
{
    /// <summary>Entra's method names as a person reads them.</summary>
    public static string MethodName(string method) => method switch
    {
        "microsoftAuthenticatorPush" => "Microsoft Authenticator",
        "microsoftAuthenticatorPasswordless" => "Authenticator (passwordless)",
        "softwareOneTimePasscode" => "Authenticator app code",
        "hardwareOneTimePasscode" => "Hardware token",
        "mobilePhone" => "Mobile phone",
        "alternateMobilePhone" => "Alternate phone",
        "officePhone" => "Office phone",
        "fido2SecurityKey" or "passKeyDeviceBound" or "passKeyDeviceBoundAuthenticator" => "Passkey or security key",
        "windowsHelloForBusiness" => "Windows Hello",
        "email" => "Email",
        "securityQuestion" => "Security questions",
        "temporaryAccessPass" => "Temporary Access Pass",
        "push" => "Microsoft Authenticator",
        "sms" => "Text message",
        "voiceMobile" or "voiceAlternateMobile" or "voiceOffice" => "Phone call",
        _ => method,
    };
}

/// <summary>One way a person signs in or proves it's them.</summary>
/// <param name="Segment">The Graph collection it lives in ("phoneMethods"), for removing it.</param>
/// <param name="Removable">The password can't be removed; everything else can.</param>
public sealed record AuthMethod(string Id, string Segment, string Kind, string? Detail, DateTimeOffset? Added, bool Removable);

/// <summary>One sign-in from the sign-in log (Entra ID P1).</summary>
/// <param name="ClientApp">Entra's client app ("Browser", "Mobile Apps and Desktop clients", "IMAP4"…).</param>
/// <param name="MultiFactor">Whether the sign-in needed more than a password; null when the log doesn't say.</param>
public sealed record SignInEvent(DateTimeOffset When, string App, string? Location, string? IpAddress, bool Succeeded, string? Problem,
    string? Device, string? ConditionalAccess, string? Detail, string? ClientApp = null, bool? MultiFactor = null)
{
    /// <summary>The client as a person reads it; the old protocols (which often skip MFA) named as such.</summary>
    public string ClientKind => ClientApp switch
    {
        null or "" => "Unknown",
        "Browser" => "Browser",
        "Mobile Apps and Desktop clients" => "Apps and desktop clients",
        var other => $"Legacy: {other}",
    };
}

public enum SignInKind
{
    /// <summary>A person signed in (typed a password, approved a prompt).</summary>
    Interactive,

    /// <summary>Apps refreshing their access in the background: most of the traffic, and where stolen tokens show.</summary>
    NonInteractive,
}

public enum SignInResult
{
    All,
    Succeeded,
    Failed,
}

/// <summary>What the sign-in log is asked for; the rest of the filtering happens on what comes back.</summary>
public sealed record SignInQuery(int Days, SignInKind Kind = SignInKind.Interactive, SignInResult Result = SignInResult.All);

/// <summary>A directory role held directly (Entra roles; not Azure resource roles).</summary>
/// <param name="Scope">"The whole organization", or the administrative unit it's limited to.</param>
public sealed record RoleHeld(string AssignmentId, string RoleName, string Scope);

/// <summary>An app they let read their data on their own (a delegated consent).</summary>
public sealed record AppConsent(string GrantId, string AppName, IReadOnlyList<string> Scopes);

public enum PolicyFit
{
    Applies,
    Excluded,
    NotTargeted,
}

/// <summary>A Conditional Access policy and whether it targets this person.</summary>
/// <param name="State">"On", "Report-only" or "Off".</param>
/// <param name="Why">Why it applies or doesn't ("Everyone", "Member of Finance", "Excluded: in Break glass").</param>
/// <param name="Controls">What it asks for ("Require MFA", "Block access").</param>
/// <param name="Apps">Which apps it covers ("All cloud apps", "3 apps").</param>
public sealed record PolicyMatch(string Name, string State, PolicyFit Fit, string Why, string Controls, string Apps);

/// <summary>Identity Protection's view of the person (Entra ID P2).</summary>
public sealed record UserRisk(string Level, string State, string? Detail, DateTimeOffset? Updated)
{
    public bool IsAtRisk => State is "atRisk" or "confirmedCompromised";
}

/// <summary>Something a tab can show, or why it can't (a license the tenant lacks).</summary>
public sealed record Available<T>(T? Value, string? Unavailable = null)
{
    public static Available<T> Because(string reason) => new(default, reason);
}

/// <summary>The Security &amp; sign-in tab's reads, live from Graph. Each is separate so a card shows as soon as its own data arrives.</summary>
public interface ISecurityRecords
{
    Task<IReadOnlyList<AuthMethod>> GetAuthMethodsAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>The latest sign-ins matching the query (up to <see cref="SignInLimit"/>).</summary>
    Task<Available<IReadOnlyList<SignInEvent>>> GetSignInsAsync(string userId, SignInQuery query, CancellationToken cancellationToken = default);

    /// <summary>How many sign-ins one query brings back at most.</summary>
    const int SignInLimit = 500;

    Task<Available<UserRisk>> GetRiskAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Active roles, and eligible ones (Privileged Identity Management, Entra ID P2) or why they can't be read.</summary>
    Task<(IReadOnlyList<RoleHeld> Active, Available<IReadOnlyList<RoleHeld>> Eligible)> GetRolesAsync(string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppConsent>> GetAppConsentsAsync(string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PolicyMatch>> GetConditionalAccessAsync(string userId, bool isGuest, CancellationToken cancellationToken = default);
}
