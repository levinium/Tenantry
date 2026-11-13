using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Purview;

namespace Tenantry.Tests;

/// <summary>Purview protection (14a-14f): labels, auto-labeling, DLP, information types, insider risk, barriers, the file plan.</summary>
public sealed class PurviewProtectionTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Every_kind_is_whole_and_every_page_is_in_a_section()
    {
        Assert.Equal(PurviewKinds.All.Count, PurviewKinds.All.Select(k => k.Key).Distinct().Count());
        foreach (var kind in PurviewKinds.All)
        {
            if (kind.RulesOf is { } rules)
                Assert.True(PurviewKinds.Get(rules).IsRule);
            foreach (var f in kind.Fields ?? [])
            {
                Assert.Equal(f.Add is null, f.Remove is null);
                if (f.CanChange && f.Add is null)
                    Assert.NotNull(kind.Set);
            }
            foreach (var option in kind.Creates ?? [])
            {
                Assert.NotNull(kind.New);
                Assert.Contains(option.Fields, f => f.Path == "Name");
            }
        }
        var pages = SecurityAreaViewModel.Sections.SelectMany(s => s.Pages).Select(p => p.Key).ToHashSet();
        Assert.All(PurviewKinds.Pages.Keys, key => Assert.Contains(key, pages));
        // Simulation first: new DLP and auto-labeling policies never start on.
        Assert.Equal("TestWithoutNotifications", (string?)PurviewKinds.DlpPolicies.Creates![0].Fixed["Mode"]);
        Assert.Equal("TestWithoutNotifications", (string?)PurviewKinds.AutoLabelPolicies.Creates![0].Fixed["Mode"]);
    }

    [Fact]
    public void Sensitive_types_read_from_the_key_value_pairs_Purview_answers_over_REST()
    {
        // As Get-DlpComplianceRule answered live: a list of tables, each a list of _key/_value pairs.
        var raw = J("""[[{"_key":"id","_value":"50842eb7"},{"_key":"name","_value":"Credit Card Number"},{"_key":"mincount","_value":"2"},{"_key":"maxcount","_value":"-1"}],[{"_key":"name","_value":"U.S. Social Security Number (SSN)"},{"_key":"mincount","_value":"1"}]]""");
        var found = SensitiveTypeMatch.From(raw);
        Assert.Equal(["Credit Card Number", "U.S. Social Security Number (SSN)"], found.Select(f => f.Name));
        Assert.Equal(2, found[0].MinCount);
    }

    [Fact]
    public void Sensitive_types_read_from_every_shape_Purview_gives_and_lines_parse_with_counts()
    {
        var flat = SensitiveTypeMatch.From(J("""[{"name":"Credit Card Number","mincount":"2"},{"Name":"U.S. Bank Account Number","minCount":1}]"""));
        Assert.Equal(["Credit Card Number (at least 2)", "U.S. Bank Account Number"], flat.Select(m => m.Text));
        var grouped = SensitiveTypeMatch.From(J("""{"groups":[{"name":"Default","sensitivetypes":[{"name":"ABA Routing Number","mincount":"1"}]}]}"""));
        Assert.Equal("ABA Routing Number", Assert.Single(grouped).Name);
        var asText = SensitiveTypeMatch.From(J("\"[{\\\"name\\\":\\\"Passport\\\",\\\"mincount\\\":\\\"3\\\"}]\""));
        Assert.Equal(3, Assert.Single(asText).MinCount);

        var parsed = SensitiveTypeMatch.Parse("Credit Card Number\nU.S. Bank Account Number, 5\ncredit card number");
        Assert.Equal(2, parsed.Count);
        Assert.Equal(5, parsed[1].MinCount);
        var parameter = SensitiveTypeMatch.ToParameter(parsed);
        Assert.Equal("5", (string?)parameter[1]!["minCount"]);
    }

    [Fact]
    public void A_labels_markings_and_encryption_come_out_of_its_label_actions()
    {
        var label = J("""{"LabelActions":["{\"Type\":\"encrypt\",\"Settings\":[{\"Key\":\"protectiontype\",\"Value\":\"Template\"},{\"Key\":\"disabled\",\"Value\":\"false\"}]}","{\"Type\":\"applycontentmarking\",\"SubType\":\"header\",\"Settings\":[{\"Key\":\"text\",\"Value\":\"SECRET\"}]}","{\"Type\":\"protectgroup\",\"Settings\":[{\"Key\":\"privacy\",\"Value\":\"Private\"},{\"Key\":\"allowaccesstoguestusers\",\"Value\":\"false\"}]}"]}""");
        var lifted = PurviewKinds.LiftLabelActions(label);
        Assert.True((bool)lifted["EncryptionEnabled"]!);
        Assert.Equal("Template", (string?)lifted["EncryptionProtectionType"]);
        Assert.Equal("SECRET", (string?)lifted["ApplyContentMarkingHeaderText"]);
        Assert.False((bool)lifted["ApplyContentMarkingFooterEnabled"]!);
        Assert.Equal("Private", (string?)lifted["SiteAndGroupProtectionPrivacy"]);
        Assert.False((bool)lifted["SiteAndGroupProtectionAllowAccessToGuestUsers"]!);
    }

    [Fact]
    public void Test_mode_judges_the_item_and_every_place_person_or_segment_it_names()
    {
        var test = PurviewChange.TargetFor("dlp", "zz-tenantry-test-dlp");
        var real = PurviewChange.TargetFor("dlp", "U.S. Financial Data");
        Assert.NotNull(WriteGuard.Refusal(new SetPurviewSetting(real, "DLP Policy", "Set-DlpCompliancePolicy", "Mode", "Mode", "Enable", "Simulation", "On"), testMode: true));
        Assert.Null(WriteGuard.Refusal(new SetPurviewSetting(test, "DLP Policy", "Set-DlpCompliancePolicy", "Mode", "Mode", "Enable", "Simulation", "On"), testMode: true));

        var everyone = new SetPurviewList(test, "DLP Policy", "Set-DlpCompliancePolicy", "Teams", "AddTeamsLocation", "RemoveTeamsLocation", [], ["All"], Guard: true);
        Assert.NotNull(WriteGuard.Refusal(everyone, testMode: true));
        Assert.True(everyone.IsCaution);
        var testPerson = everyone with { New = ["zz-tenantry-test-ada@contoso.example"] };
        Assert.Null(WriteGuard.Refusal(testPerson, testMode: true));
        Assert.False(testPerson.IsCaution);

        var made = new CreatePurviewObject(PurviewChange.TargetFor("barriers", "zz-tenantry-test-ib"), "Information Barrier Policy", "New-InformationBarrierPolicy",
            new JsonObject { ["Name"] = "zz-tenantry-test-ib" }, "x", ["zz-tenantry-test-seg", "Sales"]);
        Assert.NotNull(WriteGuard.Refusal(made, testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new RunPurviewAction(RunPurviewAction.Organization("Start Applying"), "Start Applying", "Start-InformationBarrierPoliciesApplication", "x"), testMode: true));
    }

    [Fact]
    public async Task Changes_go_to_Purviews_cmdlets_with_places_added_and_taken_away_separately()
    {
        var exchange = new FakeCmdlets();
        var executor = new GraphChangeExecutor(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);
        var item = PurviewChange.TargetFor("dlp", "zz-tenantry-test-dlp");

        await executor.ApplyAsync(new SetPurviewList(item, "DLP Policy", "Set-DlpCompliancePolicy", "Teams", "AddTeamsLocation", "RemoveTeamsLocation",
            ["zz-tenantry-test-ada@x.example"], ["zz-tenantry-test-bo@x.example"], Guard: true), Ct);
        var (cmdlet, sent) = exchange.Calls[^1];
        Assert.Equal("Set-DlpCompliancePolicy", cmdlet);
        Assert.Contains("\"AddTeamsLocation\":[\"zz-tenantry-test-bo@x.example\"]", sent, StringComparison.Ordinal);
        Assert.Contains("\"RemoveTeamsLocation\":[\"zz-tenantry-test-ada@x.example\"]", sent, StringComparison.Ordinal);

        var types = SensitiveTypeMatch.ToParameter([new("Credit Card Number", 2)]);
        await executor.ApplyAsync(new SetPurviewSetting(PurviewChange.TargetFor("dlprules", "zz-tenantry-test-rule"), "Rule", "Set-DlpComplianceRule",
            "ContentContainsSensitiveInformation", "Sensitive Information", types, "Nothing", "Credit Card Number (at least 2)"), Ct);
        Assert.Contains("\"ContentContainsSensitiveInformation\":[{\"Name\":\"Credit Card Number\",\"minCount\":\"2\"}]", exchange.Calls[^1].Parameters, StringComparison.Ordinal);

        await executor.ApplyAsync(new CreatePurviewObject(PurviewChange.TargetFor("keywords", "zz-tenantry-test-kw"), "Keyword Dictionary", "New-DlpKeywordDictionary",
            new JsonObject { ["Name"] = "zz-tenantry-test-kw", ["FileData"] = new JsonArray("alpha", "beta") }, "x", []), Ct);
        // UTF-8 text, one word per line, as bytes (UTF-16 kept only the first letter over REST, seen live): "al" is 97, 108.
        Assert.StartsWith("{\"Name\":\"zz-tenantry-test-kw\",\"FileData\":[97,108,112,104,97,13,10,98", exchange.Calls[^1].Parameters, StringComparison.Ordinal);

        await executor.ApplyAsync(new SetPurviewList(PurviewChange.TargetFor("purviewroles", "zz-tenantry-test-rg"), "Role Group", "", "Members", null, null,
            ["a@x.example"], ["b@x.example"], Guard: true, MemberCmdlets: PurviewKinds.RoleGroups.Members), Ct);
        Assert.Equal(["Add-RoleGroupMember", "Remove-RoleGroupMember"], exchange.Calls.TakeLast(2).Select(c => c.Cmdlet));
    }

    [Fact]
    public void The_PowerShell_shown_is_what_is_sent_with_keyword_bytes_shown_as_words()
    {
        var script = GraphChangeExecutor.ScriptFor(new CreatePurviewObject(PurviewChange.TargetFor("keywords", "zz-tenantry-test-kw"), "Keyword Dictionary", "New-DlpKeywordDictionary",
            new JsonObject { ["Name"] = "zz-tenantry-test-kw", ["FileData"] = new JsonArray("alpha", "beta") }, "x", []))!;
        Assert.Contains("New-DlpKeywordDictionary", script.Text, StringComparison.Ordinal);
        Assert.Contains("[System.Text.Encoding]::UTF8.GetBytes((@('alpha', 'beta') -join \"`r`n\"))", script.Text, StringComparison.Ordinal);

        var mode = GraphChangeExecutor.ScriptFor(new SetPurviewSetting(PurviewChange.TargetFor("dlp", "zz-tenantry-test-dlp"), "DLP Policy", "Set-DlpCompliancePolicy", "Mode", "Mode",
            JsonValue.Create("Enable"), "Simulation", "On", true))!;
        Assert.Equal("Set-DlpCompliancePolicy -Identity 'zz-tenantry-test-dlp' -Mode 'Enable'", mode.Text.Trim());
    }

    static async Task<(ChangePipeline Pipeline, PurviewObjectsViewModel Page)> Page(PurviewKind kind)
    {
        var directory = new DemoTenantDirectory(userCount: 20);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var page = new PurviewObjectsViewModel(kind, records, pipeline, Availability.Yes);
        await page.Refresh();
        return (pipeline, page);
    }

    [Fact]
    public async Task A_DLP_policy_shows_its_rules_and_turning_it_on_asks_to_confirm()
    {
        var (pipeline, page) = await Page(PurviewKinds.DlpPolicies);
        var policy = page.Cards.Single(c => c.Name == "Health Records");
        Assert.Equal("Simulation", policy.ModeBadge);
        var rule = Assert.Single(policy.Rules);
        var types = rule.Groups.SelectMany(g => g.Rows).Single(r => r.Def.Key == "ContentContainsSensitiveInformation");
        Assert.Contains("International Classification of Diseases", types.TextValue, StringComparison.Ordinal);

        var mode = policy.Groups.SelectMany(g => g.Rows).Single(r => r.Def.Key == "Mode");
        mode.Choice = mode.Choices.Single(c => c.Value == "Enable");
        var staged = Assert.IsType<SetPurviewSetting>(Assert.Single(pipeline.Pending));
        Assert.True(staged.IsCaution);
        Assert.Equal("Simulation with Policy Tips", staged.Before);

        await pipeline.ApplyAsync(cancellationToken: Ct);
        await page.Refresh();
        Assert.Equal("On", page.Cards.Single(c => c.Name == "Health Records").ModeBadge);
    }

    [Fact]
    public async Task New_rule_checks_the_type_names_and_is_made_in_its_policy()
    {
        var (pipeline, page) = await Page(PurviewKinds.DlpPolicies);
        var policy = page.Cards.Single(c => c.Name == "Health Records");
        var form = policy.RuleCreate!;
        await form.ToggleCommand.ExecuteAsync(null);
        form.Fields.Single(f => f.Field.Path == "Name").Text = "zz-tenantry-test-rule";
        form.Fields.Single(f => f.Field.Path == "ContentContainsSensitiveInformation").Text = "Not A Real Type";
        form.AddCommand.Execute(null);
        Assert.Contains("isn't a sensitive information type", form.Problem, StringComparison.Ordinal);

        form.Fields.Single(f => f.Field.Path == "ContentContainsSensitiveInformation").Text = "Credit Card Number";
        form.AddCommand.Execute(null);
        var made = Assert.IsType<CreatePurviewObject>(Assert.Single(pipeline.Pending));
        Assert.Equal("Health Records", (string?)made.Parameters["Policy"]);
        Assert.Equal("Credit Card Number", (string?)made.Parameters["ContentContainsSensitiveInformation"]![0]!["Name"]);
        Assert.False((bool)made.Parameters["BlockAccess"]!);

        await pipeline.ApplyAsync(cancellationToken: Ct);
        await page.Refresh();
        Assert.Equal(2, page.Cards.Single(c => c.Name == "Health Records").Rules.Count);
    }

    [Fact]
    public async Task Places_change_piece_by_piece_and_Microsofts_types_cant_be_deleted()
    {
        var (pipeline, page) = await Page(PurviewKinds.LabelPolicies);
        var people = page.Cards[0].Groups.SelectMany(g => g.Rows).Single(r => r.Def.Key == "ExchangeLocation");
        people.ListText = "All\nzz-tenantry-test-ada@x.example";
        people.CommitListCommand.Execute(null);
        var list = Assert.IsType<SetPurviewList>(Assert.Single(pipeline.Pending));
        Assert.Equal(["zz-tenantry-test-ada@x.example"], list.Added);
        Assert.Empty(list.Removed);

        var (_, types) = await Page(PurviewKinds.InfoTypes);
        Assert.False(types.Cards.Single(c => c.Name == "Credit Card Number").CanDelete);
        Assert.True(types.Cards.Single(c => c.Name == "Project Code Names").CanDelete);
    }

    [Fact]
    public async Task Labels_show_their_markings_and_a_sublabel_names_its_parent()
    {
        var (_, page) = await Page(PurviewKinds.Labels);
        var confidential = page.Cards.Single(c => c.Name == "Confidential");
        var rows = confidential.Groups.SelectMany(g => g.Rows).ToDictionary(r => r.Def.Key);
        Assert.True(rows["EncryptionEnabled"].IsOn);
        Assert.Equal("CONFIDENTIAL", rows["ApplyContentMarkingHeaderText"].TextValue);
        Assert.Contains(page.Cards.Single(c => c.Name == "Confidential-Finance").Details, d => d.Label == "Inside Label" && d.Value == "Confidential");
    }
}
