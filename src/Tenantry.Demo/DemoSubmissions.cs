using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's reported messages and report settings (12d): people's reports and admin submissions, in memory.</summary>
public sealed partial class DemoUserRecords : ISubmissionRecords
{
    List<ThreatSubmission>? _submissions;

    ReportRouting _routing = new(ReportMode.MicrosoftAndMailbox, "reports@northgate.example", true, true, true, true, true);

    List<ThreatSubmission> SubmissionList()
    {
        if (_submissions is not null)
            return _submissions;
        var now = DateTimeOffset.UtcNow;
        ThreatSubmission User(int i, string subject, string sender, string reporter, string category, string? result, string? detail, int hours, string? reviewed = null) =>
            new($"demo-s-{i}", "email", true, category, result is null ? "running" : "succeeded", result, detail, subject, sender, reporter,
                now.AddHours(-hours - 2), now.AddHours(-hours), reporter, reviewed, reviewed is null ? null : "admin@northgate.example", reviewed is null ? null : now.AddHours(-hours + 1));
        _submissions =
        [
            User(1, "Your mailbox is almost full: verify now", "alerts@secure-mail.example", "amara.hermann@northgate.example", "phishing", "phishing", "itemFoundMalicious", 3),
            User(2, "Weekly deals just for you", "promo@deals.example", "connie.armstrong@northgate.example", "spam", "bulk", "itemFoundBulk", 9),
            User(3, "Board meeting minutes", "secretary@partner.example", "zz-tenantry-test-bo@northgate.example", "phishing", "notJunk", "itemFoundClean", 20),
            User(4, "Invoice 4471 overdue", "billing@invoices.example", "gabriella.quigley@northgate.example", "phishing", null, "underInvestigation", 1),
            User(5, "Conference speaker invitation", "events@conference.example", "andre.wisoky@northgate.example", "notJunk", "spam", "itemFoundSpam", 40, "notSpam"),
            new("demo-s-6", "email", false, "notJunk", "succeeded", "notJunk", "allowedByTenantAllowBlockList", "Shipment notification", "no-reply@shipping.example",
                "daisha.simonis@northgate.example", now.AddDays(-2), now.AddDays(-2).AddHours(1), "admin@northgate.example", null, null, null),
            new("demo-s-7", "url", false, "phishing", "succeeded", "phishing", "itemFoundMalicious", "https://login-verify.example/reset", null, null, null,
                now.AddDays(-3), "admin@northgate.example", null, null, null),
        ];
        return _submissions;
    }

    public Task<IReadOnlyList<ThreatSubmission>> GetSubmissionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ThreatSubmission>>([.. SubmissionList().OrderByDescending(s => s.Created)]);

    ThreatSubmission Added(ThreatSubmission s)
    {
        SubmissionList().Insert(0, s);
        return s;
    }

    public async Task<ThreatSubmission> SubmitQuarantinedAsync(string identity, string recipient, string category, CancellationToken cancellationToken = default)
    {
        var m = (await QuarantineAsync(cancellationToken)).First(x => x.Identity == identity);
        return Added(new ThreatSubmission($"demo-s-{Guid.NewGuid():N}", "email", false, category, "running", null, "underInvestigation", m.Subject, m.Sender, recipient,
            m.Received, DateTimeOffset.UtcNow, "admin@northgate.example", null, null, null));
    }

    public Task<ThreatSubmission> SubmitUrlAsync(string url, string category, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added(new ThreatSubmission($"demo-s-{Guid.NewGuid():N}", "url", false, category, "running", null, "underInvestigation", url, null, null, null,
            DateTimeOffset.UtcNow, "admin@northgate.example", null, null, null)));

    public Task<ThreatSubmission> SubmitFileAsync(string fileName, byte[] content, string category, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added(new ThreatSubmission($"demo-s-{Guid.NewGuid():N}", "file", false, category, "running", null, "underInvestigation", fileName, null, null, null,
            DateTimeOffset.UtcNow, "admin@northgate.example", null, null, null)));

    public Task<ReportRouting> GetReportRoutingAsync(CancellationToken cancellationToken = default) => Task.FromResult(_routing);

    /// <summary>Applies a report settings change or a review to the demo; null when it isn't one.</summary>
    public ApplyResult? ApplySubmission(ChangeOp change)
    {
        switch (change)
        {
            case SetReportRouting r:
                _routing = r.New with { PolicyExists = true, RuleExists = r.New.UsesMailbox };
                return new ApplyResult(ChangeOutcome.Applied);
            case ReviewSubmission r:
                var i = SubmissionList().FindIndex(s => s.Id == r.SubmissionId);
                if (i >= 0)
                    _submissions![i] = _submissions[i] with { ReviewedAs = r.Verdict, ReviewedBy = "admin@northgate.example", ReviewedAt = DateTimeOffset.UtcNow };
                return new ApplyResult(ChangeOutcome.Applied);
            default:
                return null;
        }
    }
}
