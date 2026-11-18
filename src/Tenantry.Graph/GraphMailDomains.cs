using System.Text.Json;
using DnsClient;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>Mail domains and blocked senders (12c): Exchange's accepted domains and DKIM, DNS for the rest.</summary>
public sealed partial class GraphUserRecords : IMailDomainRecords
{
    IMailDnsLookup _dns = new SystemMailDns();

    /// <summary>Where DNS answers come from (tests give canned ones).</summary>
    public IMailDnsLookup Dns { init => _dns = value; }

    public async Task<IReadOnlyList<BlockedSender>> GetBlockedSendersAsync(CancellationToken cancellationToken = default) =>
        [.. (await Run("Get-BlockedSenderAddress", new(), cancellationToken)).Select(r => new BlockedSender(
                Str(r, "SenderAddress") ?? Str(r, "Identity") ?? "",
                Str(r, "Reason") is { Length: > 0 } reason ? reason : null,
                Date(r, "CreatedDatetime") ?? Date(r, "CreatedDateTime") ?? Date(r, "WhenCreated"),
                Date(r, "ExpireDatetime") ?? Date(r, "ExpireDateTime") ?? Date(r, "ExpiryDate"),
                r.TryGetProperty("TemporaryBlock", out var t) && t.ValueKind == JsonValueKind.True))
            .Where(b => b.Address.Length > 0)
            .OrderByDescending(b => b.BlockedAt)];

    public async Task<IReadOnlyList<string>> GetAcceptedDomainNamesAsync(CancellationToken cancellationToken = default) =>
        [.. (await Run("Get-AcceptedDomain", new(), cancellationToken)).Select(r => Str(r, "DomainName") ?? Str(r, "Name") ?? "").Where(n => n.Length > 0)];

    public async Task<IReadOnlyList<MailDomain>> GetMailDomainsAsync(CancellationToken cancellationToken = default)
    {
        var accepted = Run("Get-AcceptedDomain", new(), cancellationToken);
        var dkim = Run("Get-DkimSigningConfig", new(), cancellationToken);
        var domains = (await accepted).Select(r => (Name: Str(r, "DomainName") ?? Str(r, "Name") ?? "", IsDefault: r.TryGetProperty("Default", out var d) && d.ValueKind == JsonValueKind.True))
            .Where(d => d.Name.Length > 0).ToArray();
        var signing = (await dkim).Where(r => Str(r, "Domain") is { Length: > 0 })
            .GroupBy(r => Str(r, "Domain")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => ToDkim(g.First()), StringComparer.OrdinalIgnoreCase);
        // DNS for every domain at once: each lookup waits on the network, not the processor.
        var lookups = domains.Select(async d => new MailDomain(d.Name, d.IsDefault, IsInitial(d.Name), signing.GetValueOrDefault(d.Name),
            IsInitial(d.Name) ? new MailDns([], [], [], null, null) : await _dns.LookupAsync(d.Name, cancellationToken)));
        return [.. (await Task.WhenAll(lookups)).OrderBy(d => d.IsInitial).ThenByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }

    static bool IsInitial(string domain) => domain.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase);

    static DkimSetup ToDkim(JsonElement r)
    {
        // The key in use is the selector Exchange signs with after its last rotation.
        var active = Str(r, "SelectorAfterRotateOnDate") ?? "selector1";
        var size = active.Contains('2') ? Int(r, "Selector2KeySize") : Int(r, "Selector1KeySize");
        return new DkimSetup(r.TryGetProperty("Enabled", out var e) && e.ValueKind == JsonValueKind.True, Str(r, "Status"),
            Str(r, "Selector1CNAME"), Str(r, "Selector2CNAME"), size, Date(r, "RotateOnDate"), Date(r, "KeyCreationTime"));
    }

    static int? Int(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}

/// <summary>
/// Mail records from DNS through the computer's own resolvers (DnsClient). Some networks never pass
/// on a "no such name" answer: each such lookup then waits out every resolver in turn (16 s or more
/// seen live, for each missing DMARC or DKIM record). Real answers come in well under a second, so
/// a domain's lookups get <see cref="Limit"/> together, and a lookup with no answer by then counts
/// as no record. Only when none of them answers is the domain marked as unreadable.
/// </summary>
public sealed class SystemMailDns : IMailDnsLookup
{
    /// <summary>How long one domain's lookups may take, all together.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

    readonly LookupClient _client = new(new LookupClientOptions
    {
        UseCache = false, Timeout = Limit, Retries = 0, ThrowDnsErrors = false, ContinueOnDnsError = false, ContinueOnEmptyResponse = false,
    });

    public async Task<MailDns> LookupAsync(string domain, CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Limit);
        async Task<IDnsQueryResponse?> Ask(string name, QueryType type)
        {
            try
            {
                return await _client.QueryAsync(name, type, cancellationToken: limit.Token);
            }
            catch (Exception e) when (e is DnsResponseException or OperationCanceledException or System.Net.Sockets.SocketException && !cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
        var mx = Ask(domain, QueryType.MX);
        var txt = Ask(domain, QueryType.TXT);
        var dmarc = Ask($"_dmarc.{domain}", QueryType.TXT);
        var s1 = Ask($"selector1._domainkey.{domain}", QueryType.CNAME);
        var s2 = Ask($"selector2._domainkey.{domain}", QueryType.CNAME);
        var answers = await Task.WhenAll(mx, txt, dmarc, s1, s2);
        cancellationToken.ThrowIfCancellationRequested();
        if (answers.All(a => a is null))
            return new MailDns([], [], [], null, null,
                $"DNS gave no answer for {domain} within {Limit.TotalSeconds:0} seconds: it may not be published, or this network doesn't pass the answer on.");
        static IEnumerable<string> Texts(IDnsQueryResponse? r) => r?.Answers.TxtRecords().Select(t => string.Concat(t.Text)) ?? [];
        return new MailDns(
            [.. (answers[0]?.Answers.MxRecords() ?? []).OrderBy(m => m.Preference).Select(m => m.Exchange.Value.TrimEnd('.'))],
            [.. Texts(answers[1]).Where(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))],
            [.. Texts(answers[2]).Where(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))],
            answers[3]?.Answers.CnameRecords().FirstOrDefault()?.CanonicalName.Value.TrimEnd('.'),
            answers[4]?.Answers.CnameRecords().FirstOrDefault()?.CanonicalName.Value.TrimEnd('.'));
    }
}
