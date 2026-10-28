using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's Conditional Access: the familiar baseline policies (MFA for everyone and for admins,
/// legacy sign-in blocked, guests asked to sign in daily in report-only), one off, a named location,
/// and a rough What If that judges who, which kind of app and the state, as Entra would.
/// </summary>
public sealed partial class DemoUserRecords : IConditionalAccessRecords
{
    List<CaPolicy>? _caPolicies;

    static readonly NamedLocation Office = new("loc-office", "Head Office", true, NamedLocation.IpRanges, ["203.0.113.0/24"]);

    readonly List<NamedLocation> _locations = [Office, new("loc-home", "Home Countries", false, NamedLocation.Countries, ["US", "CA"])];

    /// <summary>Microsoft's three built-in authentication strengths, as Entra lists them.</summary>
    static readonly AuthStrength[] Strengths =
    [
        new("00000000-0000-0000-0000-000000000002", "Multifactor authentication", "Combinations of methods that satisfy strong authentication, such as a password and SMS", true,
            ["windowsHelloForBusiness", "fido2", "x509CertificateMultiFactor", "deviceBasedPush", "temporaryAccessPassOneTime", "password,microsoftAuthenticatorPush",
                "password,softwareOath", "password,hardwareOath", "password,sms", "password,voice"]),
        new("00000000-0000-0000-0000-000000000003", "Passwordless MFA", "Passwordless methods that satisfy strong authentication, such as Passwordless sign-in with the Microsoft Authenticator", true,
            ["windowsHelloForBusiness", "fido2", "x509CertificateMultiFactor", "deviceBasedPush"]),
        new("00000000-0000-0000-0000-000000000004", "Phishing-resistant MFA", "Phishing-resistant, Passwordless methods for the strongest authentication, such as a FIDO2 security key", true,
            ["windowsHelloForBusiness", "fido2", "x509CertificateMultiFactor"]),
    ];

    static CaPolicy Policy(string id, string name, string state, int ageDays, JsonObject conditions, JsonObject? grant, JsonObject? session = null)
    {
        var json = new JsonObject
        {
            ["id"] = id,
            ["displayName"] = name,
            ["state"] = state,
            ["conditions"] = conditions,
            ["grantControls"] = grant,
            ["sessionControls"] = session,
        };
        var created = DateTimeOffset.UtcNow.AddDays(-ageDays);
        return new CaPolicy(id, name, state, created, created.AddDays(ageDays / 3), json.ToJsonString());
    }

    static JsonObject Users(JsonArray include, JsonArray? exclude = null, JsonArray? roles = null) => new()
    {
        ["includeUsers"] = include,
        ["excludeUsers"] = exclude ?? [],
        ["includeRoles"] = roles ?? [],
    };

    static JsonObject Grant(params string[] controls) => new() { ["operator"] = "OR", ["builtInControls"] = new JsonArray([.. controls.Select(c => (JsonNode)c)]) };

