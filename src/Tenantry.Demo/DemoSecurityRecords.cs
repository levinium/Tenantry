using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's Security &amp; sign-in data, seeded per person: sign-in methods matching their MFA
/// registration, a week of sign-ins (a few failed), roles for admins, an app consent or two, and
/// a handful of Conditional Access policies. Risk reads as needing Entra ID P2, as on most tenants.
/// </summary>
public sealed partial class DemoUserRecords : ISecurityRecords
{
    readonly Dictionary<string, List<AuthMethod>> _methods = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<RoleHeld>> _roles = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<AppConsent>> _consents = new(StringComparer.Ordinal);

    async Task<UserSummary> PersonAsync(string userId, CancellationToken cancellationToken) =>
        (await directory.GetUsersAsync(cancellationToken)).First(u => u.Id == userId);

    public async Task<IReadOnlyList<AuthMethod>> GetAuthMethodsAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!_methods.TryGetValue(userId, out var methods))
        {
            var person = await PersonAsync(userId, cancellationToken);
            var random = new Random(StableSeed(userId) + 20);
            var added = DateTimeOffset.UtcNow.AddDays(-random.Next(60, 900));
            methods = [new AuthMethod("pw", "passwordMethods", "Password", null, null, false)];
            foreach (var m in person.Security?.Methods ?? [])
                methods.Add(m switch
                {
                    "microsoftAuthenticatorPush" => new AuthMethod($"ma-{userId}", "microsoftAuthenticatorMethods", "Microsoft Authenticator", "iPhone 15", added, true),
                    "mobilePhone" => new AuthMethod($"ph-{userId}", "phoneMethods", "Phone", $"+1 55501{random.Next(10000, 99999)} (mobile)", added.AddDays(-30), true),
                    _ => new AuthMethod($"em-{userId}", "emailMethods", "Email (password reset)", person.Mail?.Replace("@", ".home@"), added, true),
                });
            _methods[userId] = methods;
        }
        return methods.ToArray();
    }

    public async Task<Available<IReadOnlyList<SignInEvent>>> GetSignInsAsync(string userId, int days, bool failuresOnly, CancellationToken cancellationToken = default)
    {
        var person = await PersonAsync(userId, cancellationToken);
        var random = new Random(StableSeed(userId) + 21 + days);
        string[] apps = ["Outlook", "Microsoft Teams", "OneDrive", "Office 365 portal", "SharePoint Online"];
        var events = Enumerable.Range(0, Math.Min(60, days * random.Next(2, 6)))
            .Select(_ =>
            {
                var failed = random.NextDouble() < 0.08;
                return new SignInEvent(DateTimeOffset.UtcNow.AddMinutes(-random.Next(days * 24 * 60)), apps[random.Next(apps.Length)],
                    failed && random.NextDouble() < 0.5 ? "Lagos, NG" : "Springfield, US", $"203.0.113.{random.Next(2, 250)}", !failed,
                    failed ? "Invalid username or password." : null, random.NextDouble() < 0.6 ? "Windows 11 · Edge" : "iOS · Outlook",
                    failed ? null : "Conditional Access passed", failed ? null : person.Security?.MfaRegistered == true ? "MFA requirement satisfied by claim in the token" : null);
            })
            .Where(e => !failuresOnly || !e.Succeeded)
            .OrderByDescending(e => e.When)
            .ToArray();
        return new(events);
    }

    public Task<Available<UserRisk>> GetRiskAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Available<UserRisk>.Because("Risk detection (Identity Protection) needs Microsoft Entra ID P2."));

    public async Task<(IReadOnlyList<RoleHeld> Active, Available<IReadOnlyList<RoleHeld>> Eligible)> GetRolesAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!_roles.TryGetValue(userId, out var roles))
        {
            var person = await PersonAsync(userId, cancellationToken);
            roles = person.Security?.IsAdmin == true
                ? [new RoleHeld($"ra-{userId}", new Random(StableSeed(userId)).NextDouble() < 0.3 ? "Global Administrator" : "User Administrator", "The whole organization")]
                : [];
            _roles[userId] = roles;
        }
        return (roles.ToArray(), Available<IReadOnlyList<RoleHeld>>.Because("Eligible roles (Privileged Identity Management) need Microsoft Entra ID P2."));
    }

    public Task<IReadOnlyList<AppConsent>> GetAppConsentsAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!_consents.TryGetValue(userId, out var consents))
        {
            var random = new Random(StableSeed(userId) + 22);
            consents = random.NextDouble() < 0.5 ? [new AppConsent($"g1-{userId}", "Calendly", ["User.Read", "Calendars.ReadWrite", "offline_access"])] : [];
            if (random.NextDouble() < 0.15)
                consents.Add(new AppConsent($"g2-{userId}", "PDF Mail Helper", ["Mail.ReadWrite", "Mail.Send", "offline_access"]));
            _consents[userId] = consents;
        }
        return Task.FromResult<IReadOnlyList<AppConsent>>(consents.ToArray());
    }

    public async Task<IReadOnlyList<PolicyMatch>> GetConditionalAccessAsync(string userId, bool isGuest, CancellationToken cancellationToken = default)
    {
        var person = await PersonAsync(userId, cancellationToken);
        var admin = person.Security?.IsAdmin == true;
        return
        [
            new("Require MFA for everyone", "On", PolicyFit.Applies, "Everyone", "Require MFA", "All cloud apps"),
            new("Require MFA for admins", "On", admin ? PolicyFit.Applies : PolicyFit.NotTargeted, admin ? "Holds an admin role" : "Doesn't target them", "Require phishing-resistant MFA", "All cloud apps"),
            new("Block legacy sign-in", "On", PolicyFit.Applies, "Everyone", "Block access", "All cloud apps"),
            new("Guests: sign in again daily", "Report-only", isGuest ? PolicyFit.Applies : PolicyFit.NotTargeted, isGuest ? "Guests and external users" : "Doesn't target them", "Sign in again every 1 days", "Office 365"),
            new("Break-glass exclusion test", "Off", PolicyFit.Excluded, "Excluded: member of Emergency access", "Block access", "All cloud apps"),
        ];
    }

    /// <summary>Applies a 4c-1 change to the demo's data; false when it isn't one.</summary>
    public bool ApplySecurity(ChangeOp change) => change switch
    {
        RemoveAuthMethod m when _methods.TryGetValue(m.User.Id, out var list) => list.RemoveAll(x => x.Id == m.MethodId) >= 0,
        AddPhoneMethod p when _methods.TryGetValue(p.User.Id, out var list) => Added(list, new AuthMethod($"ph-{p.PhoneType}-{p.User.Id}", "phoneMethods", "Phone",
            $"{p.Number} ({p.PhoneType switch { "alternateMobile" => "alternate", "office" => "office", _ => "mobile" }})", DateTimeOffset.UtcNow, true)),
        RemoveRoleAssignment r when _roles.TryGetValue(r.User.Id, out var list) => list.RemoveAll(x => x.AssignmentId == r.AssignmentId) >= 0,
        RevokeAppConsent c when _consents.TryGetValue(c.User.Id, out var list) => list.RemoveAll(x => x.GrantId == c.GrantId) >= 0,
        RemoveAuthMethod or AddPhoneMethod or RemoveRoleAssignment or RevokeAppConsent or SetUserRisk => true,
        _ => false,
    };

    static bool Added(List<AuthMethod> list, AuthMethod method)
    {
        list.Add(method);
        return true;
    }
}
