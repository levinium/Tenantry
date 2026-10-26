using System.Globalization;
using System.Net;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>The Security &amp; sign-in tab's reads (4c-1), live from Graph.</summary>
public sealed partial class GraphUserRecords : ISecurityRecords
{
    /// <summary>Each method's Graph type → its collection (for removing it) and how it reads.</summary>
    static readonly Dictionary<string, (string Segment, string Kind)> MethodTypes = new(StringComparer.Ordinal)
    {
        ["#microsoft.graph.passwordAuthenticationMethod"] = ("passwordMethods", "Password"),
        ["#microsoft.graph.phoneAuthenticationMethod"] = ("phoneMethods", "Phone"),
        ["#microsoft.graph.microsoftAuthenticatorAuthenticationMethod"] = ("microsoftAuthenticatorMethods", "Microsoft Authenticator"),
        ["#microsoft.graph.fido2AuthenticationMethod"] = ("fido2Methods", "Passkey or Security Key"),
        ["#microsoft.graph.windowsHelloForBusinessAuthenticationMethod"] = ("windowsHelloForBusinessMethods", "Windows Hello"),
        ["#microsoft.graph.emailAuthenticationMethod"] = ("emailMethods", "Email (Password Reset)"),
        ["#microsoft.graph.softwareOathAuthenticationMethod"] = ("softwareOathMethods", "Authenticator App Code"),
        ["#microsoft.graph.temporaryAccessPassAuthenticationMethod"] = ("temporaryAccessPassMethods", "Temporary Access Pass"),
        ["#microsoft.graph.platformCredentialAuthenticationMethod"] = ("platformCredentialMethods", "Platform Credential (Mac)"),
    };

