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

/// <summary>Mail records from DNS through the computer's own resolvers (DnsClient).</summary>
public sealed class SystemMailDns : IMailDnsLookup
{
    readonly LookupClient _client = new(new LookupClientOptions { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1, ThrowDnsErrors = false });

    public async Task<MailDns> LookupAsync(string domain, CancellationToken cancellationToken = default)
    {
        try
        {
            var mx = _client.QueryAsync(domain, QueryType.MX, cancellationToken: cancellationToken);
            var txt = _client.QueryAsync(domain, QueryType.TXT, cancellationToken: cancellationToken);
            var dmarc = _client.QueryAsync($"_dmarc.{domain}", QueryType.TXT, cancellationToken: cancellationToken);
            var s1 = _client.QueryAsync($"selector1._domainkey.{domain}", QueryType.CNAME, cancellationToken: cancellationToken);
            var s2 = _client.QueryAsync($"selector2._domainkey.{domain}", QueryType.CNAME, cancellationToken: cancellationToken);
            await Task.WhenAll(mx, txt, dmarc, s1, s2);
            static IEnumerable<string> Texts(IDnsQueryResponse r) => r.Answers.TxtRecords().Select(t => string.Concat(t.Text));
            return new MailDns(
                [.. (await mx).Answers.MxRecords().OrderBy(m => m.Preference).Select(m => m.Exchange.Value.TrimEnd('.'))],
                [.. Texts(await txt).Where(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))],
                [.. Texts(await dmarc).Where(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))],
                (await s1).Answers.CnameRecords().FirstOrDefault()?.CanonicalName.Value.TrimEnd('.'),
                (await s2).Answers.CnameRecords().FirstOrDefault()?.CanonicalName.Value.TrimEnd('.'));
        }
        catch (Exception e) when (e is DnsResponseException or OperationCanceledException { CancellationToken.IsCancellationRequested: false } or System.Net.Sockets.SocketException)
        {
            return new MailDns([], [], [], null, null, $"DNS didn't answer for {domain}: {e.Message}");
        }
    }
}
