using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.Tests;

/// <summary>Organization mail settings, remote domains, connectors, spoofed senders and journal rules (12e).</summary>
public sealed class ExchangeOrgTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    static SettingDef Def(string key) => OrgMailSettings.All.Single(s => s.Key == key);

    [Fact]
    public void An_inverted_setting_reads_and_writes_the_way_people_think_of_it()
    {
        var smtp = Def("smtpauth");

        Assert.True(smtp.Read(Json("""{"SmtpClientAuthenticationDisabled":false}"""))!.GetValue<bool>());
        Assert.Equal(true, smtp.ToExchange(JsonValue.Create(false)));
        Assert.Null(Def("recall").Read(Json("""{"MessageRecallEnabled":null}""")));
        Assert.Equal("25 people", Def("tipslarge").Words(JsonValue.Create(25)));
    }

    static ChangePipeline Pipeline(bool testMode = false) =>
        new(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(testMode), "admin");

    [Fact]
    public void A_setting_row_stages_its_change_and_setting_it_back_drops_it()
    {
        var pipeline = Pipeline();
        var row = new SettingRow(Def("smtpauth"), JsonValue.Create(true), new SettingContext(pipeline, Availability.Yes), SetExchangeSetting.Organization("Mail Settings"), null);

        row.IsOn = false;
        var change = (SetExchangeSetting)Assert.Single(pipeline.Pending);
        Assert.Equal(("Set-TransportConfig", "SmtpClientAuthenticationDisabled", true, "On", "Off"),
            (change.SetCmdlet, change.Parameter, change.ExchangeValue!.GetValue<bool>(), change.OldText, change.NewText));
        Assert.NotNull(WriteGuard.Refusal(change, testMode: true));
        row.IsOn = true;
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public void Turning_off_a_safety_setting_is_a_caution_and_a_number_waits_for_Enter()
    {
        var pipeline = Pipeline();
        var context = new SettingContext(pipeline, Availability.Yes);
        var audit = new SettingRow(Def("audit"), JsonValue.Create(true), context, SetExchangeSetting.Organization("Mail Settings"), null);
        audit.IsOn = false;
        Assert.True(Assert.Single(pipeline.Pending).IsCaution);
        pipeline.Clear();

        var large = new SettingRow(Def("tipslarge"), JsonValue.Create(25), context, SetExchangeSetting.Organization("Mail Settings"), null);
        large.NumberText = "0";
        large.CommitNumberCommand.Execute(null);
        Assert.True(large.HasProblem);
        large.NumberText = "100";
        large.CommitNumberCommand.Execute(null);
        Assert.Equal(100, ((SetExchangeSetting)Assert.Single(pipeline.Pending)).ExchangeValue!.GetValue<int>());
    }

    [Fact]
    public async Task A_setting_is_sent_with_its_identity_and_read_back()
    {
        var reads = 0;
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-RemoteDomain" ? (++reads > 0 ? """[{"Name":"zz-tenantry-test-partner","AutoForwardEnabled":false}]""" : "[]") : "[]");
        var executor = new GraphChangeExecutor(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);
        var change = new SetExchangeSetting(new ChangeTarget("remotedomain", "zz-tenantry-test-partner", "zz-tenantry-test-partner", null), "Set-RemoteDomain", "Get-RemoteDomain",
            "zz-tenantry-test-partner", "AutoForwardEnabled", "Automatic Forwarding", JsonValue.Create(false), "On", "Off");

        var result = await executor.ApplyAsync(change, Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var sent = exchange.Calls.Single(c => c.Cmdlet == "Set-RemoteDomain").Parameters;
        Assert.Contains("\"Identity\":\"zz-tenantry-test-partner\"", sent);
        Assert.Contains("\"AutoForwardEnabled\":false", sent);
    }

    [Fact]
    public void Connectors_are_described_in_sentences()
    {
        var inbound = GraphUserRecords.ToConnector(Json("""
            {"Name":"Filter","Enabled":true,"ConnectorType":"Partner","SenderDomains":["*"],"SenderIPAddresses":["203.0.113.0/24","198.51.100.7"],"RequireTls":true,"EFSkipLastIP":true}
            """), inbound: true);
        var outbound = GraphUserRecords.ToConnector(Json("""{"Name":"Out","Enabled":false,"ConnectorType":"Partner","RecipientDomains":["partner.example"],"UseMXRecord":false,"SmartHosts":["relay.partner.example"],"TlsSettings":"CertificateValidation"}"""), inbound: false);

        Assert.Equal(["From a partner organization or service.", "Applies to mail from any domain.", "Sent from 2 IP addresses or ranges.", "Mail must arrive encrypted (TLS).",
            "Enhanced filtering looks past the filtering service in front of Microsoft 365, to the real sender."], inbound.Facts);
        Assert.Contains("Delivers through relay.partner.example.", outbound.Facts);
        Assert.Contains("Always encrypts, and checks the certificate is valid.", outbound.Facts);
    }

    [Fact]
    public void A_journal_rule_needs_a_name_and_an_outside_address_and_sends_its_scope()
    {
        var pipeline = Pipeline();
        var page = new JournalRulesViewModel(null, pipeline, Availability.Yes) { NewName = "zz-tenantry-test-journal" };
        page.AddCommand.Execute(null);
        Assert.True(page.HasAddProblem);

        page.NewAddress = "archive@records.example";
        page.NewScope = JournalRulesViewModel.Scopes.Single(s => s.Value == "External");
        page.AddCommand.Execute(null);
        var create = (CreateJournalRule)Assert.Single(pipeline.Pending);
        var sent = GraphChangeExecutor.JournalParameters(create);
        Assert.Equal(("External", "archive@records.example", true), ((string)sent["Scope"]!, (string)sent["JournalEmailAddress"]!, (bool)sent["Enabled"]!));
        Assert.False(sent.ContainsKey("Recipient"));
    }

    [Fact]
    public void A_remote_domain_can_be_added_once_and_the_default_isnt_removable()
    {
        var pipeline = Pipeline();
        var page = new RemoteDomainsViewModel(null, pipeline, Availability.Yes) { NewDomain = "partner.example" };
        page.AddCommand.Execute(null);
        var create = (CreateRemoteDomain)Assert.Single(pipeline.Pending);
        Assert.Equal(("partner.example", "partner.example"), (create.Domain.Name, create.DomainName));

        page.NewDomain = "not a domain";
        page.AddCommand.Execute(null);
        Assert.True(page.HasAddProblem);

        var row = new RemoteDomainRow(new RemoteDomain("Default", "*", new Dictionary<string, JsonNode?>()), [], page);
        Assert.False(row.CanRemove);
    }

    [Fact]
    public void A_spoofed_pair_is_allowed_or_blocked_as_a_spoof_entry_for_that_server()
    {
        var pipeline = Pipeline();
        var preview = new SpoofPreviewViewModel(new SpoofedSender("zz-tenantry-test.example", "mailer.example", 40, null, "External", "Block"), pipeline, Availability.Yes);

        preview.AllowCommand.Execute(null);
        var allow = (AddListEntry)Assert.Single(pipeline.Pending);
        Assert.Equal((ListKind.Spoof, false, "mailer.example", "External"), (allow.Kind, allow.Block, allow.Infrastructure, allow.SpoofType));
        preview.BlockCommand.Execute(null);
        Assert.True(((AddListEntry)Assert.Single(pipeline.Pending)).Block);
        Assert.Equal("Undo Block", preview.BlockText);
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }
}
