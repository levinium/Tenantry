using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's mail domains and blocked senders (12c): one domain set up well, one half set up
/// (DKIM keys made but its CNAMEs not published, DMARC only watching), one parked domain with no
/// records, and Microsoft's initial domain. Changes play out in memory.
/// </summary>
public sealed partial class DemoUserRecords : IMailDomainRecords
{
    List<BlockedSender>? _blocked;

    List<BlockedSender> Blocked() => _blocked ??=
    [
        new("frontdesk@northgate.example", "Outbound spam: the account sent unusually many messages", DateTimeOffset.UtcNow.AddHours(-5), DateTimeOffset.UtcNow.AddHours(19), true),
        new("zz-tenantry-test-cy@northgate.example", "Outbound spam limit exceeded", DateTimeOffset.UtcNow.AddDays(-1), null, false),
    ];

    Dictionary<string, DkimSetup?>? _dkim;

    Dictionary<string, DkimSetup?> Dkim() => _dkim ??= new(StringComparer.OrdinalIgnoreCase)
    {
        ["northgate.example"] = new(true, "Valid", "selector1-northgate-example._domainkey.northgate.onmicrosoft.com",
            "selector2-northgate-example._domainkey.northgate.onmicrosoft.com", 2048, null, DateTimeOffset.UtcNow.AddDays(-400)),
        ["northgate-events.example"] = new(false, "CnameMissing", "selector1-northgate-events-example._domainkey.northgate.onmicrosoft.com",
            "selector2-northgate-events-example._domainkey.northgate.onmicrosoft.com", 1024, null, DateTimeOffset.UtcNow.AddDays(-30)),
        ["old-northgate.example"] = null,
        ["northgate.onmicrosoft.com"] = new(true, "Valid", null, null, 2048, null, null),
    };

    static MailDns DnsFor(string domain) => domain switch
    {
        "northgate.example" => new(["northgate-example.mail.protection.outlook.com"], ["v=spf1 include:spf.protection.outlook.com -all"],
            ["v=DMARC1; p=quarantine; rua=mailto:dmarc@northgate.example"],
            "selector1-northgate-example._domainkey.northgate.onmicrosoft.com", "selector2-northgate-example._domainkey.northgate.onmicrosoft.com"),
        "northgate-events.example" => new(["northgate-events-example.mail.protection.outlook.com"], ["v=spf1 include:spf.protection.outlook.com ~all"],
            ["v=DMARC1; p=none;"], null, null),
        _ => new([], [], [], null, null),
    };

    public Task<IReadOnlyList<BlockedSender>> GetBlockedSendersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BlockedSender>>([.. Blocked()]);

    public Task<IReadOnlyList<string>> GetAcceptedDomainNamesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. Dkim().Keys]);

    public Task<IReadOnlyList<MailDomain>> GetMailDomainsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MailDomain>>([.. Dkim()
            .Select(d => new MailDomain(d.Key, d.Key == "northgate.example", d.Key.EndsWith(".onmicrosoft.com"), d.Value, DnsFor(d.Key)))
            .OrderBy(d => d.IsInitial).ThenByDescending(d => d.IsDefault).ThenBy(d => d.Name)]);

    /// <summary>Applies a mail domain or blocked sender change to the demo; null when it isn't one.</summary>
    public ApplyResult? ApplyMailDomain(ChangeOp change)
    {
        switch (change)
        {
            case UnblockSender u:
                Blocked().RemoveAll(b => string.Equals(b.Address, u.Sender.Id, StringComparison.OrdinalIgnoreCase));
                return new ApplyResult(ChangeOutcome.Applied);
            case CreateDkimKeys c:
                var slug = c.Domain.Id.Replace('.', '-');
                Dkim()[c.Domain.Id] = new DkimSetup(false, "CnameMissing", $"selector1-{slug}._domainkey.northgate.onmicrosoft.com",
                    $"selector2-{slug}._domainkey.northgate.onmicrosoft.com", 2048, null, DateTimeOffset.UtcNow);
                return new ApplyResult(ChangeOutcome.Applied);
            case SetDkimSigning s when Dkim().GetValueOrDefault(s.Domain.Id) is { } setup:
                if (s.New && setup.CnamesMissing)
                    return new ApplyResult(ChangeOutcome.Failed, "Exchange refused: the CNAME records aren't in DNS yet.");
                Dkim()[s.Domain.Id] = setup with { Enabled = s.New };
                return new ApplyResult(ChangeOutcome.Applied);
            case RotateDkimKeys r when Dkim().GetValueOrDefault(r.Domain.Id) is { } setup:
                Dkim()[r.Domain.Id] = setup with { KeySize = r.KeySize, RotateOnDate = DateTimeOffset.UtcNow.AddDays(4) };
                return new ApplyResult(ChangeOutcome.Applied);
            case DkimChange:
                return new ApplyResult(ChangeOutcome.Failed, "That domain has no DKIM setup.");
            default:
                return null;
        }
    }
}
