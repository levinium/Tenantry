using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's mail flow reports: made-up daily volumes with weekday rhythm, and top lists from the demo's people.</summary>
public sealed partial class DemoUserRecords : IMailFlowReports
{
    public Task<IReadOnlyList<MailFlowCount>> GetMailFlowStatusAsync(int days, CancellationToken cancellationToken = default)
    {
        var counts = new List<MailFlowCount>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (var day = today.AddDays(-days + 1); day <= today; day = day.AddDays(1))
        {
            var random = new Random(day.DayNumber);
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var scale = weekend ? 0.35 : 1.0;
            int N(int typical) => (int)(typical * scale * (0.8 + random.NextDouble() * 0.4));
            counts.Add(new(day, "GoodMail", "Inbound", N(2900)));
            counts.Add(new(day, "GoodMail", "IntraOrg", N(1150)));
            counts.Add(new(day, "GoodMail", "Outbound", N(640)));
            counts.Add(new(day, "SpamDetections", "Inbound", N(260)));
            counts.Add(new(day, "EmailPhish", "Inbound", N(45)));
            counts.Add(new(day, "EdgeBlockSpam", "Inbound", (int)(1000 * (0.8 + random.NextDouble() * 0.4))));
            if (random.Next(3) == 0)
                counts.Add(new(day, "EmailMalware", "Inbound", 1 + random.Next(4)));
        }
        return Task.FromResult<IReadOnlyList<MailFlowCount>>(counts);
    }

    public async Task<IReadOnlyList<MailFlowTop>> GetTopTrafficAsync(string category, int days, CancellationToken cancellationToken = default)
    {
        var people = (await directory.GetUsersAsync(cancellationToken)).Select(u => u.Mailbox?.PrimaryAddress).OfType<string>().ToArray();
        var random = new Random(category.Length * 31 + days);
        var scale = days / 7.0;
        IEnumerable<MailFlowTop> Of(int typical, int take) => people.Take(take).Select((p, i) => new MailFlowTop(p, (int)(typical * scale / (i + 1.5) * (0.7 + random.NextDouble() * 0.6)) + 1));
        IReadOnlyList<MailFlowTop> top = category switch
        {
            "TopMailSender" => [.. Of(900, 20)],
            "TopMailRecipient" => [.. Of(1500, 20)],
            "TopSpamRecipient" => [.. Of(160, 20)],
            "TopPhishRecipient" => [.. Of(30, 12)],
            "TopMalwareRecipient" => [.. Of(3, 4)],
            "TopMalware" => [new("HTML/Phish.Gen", (int)(4 * scale) + 1), new("Trojan:Script/Wacatac", (int)(2 * scale) + 1), new("JS/Redirector", 1)],
            "InboundTransportRuleHits" => [new("External sender warning", (int)(14000 * scale)), new("Block risky attachments", (int)(9 * scale) + 1)],
            "OutboundTransportRuleHits" => [new("Add a disclaimer", (int)(3800 * scale))],
            _ => [],
        };
        return MailFlowReport.Combine(top, take: 50);
    }

    public Task<IReadOnlyList<ThreatCatch>> GetThreatCatchesAsync(int days, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ThreatCatch>>(
        [
            new("NotSpam", "Message passed", "Inbound", 19100),
            new("Spam", "IP reputation", "Inbound", 880),
            new("Spam", "Bulk", "Inbound", 520),
            new("Spam", "Advanced filter", "Inbound", 310),
            new("Phish", "URL detonation", "Inbound", 120),
            new("Phish", "Impersonation brand", "Inbound", 64),
            new("Phish", "Spoof DMARC", "Inbound", 41),
            new("Malware", "File detonation", "Inbound", 6),
            new("Allow", "Message passed", "Inbound", 230),
        ]);
}
