using System.Globalization;
using System.Net;
using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>Microsoft's Microsoft 365 Apps activation report: one row per person, each with a count per product and platform.</summary>
public static class ActivationReport
{
    /// <summary>Beta, because v1.0 sends only CSV.</summary>
    public const string Path = "https://graph.microsoft.com/beta/reports/getOffice365ActivationsUserDetail?$format=application/json";

    public static string? UserPrincipalName(JsonElement row) =>
        row.TryGetProperty("userPrincipalName", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;

    /// <summary>With names concealed in reports (Microsoft's default since 2021) the sign-in names are scrambled.</summary>
    public static bool IsConcealed(IReadOnlyCollection<string> names) => names.Count > 0 && !names.Any(n => n.Contains('@'));

    public const string ConcealedReason =
        "Microsoft 365 hides names in usage reports for this tenant. A global admin can show them in the Microsoft 365 admin center: Settings › Org settings › Reports.";

    public static IReadOnlyList<OfficeActivation> Parse(JsonElement row)
    {
        if (!row.TryGetProperty("userActivationCounts", out var counts) || counts.ValueKind != JsonValueKind.Array)
            return [];
        static int Count(JsonElement c, string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        return counts.EnumerateArray().Select(c => new OfficeActivation(
            c.TryGetProperty("productType", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "Microsoft 365 Apps",
            c.TryGetProperty("lastActivatedDate", out var d) && d.ValueKind == JsonValueKind.String
                && DateOnly.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var day) ? day : null,
            Count(c, "windows"), Count(c, "mac"), Count(c, "windows10Mobile"), Count(c, "ios"), Count(c, "android"),
            c.TryGetProperty("activatedOnSharedComputer", out var s) && s.ValueKind == JsonValueKind.True)).ToArray();
    }
}

/// <summary>The Devices tab's Microsoft 365 Apps card: activations (the report) and the devices they signed in from (the sign-in log).</summary>
public sealed partial class GraphUserRecords : IAppActivationRecords
{
    /// <summary>Sign-ins read per kind (interactive, background) for the device list.</summary>
    const int DeviceSignIns = 500;

    // Like the usage report: read once for everyone, looked up by sign-in name, kept a while.
    Task<Dictionary<string, JsonElement>>? _activationReport;
    DateTimeOffset _activationReadAt;

    public async Task<Available<IReadOnlyList<OfficeActivation>>> GetActivationsAsync(string userPrincipalName, CancellationToken cancellationToken = default)
    {
        Dictionary<string, JsonElement> rows;
        try
        {
            if (_activationReport is null || DateTimeOffset.UtcNow - _activationReadAt > ReportKeep || _activationReport is { IsCompleted: true, IsCompletedSuccessfully: false })
            {
                _activationReadAt = DateTimeOffset.UtcNow;
                _activationReport = ReadActivationReportAsync(CancellationToken.None);
            }
            rows = await _activationReport.WaitAsync(cancellationToken);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return Available<IReadOnlyList<OfficeActivation>>.Because("The activation report needs the Reports Reader or Global Reader role.");
        }
        if (ActivationReport.IsConcealed(rows.Keys))
            return Available<IReadOnlyList<OfficeActivation>>.Because(ActivationReport.ConcealedReason);
        return new(rows.TryGetValue(userPrincipalName, out var row) ? ActivationReport.Parse(row) : []);
    }

    async Task<Dictionary<string, JsonElement>> ReadActivationReportAsync(CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        await foreach (var row in graph.GetAllAsync(ActivationReport.Path, cancellationToken))
            if (ActivationReport.UserPrincipalName(row) is { } upn)
                rows[upn] = row;
        return rows;
    }

    /// <summary>
    /// The last 30 days of sign-ins, interactive and background (where an app keeps someone signed
    /// in), grouped by device. Background sign-ins are most of the traffic, so each kind is capped.
    /// </summary>
    public async Task<Available<IReadOnlyList<SignedInDevice>>> GetSignedInDevicesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var since = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        async Task<IReadOnlyList<JsonElement>> Read(string type)
        {
            var filter = $"userId eq '{userId}' and createdDateTime ge {since} and status/errorCode eq 0 and signInEventTypes/any(t: t eq '{type}')";
            // Only what the device list needs: measured live, several times faster than whole sign-ins.
            var page = await graph.GetAsync($"https://graph.microsoft.com/beta/auditLogs/signIns?$filter={Uri.EscapeDataString(filter)}&$top={DeviceSignIns}" +
                                            "&$select=createdDateTime,appDisplayName,deviceDetail,location", cancellationToken);
            return page.GetProperty("value").EnumerateArray().Select(e => e.Clone()).ToArray();
        }
        try
        {
            var reads = await Task.WhenAll(Read("interactiveUser"), Read("nonInteractiveUser"));
            return new(SignedInDevices.Group(reads.SelectMany(r => r).Select(s =>
            {
                var dev = s.TryGetProperty("deviceDetail", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
                string? DevText(string name) => dev.ValueKind == JsonValueKind.Object ? Text(dev, name) : null;
                bool? DevBool(string name) => dev.ValueKind == JsonValueKind.Object && dev.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean() : null;
                var location = s.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object
                    ? string.Join(", ", new[] { Text(loc, "city"), Text(loc, "countryOrRegion") }.Where(x => !string.IsNullOrEmpty(x)))
                    : null;
                return (When(s, "createdDateTime") ?? DateTimeOffset.MinValue, Text(s, "appDisplayName") ?? "", DevText("deviceId"), DevText("displayName"),
                    DevText("operatingSystem"), DevText("browser"), DevText("trustType"), DevBool("isCompliant"), DevBool("isManaged"),
                    string.IsNullOrEmpty(location) ? null : location);
            })));
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden && (e.Code ?? "").Contains("NonPremium", StringComparison.OrdinalIgnoreCase))
        {
            return Available<IReadOnlyList<SignedInDevice>>.Because("Seeing where they signed in needs Microsoft Entra ID P1.");
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return Available<IReadOnlyList<SignedInDevice>>.Because("The sign-in log needs the Reports Reader, Security Reader or Global Reader role.");
        }
    }
}
