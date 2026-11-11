using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.Tests;

/// <summary>Preset security policies: Strict, Standard and built-in protection.</summary>
public sealed class PresetPolicyTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static JsonElement[] Rows(string json) => [.. JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone())];

    static ChangePipeline Pipeline(bool testMode = false) => new(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(testMode), "admin");

    [Fact]
    public void A_preset_never_turned_on_reads_as_missing_and_Defender_is_known_from_the_ATP_read()
    {
        var builtIn = Rows("""[{"Name":"ATP Built-In Protection Rule","State":"Enabled","ExceptIfSentTo":["scanner@contoso.example"]}]""");

        var presets = GraphUserRecords.ToPresets([], [], builtIn);

        Assert.Equal([PresetKind.Strict, PresetKind.Standard, PresetKind.BuiltIn], presets.Select(p => p.Kind));
        Assert.All(presets.Take(2), p => Assert.False(p.Exists));
        Assert.True(presets[1].HasDefender);
        Assert.Equal(["scanner@contoso.example"], presets[2].Values("ExceptIfSentTo"));
        Assert.Equal("Everyone not in a Safe Links or Safe Attachments policy; except people: scanner@contoso.example", presets[2].AppliesTo);
        Assert.False(GraphUserRecords.ToPresets([], null, null)[1].HasDefender);
    }

    [Fact]
    public void Standard_reads_its_state_and_who_it_covers()
    {
        var eop = Rows("""[{"Name":"Standard Preset Security Policy","State":"Disabled","SentToMemberOf":["Leadership"],"ExceptIfSentTo":null}]""");

        var standard = GraphUserRecords.ToPresets(eop, [], null)[1];

        Assert.Equal((true, false, "groups: Leadership"), (standard.Exists, standard.Enabled, standard.AppliesTo));
    }

    [Fact]
    public void Test_mode_lets_test_people_in_or_out_of_a_preset_but_never_switches_one()
    {
        var addTest = new SetPresetScope(PresetKind.BuiltIn, true, "ExceptIfSentTo", "Except People", true, ["scanner@contoso.example"],
            ["scanner@contoso.example", "zz-tenantry-test-bo@contoso.example"]);
        var addReal = addTest with { New = ["scanner@contoso.example", "ada@contoso.example"] };
        var off = new SetPresetEnabled(PresetKind.Standard, true, true, false);

        Assert.Null(WriteGuard.Refusal(addTest, testMode: true));
        Assert.NotNull(WriteGuard.Refusal(addReal, testMode: true));
        Assert.NotNull(WriteGuard.Refusal(off, testMode: true));
        Assert.True(addTest.IsCaution);
        Assert.False((addTest with { New = [] , Old = ["zz-tenantry-test-bo@contoso.example"] }).IsCaution);
        var narrow = new SetPresetScope(PresetKind.Strict, true, "SentTo", "People", false, [], ["zz-tenantry-test-bo@contoso.example"]);
        Assert.True(narrow.IsCaution);
        Assert.False((narrow with { Old = ["zz-tenantry-test-cy@contoso.example"] }).IsCaution);
    }

    [Fact]
    public void Standard_and_Strict_change_both_rules_and_built_in_protection_its_own()
    {
        var scope = new SetPresetScope(PresetKind.Standard, true, "SentTo", "People", false, [], ["zz-tenantry-test-bo@contoso.example"]);
        Assert.Equal(["Set-EOPProtectionPolicyRule", "Set-ATPProtectionPolicyRule"], GraphChangeExecutor.PresetCalls(scope).Select(c => c.Cmdlet));
        Assert.Equal(["Set-EOPProtectionPolicyRule"], GraphChangeExecutor.PresetCalls(scope with { HasDefender = false }).Select(c => c.Cmdlet));
        var cleared = GraphChangeExecutor.PresetCalls(new SetPresetScope(PresetKind.BuiltIn, true, "ExceptIfSentTo", "Except People", true, ["x@contoso.example"], []));
        Assert.Equal("Set-ATPBuiltInProtectionRule", Assert.Single(cleared).Cmdlet);
        Assert.Null(cleared[0].Parameters["ExceptIfSentTo"]);
        Assert.Equal(["Disable-EOPProtectionPolicyRule", "Disable-ATPProtectionPolicyRule"],
            GraphChangeExecutor.PresetCalls(new SetPresetEnabled(PresetKind.Strict, true, true, false)).Select(c => c.Cmdlet));
    }

    [Fact]
    public async Task A_scope_change_is_read_back_from_the_rule()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-ATPBuiltInProtectionRule"
            ? """[{"Name":"ATP Built-In Protection Rule","ExceptIfSentTo":["Zz-Tenantry-Test-Bo@contoso.example"]}]""" : "[]");
        var executor = new GraphChangeExecutor(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);

        var result = await executor.ApplyAsync(new SetPresetScope(PresetKind.BuiltIn, true, "ExceptIfSentTo", "Except People", true, [], ["zz-tenantry-test-bo@contoso.example"]), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal(["Set-ATPBuiltInProtectionRule", "Get-ATPBuiltInProtectionRule"], exchange.Calls.Select(c => c.Cmdlet));
    }

    sealed class Records(params PresetPolicy[] presets) : IPresetPolicyRecords
    {
        public Task<IReadOnlyList<PresetPolicy>> GetPresetPoliciesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PresetPolicy>>(presets);
    }

    static PresetPolicy Preset(PresetKind kind, bool exists, bool enabled = true) =>
        new(kind, exists, enabled, true, Presets.ScopeParts(kind).ToDictionary(p => p.Parameter, _ => (IReadOnlyList<string>)[]));

    [Fact]
    public async Task A_never_turned_on_preset_points_to_Defender_and_offers_no_switch()
    {
        var page = new PresetPoliciesViewModel(new Records(Preset(PresetKind.Strict, false), Preset(PresetKind.Standard, true)), Pipeline(), Availability.Yes);
        await page.Refresh();

        Assert.True(page.Cards[0].NeverTurnedOn);
        Assert.False(page.Cards[0].CanSwitch);
        Assert.False(page.Cards[0].Scope[0].CanEdit);
        Assert.Equal(("On", "Turn Off"), (page.Cards[1].StateText, page.Cards[1].SwitchText));
    }

    [Fact]
    public async Task Editing_who_a_preset_covers_stages_one_change_and_putting_it_back_drops_it()
    {
        var pipeline = Pipeline();
        var page = new PresetPoliciesViewModel(new Records(Preset(PresetKind.Standard, true)), pipeline, Availability.Yes);
        await page.Refresh();
        var card = page.Cards[0];
        var except = card.Scope.Single(s => s.Part.Parameter == "ExceptIfSentToMemberOf");

        except.Text = "Leadership\nFinance";
        except.CommitCommand.Execute(null);
        var change = (SetPresetScope)Assert.Single(pipeline.Pending);
        Assert.Equal(["Leadership", "Finance"], change.New);
        except.Text = "";
        except.CommitCommand.Execute(null);
        Assert.Empty(pipeline.Pending);

        card.SwitchCommand.Execute(null);
        Assert.Equal("Undo", card.SwitchText);
        card.SwitchCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }
}
