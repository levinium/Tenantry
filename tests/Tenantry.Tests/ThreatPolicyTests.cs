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

/// <summary>Threat policies, quarantine policies and the connection filter (12f).</summary>
public sealed class ThreatPolicyTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    static ChangePipeline Pipeline(bool testMode = false) => new(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(testMode), "admin");

    static readonly string[] Tags = ["AdminOnlyAccessPolicy", "DefaultFullAccessPolicy"];

    [Fact]
    public void Every_kind_has_settings_and_each_setting_names_its_own_policy_cmdlets()
    {
        foreach (var kind in ThreatPolicyCatalog.Kinds)
        {
            var settings = ThreatPolicyCatalog.Settings(kind, Tags);
            Assert.NotEmpty(settings);
            Assert.All(settings, s => Assert.Equal(("Get-" + kind.PolicyNoun, "Set-" + kind.PolicyNoun), (s.GetCmdlet, s.SetCmdlet)));
            Assert.Equal(settings.Count, settings.Select(s => s.Parameter).Distinct().Count());
        }
    }

    [Fact]
    public void A_policy_card_counts_the_settings_looser_than_Microsofts_recommendation()
    {
        var policy = new ThreatPolicy(ThreatPolicyCatalog.AntiSpam, "Default", true, false,
            Json("""{"Name":"Default","BulkThreshold":7,"PhishSpamAction":"MoveToJmf","SpamAction":"MoveToJmf","SpamZapEnabled":true}"""), null);
        var page = new ThreatPoliciesViewModel(ThreatPolicyCatalog.AntiSpam, null, Pipeline(), Availability.Yes);
        var card = new ThreatPolicyCard(policy, ThreatPolicyCatalog.Settings(ThreatPolicyCatalog.AntiSpam, Tags), new SettingContext(Pipeline(), Availability.Yes), page);

        Assert.Equal(2, card.DifferCount);
        var bulk = card.Groups.SelectMany(g => g.Rows).Single(r => r.Def.Parameter == "BulkThreshold");
        Assert.Equal("Microsoft's recommendation: 6", bulk.RecommendedNote);
        Assert.False(card.CanRemove);
        Assert.Equal("Everyone not in another policy", card.AppliesTo);
    }

    [Fact]
    public void A_custom_policy_reads_who_it_covers_and_its_rule_settings_go_to_the_rule()
    {
        var pipeline = Pipeline();
        var context = new SettingContext(pipeline, Availability.Yes);
        var policy = new ThreatPolicy(ThreatPolicyCatalog.AntiSpam, "zz-tenantry-test-spam", false, false, Json("""{"Name":"zz-tenantry-test-spam"}"""),
            Json("""{"Name":"zz-tenantry-test-spam","HostedContentFilterPolicy":"zz-tenantry-test-spam","State":"Enabled","Priority":1,"SentTo":["a@contoso.example"],"RecipientDomainIs":["contoso.example"],"ExceptIfSentTo":["b@contoso.example"]}"""));
        var page = new ThreatPoliciesViewModel(ThreatPolicyCatalog.AntiSpam, null, pipeline, Availability.Yes);
        var card = new ThreatPolicyCard(policy, ThreatPolicyCatalog.Settings(ThreatPolicyCatalog.AntiSpam, Tags), context, page);

        Assert.Equal("people: a@contoso.example; domains: contoso.example; except people: b@contoso.example", card.AppliesTo);
        var domains = card.Rule.SelectMany(g => g.Rows).Single(r => r.Def.Parameter == "RecipientDomainIs");
        domains.ListText = "contoso.example\nfabrikam.example";
        domains.CommitListCommand.Execute(null);
        var change = (SetExchangeSetting)Assert.Single(pipeline.Pending);
        Assert.Equal(("Set-HostedContentFilterRule", "zz-tenantry-test-spam"), (change.SetCmdlet, change.Identity));
        Assert.Equal(["contoso.example", "fabrikam.example"], change.ExchangeValue!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Null(WriteGuard.Refusal(change, testMode: true));

        card.SwitchCommand.Execute(null);
        Assert.Contains(pipeline.Pending, c => c is SetThreatRuleEnabled { New: false });
    }

    [Fact]
    public void Outbound_policies_cover_senders()
    {
        Assert.Equal(("From", "FromMemberOf", "SenderDomainIs"), ThreatPolicyCatalog.Outbound.Who);
        Assert.Equal(("SentTo", "SentToMemberOf", "RecipientDomainIs"), ThreatPolicyCatalog.AntiPhish.Who);
    }

    [Fact]
    public void A_new_policy_makes_the_policy_then_its_rule_and_removing_takes_both()
    {
        var page = new ThreatPoliciesViewModel(ThreatPolicyCatalog.AntiPhish, null, Pipeline(), Availability.Yes) { NewName = "zz-tenantry-test-phish" };
        page.AddCommand.Execute(null);
        Assert.True(page.HasAddProblem);
        var pipeline = Pipeline();
        page = new ThreatPoliciesViewModel(ThreatPolicyCatalog.AntiPhish, null, pipeline, Availability.Yes) { NewName = "zz-tenantry-test-phish", NewPeople = "zz-tenantry-test-bo@contoso.example" };
        page.AddCommand.Execute(null);

        var create = (CreateThreatPolicy)Assert.Single(pipeline.Pending);
        var calls = GraphChangeExecutor.CreatePolicyCalls(create);
        Assert.Equal(["New-AntiPhishPolicy", "New-AntiPhishRule"], calls.Select(c => c.Cmdlet));
        Assert.Equal("zz-tenantry-test-phish", calls[1].Parameters["AntiPhishPolicy"]);
        Assert.Equal(new[] { "zz-tenantry-test-bo@contoso.example" }, calls[1].Parameters["SentTo"]);
        Assert.False(calls[1].Parameters.ContainsKey("SentToMemberOf"));

        var remove = new RemoveThreatPolicy(create.Policy, "AntiPhishPolicy", "AntiPhishRule", "Anti-Phishing", HasRule: true);
        Assert.Equal(["Remove-AntiPhishRule", "Remove-AntiPhishPolicy"], GraphChangeExecutor.RemovePolicyCalls(remove).Select(c => c.Cmdlet));
    }

    [Fact]
    public async Task Policies_pair_with_their_rules_and_the_default_comes_last()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet switch
        {
            "Get-HostedContentFilterPolicy" => """[{"Name":"Default","IsDefault":true},{"Name":"B"},{"Name":"A"}]""",
            "Get-HostedContentFilterRule" => """[{"Name":"A","HostedContentFilterPolicy":"A","Priority":1,"State":"Disabled"},{"Name":"B","HostedContentFilterPolicy":"B","Priority":0,"State":"Enabled"}]""",
            _ => "[]",
        });
        var records = new GraphUserRecords(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), exchange);

        var policies = (await records.GetThreatPoliciesAsync(ThreatPolicyCatalog.AntiSpam, Ct))!;

        Assert.Equal(["B", "A", "Default"], policies.Select(p => p.Name));
        Assert.Equal((true, false, 0), (policies[0].Enabled, policies[1].Enabled, policies[0].Priority!.Value));
        Assert.Null(policies[2].Rule);
    }

    [Fact]
    public void Quarantine_permissions_read_from_Exchanges_text_and_add_up_to_the_bits()
    {
        var flags = QuarantinePermissions.Parse("[PermissionToBlockSender: False\nPermissionToDelete: True\nPermissionToDownload: False\nPermissionToPreview: True\nPermissionToRelease: False\nPermissionToRequestRelease: True\nPermissionToViewHeader: False]");

        Assert.Equal(1 + 2 + 8, QuarantinePermissions.Value(flags));
        Assert.Equal("People can ask for release, preview, delete", new QuarantinePolicy("p", false, flags, false).Describe());
        Assert.Equal("Only admins can see or release it", new QuarantinePolicy("p", true, QuarantinePermissions.Parse("[PermissionToRelease: False]"), false).Describe());
    }

    [Fact]
    public void A_custom_quarantine_policy_takes_a_preset_and_a_built_in_one_does_not()
    {
        var pipeline = Pipeline();
        var page = new QuarantinePoliciesViewModel(null, pipeline, Availability.Yes);
        var context = new SettingContext(pipeline, Availability.Yes);
        var custom = new QuarantinePolicyRow(new QuarantinePolicy("zz-tenantry-test-q", false,
            QuarantinePermissions.Parse("[PermissionToRelease: True\nPermissionToPreview: True\nPermissionToDelete: True\nPermissionToBlockSender: True]"), false), page, context);
        var builtIn = new QuarantinePolicyRow(new QuarantinePolicy("AdminOnlyAccessPolicy", true, QuarantinePermissions.Parse("[PermissionToRelease: False]"), false), page, context);

        Assert.Equal("23", custom.Preset!.Value);
        custom.Preset = QuarantinePolicyRow.Presets[1];
        var change = (SetQuarantinePermissions)Assert.Single(pipeline.Pending);
        Assert.Equal((23, 27), (change.Old, change.New));
        Assert.False(builtIn.CanChangePermissions);
        Assert.False(builtIn.Notify.CanEdit);
    }

    [Fact]
    public void Connection_filter_lists_send_as_arrays_and_the_organization_is_guarded()
    {
        var pipeline = Pipeline();
        var row = new SettingRow(new SettingDef("", "allow", "Always Let In", "", "Get-HostedConnectionFilterPolicy", "Set-HostedConnectionFilterPolicy", "IPAllowList", SettingKind.List),
            new JsonArray(), new SettingContext(pipeline, Availability.Yes), SetExchangeSetting.Organization("Connection Filter"), "Default");

        row.ListText = "198.51.100.20; 203.0.113.0/24";
        row.CommitListCommand.Execute(null);

        var change = (SetExchangeSetting)Assert.Single(pipeline.Pending);
        Assert.Equal(2, change.ExchangeValue!.AsArray().Count);
        Assert.Equal("198.51.100.20, 203.0.113.0/24", change.NewText);
        Assert.NotNull(WriteGuard.Refusal(change, testMode: true));
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }
}
