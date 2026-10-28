using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>A person Identity Protection thinks may be compromised.</summary>
/// <param name="Level">"low", "medium", "high" or "none".</param>
/// <param name="State">"atRisk", "confirmedCompromised", "remediated", "dismissed" or "confirmedSafe".</param>
public sealed record RiskyUser(string Id, string Name, string? Upn, string Level, string State, string? Detail, DateTimeOffset? Updated);

/// <summary>One thing Identity Protection noticed: a sign-in or an account that looked wrong, and why.</summary>
/// <param name="EventType">Microsoft's name for it ("unfamiliarFeatures"); see <see cref="Risk.EventName"/>.</param>
/// <param name="Activity">"signin" or "user".</param>
public sealed record RiskDetection(string Id, string? UserId, string UserName, string? Upn, string EventType, string Level, string State, string Activity,
    string? Ip, string? Location, DateTimeOffset? Detected, string? Timing);

/// <summary>An app (service principal) Identity Protection thinks may be compromised.</summary>
public sealed record RiskyApp(string Id, string AppId, string Name, string Level, string State, string? Detail, DateTimeOffset? Updated);

/// <summary>Identity Protection's lists, or why each can't be read here.</summary>
public sealed record RiskDirectory(IReadOnlyList<RiskyUser> Users, IReadOnlyList<RiskDetection> Detections, IReadOnlyList<RiskyApp> Apps,
    string? UsersUnavailable = null, string? DetectionsUnavailable = null, string? AppsUnavailable = null);

/// <summary>Identity Protection (8d-2), read live. Everything here needs Microsoft Entra ID P2.</summary>
public interface IRiskRecords
{
    Task<RiskDirectory> GetRiskAsync(CancellationToken cancellationToken = default);
}

/// <summary>Risk in words.</summary>
public static class Risk
{
    public const string NeedsP2 = "Identity Protection (risky users, risky sign-ins and risky apps) needs Microsoft Entra ID P2.";

    public const string NeedsWorkloadPremium = "Risky apps need Microsoft Entra Workload ID Premium.";

    public static string LevelName(string level) => level switch
    {
        "high" => "High",
        "medium" => "Medium",
        "low" => "Low",
        "hidden" => "Hidden",
        _ => "None",
    };

    public static string StateName(string state) => state switch
    {
        "atRisk" => "At Risk",
        "confirmedCompromised" => "Confirmed Compromised",
        "remediated" => "Remediated",
        "dismissed" => "Dismissed",
        "confirmedSafe" => "Confirmed Safe",
        _ => "None",
    };

    static readonly Dictionary<string, string> Events = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unfamiliarFeatures"] = "Unfamiliar sign-in properties",
        ["anonymizedIPAddress"] = "Anonymous IP address",
        ["maliciousIPAddress"] = "Malicious IP address",
        ["leakedCredentials"] = "Leaked credentials",
        ["impossibleTravel"] = "Atypical travel",
        ["passwordSpray"] = "Password spray",
        ["mcasSuspiciousInboxManipulationRules"] = "Suspicious inbox manipulation rules",
        ["mcasImpossibleTravel"] = "Impossible travel (Defender for Cloud Apps)",
        ["suspiciousAPITraffic"] = "Suspicious API traffic",
        ["newCountry"] = "New country",
        ["adminConfirmedUserCompromised"] = "An admin confirmed it was compromised",
        ["anomalousToken"] = "Anomalous token",
        ["tokenIssuerAnomaly"] = "Token issuer anomaly",
        ["suspiciousBrowser"] = "Suspicious browser",
        ["investigationsThreatIntelligence"] = "Microsoft threat intelligence",
        ["riskyIPAddress"] = "Activity from a risky IP address",
        ["userReportedSuspiciousActivity"] = "They reported an MFA request they didn't start",
        ["suspiciousSendingPatterns"] = "Suspicious sending patterns",
        ["attackerinTheMiddle"] = "Attacker in the middle",
        ["generic"] = "Other risk",
    };

    /// <summary>Microsoft's detection type in words; unknown ones are split at their capitals.</summary>
    public static string EventName(string eventType) => Events.TryGetValue(eventType, out var name) ? name
        : string.Concat(eventType.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : i == 0 ? char.ToUpperInvariant(c).ToString() : c.ToString()));
}

