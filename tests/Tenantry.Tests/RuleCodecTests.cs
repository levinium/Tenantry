using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.MailFlow;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;

namespace Tenantry.Tests;

/// <summary>The rule engine (5d): reading a rule into a draft and turning it back into exactly what Exchange expects.</summary>
public sealed class RuleCodecTests
{
    const string Sample = """
        {"Guid":"g1","Name":"Invoice warning","Priority":3,"State":"Enabled","Mode":"Audit","Description":"…","WhenChanged":"2026-01-01",
         "FromScope":"NotInOrganization","SubjectContainsWords":["invoice","payment"],"ExceptIfSentTo":["billing@contoso.example"],
         "AttachmentSizeOver":"10 MB (10,485,760 bytes)","PrependSubject":"[EXTERNAL] ",
         "ApplyHtmlDisclaimerText":"<p>Careful</p>","ApplyHtmlDisclaimerLocation":"Prepend","ApplyHtmlDisclaimerFallbackAction":"Wrap",
         "SetHeaderName":"X-Checked","SetHeaderValue":"yes","StopRuleProcessing":true,"Comments":"note","SentTo":null,"SetSCL":null,
         "SomeFutureOption":"kept as is"}
        """;

    static RuleDraft Read(string json) => RuleCodec.Read(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void The_catalog_loads_every_condition_and_action()
    {
        var catalog = RuleCatalog.Instance;
        Assert.True(catalog.Conditions.Count >= 50);
        Assert.True(catalog.Actions.Count >= 30);
        Assert.Contains(catalog.For(RulePartKind.Exception), d => d.Parameter == "SentTo" && d.ExceptParameter == "ExceptIfSentTo");
        Assert.Equal(2, catalog.Actions.Count(a => a.Parameter == "ApplyHtmlDisclaimerText"));
    }

    [Fact]
    public void A_rule_reads_into_lines_settings_and_kept_parts()
    {
        var draft = Read(Sample);

        Assert.Equal(("g1", "Invoice warning", RuleMode.Test, true), (draft.Identity, draft.Name, draft.Mode, draft.Enabled));
        Assert.Equal(["AttachmentSizeOver", "FromScope", "SubjectContainsWords"], draft.Conditions.Select(l => l.Def.Parameter).Order(StringComparer.Ordinal));
        Assert.Equal(10_485_760, draft.Conditions.Single(l => l.Def.Parameter == "AttachmentSizeOver").Value!.GetValue<long>());
        Assert.Equal("ExceptIfSentTo", Assert.Single(draft.Exceptions).Def.ParameterFor(RulePartKind.Exception));

        var disclaimer = draft.Actions.Single(l => l.Def.Parameter == "ApplyHtmlDisclaimerText");
        Assert.Equal("Prepend", disclaimer.Def.Fixed["ApplyHtmlDisclaimerLocation"]);
        Assert.Equal("Wrap", disclaimer.Extras["ApplyHtmlDisclaimerFallbackAction"]!.GetValue<string>());
        Assert.Equal("yes", draft.Actions.Single(l => l.Def.Parameter == "SetHeaderName").Extras["SetHeaderValue"]!.GetValue<string>());

        Assert.True(draft.Settings["StopRuleProcessing"]!.GetValue<bool>());
        Assert.Equal("kept as is", Assert.Single(draft.Kept).Value.GetString());
    }

    [Fact]
    public void A_rule_turns_back_into_the_same_parameters()
    {
        var original = JsonDocument.Parse(Sample).RootElement;
        var parameters = RuleCodec.Parameters(Read(Sample));

        string[] expected = ["FromScope", "SubjectContainsWords", "ExceptIfSentTo", "AttachmentSizeOver", "PrependSubject", "ApplyHtmlDisclaimerText",
            "ApplyHtmlDisclaimerLocation", "ApplyHtmlDisclaimerFallbackAction", "SetHeaderName", "SetHeaderValue", "StopRuleProcessing", "Comments"];
        Assert.Equal(expected.Order(StringComparer.Ordinal), parameters.Keys.Order(StringComparer.Ordinal));
        foreach (var name in expected.Where(n => n != "AttachmentSizeOver"))
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original.GetProperty(name).GetRawText()), parameters[name]), name);
        Assert.Equal("10485760", parameters["AttachmentSizeOver"]!.GetValue<string>());
    }

    [Fact]
    public void An_unchanged_rule_has_no_changes()
    {
        var draft = Read(Sample);
        Assert.Empty(RuleCodec.Changes(draft, draft.Clone()));
    }

    [Fact]
    public void An_edit_sends_only_what_changed_and_clears_what_was_removed()
    {
        var original = Read(Sample);
        var edited = original.Clone();
        edited.Name = "Invoice warning v2";
        edited.Conditions.Single(l => l.Def.Parameter == "SubjectContainsWords").Value = new JsonArray("invoice");
        edited.Actions.RemoveAll(l => l.Def.Parameter == "SetHeaderName");
        edited.Settings.Remove("StopRuleProcessing");
        edited.Mode = RuleMode.Enforce;

        var changes = RuleCodec.Changes(original, edited);

        Assert.Equal(["Mode", "Name", "SetHeaderName", "SetHeaderValue", "StopRuleProcessing", "SubjectContainsWords"], changes.Keys.Order(StringComparer.Ordinal));
        Assert.Null(changes["SetHeaderName"]);
        Assert.False(changes["StopRuleProcessing"]!.GetValue<bool>());
        Assert.Equal("Enforce", changes["Mode"]!.GetValue<string>());
        Assert.DoesNotContain("SomeFutureOption", changes.Keys);
    }

    [Fact]
    public void Changing_a_companion_value_sends_the_whole_line()
    {
        var original = Read(Sample);
        var edited = original.Clone();
        edited.Actions.Single(l => l.Def.Parameter == "SetHeaderName").Extras["SetHeaderValue"] = "no";

        Assert.Equal(["SetHeaderName", "SetHeaderValue"], RuleCodec.Changes(original, edited).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Switching_a_disclaimer_from_prepend_to_append_changes_its_location()
    {
        var original = Read(Sample);
        var edited = original.Clone();
        var index = edited.Actions.FindIndex(l => l.Def.Parameter == "ApplyHtmlDisclaimerText");
        var append = RuleCatalog.Instance.Actions.Single(a => a.Parameter == "ApplyHtmlDisclaimerText" && a.Fixed["ApplyHtmlDisclaimerLocation"] == "Append");
        var line = new RuleLine(append, RulePartKind.Action) { Value = edited.Actions[index].Value };
        foreach (var (k, v) in edited.Actions[index].Extras)
            line.Extras[k] = v;
        edited.Actions[index] = line;

        var changes = RuleCodec.Changes(original, edited);
        Assert.Equal("Append", changes["ApplyHtmlDisclaimerLocation"]!.GetValue<string>());
        Assert.Equal(["ApplyHtmlDisclaimerFallbackAction", "ApplyHtmlDisclaimerLocation", "ApplyHtmlDisclaimerText"], changes.Keys.Order(StringComparer.Ordinal));
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static GraphChangeExecutor Executor(FakeCmdlets exchange) =>
        new(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);

    [Fact]
    public async Task A_new_rule_is_made_and_read_back()
    {
        var draft = Read(Sample);
        draft.Name = "zz-tenantry-test-new";
        var parameters = RuleCodec.NewRuleParameters(draft);
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-TransportRule" ? $"[{Sample}]" : "[]");

        var result = await Executor(exchange).ApplyAsync(new CreateRule(new ChangeTarget("rule", draft.Name, draft.Name, null), parameters.ToJsonString(), RuleCodec.Summary(draft), false), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var sent = Assert.Single(exchange.Calls, c => c.Cmdlet == "New-TransportRule").Parameters;
        Assert.Contains("\"Name\":\"zz-tenantry-test-new\"", sent);
        Assert.Contains("\"Mode\":\"Audit\"", sent);
        Assert.Contains("\"AttachmentSizeOver\":\"10485760\"", sent);
    }

    [Fact]
    public async Task An_edit_that_does_not_read_back_fails_and_names_the_setting()
    {
        var original = Read(Sample);
        var edited = original.Clone();
        edited.Actions.Single(l => l.Def.Parameter == "PrependSubject").Value = "[OUTSIDE] ";
        var changes = new JsonObject(RuleCodec.Changes(original, edited));
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-TransportRule" ? $"[{Sample}]" : "[]");

        var result = await Executor(exchange).ApplyAsync(
            new UpdateRule(new ChangeTarget("rule", "g1", "zz-tenantry-test-x", null), changes.ToJsonString(), "a", "b", false), Ct);

        Assert.Equal(ChangeOutcome.Failed, result.Outcome);
        Assert.Contains("PrependSubject", result.Detail);
        Assert.Contains(exchange.Calls, c => c.Cmdlet == "Set-TransportRule" && c.Parameters.Contains("\"Identity\":\"g1\""));
    }

    [Fact]
    public void Rule_edits_keep_their_first_before_and_survive_the_change_log()
    {
        var target = new ChangeTarget("rule", "g1", "r", null);
        var merged = new UpdateRule(target, "{\"Comments\":\"b\"}", "", "second", false).Since(new UpdateRule(target, "{\"Comments\":\"a\"}", "first", "", false));
        Assert.Equal("first", merged.Before);
        Assert.True(new UpdateRule(target, "{}", "x", "x", false).IsNoChange);

        ChangeOp op = new CreateRule(target, "{\"PrependSubject\":\"[X]\"}", "When …", true);
        var back = JsonSerializer.Deserialize<ChangeOp>(JsonSerializer.Serialize(op));
        Assert.Equal(op, back);
        Assert.True(back!.IsCaution);
    }

    [Fact]
    public void The_summary_reads_as_a_sentence()
    {
        var draft = new RuleDraft();
        var subject = RuleCatalog.Instance.Conditions.Single(c => c.Parameter == "SubjectContainsWords");
        var prepend = RuleCatalog.Instance.Actions.Single(a => a.Parameter == "PrependSubject");
        draft.Conditions.Add(new RuleLine(subject, RulePartKind.Condition) { Value = new JsonArray("a", "b", "c") });
        draft.Actions.Add(new RuleLine(prepend, RulePartKind.Action) { Value = "[X] " });

        var summary = RuleCodec.Summary(draft);

        Assert.StartsWith("When ", summary);
        Assert.Contains("'a', 'b' or 'c'", summary);
        Assert.Contains("'[X] '", summary);
        Assert.Equal("When every message: do nothing.", RuleCodec.Summary(new RuleDraft()));
    }

    [Theory]
    [InlineData("2026-12-01T05:00:00Z")]
    [InlineData("2026-12-01T00:00:00")]
    [InlineData("2026-12-01T00:00:00-05:00")]
    public void A_date_reads_back_as_sent_however_Exchange_writes_it(string readBack)
    {
        using var rule = JsonDocument.Parse($$"""{"ExpiryDate":"{{readBack}}"}""");

        Assert.Empty(RuleCodec.Mismatches(rule.RootElement, [new("ExpiryDate", JsonValue.Create("2026-12-01T00:00:00-05:00"))]));
        Assert.Equal("ExpiryDate", Assert.Single(RuleCodec.Mismatches(rule.RootElement, [new("ExpiryDate", JsonValue.Create("2026-12-09T00:00:00-05:00"))])));
    }

    [Fact]
    public void A_rename_or_a_note_shows_in_the_edit_summaries()
    {
        var original = new RuleDraft { Name = "Old name" };
        var edited = original.Clone();
        edited.Name = "New name";
        edited.Settings["Comments"] = "Asked for by the help desk";

        var (before, after) = RuleCodec.EditSummaries(original, edited);

        Assert.NotEqual(before, after);
        Assert.Contains("Name 'Old name'.", before);
        Assert.Contains("Name 'New name'.", after);
        Assert.Contains("'Asked for by the help desk'", after);
        Assert.Contains("(not set)", before);
    }

    [Fact]
    public void Rule_dates_go_out_as_the_start_of_the_local_day()
    {
        var sent = Tenantry.ViewModels.MailSecurity.RuleValueEditor.SendDate(new DateTime(2026, 12, 1));

        Assert.StartsWith("2026-12-01T00:00:00", sent);
        Assert.Equal(new DateTime(2026, 12, 1), Tenantry.ViewModels.MailSecurity.RuleValueEditor.ReadDate(sent));
        Assert.Equal(new DateTime(2026, 12, 1), Tenantry.ViewModels.MailSecurity.RuleValueEditor.ReadDate("2026-12-01"));
    }
}
