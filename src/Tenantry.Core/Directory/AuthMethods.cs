using System.Globalization;
using System.Text.Json.Nodes;

namespace Tenantry.Core.Directory;

/// <summary>How a setting is edited.</summary>
public enum AuthSettingKind
{
    Bool,
    Number,
    Choice,
}

/// <summary>
/// One setting of a sign-in method or policy section, at a JSON path inside it
/// ("featureSettings/displayLocationInformationRequiredState/state").
/// </summary>
public sealed record AuthSetting(string Path, string Label, string Detail, AuthSettingKind Kind, IReadOnlyList<(string Value, string Label)>? Options = null,
    int Min = 0, int Max = 0, string Unit = "")
{
    public JsonNode? Read(JsonObject item)
    {
        JsonNode? node = item;
        foreach (var part in Path.Split('/'))
            node = node?[part];
        return node;
    }

    public void Write(JsonObject item, JsonNode? value)
    {
        var parts = Path.Split('/');
        var node = item;
        foreach (var part in parts[..^1])
        {
            if (node[part] is not JsonObject next)
                node[part] = next = new JsonObject();
            node = next;
        }
        node[parts[^1]] = value;
    }

    /// <summary>The value in words ("On", "60 minutes", "Microsoft Managed").</summary>
    public string Words(JsonObject item) => Kind switch
    {
        AuthSettingKind.Bool => Read(item)?.GetValue<bool>() == true ? "Yes" : "No",
        AuthSettingKind.Number => Read(item) is JsonValue v && v.TryGetValue<int>(out var n)
            ? $"{n.ToString(CultureInfo.CurrentCulture)}{(Unit.Length > 0 ? " " + (n == 1 && Unit.EndsWith('s') ? Unit[..^1] : Unit) : "")}"
            : "Not set",
        _ => Options?.FirstOrDefault(o => o.Value == Read(item)?.GetValue<string>()).Label ?? Read(item)?.GetValue<string>() ?? "Not set",
    };
}

/// <summary>
/// A sign-in method in the authentication methods policy (or one of the policy's own sections),
/// with Microsoft's words for it and the settings Tenantry edits.
/// </summary>
/// <param name="Targets">"list" (include and exclude lists), "single" (one includeTarget) or "none".</param>
/// <param name="Weak">Phishable or easily intercepted (text message, phone call, email): turning it on is a caution.</param>
public sealed record AuthMethodInfo(string Id, string Name, string Description, IReadOnlyList<AuthSetting> Settings, string Targets = "list", bool Weak = false,
    string? ManagedElsewhere = null);

/// <summary>The authentication methods policy as read (beta, which lists every method), with security defaults and group names.</summary>
public sealed record AuthMethodsPolicy(string Json, bool SecurityDefaultsOn, IReadOnlyDictionary<string, string> GroupNames)
{
    public JsonObject Node => JsonNode.Parse(Json)!.AsObject();

    /// <summary>Each method's configuration, by ID.</summary>
    public IReadOnlyList<JsonObject> Methods =>
        [.. (Node["authenticationMethodConfigurations"] as JsonArray ?? []).OfType<JsonObject>().Select(m => m.DeepClone().AsObject())];

    public JsonObject? Section(string section) => AuthMethods.SectionNode(Node, section)?.DeepClone() as JsonObject;
}

/// <summary>The authentication methods policy and security defaults, read live (8d).</summary>
public interface IAuthMethodRecords
{
    Task<AuthMethodsPolicy> GetAuthMethodsAsync(CancellationToken cancellationToken = default);
}

/// <summary>The methods and sections Tenantry shows, and how a change is described and sent.</summary>
public static class AuthMethods
{
    public const string Everyone = "all_users";

    static readonly IReadOnlyList<(string, string)> Managed = [("default", "Microsoft Managed"), ("enabled", "On"), ("disabled", "Off")];

