using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.Tests;

/// <summary>Blocked senders and email authentication (12c).</summary>
public sealed class MailDomainTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DkimSetup Signing = new(true, "Valid", "selector1-contoso-example._domainkey.contoso.onmicrosoft.com",
        "selector2-contoso-example._domainkey.contoso.onmicrosoft.com", 2048, null, null);

    static MailDns Good => new(["contoso-example.mail.protection.outlook.com"], ["v=spf1 include:spf.protection.outlook.com -all"],
        ["v=DMARC1; p=reject;"], Signing.Selector1Target, Signing.Selector2Target);

    static DomainCheck Check(MailDomain d, string starts) => d.Checks.Single(c => c.Check.StartsWith(starts, StringComparison.Ordinal));

    [Fact]
    public void A_well_set_up_domain_is_all_good()
    {
        var d = new MailDomain("contoso.example", true, false, Signing, Good);

        Assert.All(d.Checks, c => Assert.Equal(CheckState.Good, c.State));
        Assert.Equal(CheckState.Good, d.Worst);
        Assert.True(d.ReceivesHere);
    }

    [Fact]
    public void Missing_records_are_problems_with_the_record_to_publish()
    {
        var d = new MailDomain("contoso.example", false, false, Signing with { Enabled = false, Status = "CnameMissing" },
            new MailDns(["contoso-example.mail.protection.outlook.com"], [], [], null, null));

        var spf = Check(d, "Who May Send");
        var dkim = Check(d, "Signing");
        var dmarc = Check(d, "What Receivers Do");
        Assert.Equal((CheckState.Problem, "v=spf1 include:spf.protection.outlook.com -all", "contoso.example"), (spf.State, spf.Record, spf.RecordName));
        Assert.Equal(CheckState.Problem, dkim.State);
        Assert.Contains("selector1._domainkey.contoso.example  CNAME  selector1-contoso-example._domainkey.contoso.onmicrosoft.com", dkim.Record);
        Assert.Equal(("_dmarc.contoso.example", CheckState.Problem), (dmarc.RecordName, dmarc.State));
        Assert.False(MailDomainChecks.CanEnableDkim(d));
        Assert.Equal(CheckState.Problem, d.Worst);
    }

    [Fact]
    public void Signing_can_be_turned_on_once_DNS_shows_both_CNAMEs()
    {
        var d = new MailDomain("contoso.example", false, false, Signing with { Enabled = false, Status = "CnameMissing" }, Good);

        Assert.True(MailDomainChecks.CanEnableDkim(d));
        Assert.Equal(CheckState.Warning, Check(d, "Signing").State);
    }

    [Theory]
    [InlineData("v=spf1 include:other.example -all", CheckState.Problem, "Doesn't include Microsoft 365")]
    [InlineData("v=spf1 include:spf.protection.outlook.com +all", CheckState.Problem, "Lets anyone send")]
    [InlineData("v=spf1 include:spf.protection.outlook.com ~all", CheckState.Good, "Set (soft fail for others)")]
    public void SPF_records_are_judged(string spf, CheckState state, string summary)
    {
        var d = new MailDomain("contoso.example", false, false, Signing, Good with { Spf = [spf] });

        Assert.Equal((state, summary), (Check(d, "Who May Send").State, Check(d, "Who May Send").Summary));
    }

    [Fact]
    public void Two_SPF_records_and_a_watching_only_DMARC_are_called_out()
    {
        var d = new MailDomain("contoso.example", false, false, Signing, Good with { Spf = ["v=spf1 -all", "v=spf1 ~all"], Dmarc = ["v=DMARC1; p=none"] });

        Assert.Contains("only one is allowed", Check(d, "Who May Send").Summary);
        Assert.Equal(CheckState.Warning, Check(d, "What Receivers Do").State);
    }

    [Fact]
    public void A_parked_domain_should_say_it_sends_nothing()
    {
        var d = new MailDomain("old.example", false, false, null, new MailDns([], [], [], null, null));

        Assert.True(d.Parked);
        Assert.Equal("v=spf1 -all", Check(d, "Who May Send").Record);
        Assert.Equal("v=DMARC1; p=reject;", Check(d, "What Receivers Do").Record);
        Assert.Equal(CheckState.Info, Check(d, "Signing").State);
    }

    [Fact]
    public void The_initial_domain_is_Microsofts_and_DNS_trouble_is_said_plainly()
    {
        Assert.Single(new MailDomain("contoso.onmicrosoft.com", false, true, Signing, Good).Checks);
        var offline = new MailDomain("contoso.example", false, false, Signing, new MailDns([], [], [], null, null, "DNS didn't answer"));
        Assert.Equal(CheckState.Warning, Assert.Single(offline.Checks).State);
    }

    [Fact]
    public async Task Domains_join_Exchanges_accepted_domains_DKIM_and_DNS()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet switch
        {
            "Get-AcceptedDomain" => """[{"DomainName":"contoso.example","Default":true},{"DomainName":"contoso.onmicrosoft.com","Default":false}]""",
            "Get-DkimSigningConfig" => """[{"Domain":"contoso.example","Enabled":false,"Status":"CnameMissing","Selector1CNAME":"s1.contoso.onmicrosoft.com","Selector2CNAME":"s2.contoso.onmicrosoft.com","Selector1KeySize":1024,"Selector2KeySize":2048,"SelectorAfterRotateOnDate":"selector1"}]""",
            _ => "[]",
        });
        var asked = new List<string>();
        var records = new GraphUserRecords(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), exchange)
        {
            Dns = new CannedDns(d => { asked.Add(d); return Good; }),
        };

        var domains = await records.GetMailDomainsAsync(Ct);

        Assert.Equal(["contoso.example", "contoso.onmicrosoft.com"], domains.Select(d => d.Name));
        Assert.Equal((false, "CnameMissing", 1024), (domains[0].Dkim!.Enabled, domains[0].Dkim!.Status, domains[0].Dkim!.KeySize));
        Assert.True(domains[1].IsInitial);
        // Microsoft's own domain isn't looked up.
        Assert.Equal(["contoso.example"], asked);
    }

    [Fact]
    public async Task DKIM_changes_send_their_cmdlets_and_unblocking_reads_back()
    {
        var reads = 0;
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet switch
        {
            "Get-DkimSigningConfig" => """[{"Domain":"zz-tenantry-test.example","Enabled":true}]""",
            "Get-BlockedSenderAddress" => ++reads == 1 ? "[]" : "[]",
            _ => "[]",
        });
        var executor = new GraphChangeExecutor(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);
        var domain = DkimChange.TargetFor("zz-tenantry-test.example");

        var on = await executor.ApplyAsync(new SetDkimSigning(domain, false, true), Ct);
        await executor.ApplyAsync(new CreateDkimKeys(domain), Ct);
        await executor.ApplyAsync(new RotateDkimKeys(domain), Ct);
        var unblock = await executor.ApplyAsync(new UnblockSender(UnblockSender.TargetFor("zz-tenantry-test-bo@contoso.example"), "secured"), Ct);

        Assert.Equal((ChangeOutcome.Applied, ChangeOutcome.Applied), (on.Outcome, unblock.Outcome));
        Assert.Contains("\"Enabled\":true", exchange.Calls.Single(c => c.Cmdlet == "Set-DkimSigningConfig").Parameters);
        Assert.Contains("\"DomainName\":\"zz-tenantry-test.example\"", exchange.Calls.Single(c => c.Cmdlet == "New-DkimSigningConfig").Parameters);
        Assert.Contains("\"KeySize\":2048", exchange.Calls.Single(c => c.Cmdlet == "Rotate-DkimSigningConfig").Parameters);
        Assert.Contains("\"SenderAddress\":\"zz-tenantry-test-bo@contoso.example\"", exchange.Calls.Single(c => c.Cmdlet == "Remove-BlockedSenderAddress").Parameters);
    }

    [Fact]
    public void Test_mode_refuses_a_real_domain()
    {
        Assert.NotNull(WriteGuard.Refusal(new SetDkimSigning(DkimChange.TargetFor("contoso.example"), false, true), testMode: true));
    }

    [Fact]
    public void Turn_Signing_On_waits_for_DNS_and_stages_when_it_can()
    {
        var pipeline = new ChangePipeline(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var page = new MailDomainsViewModel(null, pipeline, Availability.Yes);
        var waiting = new MailDomainRow(new MailDomain("contoso.example", false, false, Signing with { Enabled = false, Status = "CnameMissing" },
            Good with { Selector1 = null, Selector2 = null }), page);
        var ready = new MailDomainRow(new MailDomain("contoso.example", false, false, Signing with { Enabled = false, Status = "CnameMissing" }, Good), page);

        Assert.Equal((true, false), (waiting.ShowTurnOn, waiting.CanTurnOn));
        Assert.True(ready.CanTurnOn);
        ready.TurnOnCommand.Execute(null);
        Assert.True(((SetDkimSigning)Assert.Single(pipeline.Pending)).New);
        Assert.Equal("Undo Turn On", ready.TurnOnText);
    }

    [Fact]
    public void Block_and_allow_are_offered_for_outside_senders_only()
    {
        var pipeline = new ChangePipeline(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        bool Ours(string a) => a.EndsWith("@contoso.example", StringComparison.OrdinalIgnoreCase);
        TracePreviewViewModel From(string sender) => new(new TracedMessage("t", DateTimeOffset.UnixEpoch, sender, "x@y.example", "Hi", "Delivered", 1),
            null, null, pipeline, Availability.Yes, null, Ours);

        Assert.False(From("pat@contoso.example").CanEditLists);
        Assert.True(From("x@bad.example").CanEditLists);
    }

    sealed class CannedDns(Func<string, MailDns> answer) : IMailDnsLookup
    {
        public Task<MailDns> LookupAsync(string domain, CancellationToken cancellationToken = default) => Task.FromResult(answer(domain));
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }
}