    List<CaPolicy> CaPolicies() => _caPolicies ??=
    [
        Policy("ca-mfa-all", "Require MFA for everyone", "enabled", 400,
            new JsonObject { ["users"] = Users(["All"]), ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray("All") }, ["clientAppTypes"] = new JsonArray("all") },
            Grant("mfa")),
        Policy("ca-mfa-admins", "Require MFA for admins", "enabled", 380,
            new JsonObject
            {
                ["users"] = Users([], roles: ["62e90394-69f5-4237-9190-012177145e10", "fe930be7-5e62-47db-91af-98c3a49a38b1"]),
                ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray("All") },
                ["clientAppTypes"] = new JsonArray("all"),
            },
            Grant("mfa")),
        Policy("ca-legacy", "Block legacy sign-in", "enabled", 360,
            new JsonObject
            {
                ["users"] = Users(["All"]),
                ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray("All") },
                ["clientAppTypes"] = new JsonArray("exchangeActiveSync", "other"),
            },
            Grant("block")),
        Policy("ca-guests", "Guests: sign in again daily", "enabledForReportingButNotEnforced", 90,
            new JsonObject
            {
                ["users"] = Users(["GuestsOrExternalUsers"]),
                ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray("Office365") },
                ["clientAppTypes"] = new JsonArray("all"),
            },
            null,
            new JsonObject { ["signInFrequency"] = new JsonObject { ["isEnabled"] = true, ["value"] = 1, ["type"] = "days", ["frequencyInterval"] = "timeBased" } }),
        Policy("ca-outside", "Block sign-ins away from the office", "disabled", 30,
            new JsonObject
            {
                ["users"] = Users(["All"]),
                ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray("MicrosoftAdminPortals") },
                ["locations"] = new JsonObject { ["includeLocations"] = new JsonArray("All"), ["excludeLocations"] = new JsonArray(Office.Id) },
                ["clientAppTypes"] = new JsonArray("all"),
            },
            Grant("block")),
    ];

    public Task<CaDirectory> GetPoliciesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new CaDirectory([.. CaPolicies().OrderBy(p => p.Name, StringComparer.CurrentCulture)],
            new CaNames(new Dictionary<string, string>(), new Dictionary<string, string>(),
                Catalog.ToDictionary(c => c.Id, c => c.Name), new Dictionary<string, string>(),
                _locations.ToDictionary(l => l.Id, l => l.Name), Strengths.ToDictionary(x => x.Id, x => x.Name)),
            [.. _locations.OrderBy(l => l.Name, StringComparer.CurrentCulture)], Strengths));

    public async Task<IReadOnlyList<WhatIfResult>> WhatIfAsync(WhatIfInput input, CancellationToken cancellationToken = default)
    {
        var person = (await directory.GetUsersAsync(cancellationToken)).FirstOrDefault(u => u.Id == input.UserId);
        var guest = person?.Kind == UserKind.Guest;
        var admin = person?.Security?.IsAdmin == true;
        return
        [
            .. CaPolicies().Select(p =>
            {
                var node = p.Node;
                var users = node["conditions"]?["users"];
                var include = (users?["includeUsers"] as JsonArray)?.Select(x => x?.GetValue<string>()).ToArray() ?? [];
                var roles = (users?["includeRoles"] as JsonArray)?.Count ?? 0;
                var clients = (node["conditions"]?["clientAppTypes"] as JsonArray)?.Select(x => x?.GetValue<string>()).ToArray() ?? [];
                var reasons = new List<string>();
                if (p.State == "disabled")
                    reasons.Add(CaText.Reason("policyNotEnabled"));
                if (!(include.Contains("All") || (guest && include.Contains("GuestsOrExternalUsers")) || (admin && roles > 0)))
                    reasons.Add(CaText.Reason("users"));
                if (!clients.Contains("all") && !clients.Contains(input.ClientApp))
                    reasons.Add(CaText.Reason("clientApps"));
                return new WhatIfResult(p.Id, p.Name, p.State, reasons.Count == 0, reasons, CaText.Requires(node, CaNames.Empty));
            }).OrderByDescending(r => r.Applies).ThenBy(r => r.Name, StringComparer.CurrentCulture),
        ];
    }

    /// <summary>Applies a Conditional Access change to the demo; false when it isn't one.</summary>
    public bool ApplyCa(ChangeOp change)
    {
        if (ApplyCaEdit(change))
            return true;
        switch (change)
        {
            case CreateNamedLocation create:
                _locations.Add(CaPlaces.Read($"loc-{Guid.NewGuid():N}", create.Json));
                return true;
            case UpdateNamedLocation update:
                var at = _locations.FindIndex(l => l.Id == update.Location.Id);
                if (at >= 0)
                    _locations[at] = CaPlaces.Read(update.Location.Id, update.Json);
                return true;
            case DeleteNamedLocation delete:
                _locations.RemoveAll(l => l.Id == delete.Location.Id);
                return true;
        }
        var policies = CaPolicies();
        switch (change)
        {
            case SetCaPolicyState state:
                var index = policies.FindIndex(p => p.Id == state.Policy.Id);
                if (index >= 0)
                {
                    var node = policies[index].Node;
                    node["state"] = state.New;
                    policies[index] = policies[index] with { State = state.New, Modified = DateTimeOffset.UtcNow, Json = node.ToJsonString() };
                }
                return true;
            case DeleteCaPolicy delete:
                policies.RemoveAll(p => p.Id == delete.Policy.Id);
                return true;
            default:
                return false;
        }
    }
}