    public static IReadOnlyList<AuthMethodInfo> Catalog { get; } =
    [
        new("MicrosoftAuthenticator", "Microsoft Authenticator", "Approve sign-ins in the app, or sign in with no password.",
        [
            new("isSoftwareOathEnabled", "Codes in the App", "People can also type a six-digit code from the app.", AuthSettingKind.Bool),
            new("featureSettings/displayAppInformationRequiredState/state", "Show the App Name", "Notifications say which app is signing in.", AuthSettingKind.Choice, Managed),
            new("featureSettings/displayLocationInformationRequiredState/state", "Show the Location", "Notifications show where the sign-in comes from, on a map.", AuthSettingKind.Choice, Managed),
            new("featureSettings/companionAppAllowedState/state", "Approve in Other Microsoft Apps", "Approve sign-ins in apps like Outlook, not only Authenticator.", AuthSettingKind.Choice, Managed),
        ]),
        new("Fido2", "Passkeys (FIDO2)", "Security keys and passkeys: phishing-resistant sign-in.",
        [
            new("isSelfServiceRegistrationAllowed", "People Can Add Their Own", "They set up a passkey themselves at My Security Info.", AuthSettingKind.Bool),
            new("isAttestationEnforced", "Check Each Key's Make", "Only keys whose maker proves what they are (attestation).", AuthSettingKind.Bool),
        ]),
        new("TemporaryAccessPass", "Temporary Access Pass", "A time-limited code an admin gives someone, to set up or recover sign-in.",
        [
            new("defaultLifetimeInMinutes", "Default Length", "How long a new pass works unless the admin picks otherwise.", AuthSettingKind.Number, Min: 10, Max: 43200, Unit: "minutes"),
            new("minimumLifetimeInMinutes", "Shortest", "", AuthSettingKind.Number, Min: 10, Max: 43200, Unit: "minutes"),
            new("maximumLifetimeInMinutes", "Longest", "", AuthSettingKind.Number, Min: 10, Max: 43200, Unit: "minutes"),
            new("defaultLength", "Length", "Characters in a pass.", AuthSettingKind.Number, Min: 8, Max: 48, Unit: "characters"),
            new("isUsableOnce", "One Use Only", "Each pass works for one sign-in.", AuthSettingKind.Bool),
        ]),
        new("SoftwareOath", "Authenticator App Codes (OATH)", "Six-digit codes from any authenticator app.", []),
        new("HardwareOath", "Hardware Tokens (OATH)", "Six-digit codes from a key-fob token.", []),
        new("Sms", "Text Message", "A code sent by text. Easy to intercept: prefer the Authenticator app.", [], Weak: true),
        new("Voice", "Phone Call", "A call that asks to press #. Easy to intercept: prefer the Authenticator app.",
            [new("isOfficePhoneAllowed", "Office Phones Too", "Calls can go to an office phone, not only a mobile.", AuthSettingKind.Bool)], Weak: true),
        new("Email", "Email Code", "A code sent by email, for password reset and for guests.",
            [new("allowExternalIdToUseEmailOtp", "Guests Can Sign In With an Emailed Code", "", AuthSettingKind.Choice, Managed)], Weak: true),
        new("X509Certificate", "Certificates", "Smart cards and device certificates.", [],
            ManagedElsewhere: "Its certificate rules and authorities are set in the Entra admin center."),
        new("QRCodePin", "QR Code", "Frontline workers scan a QR code and enter a PIN.",
        [
            new("pinLength", "PIN Length", "", AuthSettingKind.Number, Min: 8, Max: 20, Unit: "digits"),
            new("standardQRCodeLifetimeInDays", "Code Lasts", "", AuthSettingKind.Number, Min: 1, Max: 395, Unit: "days"),
        ]),
        new("VerifiableCredentials", "Verified ID", "Sign in with a verified digital ID card.", []),
        new("FederatedIdentityCredential", "Federated Identity Credential", "Sign-in trusted from another identity provider.", [],
            ManagedElsewhere: "Its profiles are set in the Entra admin center."),
        new("Password", "Password", "Always on. Whether a password can be changed after signing in another strong way.",
            [new("allowPasswordChangeAfterSecureSignIn", "Change the Password After a Strong Sign-In", "", AuthSettingKind.Bool)], Targets: "none"),
    ];

    /// <summary>The policy's own sections, edited like a method.</summary>
    public static IReadOnlyList<AuthMethodInfo> Sections { get; } =
    [
        new("registrationCampaign", "Registration Campaign", "Nudges people to set up the Authenticator app (or a passkey) when they sign in.",
        [
            new("snoozeDurationInDays", "Remind Again After", "", AuthSettingKind.Number, Min: 0, Max: 14, Unit: "days"),
            new("enforceRegistrationAfterAllowedSnoozes", "Limited Snoozes", "After three snoozes they must set it up.", AuthSettingKind.Bool),
        ]),
        new("systemCredentialPreferences", "System-Preferred MFA", "Asks for the strongest method each person has, rather than the one they chose.", []),
        new("reportSuspiciousActivitySettings", "Report Suspicious Activity", "People can report an MFA request they didn't start; their risk goes up.",
            [new("voiceReportingCode", "Phone Call Report Code", "The key they press on a phone call to report it (0 for none).", AuthSettingKind.Number, Min: 0, Max: 9)], Targets: "single"),
    ];

    public static AuthMethodInfo? Info(string id) => Catalog.Concat(Sections).FirstOrDefault(m => m.Id == id);

    public static bool IsSection(string id) => Sections.Any(s => s.Id == id);

