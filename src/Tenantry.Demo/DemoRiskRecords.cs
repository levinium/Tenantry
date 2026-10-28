using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's Identity Protection. Without P2 every list says it needs P2, as Graph does. As a P2
/// tenant: a few people at risk (one already confirmed compromised, one dismissed), the detections
/// behind them on documentation-range addresses, and one third-party app at risk.
/// </summary>
public sealed partial class DemoUserRecords : IRiskRecords
{
    List<RiskyUser>? _riskyUsers;
    List<RiskDetection>? _detections;
    List<RiskyApp>? _riskyApps;

    async Task EnsureRiskAsync(CancellationToken cancellationToken)
    {
        if (_riskyUsers is not null)
            return;
        var people = (await directory.GetUsersAsync(cancellationToken)).Where(u => u.Kind == UserKind.Member).Skip(5).Take(5).ToArray();
        var now = DateTimeOffset.UtcNow;
        (string Level, string State, string? Detail, int Hours)[] shape =
        [
            ("high", "atRisk", null, 3),
            ("medium", "atRisk", null, 20),
            ("low", "atRisk", null, 50),
            ("high", "confirmedCompromised", "Admin confirmed user compromised", 120),
            ("medium", "dismissed", "Admin dismissed all risk for user", 300),
        ];
        _riskyUsers = [.. people.Select((p, i) => new RiskyUser(p.Id, p.DisplayName, p.UserPrincipalName, shape[i].Level, shape[i].State, shape[i].Detail,
            now.AddHours(-shape[i].Hours)))];
        (int Person, string Type, string Level, string Ip, string Place, int Hours, string Activity)[] seen =
        [
            (0, "anonymizedIPAddress", "high", "198.51.100.23", "Amsterdam, NL", 3, "signin"),
            (0, "unfamiliarFeatures", "medium", "198.51.100.23", "Amsterdam, NL", 4, "signin"),
            (0, "leakedCredentials", "high", "", "", 30, "user"),
            (1, "impossibleTravel", "medium", "203.0.113.77", "Singapore, SG", 20, "signin"),
            (1, "userReportedSuspiciousActivity", "medium", "203.0.113.77", "Singapore, SG", 21, "signin"),
            (2, "unfamiliarFeatures", "low", "192.0.2.10", "Toronto, Ontario, CA", 50, "signin"),
            (3, "passwordSpray", "high", "198.51.100.200", "Sao Paulo, BR", 125, "signin"),
            (3, "adminConfirmedUserCompromised", "high", "", "", 120, "user"),
            (4, "newCountry", "medium", "203.0.113.4", "Dublin, IE", 310, "signin"),
        ];
        _detections = [.. seen.Select((s, i) => new RiskDetection($"rd-{i}", people[s.Person].Id, people[s.Person].DisplayName, people[s.Person].UserPrincipalName, s.Type,
            s.Level, _riskyUsers[s.Person].State, s.Activity, s.Ip.Length > 0 ? s.Ip : null, s.Place.Length > 0 ? s.Place : null, now.AddHours(-s.Hours),
            s.Activity == "signin" ? "realtime" : "offline"))];
        _riskyApps = [new RiskyApp("sp-litware", "1e1e1e1e-0000-4000-8000-0000litware", "Litware Calendar Sync", "medium", "atRisk", null, now.AddHours(-8))];
    }

    public async Task<RiskDirectory> GetRiskAsync(CancellationToken cancellationToken = default)
    {
        if (!pim)
            return new RiskDirectory([], [], [], Risk.NeedsP2, Risk.NeedsP2, Risk.NeedsWorkloadPremium);
        await EnsureRiskAsync(cancellationToken);
        return new RiskDirectory([.. _riskyUsers!], [.. _detections!], [.. _riskyApps!]);
    }

    /// <summary>
    /// Follows risk changes in the demo's lists. A person's risk change also goes on to the
    /// Security &amp; sign-in tab's records (null here); an app's is finished here.
    /// </summary>
    public ApplyResult? ApplyRisk(ChangeOp change)
    {
        switch (change)
        {
            case SetUserRisk r when _riskyUsers is not null:
                var at = _riskyUsers.FindIndex(u => u.Id == r.User.Id);
                if (at >= 0)
                    _riskyUsers[at] = _riskyUsers[at] with
                    {
                        State = r.Compromised ? "confirmedCompromised" : "dismissed",
                        Level = r.Compromised ? "high" : "none",
                        Detail = r.Compromised ? "Admin confirmed user compromised" : "Admin dismissed all risk for user",
                        Updated = DateTimeOffset.UtcNow,
                    };
                return null;
            case SetAppRisk a:
                if (_riskyApps?.FindIndex(x => x.Id == a.App.Id) is >= 0 and var i)
                    _riskyApps[i] = _riskyApps[i] with { State = a.Compromised ? "confirmedCompromised" : "dismissed", Level = a.Compromised ? "high" : "none", Updated = DateTimeOffset.UtcNow };
                return new ApplyResult(ChangeOutcome.Applied);
            default:
                return null;
        }
    }
}
