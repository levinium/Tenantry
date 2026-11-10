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

    /// <summary>Five rules, the test one third (index 2), as the Rules page holds them.</summary>
    static IReadOnlyList<MailFlowRule> Five() =>
        [.. Enumerable.Range(0, 5).Select(i => i == 2 ? Rule() : Rule(priority: i, name: $"zz-tenantry-test-r{i}") with { Identity = $"r{i}" })];

    [Fact]
    public void The_preview_stages_on_off_mode_order_and_delete_and_undoes_them()
    {
        var pipeline = Pipeline(new Nothing());
        var all = Five();
        var preview = new RulePreviewViewModel(Rule(), 5, pipeline, Availability.Yes, allRules: () => all);

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
        var move = Assert.IsType<ReorderRules>(Assert.Single(pipeline.Pending));
        Assert.Equal("g1", move.New[0].Identity);
        Assert.Equal([("g1", 0)], move.Moves());
        Assert.False(preview.CanMoveUp);
        preview.MoveToBottomCommand.Execute(null);
        Assert.Equal("g1", Assert.IsType<ReorderRules>(Assert.Single(pipeline.Pending)).New[4].Identity);

        preview.DeleteCommand.Execute(null);
        Assert.Contains(pipeline.Pending, c => c is RemoveRule);
    }

    [Fact]
    public void Moves_of_several_rules_make_one_new_order()
    {
        var pipeline = Pipeline(new Nothing());
        var all = Five();
        var third = new RulePreviewViewModel(all[2], 5, pipeline, Availability.Yes, allRules: () => all);
        var last = new RulePreviewViewModel(all[4], 5, pipeline, Availability.Yes, allRules: () => all);

        third.MoveToTopCommand.Execute(null);
        last.MoveUpCommand.Execute(null);

        var order = Assert.IsType<ReorderRules>(Assert.Single(pipeline.Pending));
        Assert.Equal(["g1", "r0", "r1", "r4", "r3"], order.New.Select(s => s.Identity));
        // Two rules out of place: two moves (either pair of them, as long as they end in the new order).
        Assert.Equal(2, order.Moves().Count);
    }

    [Fact]
    public void The_moves_turn_the_old_order_into_the_new_one_with_as_few_calls_as_it_takes()
    {
        RuleSlot S(string id) => new(id, id);
        var old = new[] { S("a"), S("b"), S("c"), S("d"), S("e"), S("f") };
        var cases = new[] { "fabcde", "bcdefa", "abcdfe", "fedcba", "cabdef", "abcdef" };
        foreach (var wanted in cases)
        {
            var change = new ReorderRules(ReorderRules.OrderTarget, old, [.. wanted.Select(c => S(c.ToString()))]);
            // Playing the moves as Exchange does (each rule to its place, the rest shifting) ends in the order asked for.
            var current = old.Select(s => s.Identity).ToList();
            foreach (var (id, priority) in change.Moves())
            {
                current.Remove(id);
                current.Insert(priority, id);
            }
            Assert.Equal(wanted.Select(c => c.ToString()), current);
        }
        Assert.Single(new ReorderRules(ReorderRules.OrderTarget, old, [.. "fabcde".Select(c => S(c.ToString()))]).Moves());
        Assert.Single(new ReorderRules(ReorderRules.OrderTarget, old, [.. "bcdefa".Select(c => S(c.ToString()))]).Moves());
        Assert.Empty(new ReorderRules(ReorderRules.OrderTarget, old, old).Moves());
    }

    [Fact]
    public void Any_new_order_is_reached_and_never_with_more_moves_than_rules_out_of_place()
    {
        var random = new Random(20261110);
        for (var round = 0; round < 300; round++)
        {
            var count = random.Next(1, 40);
            var old = Enumerable.Range(0, count).Select(i => new RuleSlot($"r{i}", $"Rule {i}")).ToArray();
            var wanted = old.OrderBy(_ => random.Next()).ToArray();
            var change = new ReorderRules(ReorderRules.OrderTarget, old, wanted);
            var current = old.Select(s => s.Identity).ToList();
            foreach (var (id, priority) in change.Moves())
            {
                Assert.InRange(priority, 0, count - 1);
                current.Remove(id);
                current.Insert(priority, id);
            }
            Assert.Equal(wanted.Select(s => s.Identity), current);
            // The fewest possible: every rule outside the longest run already in order moves once.
            var positions = wanted.Select(s => int.Parse(s.Identity[1..], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var run = new int[count];
            for (var i = 0; i < count; i++)
            {
                run[i] = 1;
                for (var j = 0; j < i; j++)
                    if (positions[j] < positions[i])
                        run[i] = Math.Max(run[i], run[j] + 1);
            }
            Assert.Equal(count - run.Max(), change.Moves().Count);
        }
    }

    [Fact]
    public void Reorder_Rules_moves_ticked_rules_together_drags_and_keeps_one_pending_order()
    {
        var pipeline = Pipeline(new Nothing());
        var all = Five();
        using var order = new RuleOrderViewModel(all, pipeline, Availability.Yes);

        Assert.False(order.MoveUpCommand.CanExecute(null));
        order.Tick(order.Rows[3], shift: false);
        order.Tick(order.Rows[4], shift: true);
        Assert.Equal(2, order.TickedCount);
        order.MoveToTopCommand.Execute(null);
        Assert.Equal(["r3", "r4", "r0", "r1", "g1"], order.Rows.Select(r => r.Identity));
        // Only the two that moved say where they were; the rest just shifted down.
        Assert.Equal(["was 4", "was 5"], order.Rows.Where(r => r.IsMoved).Select(r => r.WasText));

        order.Drop(order.Rows.Single(r => r.Identity == "g1"), 0);
        var change = Assert.IsType<ReorderRules>(Assert.Single(pipeline.Pending));
        Assert.Equal(["g1", "r3", "r4", "r0", "r1"], change.New.Select(s => s.Identity));
        // g1, r3 and r4 are already in order among themselves: only r0 and r1 move.
        Assert.Contains("applying it sends 2 moves", order.SummaryText, StringComparison.Ordinal);

        // Undone in the tray: the rows go back as they are.
        pipeline.Unstage(change);
        Assert.Equal(["r0", "r1", "g1", "r3", "r4"], order.Rows.Select(r => r.Identity));
        Assert.False(order.HasMoved);
    }

    [Fact]
    public async Task A_new_order_sends_its_moves_one_after_another_then_reads_the_order_back()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-TransportRule"
            // As Exchange answers: Identity is the name, Guid is the id Tenantry keeps.
            ? "[{\"Guid\":\"c\",\"Identity\":\"C\",\"Priority\":0},{\"Guid\":\"a\",\"Identity\":\"A\",\"Priority\":1},{\"Guid\":\"b\",\"Identity\":\"B\",\"Priority\":2}]"
            : "[]");
        var executor = new GraphChangeExecutor(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);

        var result = await executor.ApplyAsync(new ReorderRules(ReorderRules.OrderTarget,
            [new("a", "A"), new("b", "B"), new("c", "C")], [new("c", "C"), new("a", "A"), new("b", "B")]), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var sets = exchange.Calls.Where(c => c.Cmdlet == "Set-TransportRule").ToArray();
        Assert.Contains("\"Identity\":\"c\"", Assert.Single(sets).Parameters, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pending_changes_shows_which_move_is_going_while_a_new_order_applies()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-TransportRule"
            ? "[{\"Guid\":\"c\",\"Priority\":0},{\"Guid\":\"b\",\"Priority\":1},{\"Guid\":\"a\",\"Priority\":2}]"
            : "[]");
        var executor = new GraphChangeExecutor(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);
        var pipeline = new ChangePipeline(executor, new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var tray = new Tenantry.ViewModels.Editing.PendingChangesViewModel(pipeline, "Contoso", true, () => Task.CompletedTask);
        RuleSlot Slot(string id) => new(id, $"zz-tenantry-test-{id}");
        pipeline.Stage(new ReorderRules(ReorderRules.OrderTarget, [Slot("a"), Slot("b"), Slot("c")], [Slot("c"), Slot("b"), Slot("a")]));
        var row = Assert.Single(tray.Rows);
        var seen = new List<string>();
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(row.Progress) && row.Progress is { } text) seen.Add(text); };

        await tray.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(3, seen.Count);
        Assert.StartsWith("Moving rule 1 of 2: \"zz-tenantry-test-", seen[0], StringComparison.Ordinal);
        Assert.StartsWith("Moving rule 2 of 2: \"zz-tenantry-test-", seen[1], StringComparison.Ordinal);
        Assert.Equal("Checking the new order", seen[2]);
        // With no one listening (outside an apply), a report does nothing.
        ApplyProgress.Report("ignored");
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
