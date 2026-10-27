using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's Microsoft 365 Apps: activations as the directory seeded them, and the devices people
/// signed in from: their own devices (as the Devices tab shows them), plus now and then a home
/// computer Entra doesn't know.
/// </summary>
public sealed partial class DemoUserRecords : IAppActivationRecords
{
    public async Task<Available<IReadOnlyList<OfficeActivation>>> GetActivationsAsync(string userPrincipalName, CancellationToken cancellationToken = default)
    {
        var person = (await directory.GetUsersAsync(cancellationToken)).FirstOrDefault(u => string.Equals(u.UserPrincipalName, userPrincipalName, StringComparison.OrdinalIgnoreCase));
        return new(person?.Activations ?? []);
    }

    public async Task<Available<IReadOnlyList<SignedInDevice>>> GetSignedInDevicesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var devices = (await GetDevicesAsync(userId, cancellationToken)).Value ?? [];
        var random = new Random(StableSeed(userId) + 40);
        var now = DateTimeOffset.UtcNow;
        var signIns = new List<(DateTimeOffset, string, string?, string?, string?, string?, string?, bool?, bool?, string?)>();
        foreach (var d in devices)
        {
            string[] apps = d.Os is "iOS" or "iPadOS" ? ["Outlook Mobile", "Microsoft Teams"] : ["Microsoft Office", "Outlook", "Microsoft Teams", "OneDrive SyncEngine"];
            foreach (var app in apps.Take(random.Next(1, apps.Length + 1)))
                signIns.Add((now.AddHours(-random.Next(1, 200)), app, d.DeviceId, d.Name, d.Os == "Windows" ? "Windows 11" : d.Os, d.Os == "Windows" ? "Edge 141.0.0" : null,
                    d.JoinType == "Entra joined" ? "Azure AD joined" : "Azure AD registered", d.Compliant, d.IsManaged, "New York, US"));
        }
        if (random.NextDouble() < 0.35)
            signIns.Add((now.AddDays(-random.Next(2, 20)), "Microsoft Office", null, null, "MacOs", "Safari 18.6", null, null, null, "Brooklyn, US"));
        return new(SignedInDevices.Group(signIns));
    }
}
