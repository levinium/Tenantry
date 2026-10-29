using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// Retention (8f-3) through Purview's cmdlets. Seen live: a policy read on its own lists its places
/// (by name); its one rule says what it does, and whether it publishes or applies labels.
/// </summary>
public sealed partial class GraphUserRecords : IRetentionRecords
{
    const int PolicyReadsAtOnce = 8;

    public async Task<RetentionDirectory> GetRetentionAsync(CancellationToken cancellationToken = default)
    {
        var policiesRead = Compliance("Get-RetentionCompliancePolicy", null, cancellationToken);
        var rulesRead = Compliance("Get-RetentionComplianceRule", null, cancellationToken);
        var labelsRead = Compliance("Get-ComplianceTag", null, cancellationToken);
        var typesRead = Compliance("Get-ComplianceRetentionEventType", null, cancellationToken);
        var scopesRead = Optional("Get-AdaptiveScope", cancellationToken);
        var eventsRead = Optional("Get-ComplianceRetentionEvent", cancellationToken);

        var types = (await typesRead).Select(t => new RetentionEventType(EdStr(t, "Guid") ?? EdStr(t, "Name") ?? "", EdStr(t, "Name") ?? "An event type",
            EdStr(t, "Comment") is { Length: > 0 } c ? c : null, string.IsNullOrEmpty(EdStr(t, "CreatedBy")))).ToArray();
        var typeNames = types.ToDictionary(t => t.Id, t => t.Name, StringComparer.OrdinalIgnoreCase);
        var rules = (await rulesRead).GroupBy(r => EdStr(r, "Policy") ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        using var gate = new SemaphoreSlim(PolicyReadsAtOnce);
        var policies = await Task.WhenAll((await policiesRead).Select(async listed =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                // Listing leaves places out; a read by name with -DistributionDetail has them (seen live:
                // without it, a read by name came back with no places too).
                var name = EdStr(listed, "Name") ?? "";
                var one = name.Length == 0 ? listed
                    : (await Compliance("Get-RetentionCompliancePolicy", new() { ["Identity"] = name, ["DistributionDetail"] = true }, cancellationToken)).FirstOrDefault() is
                        { ValueKind: JsonValueKind.Object } read ? read : listed;
                return ToPolicy(one, rules.GetValueOrDefault(EdStr(one, "Guid") ?? ""));
            }
            finally
            {
                gate.Release();
            }
        }));

        var labels = (await labelsRead).Select(l => new RetentionLabel(EdStr(l, "Guid") ?? EdStr(l, "Name") ?? "", EdStr(l, "Name") ?? "A label",
            EdStr(l, "Comment") is { Length: > 0 } c ? c : null, EdStr(l, "Notes") is { Length: > 0 } n ? n : null,
            EdBool(l, "HasRetentionAction") == false ? null : EdStr(l, "RetentionAction") is { Length: > 0 } a ? a : null,
            RetentionSetting.ParseDays(EdStr(l, "RetentionDuration")), EdStr(l, "RetentionType") ?? "CreationAgeInDays",
            EdBool(l, "IsRecordLabel") == true, EdBool(l, "Regulatory") == true,
            EdStr(l, "EventTypeId") is { Length: > 0 } eventId ? typeNames.GetValueOrDefault(eventId, eventId) : null,
            Addresses(l, "ReviewerEmail"), IsPendingDeletion(l), Utc(l, "WhenCreatedUTC"), Utc(l, "WhenChangedUTC"), Who(l))).ToArray();

