using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.Tests;

/// <summary>Mail flow rules (5c): reading Exchange's description, the preview's changes, what they send, and the guard.</summary>
public sealed class MailFlowRuleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Exchanges_description_splits_into_when_except_and_then()
    {
        const string description = "If the message:\r\n\tIs received from 'Outside the organization'\r\n\tand\r\n\tIncludes these words in the subject: 'invoice'\r\n"
                                   + "Take the following actions:\r\n\tPrepend the subject of the message with '[EXTERNAL] '\r\n"
                                   + "Except if the message:\r\n\tIs sent to 'billing@contoso.example'\r\n";

        var (conditions, exceptions, actions) = MailFlowRule.ParseDescription(description);

        Assert.Equal(["Is received from 'Outside the organization'", "Includes these words in the subject: 'invoice'"], conditions);
        Assert.Equal(["Is sent to 'billing@contoso.example'"], exceptions);
        Assert.Equal(["Prepend the subject of the message with '[EXTERNAL] '"], actions);
    }

    [Fact]
    public async Task Rules_read_their_state_mode_and_order()
    {
        var exchange = new FakeCmdlets((_, _) => """
            [{"Guid":"g2","Name":"Second","Priority":1,"State":"Disabled","Mode":"Audit","Description":"If the message:\n\tIs sent to 'x'\nTake the following actions:\n\tDelete the message without notifying anyone\n"},
             {"Guid":"g1","Name":"First","Priority":0,"State":"Enabled","Mode":"Enforce","Description":"Take the following actions:\n\tSet the spam confidence level (SCL) to '-1'\n"}]
            """);
        var records = new GraphUserRecords(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), exchange);

        var rules = await records.GetRulesAsync(Ct);

        Assert.Equal(["First", "Second"], rules.Select(r => r.Name));
        Assert.Equal((false, RuleMode.Test, 1), (rules[1].Enabled, rules[1].Mode, rules[1].Conditions.Count));
        Assert.Empty(rules[0].Conditions);
    }

    static ChangePipeline Pipeline(IChangeExecutor executor, bool testMode = true) =>
        new(executor, new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(testMode), "admin");

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static MailFlowRule Rule(int priority = 2, bool enabled = true, RuleMode mode = RuleMode.Enforce, string name = "zz-tenantry-test-rule") =>
        new("g1", name, priority, enabled, mode, ["Is sent to 'x'"], [], ["Set the message header 'X-Test' to 'yes'"], null, null, null, null, null);

    [Fact]
    public void The_preview_stages_on_off_mode_order_and_delete_and_undoes_them()
    {
        var pipeline = Pipeline(new Nothing());
        var preview = new RulePreviewViewModel(Rule(), 5, pipeline, Availability.Yes);

        preview.ToggleEnabledCommand.Execute(null);
        Assert.False(Assert.IsType<SetRuleEnabled>(Assert.Single(pipeline.Pending)).New);
        Assert.Equal("Undo", preview.EnableText);
        preview.ToggleEnabledCommand.Execute(null);
        Assert.Empty(pipeline.Pending);

        preview.Mode = RulePreviewViewModel.Modes.Single(m => m.Value == RuleMode.Test);
        Assert.Equal(RuleMode.Test, Assert.IsType<SetRuleMode>(Assert.Single(pipeline.Pending)).New);
        preview.Mode = RulePreviewViewModel.Modes.Single(m => m.Value == RuleMode.Enforce);
        Assert.Empty(pipeline.Pending);

        preview.MoveUpCommand.Execute(null);
        preview.MoveUpCommand.Execute(null);
        var move = Assert.IsType<SetRulePriority>(Assert.Single(pipeline.Pending));
        Assert.Equal((2, 0), (move.Old, move.New));
        Assert.False(preview.CanMoveUp);
        preview.MoveToBottomCommand.Execute(null);
        Assert.Equal(4, Assert.IsType<SetRulePriority>(Assert.Single(pipeline.Pending)).New);

        preview.DeleteCommand.Execute(null);
        Assert.Contains(pipeline.Pending, c => c is RemoveRule);
    }

    [Fact]
    public void Rules_move_one_at_a_time()
    {
        var pipeline = Pipeline(new Nothing());
        var first = new RulePreviewViewModel(Rule(priority: 2), 5, pipeline, Availability.Yes);
        var other = new RulePreviewViewModel(Rule(priority: 3) with { Identity = "g2", Name = "zz-tenantry-test-other" }, 5, pipeline, Availability.Yes);

        first.MoveUpCommand.Execute(null);

        Assert.False(other.CanMoveUp);
        Assert.False(other.CanMoveDown);
        Assert.True(other.HasOrderNote);
        other.MoveToTopCommand.Execute(null);
        Assert.Single(pipeline.Pending);
        // The rule with the pending move can still adjust it.
        Assert.True(first.CanMoveUp);
        Assert.False(first.HasOrderNote);
    }

    [Fact]
    public async Task Each_change_sends_its_cmdlet_and_reads_the_rule_back()
    {
        var reads = new Queue<string>(["""[{"Guid":"g1","Name":"zz-tenantry-test-rule","Priority":0,"State":"Disabled","Mode":"Audit"}]""", "[]"]);
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-TransportRule" ? (reads.Count > 1 ? reads.Peek() : reads.Dequeue()) : "[]");
        var executor = new GraphChangeExecutor(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);
        var target = new ChangeTarget("rule", "g1", "zz-tenantry-test-rule", null);

        var off = await executor.ApplyAsync(new SetRuleEnabled(target, true, false), Ct);
        var test = await executor.ApplyAsync(new SetRuleMode(target, RuleMode.Enforce, RuleMode.Test), Ct);
        var first = await executor.ApplyAsync(new SetRulePriority(target, 3, 0), Ct);
        reads.Dequeue();
        var gone = await executor.ApplyAsync(new RemoveRule(target, "Off, Test"), Ct);

        Assert.All(new[] { off, test, first, gone }, r => Assert.Equal(ChangeOutcome.Applied, r.Outcome));
        Assert.Contains(exchange.Calls, c => c.Cmdlet == "Disable-TransportRule");
        Assert.Contains(exchange.Calls, c => c.Cmdlet == "Set-TransportRule" && c.Parameters.Contains("\"Mode\":\"Audit\""));
        Assert.Contains(exchange.Calls, c => c.Cmdlet == "Set-TransportRule" && c.Parameters.Contains("\"Priority\":0"));
        Assert.Contains(exchange.Calls, c => c.Cmdlet == "Remove-TransportRule");
    }

    [Fact]
    public async Task Test_mode_only_lets_test_named_rules_change()
    {
        var exchange = new FakeCmdlets();
        var executor = new GraphChangeExecutor(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);
        var pipeline = Pipeline(executor);
        pipeline.Stage(new SetRuleEnabled(new ChangeTarget("rule", "g9", "External sender warning", null), true, false));

        Assert.Equal(ChangeOutcome.Refused, Assert.Single(await pipeline.ApplyAsync(cancellationToken: Ct)).Result.Outcome);
        Assert.Empty(exchange.Calls);
    }

    [Fact]
    public void Switching_a_rule_to_Enforce_and_turning_one_off_need_a_second_look()
    {
        var target = new ChangeTarget("rule", "g1", "r", null);
        Assert.True(new SetRuleMode(target, RuleMode.Test, RuleMode.Enforce).IsCaution);
        Assert.False(new SetRuleMode(target, RuleMode.Enforce, RuleMode.Test).IsCaution);
        Assert.True(new SetRuleEnabled(target, true, false).IsCaution);
        Assert.Equal(("Runs 4th", "Runs 1st"), (new SetRulePriority(target, 3, 0).Before, new SetRulePriority(target, 3, 0).After));
    }
}
