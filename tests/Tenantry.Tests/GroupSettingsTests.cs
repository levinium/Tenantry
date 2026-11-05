using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>A group's own settings: name, description, alias, privacy, membership by rule, delete and restore.</summary>
public sealed class GroupSettingsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly UserSummary Ada = new("u1", "Ada Lovelace", "ada@contoso.example", "ada@contoso.example", "Finance", null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, []);

    static readonly GroupSummary Team = new("g1", "Finance Team", "finance@contoso.example", "The finance team", GroupKind.Microsoft365, false, false, "Private", null, [],
        MailNickname: "finance");

    static readonly GroupSummary Board = new("dl1", "Board", "board@contoso.example", null, GroupKind.Distribution, false, false, null, null, []);

    static readonly GroupSummary Printers = new("sg1", "Printer Access", null, null, GroupKind.Security, false, false, null, null, []);

    static readonly GroupSummary Synced = new("sg2", "Old Domain Group", null, null, GroupKind.Security, false, false, null, null, [], SyncedFromOnPremises: true);

    sealed class Index : IDirectoryIndex
    {
        public UserSummary? User(string id) => id == Ada.Id ? Ada : null;
        public GroupSummary? Group(string id) => AllGroups.FirstOrDefault(g => g.Id == id);
        public IEnumerable<UserSummary> AllUsers => [Ada];
        public IEnumerable<GroupSummary> AllGroups => [Team, Board, Printers, Synced];
    }

    sealed class Records : IGroupRecords
    {
        public List<string> Tested { get; } = [];
        public Task<IReadOnlyList<DeletedGroup>> GetDeletedGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeletedGroup>>([]);

        public Task<RuleTest> TestRuleAsync(string rule, string personId, string personName, CancellationToken cancellationToken = default)
        {
            Tested.Add(rule);
            return Task.FromResult(new RuleTest(personId, personName, rule.Contains("Finance", StringComparison.Ordinal), null));
        }
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static ChangePipeline Pipeline() => new(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");

    static GroupSettingsEditor Editor(GroupSummary group, ChangePipeline pipeline, Records? records = null) =>
        new(group, new Index(), pipeline, Availability.Yes, records ?? new Records());

    static void Set(GroupSettingsEditor editor, string label, string value)
    {
        var field = editor.Details.SelectMany(g => g.Fields).Single(f => f.Label == label);
        field.EditCommand.Execute(null);
        field.Draft = value;
        field.StageCommand.Execute(null);
    }

    [Fact]
    public void Each_kind_offers_its_own_settings()
    {
        string[] Labels(GroupSummary g) => [.. Editor(g, Pipeline()).Details.SelectMany(d => d.Fields).Select(f => f.Label)];

        Assert.Equal(["Name", "Description", "Alias", "Privacy"], Labels(Team));
        Assert.Equal(["Name", "Description", "Alias"], Labels(Board));
        Assert.Equal(["Name", "Description"], Labels(Printers));
        Assert.True(Editor(Team, Pipeline()).HasMembership);
        Assert.False(Editor(Board, Pipeline()).HasMembership);
    }

    [Fact]
    public void Settings_stage_with_their_limits()
    {
        var pipeline = Pipeline();
        var team = Editor(Team, pipeline);

        Set(team, "Name", new string('x', 65));
        Assert.Empty(pipeline.Pending);
        Set(team, "Name", "Finance and Payroll");
        Set(team, "Alias", "has space");
        Set(team, "Alias", "finance-payroll");
        Set(team, "Privacy", "Public");

        var settings = pipeline.Pending.OfType<SetGroupSetting>().ToDictionary(s => s.Setting);
        Assert.Equal(["displayName", "mailNickname", "visibility"], settings.Keys.Order());
        Assert.Equal(("Finance Team", "Finance and Payroll"), (settings["displayName"].Old, settings["displayName"].New));
        // A new alias and going public are caution changes.
        Assert.True(settings["mailNickname"].IsCaution && settings["visibility"].IsCaution);
        Assert.False(settings["displayName"].IsCaution);
    }

    [Fact]
    public void A_group_synced_from_AD_is_locked()
    {
        var editor = Editor(Synced, Pipeline());

        Assert.False(editor.CanEdit);
        Assert.All(editor.Details.SelectMany(d => d.Fields), f => Assert.False(f.CanEdit));
        editor.DeleteCommand.Execute(null);
        Assert.False(editor.HasPendingDelete);
    }

    [Fact]
    public async Task A_rule_is_tested_on_people_then_staged_and_a_typed_draft_survives_other_changes()
    {
        var pipeline = Pipeline();
        var records = new Records();
        var editor = Editor(Printers, pipeline, records);

        editor.IsDynamicDraft = true;
        Assert.False(editor.StageRuleCommand.CanExecute(null));
        editor.RuleDraft = "(user.department -eq \"Finance\"";
        Assert.Equal("A bracket isn't closed.", editor.RuleProblem);
        editor.RuleDraft = "(user.department -eq \"Finance\")";
        Assert.Contains("removed", editor.ConvertNote, StringComparison.Ordinal);

        editor.TestPicker.Query = "ada";
        editor.TestPicker.PickCommand.Execute(editor.TestPicker.Results.Single());
        await Task.Yield();
        Assert.Equal(("Ada Lovelace", "Would be a member"), (editor.TestRows.Single().Name, editor.TestRows.Single().Result));

        // Something else staged meanwhile leaves the typed rule alone.
        pipeline.Stage(new SetGroupSetting(GroupAccess.Target(Board), GroupKind.Distribution, SetGroupSetting.Description, "Description", null, "x"));
        Assert.True(editor.IsDynamicDraft);

        editor.StageRuleCommand.Execute(null);
        var rule = Assert.Single(pipeline.Pending.OfType<SetMembershipRule>());
        Assert.True(rule.IsCaution);
        Assert.Equal((false, true, "(user.department -eq \"Finance\")"), (rule.OldDynamic, rule.NewDynamic, rule.NewRule));
        Assert.True(editor.HasPendingRule);
    }

    [Fact]
    public void Membership_keeps_a_Microsoft_365_groups_type_and_turning_a_rule_off_pauses_it()
    {
        var target = GroupAccess.Target(Team);
        var toDynamic = GraphChangeExecutor.MembershipBody(new SetMembershipRule(target, GroupKind.Microsoft365, false, null, false, true, "(user.department -eq \"X\")", false));
        var toAssigned = GraphChangeExecutor.MembershipBody(new SetMembershipRule(target, GroupKind.Microsoft365, true, "(r)", false, false, "(r)", false));

        Assert.Equal(["Unified", "DynamicMembership"], toDynamic["groupTypes"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(("(user.department -eq \"X\")", "On"),
            (toDynamic["membershipRule"]!.GetValue<string>(), toDynamic["membershipRuleProcessingState"]!.GetValue<string>()));
        Assert.Equal("""{"groupTypes":["Unified"],"membershipRuleProcessingState":"Paused"}""", toAssigned.ToJsonString());
    }

    [Fact]
    public async Task Lists_change_through_Exchange_and_Microsoft_365_groups_through_Graph()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-DistributionGroup" ? """[{"Description":["Board members"]}]""" : "[]");
        var http = new FakeHttp()
            .On(HttpMethod.Patch, "/groups/g1", "{}")
            .On(HttpMethod.Get, "/groups/g1?$select=displayName", """{"displayName":"Finance and Payroll"}""");
        var store = new MemoryEntityStore();
        store.Apply(DirectorySync.Groups, [new EntityChange("dl1", new JsonObject { ["id"] = "dl1", ["description"] = null })]);
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store, NoDelay.Instance, exchange);

        var list = await executor.ApplyAsync(new SetGroupSetting(GroupAccess.Target(Board), GroupKind.Distribution, SetGroupSetting.Description, "Description", null, "Board members"), Ct);
        var team = await executor.ApplyAsync(new SetGroupSetting(GroupAccess.Target(Team), GroupKind.Microsoft365, SetGroupSetting.DisplayName, "Name", "Finance Team", "Finance and Payroll"), Ct);

        Assert.Equal((ChangeOutcome.Applied, ChangeOutcome.Applied), (list.Outcome, team.Outcome));
        var set = exchange.Calls.Single(c => c.Cmdlet == "Set-DistributionGroup");
        Assert.Contains("\"Description\":[\"Board members\"]", set.Parameters, StringComparison.Ordinal);
        Assert.Contains("\"Identity\":\"board@contoso.example\"", set.Parameters, StringComparison.Ordinal);
        Assert.Equal("""{"displayName":"Finance and Payroll"}""", http.Requests.Single(r => r.Method == HttpMethod.Patch).Body);
        // The local copy follows at once.
        Assert.Contains("Board members", store.Get(DirectorySync.Groups, "dl1"), StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_asks_for_the_name_and_says_whether_it_can_come_back()
    {
        var pipeline = Pipeline();
        Editor(Board, pipeline).DeleteCommand.Execute(null);
        Editor(Printers, pipeline).DeleteCommand.Execute(null);

        var deletes = pipeline.Pending.OfType<DeleteGroup>().ToDictionary(d => d.Group.Id);
        Assert.Equal(("Board", "Deleted for good (it can't be restored)"), (deletes["dl1"].ConfirmPhrase, deletes["dl1"].After));
        Assert.Equal("Deleted (restorable for 30 days)", deletes["sg1"].After);
        Assert.Equal("Remove-DistributionGroup", GraphChangeExecutor.ScriptFor(deletes["dl1"])!.Text.Split(' ')[0]);
    }

    [Fact]
    public async Task The_demo_deletes_a_group_then_restores_it_from_Deleted_Groups()
    {
        var directory = new Tenantry.Demo.DemoTenantDirectory();
        var records = new Tenantry.Demo.DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new Tenantry.Demo.DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var group = (await directory.GetGroupsAsync(Ct)).First(g => g.Kind == GroupKind.Security && !g.IsDynamic);

        pipeline.Stage(new DeleteGroup(GroupAccess.Target(group), group.Kind, ""));
        await pipeline.ApplyAsync(cancellationToken: Ct);
        Assert.DoesNotContain(await directory.GetGroupsAsync(Ct), g => g.Id == group.Id);

        using var deleted = new DeletedGroupsViewModel(records, pipeline, () => DateTimeOffset.UtcNow);
        await deleted.LoadAsync();
        var row = deleted.Rows.Single(r => r.Group.Id == group.Id);
        row.RestoreCommand.Execute(null);
        Assert.True(row.IsPending);
        await pipeline.ApplyAsync(cancellationToken: Ct);
        Assert.Contains(await directory.GetGroupsAsync(Ct), g => g.Id == group.Id);
    }
}