        var (scopeRows, scopesNote) = await scopesRead;
        var (eventRows, eventsNote) = await eventsRead;
        return new RetentionDirectory(policies, labels,
            [.. scopeRows.Select(s => new AdaptiveScope(EdStr(s, "Guid") ?? EdStr(s, "Name") ?? "", EdStr(s, "Name") ?? "A scope", EdStr(s, "LocationType") ?? "User",
                EdStr(s, "RawQuery") is { Length: > 0 } q ? q : EdStr(s, "FilterConditions"), EdStr(s, "Comment") is { Length: > 0 } c ? c : null,
                Utc(s, "WhenCreatedUTC"), Utc(s, "WhenChangedUTC")))],
            types,
            [.. eventRows.Select(e => new RetentionEvent(EdStr(e, "Guid") ?? EdStr(e, "Name") ?? "", EdStr(e, "Name") ?? "An event",
                EdStr(e, "EventType") is { } et ? typeNames.GetValueOrDefault(et, et) : "", Utc(e, "EventDateTime"), EdStr(e, "ExchangeAssetIdQuery"),
                EdStr(e, "SharePointAssetIdQuery"), EdStr(e, "Comment") is { Length: > 0 } c ? c : null, Utc(e, "WhenCreatedUTC"), EdStr(e, "CreatedBy")))],
            scopesNote, eventsNote);
    }

    /// <summary>A read that may be refused for licensing (adaptive scopes need E5-level licensing, seen live).</summary>
    async Task<(IReadOnlyList<JsonElement> Rows, string? Note)> Optional(string cmdlet, CancellationToken cancellationToken)
    {
        try
        {
            return (await Compliance(cmdlet, null, cancellationToken), null);
        }
        catch (Tenantry.Core.Exchange.CmdletException e)
        {
            // Without the license Purview has also answered with a server error page (seen live).
            return ([], e.Explanation.Contains("not licensed", StringComparison.OrdinalIgnoreCase)
                ? "This needs Microsoft 365 E5 (or E5 Compliance) licensing."
                : e.Explanation.TrimStart().StartsWith('<')
                    ? "Purview couldn't list these just now (it answered with a server error). This needs Microsoft 365 E5 (or E5 Compliance) licensing; if you have it, try Refresh."
                    : $"Couldn't read this: {e.Explanation}");
        }
    }

    static RetentionPolicy ToPolicy(JsonElement p, JsonElement rule)
    {
        var locations = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var exceptions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var kind in RetentionPlaces.All)
        {
            if (Places(p, kind + "Location") is { Count: > 0 } places)
                locations[kind] = places;
            if (Places(p, kind + "LocationException") is { Count: > 0 } except)
                exceptions[kind] = except;
        }
        var hasRule = rule.ValueKind == JsonValueKind.Object;
        var published = hasRule ? EdStr(rule, "PublishComplianceTag") : null;
        var applied = hasRule ? EdStr(rule, "ApplyComplianceTag") : null;
        var kind2 = published is { Length: > 0 } ? RetentionPolicyKind.PublishLabels
            : applied is { Length: > 0 } ? RetentionPolicyKind.AutoApplyLabel
            : RetentionPolicyKind.Retention;
        var setting = kind2 == RetentionPolicyKind.Retention && hasRule && EdStr(rule, "RetentionComplianceAction") is { Length: > 0 } action
            ? new RetentionSetting(action, RetentionSetting.ParseDays(EdStr(rule, "RetentionDuration")), EdStr(rule, "ExpirationDateOption") is { Length: > 0 } b ? b : "CreationAgeInDays")
            : null;
        IReadOnlyList<string> labels = kind2 switch
        {
            // Purview lists each published label's ID beside its name (seen live); the names are kept.
            RetentionPolicyKind.PublishLabels => [.. published!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !Guid.TryParse(l, out _)).Distinct(StringComparer.OrdinalIgnoreCase)],
            RetentionPolicyKind.AutoApplyLabel => [applied!],
            _ => [],
        };
        return new RetentionPolicy(EdStr(p, "Guid") ?? EdStr(p, "Name") ?? "", EdStr(p, "Name") ?? "A policy", EdStr(p, "Comment") is { Length: > 0 } c ? c : null,
            EdBool(p, "Enabled") ?? true, kind2, locations, exceptions, Places(p, "AdaptiveScopeLocation"), EdBool(p, "RestrictiveRetention") == true,
            setting, labels, hasRule && EdStr(rule, "ContentMatchQuery") is { Length: > 0 } q ? q : null, EdStr(p, "DistributionStatus"), IsPendingDeletion(p),
            Utc(p, "WhenCreatedUTC"), Utc(p, "WhenChangedUTC"), Who(p));
    }

    static bool IsPendingDeletion(JsonElement e) => string.Equals(EdStr(e, "Mode"), "PendingDeletion", StringComparison.OrdinalIgnoreCase);

    static string? Who(JsonElement e) => EdStr(e, "LastModifiedBy") is { Length: > 0 } by ? by : null;

    static IReadOnlyList<string> Addresses(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString()).OfType<string>().Where(x => x.Length > 0).ToArray()
            : v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } one ? one.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : []
            : [];
}