    /// <summary>States: a method is On or Off; a section can also be Microsoft Managed.</summary>
    public static IReadOnlyList<(string Value, string Label)> States(string id) => IsSection(id) ? Managed : [("enabled", "On"), ("disabled", "Off")];

    public static JsonNode? SectionNode(JsonObject policy, string section) => section switch
    {
        "registrationCampaign" => policy["registrationEnforcement"]?["authenticationMethodsRegistrationCampaign"],
        _ => policy[section],
    };

    /// <summary>Who an item covers, in words, using group names.</summary>
    public static string Who(JsonObject item, IReadOnlyDictionary<string, string> names)
    {
        string Name(string? id) => id == Everyone ? "Everyone" : id is not null && names.TryGetValue(id, out var n) ? n : "A group";
        var include = item["includeTargets"] is JsonArray list ? list.Select(t => t?["id"]?.GetValue<string>()).ToArray()
            : item["includeTarget"]?["id"]?.GetValue<string>() is { } single ? [single] : [];
        var exclude = item["excludeTargets"] is JsonArray ex ? ex.Select(t => t?["id"]?.GetValue<string>()).ToArray() : [];
        if (include.Length == 0)
            return "Nobody";
        var who = include.Contains(Everyone) ? "Everyone" : string.Join(", ", include.Select(Name));
        return exclude.Length == 0 ? who : $"{who}, except {string.Join(", ", exclude.Select(Name))}";
    }

    /// <summary>The item in a line, for the review: state, who, and each setting.</summary>
    public static string Describe(string id, JsonObject item, IReadOnlyDictionary<string, string> names)
    {
        var info = Info(id);
        var state = item["state"]?.GetValue<string>();
        var parts = new List<string> { States(id).FirstOrDefault(s => s.Value == state).Label ?? state ?? "On" };
        if (info?.Targets != "none")
            parts.Add(Who(item, names));
        parts.AddRange((info?.Settings ?? []).Select(s => $"{s.Label}: {s.Words(item)}"));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// What's sent to change a method: its type, state, targets and the settings Tenantry edits
    /// (whole top-level objects, so their other fields go back as they were). Nothing read-only.
    /// </summary>
    public static JsonObject MethodBody(JsonObject edited)
    {
        var body = new JsonObject();
        foreach (var key in new[] { "@odata.type", "state", "includeTargets", "excludeTargets", "includeTarget" })
            if (edited[key] is { } v)
                body[key] = v.DeepClone();
        var id = edited["id"]?.GetValue<string>() ?? "";
        foreach (var setting in Info(id)?.Settings ?? [])
        {
            var top = setting.Path.Split('/')[0];
            if (edited[top] is { } v && body[top] is null)
                body[top] = v.DeepClone();
        }
        return body;
    }

    /// <summary>What's sent to change one of the policy's sections.</summary>
    public static JsonObject SectionBody(string section, JsonObject edited) => section == "registrationCampaign"
        ? new JsonObject { ["registrationEnforcement"] = new JsonObject { ["authenticationMethodsRegistrationCampaign"] = edited.DeepClone() } }
        : new JsonObject { [section] = edited.DeepClone() };

    /// <summary>A new include target shaped like the item's others (Authenticator's mode, a passkey's profiles).</summary>
    public static JsonObject TargetLike(JsonObject item, string id)
    {
        var template = item["includeTargets"] is JsonArray list ? list.OfType<JsonObject>().FirstOrDefault() : null;
        var target = template?.DeepClone().AsObject() ?? new JsonObject();
        target["id"] = id;
        target["targetType"] = "group";
        return target;
    }

    /// <summary>Settings Entra would refuse (a Temporary Access Pass's usual lifetime outside its shortest and longest).</summary>
    public static IReadOnlyList<string> Problems(string id, JsonObject item)
    {
        var problems = new List<string>();
        foreach (var s in Info(id)?.Settings ?? [])
            if (s.Kind == AuthSettingKind.Number && s.Read(item) is JsonValue v && v.TryGetValue<int>(out var n) && (n < s.Min || n > s.Max))
                problems.Add($"{s.Label} must be from {s.Min} to {s.Max}{(s.Unit.Length > 0 ? " " + s.Unit : "")}.");
        if (id == "TemporaryAccessPass")
        {
            int? Get(string key) => item[key] is JsonValue x && x.TryGetValue<int>(out var i) ? i : null;
            if (Get("minimumLifetimeInMinutes") is { } min && Get("maximumLifetimeInMinutes") is { } max && Get("defaultLifetimeInMinutes") is { } usual
                && (min > max || usual < min || usual > max))
                problems.Add("The usual lifetime must be between the shortest and the longest.");
        }
        if (item["includeTargets"] is JsonArray targets && targets.Count == 0 && item["state"]?.GetValue<string>() == "enabled")
            problems.Add("Choose who can use it, or turn it off.");
        return problems;
    }
}
