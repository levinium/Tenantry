using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's Exchange organization settings, remote domains, connectors, spoofed senders and journal rules (12e).</summary>
public sealed partial class DemoUserRecords : IExchangeOrgRecords
{
    Dictionary<string, JsonObject>? _org;

    Dictionary<string, JsonObject> Org() => _org ??= new(StringComparer.OrdinalIgnoreCase)
    {
        ["Get-ExternalInOutlook"] = new() { ["Enabled"] = false },
        ["Get-TransportConfig"] = new() { ["SmtpClientAuthenticationDisabled"] = false, ["ReplyAllStormProtectionEnabled"] = true },
        ["Get-OrganizationConfig"] = new()
        {
            ["EwsEnabled"] = true, ["AuditDisabled"] = false, ["DisablePlusAddressInRecipients"] = false, ["SendFromAliasEnabled"] = false,
            ["MessageRecallEnabled"] = null, ["RecallReadMessagesEnabled"] = null, ["ReadTrackingEnabled"] = true, ["MailTipsAllTipsEnabled"] = true,
            ["MailTipsExternalRecipientsTipsEnabled"] = false, ["MailTipsLargeAudienceThreshold"] = 25, ["FocusedInboxOn"] = null, ["LinkPreviewEnabled"] = true,
            ["SmtpActionableMessagesEnabled"] = true, ["ConnectorsEnabled"] = true, ["BookingsEnabled"] = true,
        },
    };

    public Task<IReadOnlyDictionary<string, JsonElement>> GetOrgSettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, JsonElement>>(Org().ToDictionary(o => o.Key, o => JsonSerializer.SerializeToElement(o.Value), StringComparer.OrdinalIgnoreCase));

    List<RemoteDomain>? _remote;

    List<RemoteDomain> Remote() => _remote ??=
    [
        Make("Default", "*", true, "External", true, true, true, true, false, null, "MimeHtmlText"),
        Make("Partner", "partner.example", true, "InternalLegacy", false, true, true, true, true, false, "MimeHtmlText"),
    ];

    static RemoteDomain Make(string name, string domain, bool reply, string oof, bool forward, bool ndr, bool ndrDiag, bool delivery, bool meeting, bool? tnef, string content) =>
        new(name, domain, new Dictionary<string, JsonNode?>
        {
            ["autoreply"] = reply, ["ooftype"] = oof, ["autoforward"] = forward, ["ndr"] = ndr, ["ndrdiag"] = ndrDiag, ["delivery"] = delivery,
            ["meetingfwd"] = meeting, ["tnef"] = tnef is { } t ? JsonValue.Create(t) : null, ["content"] = content,
        });

    public Task<IReadOnlyList<RemoteDomain>> GetRemoteDomainsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RemoteDomain>>([.. Remote()]);

    public Task<IReadOnlyList<MailConnector>> GetConnectorsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MailConnector>>(
    [
        new("Mail from the filtering service", true, true, "Partner", "All inbound mail passes the outside filter first",
            ["From a partner organization or service.", "Applies to mail from any domain.", "Sent from 4 IP addresses or ranges.", "Mail must arrive encrypted (TLS).",
             "Enhanced filtering looks past the filtering service in front of Microsoft 365, to the real sender."], DateTimeOffset.UtcNow.AddDays(-200)),
        new("Scanner relay", true, false, "OnPremises", null,
            ["From your organization's own mail servers.", "Sent from 1 IP address or range."], DateTimeOffset.UtcNow.AddDays(-30)),
    ]);

    public Task<IReadOnlyList<SpoofedSender>> GetSpoofedSendersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SpoofedSender>>(
    [
        new("northgate.example", "newsletter-service.example", 412, DateTimeOffset.UtcNow.AddHours(-3), "Internal", "Allow"),
        new("northgate.example", "unknown-host.example", 6, DateTimeOffset.UtcNow.AddDays(-1), "Internal", "Block"),
        new("partner.example", "partner-bulkmail.example", 88, DateTimeOffset.UtcNow.AddHours(-20), "External", "Allow"),
        new("bank.example", "203.0.113.0/24", 3, DateTimeOffset.UtcNow.AddDays(-2), "External", "Block"),
    ]);

    List<JournalRule>? _journal;

    List<JournalRule> Journal() => _journal ??= [new("Finance archive", true, "Global", "finance@northgate.example", "archive@records.example")];

    public Task<IReadOnlyList<JournalRule>> GetJournalRulesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JournalRule>>([.. Journal()]);

    public Task<IReadOnlyList<AcceptedDomain>> GetAcceptedDomainsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AcceptedDomain>>(
    [
        new("northgate.example", "northgate.example", "Authoritative", true, false, false),
        new("northgate-events.example", "northgate-events.example", "Authoritative", false, false, false),
        new("old-northgate.example", "old-northgate.example", "InternalRelay", false, false, false),
        new("northgate.onmicrosoft.com", "northgate.onmicrosoft.com", "Authoritative", false, false, false),
    ]);

    /// <summary>Applies an Exchange organization change to the demo; null when it isn't one.</summary>
    public ApplyResult? ApplyExchangeOrg(ChangeOp change)
    {
        switch (change)
        {
            case SetExchangeSetting s when s.Identity is null && Org().TryGetValue(s.GetCmdlet, out var row):
                row[s.Parameter] = s.ExchangeValue?.DeepClone();
                return new ApplyResult(ChangeOutcome.Applied);
            case SetExchangeSetting s when s.SetCmdlet == "Set-RemoteDomain":
                var i = Remote().FindIndex(d => d.Name == s.Identity);
                if (i >= 0 && RemoteDomainSettings.All.FirstOrDefault(d => d.Parameter == s.Parameter) is { } def)
                {
                    var values = new Dictionary<string, JsonNode?>(_remote![i].Values) { [def.Key] = s.ExchangeValue?.DeepClone() };
                    _remote[i] = _remote[i] with { Values = values };
                }
                return new ApplyResult(ChangeOutcome.Applied);
            case SetExchangeSetting:
                return new ApplyResult(ChangeOutcome.Applied);
            case CreateRemoteDomain c:
                Remote().Add(Make(c.Domain.Name, c.DomainName, true, "External", true, true, true, true, false, null, "MimeHtmlText"));
                return new ApplyResult(ChangeOutcome.Applied);
            case RemoveRemoteDomain r:
                Remote().RemoveAll(d => d.Name == r.Domain.Name);
                return new ApplyResult(ChangeOutcome.Applied);
            case SetJournalRuleEnabled j:
                var at = Journal().FindIndex(x => x.Name == j.Rule.Name);
                if (at >= 0)
                    _journal![at] = _journal[at] with { Enabled = j.New };
                return new ApplyResult(ChangeOutcome.Applied);
            case CreateJournalRule j:
                Journal().Add(new JournalRule(j.Rule.Name, true, j.Scope, j.Recipient, j.JournalAddress));
                return new ApplyResult(ChangeOutcome.Applied);
            case RemoveJournalRule j:
                Journal().RemoveAll(x => x.Name == j.Rule.Name);
                return new ApplyResult(ChangeOutcome.Applied);
            default:
                return null;
        }
    }
}
