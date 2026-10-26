using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's quarantine and organization-wide message trace: made-up spam, phishing and malware
/// from example domains to the demo's people. Releasing and deleting change it in memory.
/// </summary>
public sealed partial class DemoUserRecords : IMailSecurity
{
    static readonly (string Subject, string Reason, string Policy)[] Held =
    [
        ("Your mailbox is almost full: verify now", "Phish", "Default anti-phishing"),
        ("Invoice 4471 overdue", "HighConfPhish", "Default anti-phishing"),
        ("You've won a gift card!", "Spam", "Default"),
        ("Weekly deals just for you", "Bulk", "Default"),
        ("Scanned document", "Malware", "Default anti-malware"),
        ("RE: Wire transfer details", "HighConfPhish", "Default anti-phishing"),
        ("Conference speaker invitation", "Spam", "Default"),
        ("Password expires today", "Phish", "Default anti-phishing"),
        ("Shipment notification", "Malware", "Default anti-malware"),
        ("Exclusive offer ends tonight", "HighConfSpam", "Default"),
    ];

    static readonly string[] Senders = ["alerts@secure-mail.example", "billing@invoices.example", "promo@deals.example", "no-reply@shipping.example", "events@conference.example"];

    List<QuarantinedMessage>? _quarantine;

    async Task<List<QuarantinedMessage>> QuarantineAsync(CancellationToken ct)
    {
        if (_quarantine is not null)
            return _quarantine;
        var people = (await directory.GetUsersAsync(ct)).Where(u => u.Mailbox is not null).Take(40).ToArray();
        var random = new Random(7);
        var now = DateTimeOffset.UtcNow;
        _quarantine = Enumerable.Range(0, 64).Select(i =>
        {
            var (subject, reason, policy) = Held[random.Next(Held.Length)];
            var received = now.AddMinutes(-random.Next(30 * 24 * 60));
            var to = people[random.Next(people.Length)].Mailbox!.PrimaryAddress ?? "";
            return new QuarantinedMessage($"demo-q-{i}", received, Senders[random.Next(Senders.Length)], [to], subject, random.Next(8_000, 400_000),
                reason, policy, received.AddDays(30), random.NextDouble() < 0.1 ? "REQUESTED" : random.NextDouble() < 0.1 ? "RELEASED" : "NOTRELEASED", "Inbound");
        }).ToList();
        return _quarantine;
    }

    public async Task<IReadOnlyList<QuarantinedMessage>> GetQuarantineAsync(int days, CancellationToken cancellationToken = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        return (await QuarantineAsync(cancellationToken)).Where(m => m.Received >= since).OrderByDescending(m => m.Received).ToArray();
    }

    public async Task<QuarantinePreview> PreviewQuarantinedAsync(string identity, CancellationToken cancellationToken = default)
    {
        var m = (await QuarantineAsync(cancellationToken)).First(x => x.Identity == identity);
        var body = m.Reason switch
        {
            "Phish" or "HighConfPhish" => "<p>Dear user,</p><p>Your account will be suspended. <a href=\"https://login-verify.example/reset\">Verify your details</a> within 24 hours.</p>",
            "Malware" => "<p>Please see the attached scanned document.</p>",
            _ => "<p>Don't miss out! Click <a href=\"https://deals.example/offer\">here</a> for today's offer.</p>",
        };
        return new QuarantinePreview(m.Subject, m.Sender, string.Join(", ", m.Recipients), m.Received, body, true,
            m.Reason == "Malware" ? ["Scan_0423.zip"] : []);
    }

    public async Task<IReadOnlyList<TracedMessage>> TraceAsync(TraceSearch search, CancellationToken cancellationToken = default)
    {
        var people = (await directory.GetUsersAsync(cancellationToken)).Where(u => u.Mailbox is not null).Take(60).ToArray();
        var random = new Random(StableSeed($"{search.Start:yyyyMMdd}{search.Sender}{search.Recipient}{search.Subject}"));
        string[] statuses = ["Delivered", "Delivered", "Delivered", "Delivered", "Delivered", "FilteredAsSpam", "Quarantined", "Failed", "Pending"];
        var span = Math.Max(1, (search.End - search.Start).TotalMinutes);
        var messages = Enumerable.Range(0, 300).Select(i =>
        {
            var inside = people[random.Next(people.Length)].Mailbox!.PrimaryAddress ?? "";
            var outside = $"{(char)('a' + random.Next(26))}.{(char)('a' + random.Next(26))}@{(random.NextDouble() < 0.5 ? "partner.example" : "mail.example")}";
            var inbound = random.NextDouble() < 0.6;
            return new TracedMessage($"demo-t-{i}", search.Start.AddMinutes(random.NextDouble() * span),
                search.Sender ?? (inbound ? outside : inside), search.Recipient ?? (inbound ? inside : outside),
                search.Subject is { } s ? $"{s} {i}" : Subjects[random.Next(Subjects.Length)],
                search.Status ?? statuses[random.Next(statuses.Length)], random.Next(4_000, 900_000));
        });
        return messages.OrderByDescending(m => m.Received).ToArray();
    }

    /// <summary>Applies a quarantine change to the demo; null when it isn't one.</summary>
    public ApplyResult? ApplyMailSecurity(ChangeOp change)
    {
        switch (change)
        {
            case ReleaseQuarantined r when _quarantine is not null:
                var i = _quarantine.FindIndex(m => m.Identity == r.Message.Id);
                if (i >= 0)
                    _quarantine[i] = _quarantine[i] with { ReleaseStatus = "RELEASED" };
                return new ApplyResult(ChangeOutcome.Applied);
            case DeleteQuarantined d when _quarantine is not null:
                _quarantine.RemoveAll(m => m.Identity == d.Message.Id);
                return new ApplyResult(ChangeOutcome.Applied);
            case SetSenderListed:
                return new ApplyResult(ChangeOutcome.Applied);
            default:
                return null;
        }
    }
}
