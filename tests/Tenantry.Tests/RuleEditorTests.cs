using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.MailFlow;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.Tests;

/// <summary>The rule editor (5d-2): starters, the sentence, what goes to Pending changes, and the add-a-part list.</summary>
public sealed class RuleEditorTests
{
    static ChangePipeline Pipeline() => new(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static readonly MailFlowRule[] Rules =
    [
        new("g0", "Existing rule", 0, true, RuleMode.Enforce, [], [], [], null, null, null, null, null),
        new("g1", "zz-tenantry-test-rule", 1, true, RuleMode.Test, [], [], [], null, null, null, null, null),
    ];

    static RuleDraft Existing()
    {
        var draft = new RuleDraft { Identity = "g1", Name = "zz-tenantry-test-rule", Mode = RuleMode.Test };
        draft.Conditions.Add(new RuleLine(RuleCatalog.Instance.Conditions.Single(c => c.Parameter == "SubjectContainsWords"), RulePartKind.Condition) { Value = new JsonArray("invoice") });
        draft.Actions.Add(new RuleLine(RuleCatalog.Instance.Actions.Single(a => a.Parameter == "PrependSubject"), RulePartKind.Action) { Value = "[X] " });
        return draft;
    }

    [Fact]
    public void Every_starter_builds_from_the_catalog()
    {
        foreach (var starter in RuleStarters.All)
        {
            var draft = starter.Build();
            Assert.All(draft.AllLines, l => Assert.True(l.Def.IsAvailable, l.Def.Label));
            foreach (var line in draft.AllLines.Where(l => l.Def.Choices.Count > 0 && l.Value is JsonValue))
                Assert.Contains(line.Def.Choices, c => c.Value == line.Value!.ToString());
        }
    }

    [Fact]
    public void A_starter_fills_the_editor_and_goes_to_pending_in_test_mode()
    {
        var pipeline = Pipeline();
        var closed = false;
        var editor = RuleEditorViewModel.New(pipeline, Availability.Yes, Rules, _ => closed = true);
        Assert.True(editor.ShowStarters);
        Assert.Empty(pipeline.Pending);

        editor.StartCommand.Execute(RuleStarters.All.Single(s => s.Title == "Block Risky Attachments"));

        Assert.False(editor.ShowStarters);
        Assert.Empty(editor.Problems);
        var create = Assert.IsType<CreateRule>(Assert.Single(pipeline.Pending));
        var parameters = (JsonObject)JsonNode.Parse(create.Parameters)!;
        Assert.Equal("Audit", parameters["Mode"]!.GetValue<string>());
        Assert.Equal(Rules.Length, parameters["Priority"]!.GetValue<int>());
        Assert.Contains("exe", parameters["AttachmentExtensionMatchesWords"]!.ToJsonString());
        Assert.False(create.IsCaution);
        Assert.StartsWith("When any attachment's file extension matches", editor.Summary);

        editor.Name = "zz-tenantry-test-blocker";
        Assert.Equal("zz-tenantry-test-blocker", Assert.IsType<CreateRule>(Assert.Single(pipeline.Pending)).Rule.Name);

        editor.DiscardCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
        Assert.True(closed);
    }

    [Fact]
    public async Task Once_its_changes_are_applied_the_editor_closes()
    {
        var pipeline = Pipeline();
        var closed = 0;
        var created = RuleEditorViewModel.New(pipeline, Availability.Yes, Rules, _ => closed++);
        created.StartCommand.Execute(RuleStarters.All.Single(s => s.Title == "Block Risky Attachments"));
        created.Name = "zz-tenantry-test-new";
        var edited = RuleEditorViewModel.Edit(Rules[1], _ => Task.FromResult(Existing()), pipeline, Availability.Yes, Rules, _ => closed++);
        await Task.Yield();
        edited.Actions[0].Main!.Text = "[Y] ";

        await pipeline.ApplyAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, closed);
    }