    public async Task<IReadOnlyList<AuthMethod>> GetAuthMethodsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var methods = new List<AuthMethod>();
        await foreach (var m in graph.GetAllAsync($"users/{userId}/authentication/methods", cancellationToken))
        {
            var type = Text(m, "@odata.type") ?? "";
            var (segment, kind) = MethodTypes.TryGetValue(type, out var known) ? known : ("", type.Replace("#microsoft.graph.", "").Replace("AuthenticationMethod", ""));
            var detail = type switch
            {
                "#microsoft.graph.phoneAuthenticationMethod" => $"{Text(m, "phoneNumber")} ({Text(m, "phoneType") switch { "alternateMobile" => "alternate", "office" => "office", _ => "mobile" }})",
                "#microsoft.graph.emailAuthenticationMethod" => Text(m, "emailAddress"),
                "#microsoft.graph.temporaryAccessPassAuthenticationMethod" => Text(m, "methodUsabilityReason") is { } reason ? Words(reason) : null,
                _ => Text(m, "displayName") ?? Text(m, "model"),
            };
            methods.Add(new AuthMethod(Text(m, "id") ?? "", segment, kind, detail, When(m, "createdDateTime"),
                Removable: segment.Length > 0 && segment != "passwordMethods"));
        }
        // The password first, then the rest by name.
        return methods.OrderBy(m => m.Removable).ThenBy(m => m.Kind, StringComparer.CurrentCulture).ToArray();
    }

    /// <summary>
    /// The beta sign-in log: unlike v1.0 it has non-interactive sign-ins, the client app and
    /// whether MFA was needed. Days, type and result go to the service; the rest is filtered on
    /// what comes back.
    /// </summary>
    public async Task<Available<IReadOnlyList<SignInEvent>>> GetSignInsAsync(string userId, SignInQuery query, CancellationToken cancellationToken = default)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(query.Days, 1, 30)).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var type = query.Kind == SignInKind.NonInteractive ? "nonInteractiveUser" : "interactiveUser";
        var filter = $"userId eq '{userId}' and createdDateTime ge {since} and signInEventTypes/any(t: t eq '{type}')" + query.Result switch
        {
            SignInResult.Failed => " and status/errorCode ne 0",
            SignInResult.Succeeded => " and status/errorCode eq 0",
            _ => "",
        };
        try
        {
            var page = await graph.GetAsync($"https://graph.microsoft.com/beta/auditLogs/signIns?$filter={Uri.EscapeDataString(filter)}&$top={ISecurityRecords.SignInLimit}",
                cancellationToken);
            var events = page.GetProperty("value").EnumerateArray().Select(s =>
            {
                var status = s.TryGetProperty("status", out var st) ? st : default;
                var code = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("errorCode", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                var location = s.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object
                    ? string.Join(", ", new[] { Text(loc, "city"), Text(loc, "countryOrRegion") }.Where(x => !string.IsNullOrEmpty(x)))
                    : null;
                var device = s.TryGetProperty("deviceDetail", out var dev) && dev.ValueKind == JsonValueKind.Object
                    ? string.Join(" · ", new[] { Text(dev, "operatingSystem"), Text(dev, "browser") }.Where(x => !string.IsNullOrEmpty(x)))
                    : null;
                return new SignInEvent(
                    When(s, "createdDateTime") ?? DateTimeOffset.MinValue,
                    Text(s, "appDisplayName") ?? "An app",
                    string.IsNullOrEmpty(location) ? null : location,
                    Text(s, "ipAddress"),
                    code == 0,
                    code == 0 ? null : status.ValueKind == JsonValueKind.Object ? Text(status, "failureReason") : null,
                    string.IsNullOrEmpty(device) ? null : device,
                    Text(s, "conditionalAccessStatus") switch { "success" => "Conditional Access passed", "failure" => "Blocked by Conditional Access", _ => null },
                    status.ValueKind == JsonValueKind.Object ? Text(status, "additionalDetails") : null,
                    Text(s, "clientAppUsed"),
                    Text(s, "authenticationRequirement") switch
                    {
                        "multiFactorAuthentication" => true,
                        "singleFactorAuthentication" => false,
                        _ => null,
                    });
            }).OrderByDescending(e => e.When).ToArray();
            return new(events);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden && (e.Code ?? "").Contains("NonPremium", StringComparison.OrdinalIgnoreCase))
        {
            return Available<IReadOnlyList<SignInEvent>>.Because("Sign-in logs need Microsoft Entra ID P1.");
        }
    }

    public async Task<Available<UserRisk>> GetRiskAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await graph.GetAsync($"identityProtection/riskyUsers/{userId}", cancellationToken);
            return new(new UserRisk(Text(r, "riskLevel") ?? "none", Text(r, "riskState") ?? "none", Text(r, "riskDetail") is { } d && d != "none" ? Words(d) : null,
                When(r, "riskLastUpdatedDateTime")));
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return new(new UserRisk("none", "none", null, null));
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            return Available<UserRisk>.Because("Risk detection (Identity Protection) needs Microsoft Entra ID P2.");
        }
    }

    public async Task<(IReadOnlyList<RoleHeld> Active, Available<IReadOnlyList<RoleHeld>> Eligible)> GetRolesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var expand = "&$expand=roleDefinition($select=displayName)";
        var active = Roles($"roleManagement/directory/roleAssignments?$filter=principalId eq '{userId}'{expand}", cancellationToken);
        Available<IReadOnlyList<RoleHeld>> eligible;
        try
        {
            eligible = new(await Roles($"roleManagement/directory/roleEligibilitySchedules?$filter=principalId eq '{userId}'{expand}", cancellationToken));
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
        {
            eligible = Available<IReadOnlyList<RoleHeld>>.Because("Eligible roles (Privileged Identity Management) need Microsoft Entra ID P2.");
        }
        return (await active, eligible);
    }

    async Task<IReadOnlyList<RoleHeld>> Roles(string path, CancellationToken cancellationToken)
    {
        var roles = new List<RoleHeld>();
        await foreach (var a in graph.GetAllAsync(path, cancellationToken))
        {
            var name = a.TryGetProperty("roleDefinition", out var d) && d.ValueKind == JsonValueKind.Object ? Text(d, "displayName") : null;
            var scope = Text(a, "directoryScopeId") is null or "/" ? "The whole organization" : "One administrative unit";
            roles.Add(new RoleHeld(Text(a, "id") ?? "", name ?? "A role", scope));
        }
        return roles.OrderBy(r => r.RoleName, StringComparer.CurrentCulture).ToArray();
    }

    public async Task<IReadOnlyList<AppConsent>> GetAppConsentsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var grants = new List<(string Id, string Client, string[] Scopes)>();
        await foreach (var g in graph.GetAllAsync($"users/{userId}/oauth2PermissionGrants", cancellationToken))
            grants.Add((Text(g, "id") ?? "", Text(g, "clientId") ?? "",
                (Text(g, "scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        // Each app's name, read once (a person rarely has more than a handful).
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var client in grants.Select(g => g.Client).Distinct())
        {
            try
            {
                names[client] = Text(await graph.GetAsync($"servicePrincipals/{client}?$select=displayName", cancellationToken), "displayName") ?? client;
            }
            catch (GraphException)
            {
                names[client] = "An app that no longer exists";
            }
        }
        return grants.Select(g => new AppConsent(g.Id, names.GetValueOrDefault(g.Client, g.Client), g.Scopes))
            .OrderBy(c => c.AppName, StringComparer.CurrentCulture).ToArray();
    }

    /// <summary>
    /// Every Conditional Access policy and whether it targets this person, judged on who it
    /// includes and excludes: the person, their groups (direct or nested), their roles, guests.
    /// The other conditions (apps, locations, devices) narrow when it acts, not whom it covers.
    /// </summary>
    public async Task<IReadOnlyList<PolicyMatch>> GetConditionalAccessAsync(string userId, bool isGuest, CancellationToken cancellationToken = default)
    {
        var policies = new List<JsonElement>();
        await foreach (var p in graph.GetAllAsync("identity/conditionalAccess/policies", cancellationToken))
            policies.Add(p);
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var o in graph.GetAllAsync($"users/{userId}/transitiveMemberOf?$select=id,displayName,roleTemplateId", cancellationToken))
        {
            if (Text(o, "@odata.type") == "#microsoft.graph.directoryRole")
            {
                if (Text(o, "roleTemplateId") is { } template)
                    roles[template] = Text(o, "displayName") ?? "a role";
            }
            else if (Text(o, "id") is { } id)
                groups[id] = Text(o, "displayName") ?? "a group";
        }
        return policies.Select(p => Match(p, userId, isGuest, groups, roles))
            .OrderBy(m => m.Fit)
            .ThenBy(m => m.State switch { "On" => 0, "Report-only" => 1, _ => 2 })
            .ThenBy(m => m.Name, StringComparer.CurrentCulture)
            .ToArray();
    }

    internal static PolicyMatch Match(JsonElement policy, string userId, bool isGuest, IReadOnlyDictionary<string, string> groups, IReadOnlyDictionary<string, string> roles)
    {
        var state = Text(policy, "state") switch { "enabled" => "On", "enabledForReportingButNotEnforced" => "Report-only", _ => "Off" };
        var conditions = policy.TryGetProperty("conditions", out var c) ? c : default;
        var users = conditions.ValueKind == JsonValueKind.Object && conditions.TryGetProperty("users", out var u) ? u : default;

        string? Hit(string usersKey, string groupsKey, string rolesKey, string guestsKey)
        {
            if (users.ValueKind != JsonValueKind.Object)
                return null;
            var listed = Strings(users, usersKey);
            if (listed.Contains("All"))
                return "Everyone";
            if (listed.Contains(userId, StringComparer.OrdinalIgnoreCase))
                return "Named directly";
            if (isGuest && (listed.Contains("GuestsOrExternalUsers") || users.TryGetProperty(guestsKey, out var g) && g.ValueKind == JsonValueKind.Object))
                return "Guests and external users";
            if (Strings(users, groupsKey).FirstOrDefault(groups.ContainsKey) is { } group)
                return $"Member of {groups[group]}";
            if (Strings(users, rolesKey).FirstOrDefault(roles.ContainsKey) is { } role)
                return $"Holds {roles[role]}";
            return null;
        }

        var excluded = Hit("excludeUsers", "excludeGroups", "excludeRoles", "excludeGuestsOrExternalUsers");
        var included = Hit("includeUsers", "includeGroups", "includeRoles", "includeGuestsOrExternalUsers");
        var (fit, why) = excluded is not null ? (PolicyFit.Excluded, $"Excluded: {LowerFirst(excluded)}")
            : included is not null ? (PolicyFit.Applies, included)
            : (PolicyFit.NotTargeted, "Doesn't target them");

        var apps = conditions.ValueKind == JsonValueKind.Object && conditions.TryGetProperty("applications", out var a) ? Strings(a, "includeApplications") : [];
        var appsText = apps.Contains("All") ? "All cloud apps" : apps.Contains("Office365") ? "Office 365" : apps.Count == 1 ? "1 app" : $"{apps.Count} apps";
        return new PolicyMatch(Text(policy, "displayName") ?? "A policy", state, fit, why, Controls(policy), appsText);
    }

    static string Controls(JsonElement policy)
    {
        var parts = new List<string>();
        if (policy.TryGetProperty("grantControls", out var grant) && grant.ValueKind == JsonValueKind.Object)
        {
            var controls = Strings(grant, "builtInControls").Select(b => b switch
            {
                "block" => "Block access",
                "mfa" => "Require MFA",
                "compliantDevice" => "Require a compliant device",
                "domainJoinedDevice" => "Require a hybrid-joined device",
                "approvedApplication" => "Require an approved app",
                "compliantApplication" => "Require app protection",
                "passwordChange" => "Require a password change",
                _ => Words(b),
            }).ToList();
            if (grant.TryGetProperty("authenticationStrength", out var strength) && strength.ValueKind == JsonValueKind.Object && Text(strength, "displayName") is { } name)
                controls.Add($"Require {name}");
            if (controls.Count > 0)
                parts.Add(string.Join(Text(grant, "operator") == "AND" ? " and " : " or ", controls));
        }
        if (policy.TryGetProperty("sessionControls", out var session) && session.ValueKind == JsonValueKind.Object
            && session.TryGetProperty("signInFrequency", out var frequency) && frequency.ValueKind == JsonValueKind.Object
            && frequency.TryGetProperty("isEnabled", out var on) && on.ValueKind == JsonValueKind.True
            && frequency.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number)
            parts.Add($"Sign in again every {value.GetInt32()} {Text(frequency, "type")}");
        return parts.Count == 0 ? "No controls" : string.Join(" · ", parts);
    }

    static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray() : [];

    static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static DateTimeOffset? When(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Text(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>"adminConfirmedSigninCompromised" → "Admin confirmed signin compromised".</summary>
    static string Words(string camel)
    {
        var spaced = string.Concat(camel.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : ch.ToString()));
        return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
