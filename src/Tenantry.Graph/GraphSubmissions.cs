using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>Reported messages and submissions (12d): Graph's threat submissions (beta only) and Exchange's report settings.</summary>
public sealed partial class GraphUserRecords : ISubmissionRecords
{
    internal const string Submissions = "https://graph.microsoft.com/beta/security/threatSubmission/";

    /// <summary>The most submissions one read lists (people can report a lot in a busy month).</summary>
    const int MaxSubmissions = 2000;

    public async Task<IReadOnlyList<ThreatSubmission>> GetSubmissionsAsync(CancellationToken cancellationToken = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var found = new List<ThreatSubmission>();
        foreach (var kind in new[] { "emailThreats", "urlThreats", "fileThreats" })
        {
            if (found.Count >= MaxSubmissions)
                break;
            // No $top: some Microsoft collections answer that many and no next link (service health did).
            await foreach (var row in graph.GetAllAsync($"{Submissions}{kind}?$filter=createdDateTime ge {since}", cancellationToken))
            {
                found.Add(ToSubmission(row));
                if (found.Count >= MaxSubmissions)
                    break;
            }
        }
        return [.. found.OrderByDescending(s => s.Created)];
    }

    internal static ThreatSubmission ToSubmission(JsonElement r)
    {
        var result = r.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object ? res : default;
        var review = r.TryGetProperty("adminReview", out var rev) && rev.ValueKind == JsonValueKind.Object ? rev : default;
        var createdBy = r.TryGetProperty("createdBy", out var cb) && cb.ValueKind == JsonValueKind.Object && cb.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
            ? Str(u, "email") ?? Str(u, "displayName") : null;
        var kind = Str(r, "contentType") ?? "email";
        return new ThreatSubmission(
            Str(r, "id") ?? "", kind,
            string.Equals(Str(r, "source"), "user", StringComparison.OrdinalIgnoreCase),
            Str(r, "category") ?? "",
            Str(r, "status") ?? "",
            result.ValueKind == JsonValueKind.Object ? Str(result, "category") : null,
            result.ValueKind == JsonValueKind.Object ? Str(result, "detail") : null,
            Str(r, "subject") ?? Str(r, "emailSubject") ?? Str(r, "webUrl") ?? Str(r, "fileName") ?? "(no subject)",
            Str(r, "sender"), Str(r, "recipientEmailAddress"), Date(r, "receivedDateTime"),
            Date(r, "createdDateTime") ?? DateTimeOffset.MinValue, createdBy,
            review.ValueKind == JsonValueKind.Object ? Str(review, "reviewResult") : null,
            review.ValueKind == JsonValueKind.Object ? Str(review, "reviewBy") : null,
            review.ValueKind == JsonValueKind.Object ? Date(review, "reviewDateTime") : null);
    }

    public async Task<ThreatSubmission> SubmitQuarantinedAsync(string identity, string recipient, string category, CancellationToken cancellationToken = default)
    {
        // A quarantined message isn't in a mailbox, so it goes as its content (Microsoft keeps only its details).
        var eml = await ExportQuarantinedAsync(identity, cancellationToken);
        var body = new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.security.emailContentThreatSubmission",
            ["category"] = category, ["recipientEmailAddress"] = recipient, ["fileContent"] = Convert.ToBase64String(eml),
        };
        return ToSubmission(await graph.PostAsync($"{Submissions}emailThreats", body, cancellationToken));
    }

    public async Task<ThreatSubmission> SubmitUrlAsync(string url, string category, CancellationToken cancellationToken = default) =>
        ToSubmission(await graph.PostAsync($"{Submissions}urlThreats", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.security.urlThreatSubmission", ["category"] = category, ["webUrl"] = url,
        }, cancellationToken));

    public async Task<ThreatSubmission> SubmitFileAsync(string fileName, byte[] content, string category, CancellationToken cancellationToken = default) =>
        ToSubmission(await graph.PostAsync($"{Submissions}fileThreats", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.security.fileContentThreatSubmission", ["category"] = category,
            ["fileName"] = fileName, ["fileContent"] = Convert.ToBase64String(content),
        }, cancellationToken));

    public async Task<ReportRouting> GetReportRoutingAsync(CancellationToken cancellationToken = default)
    {
        var policies = Run("Get-ReportSubmissionPolicy", new(), cancellationToken);
        var rules = Run("Get-ReportSubmissionRule", new(), cancellationToken);
        return ToRouting((await policies).FirstOrDefault(), (await rules).FirstOrDefault());
    }

    internal static ReportRouting ToRouting(JsonElement policy, JsonElement rule)
    {
        // No policy yet: Microsoft's defaults (to Microsoft and the admin's mailbox, with both pop-ups).
        if (policy.ValueKind != JsonValueKind.Object)
            return new ReportRouting(ReportMode.MicrosoftAndMailbox, null, false, true, true, false, rule.ValueKind == JsonValueKind.Object);
        static bool On(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var microsoft = On(policy, "EnableReportToMicrosoft");
        var mailbox = On(policy, "ReportJunkToCustomizedAddress") || On(policy, "ReportPhishToCustomizedAddress") || On(policy, "ReportNotJunkToCustomizedAddress");
        var mode = (microsoft, mailbox) switch
        {
            (true, true) => ReportMode.MicrosoftAndMailbox,
            (false, true) => ReportMode.MailboxOnly,
            (true, false) => ReportMode.MicrosoftOnly,
            _ => ReportMode.Off,
        };
        var address = List(policy, "ReportPhishAddresses").Concat(List(policy, "ReportJunkAddresses"))
            .Concat(rule.ValueKind == JsonValueKind.Object ? List(rule, "SentTo") : []).FirstOrDefault(a => a.Contains('@'));
        return new ReportRouting(mode, address, On(policy, "EnableUserEmailNotification"), On(policy, "PreSubmitMessageEnabled"),
            On(policy, "PostSubmitMessageEnabled"), true, rule.ValueKind == JsonValueKind.Object);
    }
}