    [Fact]
    public void A_rule_with_problems_leaves_pending_until_they_are_fixed()
    {
        var pipeline = Pipeline();
        var editor = RuleEditorViewModel.New(pipeline, Availability.Yes, Rules, _ => { });
        editor.StartCommand.Execute(RuleStarters.All.Single(s => s.Title == "Copy Mail to Someone"));

        Assert.Contains(editor.Problems, p => p.Contains("the recipient is", StringComparison.Ordinal));
        Assert.Empty(pipeline.Pending);

        editor.Conditions[0].Main!.NewEntry = "pat@contoso.example";
        editor.Actions[0].Main!.NewEntry = "auditor@contoso.example";
        Assert.Empty(editor.Problems);
        Assert.Single(pipeline.Pending);

        editor.Name = "Existing rule";
        Assert.Contains(editor.Problems, p => p.Contains("already called", StringComparison.Ordinal));
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public async Task Editing_stages_only_the_changes_and_mode_separately()
    {
        var pipeline = Pipeline();
        var editor = RuleEditorViewModel.Edit(Rules[1], _ => Task.FromResult(Existing()), pipeline, Availability.Yes, Rules, _ => { });
        await Task.Yield();
        Assert.False(editor.IsLoading);
        Assert.Empty(pipeline.Pending);

        editor.Conditions[0].Main!.NewEntry = "receipt";
        editor.Conditions[0].Main!.AddEntry();
        var update = Assert.IsType<UpdateRule>(Assert.Single(pipeline.Pending));
        Assert.Equal(["SubjectContainsWords"], ((JsonObject)JsonNode.Parse(update.Changes)!).Select(p => p.Key));

        editor.Mode = RuleEditorViewModel.Modes.Single(m => m.Value == RuleMode.Enforce);
        Assert.Contains(pipeline.Pending, c => c is SetRuleMode { New: RuleMode.Enforce });
        Assert.DoesNotContain("Mode", ((JsonObject)JsonNode.Parse(pipeline.Pending.OfType<UpdateRule>().Single().Changes)!).Select(p => p.Key));

        editor.Conditions[0].Main!.Chips[1].RemoveCommand.Execute(null);
        editor.Mode = RuleEditorViewModel.Modes.Single(m => m.Value == RuleMode.Test);
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public async Task Reopening_a_rule_carries_on_from_its_pending_edit()
    {
        var pipeline = Pipeline();
        var first = RuleEditorViewModel.Edit(Rules[1], _ => Task.FromResult(Existing()), pipeline, Availability.Yes, Rules, _ => { });
        await Task.Yield();
        first.Actions[0].Main!.Text = "[CHECK] ";
        first.Dispose();

        var again = RuleEditorViewModel.Edit(Rules[1], _ => Task.FromResult(Existing()), pipeline, Availability.Yes, Rules, _ => { });
        await Task.Yield();

        Assert.Equal("[CHECK] ", again.Actions[0].Main!.Text);
        Assert.Single(pipeline.Pending);
    }

    [Fact]
    public void Lines_can_be_added_removed_and_a_part_is_used_once()
    {
        var pipeline = Pipeline();
        var editor = RuleEditorViewModel.New(pipeline, Availability.Yes, Rules, _ => { });
        editor.StartCommand.Execute(RuleStarters.All[0]);

        var delete = editor.ActionPicker.Groups.SelectMany(g => g.Items).Single(i => i.Def.Parameter == "DeleteMessage");
        editor.ActionPicker.ChooseCommand.Execute(delete);
        Assert.Single(editor.Actions);
        Assert.False(editor.ActionPicker.Groups.SelectMany(g => g.Items).Single(i => i.Def.Parameter == "DeleteMessage").CanAdd);

        var prepend = editor.ActionPicker.Groups.SelectMany(g => g.Items).Single(i => i.Label == "Prepend the disclaimer");
        editor.ActionPicker.ChooseCommand.Execute(prepend);
        Assert.False(editor.ActionPicker.Groups.SelectMany(g => g.Items).Single(i => i.Label == "Append the disclaimer").CanAdd);

        editor.Actions[0].RemoveCommand.Execute(null);
        Assert.Single(editor.Actions);
        Assert.True(editor.ActionPicker.Groups.SelectMany(g => g.Items).Single(i => i.Def.Parameter == "DeleteMessage").CanAdd);
    }

    [Fact]
    public void The_picker_searches_marks_advanced_and_greys_what_cannot_work()
    {
        var picker = new RulePartPickerViewModel(RulePartKind.Condition, _ => new HashSet<string>(), (_, _) => { });
        var all = picker.Groups.SelectMany(g => g.Items).ToArray();
        Assert.Contains(all, i => i.IsAdvanced);
        Assert.Contains(all, i => !i.CanAdd && i.Status == "Not Available");
        Assert.Contains(picker.Groups, g => g.Title == "The Sender");

        picker.Search = "attachment password";
        Assert.Equal("Any attachment is password protected", Assert.Single(picker.Groups.SelectMany(g => g.Items)).Label);

        picker.Search = "zzzz";
        Assert.True(picker.HasNoMatches);
    }

    [Theory]
    [InlineData("patterns", "invoice(", "not a valid pattern")]
    [InlineData("domains", "pat@contoso.com", "enter just the domain")]
    [InlineData("ipRanges", "192.0.2.0/40", "isn't an IPv4")]
    [InlineData("dsnCode", "5.7.2", "5.7.1")]
    [InlineData("adAttributeWords", "Nonsense:x", "isn't a property")]
    public void Values_are_checked_as_they_are_typed(string valueType, string text, string expected)
    {
        var choices = valueType == "adAttributeWords" ? RuleCatalog.Instance.Conditions.First(c => c.ValueType == valueType).Choices : [];
        var editor = new RuleValueEditor("", valueType, choices, true, null, _ => { });
        if (editor.IsLines)
            editor.NewEntry = text;
        else
            editor.Text = text;
        Assert.Contains(expected, editor.Problem);
    }

    [Fact]
    public void A_pattern_over_the_limit_is_refused()
    {
        var editor = new RuleValueEditor("", "patterns", [], true, null, _ => { }) { NewEntry = new string('a', RuleValues.MaxPatternLength + 1) };
        Assert.Contains("at most", editor.Problem);
    }

    [Fact]
    public void Several_addresses_are_chips_added_by_enter_or_by_pasting_a_list()
    {
        JsonNode? value = null;
        var editor = new RuleValueEditor("", "addresses", [], true, new JsonArray("it@contoso.example"), v => value = v);
        Assert.Equal(["it@contoso.example"], editor.Chips.Select(c => c.Text));

        editor.NewEntry = "help@contoso.example";
        Assert.Equal(2, ((JsonArray)value!).Count);
        editor.AddEntry();
        Assert.Equal("", editor.NewEntry);

        editor.NewEntry = "a@contoso.example, b@contoso.example; IT@contoso.example";
        Assert.Equal(["it@contoso.example", "help@contoso.example", "a@contoso.example", "b@contoso.example"], editor.Chips.Select(c => c.Text));
        Assert.Equal(4, ((JsonArray)value!).Count);

        editor.Chips[0].RemoveCommand.Execute(null);
        editor.EditLastChip();
        Assert.Equal("b@contoso.example", editor.NewEntry);
        Assert.Equal(3, ((JsonArray)value!).Count);
    }

    [Fact]
    public void A_bad_chip_is_marked_and_named()
    {
        var editor = new RuleValueEditor("", "domains", [], true, null, _ => { }) { NewEntry = "contoso.com\npat@contoso.com" };
        Assert.True(editor.Chips[1].HasProblem);
        Assert.False(editor.Chips[0].HasProblem);
        Assert.StartsWith("'pat@contoso.com' is an address", editor.Problem);
    }

    [Fact]
    public void Patterns_keep_their_commas()
    {
        var editor = new RuleValueEditor("", "patterns", [], true, null, _ => { }) { NewEntry = @"\d{3,4}" };
        Assert.Empty(editor.Chips);
        editor.AddEntry();
        Assert.Equal([@"\d{3,4}"], editor.Chips.Select(c => c.Text));
    }

    [Fact]
    public void The_sentence_joins_conditions_with_and_and_exceptions_with_or()
    {
        var editor = RuleEditorViewModel.New(Pipeline(), Availability.Yes, Rules, _ => { });
        editor.StartCommand.Execute(RuleStarters.All.Single(s => s.Title == "Add a Disclaimer"));
        Assert.Equal(["", "and"], editor.Conditions.Select(l => l.Connector));
        Assert.Equal("the sender is located", editor.Conditions[0].Label);
    }

    [Theory]
    [InlineData("Add recipients to the To box", "Add Recipients to the To Box")]
    [InlineData("Set the spam confidence level (SCL) to", "Set the Spam Confidence Level (SCL) To")]
    [InlineData("The sender's domain is", "The Sender's Domain Is")]
    [InlineData("redirect the message to", "Redirect the Message To")]
    public void Labels_read_in_title_case(string text, string expected) =>
        Assert.Equal(expected, Tenantry.ViewModels.Display.TitleCase(text));

    [Fact]
    public void Sizes_are_entered_with_a_unit_and_kept_in_bytes()
    {
        JsonNode? value = null;
        var editor = new RuleValueEditor("", "size", [], true, JsonValue.Create(10L << 20), v => value = v);
        Assert.Equal((10m, "MB"), (editor.SizeAmount, editor.SizeUnit));

        editor.SizeUnit = "KB";
        Assert.Equal(10L << 10, value!.GetValue<long>());
    }
}
