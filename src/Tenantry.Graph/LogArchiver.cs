using System.Globalization;
using System.Net;
using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>
/// Keeps sign-ins and audit events on this computer beyond Microsoft's 30 days (10). Each run reads
/// only what's new since the last one (with an overlap, since Microsoft's logs arrive minutes late;
/// events already held are skipped), then drops what's older than the setting allows. A first run
/// reaches back as far as Microsoft keeps. Seen live: interactive sign-ins read at about 60 a
/// second, non-interactive ones are tens of thousands a day, so they're optional and kept 30 days.
/// </summary>
public sealed class LogArchiver(GraphClient graph, ILogArchive archive, IEntityStore marks, ICmdletRunner? exchange, Func<SyncSettings> settings,
    Func<DateTimeOffset>? now = null)
{
    const string Beta = "https://graph.microsoft.com/beta/";
    const int Batch = 1000;

    /// <summary>Events arrive late; each run re-reads this far before the last one (held events are skipped).</summary>
    static readonly TimeSpan Overlap = TimeSpan.FromMinutes(30);

    /// <summary>Unified audit pages are at most this many (Search-UnifiedAuditLog's limit).</summary>
    const int AuditPage = 5000;

    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Adds what's new to each log and prunes; returns how many events were added, and notes on what couldn't be read.</summary>
    public async Task<(int Added, IReadOnlyList<string> Notes)> ArchiveAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var s = settings();
        var notes = new List<string>();
        var added = 0;
        added += await GraphLogAsync(LogKinds.SignIns, "sign-ins", $"{Beta}auditLogs/signIns", "createdDateTime", null, ToSignIn, progress, notes, cancellationToken);
        if (s.BackgroundSignIns)
            added += await GraphLogAsync(LogKinds.NonInteractive, "non-interactive sign-ins", $"{Beta}auditLogs/signIns", "createdDateTime",
                "signInEventTypes/any(t: t eq 'nonInteractiveUser')", ToSignIn, progress, notes, cancellationToken, firstDays: 1);
        added += await GraphLogAsync(LogKinds.Audits, "directory audits", "auditLogs/directoryAudits", "activityDateTime", null, ToAudit, progress, notes, cancellationToken);
        if (exchange is not null)
            added += await ExchangeAdminAsync(progress, notes, cancellationToken);

        var keep = s.KeepLogsDays;
        if (keep > 0)
            foreach (var kind in new[] { LogKinds.SignIns, LogKinds.Audits, LogKinds.ExchangeAdmin })
                archive.PruneEvents(kind, _now().AddDays(-keep));
        archive.PruneEvents(LogKinds.NonInteractive, _now().AddDays(-(s.BackgroundSignIns ? Math.Min(LogKinds.BackgroundKeepDays, keep > 0 ? keep : int.MaxValue) : 0)));
        return (added, notes);
    }

    /// <summary>Where a log's next read starts: just before the newest event held, or as far back as Microsoft keeps.</summary>
    DateTimeOffset Since(string kind, int firstDays)
    {
        var floor = _now().AddDays(-Math.Min(firstDays, LogKinds.MicrosoftKeepsDays));
        return archive.Span(kind).Newest is { } newest && newest - Overlap > floor ? newest - Overlap : floor;
    }

    async Task<int> GraphLogAsync(string kind, string what, string path, string timeProperty, string? filter, Func<JsonElement, object?> map,
        IProgress<string>? progress, List<string> notes, CancellationToken cancellationToken, int firstDays = LogKinds.MicrosoftKeepsDays)
    {
        var since = Since(kind, firstDays).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var query = $"{timeProperty} ge {since}" + (filter is null ? "" : $" and {filter}");
        var added = 0;
        var read = 0;
        var batch = new List<ArchivedEvent>(Batch);
        try
        {
            await foreach (var row in graph.GetAllAsync($"{path}?$filter={Uri.EscapeDataString(query)}&$top=999", cancellationToken))
            {
                if (Event(row, timeProperty, map) is { } e)
                    batch.Add(e);
                if (++read % Batch == 0)
                {
                    added += archive.AddEvents(kind, batch);
                    batch.Clear();
                    progress?.Report($"Archiving {what}: {read:N0} read…");
                }
            }
            added += archive.AddEvents(kind, batch);
            marks.SetMark(kind, new SyncMark(null, _now()));
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            added += archive.AddEvents(kind, batch);
            notes.Add(e.Code?.Contains("NonPremium", StringComparison.OrdinalIgnoreCase) == true || e.Message.Contains("premium", StringComparison.OrdinalIgnoreCase)
                ? $"The {what} history needs Microsoft Entra ID P1."
                : $"The {what} history couldn't be read: it needs the Reports Reader, Security Reader or Global Reader role.");
        }
        return added;
    }

    static ArchivedEvent? Event(JsonElement row, string timeProperty, Func<JsonElement, object?> map) =>
        Text(row, "id") is { } id && When(row, timeProperty) is { } at && map(row) is { } entry
            ? new ArchivedEvent(id, at, LogKinds.Write(entry))
            : null;

    /// <summary>
    /// Exchange admin actions from the unified audit log (Search-UnifiedAuditLog), a day at a time,
    /// following its result set in pages of 5,000 (seen live: about 55 a day here, read in seconds).
    /// </summary>
    async Task<int> ExchangeAdminAsync(IProgress<string>? progress, List<string> notes, CancellationToken cancellationToken)
    {
        var added = 0;
        var from = Since(LogKinds.ExchangeAdmin, LogKinds.MicrosoftKeepsDays);
        try
        {
            for (var start = from; start < _now(); start = start.AddDays(1))
            {
                var end = start.AddDays(1) < _now() ? start.AddDays(1) : _now();
                var session = Guid.NewGuid().ToString();
                progress?.Report($"Archiving Exchange admin actions: {start.ToLocalTime():d MMM}…");
                for (var page = 0; page < 10; page++)
                {
                    var rows = await exchange!.InvokeAsync(CmdletEndpoint.Exchange, "Search-UnifiedAuditLog", new Dictionary<string, object?>
                    {
                        ["StartDate"] = start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                        ["EndDate"] = end.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                        ["RecordType"] = "ExchangeAdmin",
                        ["SessionId"] = session,
                        ["SessionCommand"] = "ReturnLargeSet",
                        ["ResultSize"] = AuditPage,
                    }, cancellationToken);
                    added += archive.AddEvents(LogKinds.ExchangeAdmin, [.. rows.Select(ToExchangeAdmin).OfType<ArchivedEvent>()]);
                    if (rows.Count < AuditPage)
                        break;
                }
            }
            marks.SetMark(LogKinds.ExchangeAdmin, new SyncMark(null, _now()));
        }
        catch (CmdletException e)
        {
            notes.Add($"Exchange admin actions couldn't be archived: {e.Explanation}");
        }
        return added;
    }

    internal static SignInEntry? ToSignIn(JsonElement s)
    {
        var status = Obj(s, "status");
        var code = status is { } st && st.TryGetProperty("errorCode", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        var location = Obj(s, "location") is { } l
            ? string.Join(", ", new[] { Text(l, "city"), Text(l, "state"), Text(l, "countryOrRegion") }.Where(x => !string.IsNullOrEmpty(x))) is { Length: > 0 } where ? where : null
            : null;
        var device = Obj(s, "deviceDetail");
        var interactive = !s.TryGetProperty("isInteractive", out var i) || i.ValueKind != JsonValueKind.False;
        return new SignInEntry(Text(s, "id") ?? "", When(s, "createdDateTime") ?? default, Text(s, "userDisplayName") ?? "Someone", Text(s, "userPrincipalName"),
            Text(s, "userId"), Text(s, "appDisplayName") ?? "An app", Text(s, "resourceDisplayName"), Text(s, "ipAddress"), location, Text(s, "clientAppUsed"),
            code == 0 ? null : (status is { } f ? Text(f, "failureReason") : null) ?? $"Error {code}",
            Text(s, "conditionalAccessStatus"),
            Text(s, "authenticationRequirement") switch { "multiFactorAuthentication" => "Multifactor", "singleFactorAuthentication" => "Single-factor", var other => other },
            device is { } d ? Text(d, "displayName") : null, device is { } d2 ? Text(d2, "operatingSystem") : null, device is { } d3 ? Text(d3, "browser") : null,
            Text(s, "riskLevelDuringSignIn") is { } risk && risk != "none" ? risk : null, interactive);
    }

    internal static DirectoryAuditEntry? ToAudit(JsonElement a)
    {
        var by = Obj(a, "initiatedBy") is { } who
            ? Obj(who, "user") is { } u && (Text(u, "userPrincipalName") ?? Text(u, "displayName")) is { } person ? person
            : Obj(who, "app") is { } app ? Text(app, "displayName") : null
            : null;
        IReadOnlyList<string> targets = a.TryGetProperty("targetResources", out var t) && t.ValueKind == JsonValueKind.Array
            ? [.. t.EnumerateArray().Select(x => Text(x, "userPrincipalName") ?? Text(x, "displayName")).OfType<string>().Where(x => x.Length > 0)]
            : [];
        return new DirectoryAuditEntry(Text(a, "id") ?? "", When(a, "activityDateTime") ?? default, Text(a, "activityDisplayName") ?? "An activity",
            Text(a, "category") ?? "", string.Equals(Text(a, "result"), "success", StringComparison.OrdinalIgnoreCase),
            Text(a, "resultReason") is { Length: > 0 } r ? r : null, by, targets, Text(a, "loggedByService"));
    }

    /// <summary>A unified audit row: its record (AuditData) is JSON text with the cmdlet's parameters.</summary>
    internal static ArchivedEvent? ToExchangeAdmin(JsonElement row)
    {
        if (Text(row, "AuditData") is not { } text)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var d = doc.RootElement;
            if (Text(d, "Id") is not { } id || When(d, "CreationTime") is not { } at)
                return null;
            var parameters = d.TryGetProperty("Parameters", out var p) && p.ValueKind == JsonValueKind.Array
                ? string.Join("; ", p.EnumerateArray().Select(x => $"-{Text(x, "Name")} {Text(x, "Value")}").Where(x => x.Length > 2))
                : null;
            var entry = new ExchangeAdminEntry(id, at, Text(d, "Operation") ?? Text(row, "Operations") ?? "A command", Text(d, "UserId") ?? Text(row, "UserIds"),
                Text(d, "ObjectId"), parameters is { Length: > 0 } ? parameters : null,
                Text(d, "ResultStatus") is not { } status || status.Equals("True", StringComparison.OrdinalIgnoreCase) || status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase));
            return new ArchivedEvent(id, at, LogKinds.Write(entry));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>Graph's times carry a zone; unified audit times have none and are UTC.</summary>
    static DateTimeOffset? When(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Text(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}
