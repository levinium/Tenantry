using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's authentication methods policy: the Authenticator app, passkeys and Temporary Access
/// Pass on for everyone, text messages on for one group, the weaker and specialist methods off,
/// and Microsoft-managed defaults for the policy's own sections. Security defaults are off, as
/// they must be while Conditional Access policies are on.
/// </summary>
public sealed partial class DemoUserRecords : IAuthMethodRecords
{
    JsonObject? _authPolicy;
    bool _securityDefaults;

    static JsonArray Everyone(JsonObject? extra = null)
    {
        var target = new JsonObject { ["targetType"] = "group", ["id"] = AuthMethods.Everyone, ["isRegistrationRequired"] = false };
        foreach (var (k, v) in extra ?? [])
            target[k] = v?.DeepClone();
        return [target];
    }

    static JsonObject Method(string id, string type, string state, JsonArray? include = null, JsonObject? settings = null)
    {
        var node = new JsonObject
        {
            ["@odata.type"] = $"#microsoft.graph.{type}AuthenticationMethodConfiguration",
            ["id"] = id,
            ["state"] = state,
            ["excludeTargets"] = new JsonArray(),
            ["includeTargets"] = include ?? Everyone(),
        };
        foreach (var (k, v) in settings ?? [])
            node[k] = v?.DeepClone();
        return node;
    }

    static JsonObject Feature(string state) => new()
    {
        ["state"] = state,
        ["includeTarget"] = new JsonObject { ["targetType"] = "group", ["id"] = AuthMethods.Everyone },
        ["excludeTarget"] = new JsonObject { ["targetType"] = "group", ["id"] = "00000000-0000-0000-0000-000000000000" },
    };

    async Task<JsonObject> AuthPolicyAsync(CancellationToken cancellationToken)
    {
        if (_authPolicy is not null)
            return _authPolicy;
        var groups = await directory.GetGroupsAsync(cancellationToken);
        var desk = groups.FirstOrDefault(g => g.Kind == GroupKind.Security) ?? groups.First();
        _authPolicy = new JsonObject
        {
            ["id"] = "authenticationMethodsPolicy",
            ["registrationEnforcement"] = new JsonObject
            {
                ["authenticationMethodsRegistrationCampaign"] = new JsonObject
                {
                    ["snoozeDurationInDays"] = 1,
                    ["enforceRegistrationAfterAllowedSnoozes"] = true,
                    ["state"] = "default",
                    ["excludeTargets"] = new JsonArray(),
                    ["includeTargets"] = new JsonArray(new JsonObject { ["id"] = AuthMethods.Everyone, ["targetType"] = "group", ["targetedAuthenticationMethod"] = "microsoftAuthenticator" }),
                },
            },
            ["systemCredentialPreferences"] = new JsonObject
            {
                ["state"] = "default",
                ["excludeTargets"] = new JsonArray(),
                ["includeTargets"] = new JsonArray(new JsonObject { ["id"] = AuthMethods.Everyone, ["targetType"] = "group" }),
            },
            ["reportSuspiciousActivitySettings"] = new JsonObject
            {
                ["state"] = "enabled",
                ["voiceReportingCode"] = 0,
                ["includeTarget"] = new JsonObject { ["id"] = AuthMethods.Everyone, ["targetType"] = "group" },
            },
            ["authenticationMethodConfigurations"] = new JsonArray(
                Method("MicrosoftAuthenticator", "microsoftAuthenticator", "enabled", Everyone(new JsonObject { ["authenticationMode"] = "any" }), new JsonObject
                {
                    ["isSoftwareOathEnabled"] = false,
                    ["featureSettings"] = new JsonObject
                    {
                        ["companionAppAllowedState"] = Feature("default"),
                        ["numberMatchingRequiredState"] = Feature("enabled"),
                        ["displayAppInformationRequiredState"] = Feature("enabled"),
                        ["displayLocationInformationRequiredState"] = Feature("default"),
                    },
                }),
                Method("Fido2", "fido2", "enabled", settings: new JsonObject { ["isSelfServiceRegistrationAllowed"] = true, ["isAttestationEnforced"] = false }),
                Method("TemporaryAccessPass", "temporaryAccessPass", "enabled", settings: new JsonObject
                {
                    ["defaultLifetimeInMinutes"] = 60, ["defaultLength"] = 8, ["minimumLifetimeInMinutes"] = 60, ["maximumLifetimeInMinutes"] = 480, ["isUsableOnce"] = true,
                }),
                Method("SoftwareOath", "softwareOath", "enabled"),
                Method("HardwareOath", "hardwareOath", "disabled"),
                Method("Sms", "sms", "enabled", new JsonArray(new JsonObject { ["targetType"] = "group", ["id"] = desk.Id, ["isRegistrationRequired"] = false, ["isUsableForSignIn"] = false })),
                Method("Voice", "voice", "disabled", settings: new JsonObject { ["isOfficePhoneAllowed"] = false }),
                Method("Email", "email", "enabled", settings: new JsonObject { ["allowExternalIdToUseEmailOtp"] = "default" }),
                Method("X509Certificate", "x509Certificate", "disabled"),
                Method("QRCodePin", "qrCodePin", "disabled", settings: new JsonObject { ["pinLength"] = 8, ["standardQRCodeLifetimeInDays"] = 365 }),
                Method("VerifiableCredentials", "verifiableCredentials", "disabled"),
                Method("FederatedIdentityCredential", "federatedIdentityCredential", "disabled"),
                new JsonObject { ["@odata.type"] = "#microsoft.graph.passwordAuthenticationMethodConfiguration", ["id"] = "Password", ["state"] = "enabled", ["allowPasswordChangeAfterSecureSignIn"] = false }),
        };
        return _authPolicy;
    }

    public async Task<AuthMethodsPolicy> GetAuthMethodsAsync(CancellationToken cancellationToken = default)
    {
        var policy = await AuthPolicyAsync(cancellationToken);
        var names = (await directory.GetGroupsAsync(cancellationToken)).ToDictionary(g => g.Id, g => g.DisplayName, StringComparer.OrdinalIgnoreCase);
        return new AuthMethodsPolicy(policy.ToJsonString(), _securityDefaults, names);
    }

    /// <summary>Applies a sign-in method or security defaults change to the demo; null when it isn't one.</summary>
    public ApplyResult? ApplyAuthMethods(ChangeOp change)
    {
        switch (change)
        {
            case SetAuthMethodConfig m when _authPolicy is not null:
                var body = JsonNode.Parse(m.Json)!.AsObject();
                if (m.Method.Kind == "authpolicy")
                {
                    foreach (var (k, v) in body)
                        _authPolicy[k] = v?.DeepClone();
                }
                else if ((_authPolicy["authenticationMethodConfigurations"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c => c["id"]?.GetValue<string>() == m.Method.Id) is { } config)
                {
                    foreach (var (k, v) in body)
                        config[k] = v?.DeepClone();
                }
                return new ApplyResult(ChangeOutcome.Applied);
            case SetSecurityDefaults d:
                if (d.New && CaPolicies().Any(p => p.State == "enabled"))
                    return new ApplyResult(ChangeOutcome.Failed, "Security defaults can't be turned on while Conditional Access policies are on (demo).");
                _securityDefaults = d.New;
                return new ApplyResult(ChangeOutcome.Applied);
            default:
                return null;
        }
    }
}