/// <summary>The risk lists' fields.</summary>
public static class RiskFields
{
    public const string Name = "name";
    public const string Level = "level";
    public const string State = "state";
    public const string Updated = "updated";
    public const string Detail = "detail";
    public const string Event = "event";
    public const string Detected = "detected";
    public const string Ip = "ip";
    public const string Location = "location";
    public const string Activity = "activity";

    static readonly IReadOnlyList<string> Levels = ["High", "Medium", "Low", "None"];
    static readonly IReadOnlyList<string> States = ["At Risk", "Confirmed Compromised", "Remediated", "Dismissed", "Confirmed Safe"];

    public static IReadOnlyList<Field<RiskyUser>> Users { get; } =
    [
        new() { Key = Name, Label = "Person", Kind = FieldKind.Text, Searchable = true, Aliases = ["user", "who"], Text = u => $"{u.Name} {u.Upn}" },
        new() { Key = Level, Label = "Risk Level", Kind = FieldKind.Choice, ChoiceOrder = Levels, Text = u => Risk.LevelName(u.Level) },
        new() { Key = State, Label = "Risk State", Kind = FieldKind.Choice, ChoiceOrder = States, Text = u => Risk.StateName(u.State) },
        new() { Key = Detail, Label = "Detail", Kind = FieldKind.Text, Searchable = true, Text = u => u.Detail },
        new() { Key = Updated, Label = "Last Updated", Kind = FieldKind.Date, Date = u => u.Updated },
    ];

    public static IReadOnlyList<Field<RiskDetection>> Detections { get; } =
    [
        new() { Key = Event, Label = "Detection", Kind = FieldKind.Choice, Searchable = true, Aliases = ["type"], Text = d => Risk.EventName(d.EventType) },
        new() { Key = Name, Label = "Person", Kind = FieldKind.Text, Searchable = true, Aliases = ["user", "who"], Text = d => $"{d.UserName} {d.Upn}" },
        new() { Key = Level, Label = "Risk Level", Kind = FieldKind.Choice, ChoiceOrder = Levels, Text = d => Risk.LevelName(d.Level) },
        new() { Key = State, Label = "Risk State", Kind = FieldKind.Choice, ChoiceOrder = States, Text = d => Risk.StateName(d.State) },
        new() { Key = Activity, Label = "From", Kind = FieldKind.Choice, Text = d => d.Activity == "signin" ? "A Sign-In" : "The Account" },
        new() { Key = Ip, Label = "IP Address", Kind = FieldKind.Text, Searchable = true, Text = d => d.Ip },
        new() { Key = Location, Label = "Location", Kind = FieldKind.Text, Searchable = true, Aliases = ["where"], Text = d => d.Location },
        new() { Key = Detected, Label = "Detected", Kind = FieldKind.Date, Aliases = ["when"], Date = d => d.Detected },
    ];

    public static IReadOnlyList<Field<RiskyApp>> Apps { get; } =
    [
        new() { Key = Name, Label = "App", Kind = FieldKind.Text, Searchable = true, Aliases = ["app"], Text = a => $"{a.Name} {a.AppId}" },
        new() { Key = Level, Label = "Risk Level", Kind = FieldKind.Choice, ChoiceOrder = Levels, Text = a => Risk.LevelName(a.Level) },
        new() { Key = State, Label = "Risk State", Kind = FieldKind.Choice, ChoiceOrder = States, Text = a => Risk.StateName(a.State) },
        new() { Key = Updated, Label = "Last Updated", Kind = FieldKind.Date, Date = a => a.Updated },
    ];
}
