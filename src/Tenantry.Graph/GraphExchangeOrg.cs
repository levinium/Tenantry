using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>Exchange's organization settings, remote domains, connectors, spoof intelligence and journal rules (12e).</summary>
public sealed partial class GraphUserRecords : IExchangeOrgRecords
{
    public async Task<IReadOnlyDictionary<string, JsonElement>> GetOrgSettingsAsync(CancellationToken cancellationToken = default)
    {
        var reads = OrgMailSettings.Reads.Select(async cmdlet => (cmdlet, rows: await Run(cmdlet, new(), cancellationToken))).ToArray();
        return (await Task.WhenAll(reads)).ToDictionary(r => r.cmdlet, r => r.rows.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<RemoteDomain>> GetRemoteDomainsAsync(CancellationToken cancellationToken = default) =>
        [.. (await Run("Get-RemoteDomain", new(), cancellationToken)).Select(r => new RemoteDomain(Str(r, "Name") ?? "", Str(r, "DomainName") ?? "",
                RemoteDomainSettings.All.ToDictionary(s => s.Key, s => s.Read(r))))
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.DomainName, StringComparer.OrdinalIgnoreCase)];

    public async Task<IReadOnlyList<MailConnector>> GetConnectorsAsync(CancellationToken cancellationToken = default)
    {
        var inbound = Run("Get-InboundConnector", new(), cancellationToken);
        var outbound = Run("Get-OutboundConnector", new(), cancellationToken);
        return [.. (await inbound).Select(r => ToConnector(r, true)).Concat((await outbound).Select(r => ToConnector(r, false)))];
    }

    /// <summary>A string field, for the change executor's read-backs.</summary>
    internal static string? StrOf(JsonElement r, string name) => Str(r, name);

    static bool On(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>What a connector does, said in sentences (the portal's wizard answers, read back).</summary>
    internal static MailConnector ToConnector(JsonElement r, bool inbound)
    {
        var facts = new List<string>();
        var type = Str(r, "ConnectorType") ?? "";
        if (inbound)
        {
            facts.Add(type == "OnPremises" ? "From your organization's own mail servers." : "From a partner organization or service.");
            if (List(r, "SenderDomains").ToArray() is { Length: > 0 } domains)
                facts.Add(domains.Length == 1 && domains[0] == "*" ? "Applies to mail from any domain." : $"Applies to mail from {Few(domains)}.");
            if (List(r, "SenderIPAddresses").ToArray() is { Length: > 0 } ips)
                facts.Add($"Sent from {ips.Length} IP {(ips.Length == 1 ? "address or range" : "addresses or ranges")}.");
            if (Str(r, "TlsSenderCertificateName") is { Length: > 0 } cert)
                facts.Add($"Senders prove who they are with a certificate for {cert}.");
            if (On(r, "RequireTls"))
                facts.Add("Mail must arrive encrypted (TLS).");
            if (On(r, "RestrictDomainsToIPAddresses"))
                facts.Add("Mail from those domains is refused unless it comes from those addresses.");
            if (On(r, "TreatMessagesAsInternal"))
                facts.Add("Mail through it is treated as from inside the organization.");
            if (List(r, "EFSkipIPs").ToArray() is { Length: > 0 } skip || On(r, "EFSkipLastIP"))
                facts.Add("Enhanced filtering looks past the filtering service in front of Microsoft 365, to the real sender.");
        }
        else
        {
            facts.Add(type == "OnPremises" ? "To your organization's own mail servers." : "To a partner organization or service.");
            if (On(r, "IsTransportRuleScoped"))
                facts.Add("Used only for mail a mail flow rule sends through it.");
            else if (List(r, "RecipientDomains").ToArray() is { Length: > 0 } to)
                facts.Add(to.Length == 1 && to[0] == "*" ? "Used for mail to any domain." : $"Used for mail to {Few(to)}.");
            facts.Add(On(r, "UseMXRecord") ? "Delivers by the recipient domain's MX record."
                : List(r, "SmartHosts").ToArray() is { Length: > 0 } hosts ? $"Delivers through {Few(hosts)}." : "Delivers through smart hosts.");
            if (Str(r, "TlsSettings") is { Length: > 0 } tls)
                facts.Add(tls switch
                {
                    "EncryptionOnly" => "Always encrypts (TLS), any certificate.",
                    "CertificateValidation" => "Always encrypts, and checks the certificate is valid.",
                    "DomainValidation" => $"Always encrypts, with a certificate for {Str(r, "TlsDomain") ?? "the given domain"}.",
                    _ => $"Encryption: {tls}.",
                });
        }
        return new MailConnector(Str(r, "Name") ?? Str(r, "Identity") ?? "", inbound, On(r, "Enabled"), type, Str(r, "Comment") is { Length: > 0 } c ? c : null,
            facts, Date(r, "WhenChanged") ?? Date(r, "WhenChangedUTC"));
    }

    static string Few(IReadOnlyList<string> items) => items.Count <= 3 ? string.Join(", ", items) : $"{string.Join(", ", items.Take(3))} and {items.Count - 3} more";

    public async Task<IReadOnlyList<SpoofedSender>> GetSpoofedSendersAsync(CancellationToken cancellationToken = default) =>
        [.. (await Run("Get-SpoofIntelligenceInsight", new(), cancellationToken)).Select(r => new SpoofedSender(
                Str(r, "SpoofedUser") ?? "", Str(r, "SendingInfrastructure") ?? "",
                r.TryGetProperty("MessageCount", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0,
                Date(r, "LastSeen"), Str(r, "SpoofType") ?? "", Str(r, "Action") ?? ""))
            .OrderByDescending(s => s.LastSeen)];

    public async Task<IReadOnlyList<JournalRule>> GetJournalRulesAsync(CancellationToken cancellationToken = default) =>
        [.. (await Run("Get-JournalRule", new(), cancellationToken)).Select(r => new JournalRule(
            Str(r, "Name") ?? "", On(r, "Enabled"), Str(r, "Scope") ?? "Global", Str(r, "Recipient") is { Length: > 0 } who ? who : null, Str(r, "JournalEmailAddress") ?? ""))];

    public async Task<IReadOnlyList<AcceptedDomain>> GetAcceptedDomainsAsync(CancellationToken cancellationToken = default) =>
        [.. (await Run("Get-AcceptedDomain", new(), cancellationToken)).Select(r => new AcceptedDomain(
                Str(r, "Name") ?? "", Str(r, "DomainName") ?? Str(r, "Name") ?? "", Str(r, "DomainType") ?? "", On(r, "Default"), On(r, "MatchSubDomains"),
                On(r, "SendingFromDomainDisabled")))
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.DomainName, StringComparer.OrdinalIgnoreCase)];
}
