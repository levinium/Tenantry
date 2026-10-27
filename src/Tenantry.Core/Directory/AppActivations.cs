using System.Globalization;

namespace Tenantry.Core.Directory;

/// <summary>
/// Where someone has activated one Microsoft 365 Apps product (Microsoft's activation report, read
/// daily by Microsoft): how many computers and mobile devices, by platform. The report counts
/// devices; it doesn't name them, and Microsoft offers no way for other apps to deactivate one.
/// </summary>
/// <param name="Product">Microsoft's product name, as the report spells it ("MICROSOFT 365 APPS FOR BUSINESS").</param>
public sealed record OfficeActivation(string Product, DateOnly? LastActivated, int Windows, int Mac, int Windows10Mobile, int Ios, int Android, bool SharedComputer)
{
    /// <summary>Microsoft lets each person activate the apps on this many computers (and, separately, tablets and phones).</summary>
    public const int DeviceLimit = 5;

    public int Computers => Windows + Mac;

    public int Mobile => Ios + Android + Windows10Mobile;

    public int Total => Computers + Mobile;

    /// <summary>"Microsoft 365 Apps for business", "Office mobile apps".</summary>
    public string ProductName => Product.ToUpperInvariant() switch
    {
        "MICROSOFT 365 APPS FOR BUSINESS" => "Microsoft 365 Apps for business",
        "MICROSOFT 365 APPS FOR ENTERPRISE" => "Microsoft 365 Apps for enterprise",
        "OFFICE MOBILE APPS FOR OFFICE 365" => "Office mobile apps",
        "PROJECT FOR OFFICE 365" or "PROJECT ONLINE DESKTOP CLIENT" => "Project",
        "VISIO PRO FOR OFFICE 365" or "VISIO ONLINE PLAN 2" => "Visio",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Product.ToLowerInvariant()),
    };
}

public static class OfficeActivations
{
    /// <summary>The most computers any one product is activated on: what counts against the limit of five.</summary>
    public static int? Computers(IReadOnlyList<OfficeActivation>? activations) =>
        activations is null ? null : activations.Count == 0 ? 0 : activations.Max(a => a.Computers);

    public static int? Mobile(IReadOnlyList<OfficeActivation>? activations) =>
        activations is null ? null : activations.Count == 0 ? 0 : activations.Max(a => a.Mobile);

    public static DateOnly? LastActivated(IReadOnlyList<OfficeActivation>? activations) =>
        activations?.Select(a => a.LastActivated).Where(d => d is not null).DefaultIfEmpty().Max();
}

/// <summary>
/// A device someone signed in from, built from the sign-in log (Entra ID P1): its name when Entra
/// knows the device, its system, the Microsoft 365 apps used on it and when last seen.
/// </summary>
/// <param name="DeviceId">The Entra device ID; null for a device Entra doesn't know.</param>
/// <param name="Trust">"Entra joined", "Hybrid joined", "Registered" or "Not in Entra".</param>
public sealed record SignedInDevice(
    string? DeviceId,
    string Name,
    string? Os,
    string? Browser,
    IReadOnlyList<string> Apps,
    DateTimeOffset LastSeen,
    string Trust,
    bool? Compliant,
    bool? Managed,
    string? Location)
{
    public bool IsKnown => DeviceId is not null;
}

/// <summary>Microsoft 365 Apps on someone's devices: activations (the report) and where they've signed in (the sign-in log).</summary>
public interface IAppActivationRecords
{
    /// <summary>Their activations by product; or why they can't be read (no reports role, names hidden in reports).</summary>
    Task<Available<IReadOnlyList<OfficeActivation>>> GetActivationsAsync(string userPrincipalName, CancellationToken cancellationToken = default);

    /// <summary>The devices they signed in from in the last 30 days, newest first; or why not (needs Entra ID P1).</summary>
    Task<Available<IReadOnlyList<SignedInDevice>>> GetSignedInDevicesAsync(string userId, CancellationToken cancellationToken = default);
}

public static class SignedInDevices
{
    /// <summary>
    /// Groups sign-ins by device: Entra's device ID when it has one, otherwise the system and
    /// browser (an unregistered device has no name to go by).
    /// </summary>
    public static IReadOnlyList<SignedInDevice> Group(IEnumerable<(DateTimeOffset When, string App, string? DeviceId, string? Name, string? Os, string? Browser,
        string? TrustType, bool? Compliant, bool? Managed, string? Location)> signIns) =>
        signIns
            .Select(s => s with { Os = OsName(s.Os) })
            // An unregistered device can't be told apart from another like it: grouped by system and
            // browser without versions, so browser updates don't make it look like a new device.
            .GroupBy(s => s.DeviceId is { Length: > 0 } id ? "id:" + id : $"os:{Family(s.Os)}|{Family(s.Browser)}", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.MaxBy(s => s.When);
                var known = latest.DeviceId is { Length: > 0 };
                var name = g.Select(s => s.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                           ?? (latest.Os is { Length: > 0 } os ? $"{os} device" : "Unknown device");
                return new SignedInDevice(known ? latest.DeviceId : null, name, latest.Os, Family(latest.Browser) is { Length: > 0 } b ? b : null,
                    g.Select(s => s.App).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCulture).ToArray(),
                    latest.When, TrustName(latest.TrustType, known), latest.Compliant, latest.Managed, latest.Location);
            })
            .OrderByDescending(d => d.LastSeen)
            .ToArray();

    /// <summary>"Chrome 141.0.0" → "Chrome"; "Windows 10" stays (10 and 11 are different machines).</summary>
    static string Family(string? text) =>
        string.Join(' ', (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.Contains('.') || !w.Any(char.IsDigit)));

    /// <summary>The sign-in log's spelling ("MacOs", "Ios") as Apple and Microsoft write it.</summary>
    static string? OsName(string? os) => os?.ToLowerInvariant() switch
    {
        "macos" => "macOS",
        "ios" => "iOS",
        "ipados" => "iPadOS",
        _ => os,
    };

    static string TrustName(string? trustType, bool known) => trustType switch
    {
        "Azure AD joined" or "AzureAd" => "Entra joined",
        "Hybrid Azure AD joined" or "ServerAd" => "Hybrid joined",
        "Azure AD registered" or "Workplace" => "Registered",
        _ => known ? "In Entra" : "Not in Entra",
    };
}
