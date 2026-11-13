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
using Tenantry.ViewModels.MailSecurity;
using Tenantry.ViewModels.Organization;

namespace Tenantry.Tests;

/// <summary>The Organization area's settings, identity, Teams and the rest (15b-15f).</summary>
public sealed class OrganizationSettingsTests
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static async Task<(ChangePipeline Pipeline, OrgObjectsViewModel Page, DemoUserRecords Records)> Page(OrgKind kind)
    {
        var directory = new DemoTenantDirectory(userCount: 10);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var page = new OrgObjectsViewModel(kind, records, pipeline, Availability.Yes);
        await page.Refresh();
        return (pipeline, page, records);
    }

    static SettingRow Row(OrgObjectCard card, string path) => card.Groups.SelectMany(g => g.Rows).Single(r => r.Def.Key == path);

    [Fact]
    public void Every_kind_is_whole_and_every_page_is_in_a_section()
    {
        Assert.Equal(OrgKinds.All.Count, OrgKinds.All.Select(k => k.Key).Distinct().Count());
        var sectionPages = OrganizationAreaViewModel.Sections.SelectMany(s => s.Pages).Select(p => p.Key).ToHashSet();
        Assert.All(OrgKinds.Pages.Keys, key => Assert.Contains(key, sectionPages));
        var shown = OrgKinds.Pages.Values.SelectMany(k => k).ToHashSet();
        foreach (var kind in OrgKinds.All)
        {
            if (kind.Children is { } child)
                Assert.Contains(child, OrgKinds.All.Select(k => k.Key));
            else if (!OrgKinds.All.Any(k => k.Children == kind.Key))
                Assert.Contains(kind, shown);
            foreach (var option in kind.Creates ?? [])
                Assert.True(option.NameField.Length == 0 || option.Fields.Any(f => f.Path == option.NameField), $"{kind.Key}: New names it from a box it has");
            Assert.Equal(kind.Fields?.Count ?? 0, kind.Fields?.Select(f => f.Path).Distinct().Count() ?? 0);
        }
        Assert.Equal(OrganizationAreaViewModel.Pages.Count, sectionPages.Count);
    }

    [Fact]
    public void Paths_read_and_write_nested_objects_name_value_pairs_and_list_items()
    {
        var raw = J("""{"a":{"b":true},"values":[{"name":"X","value":"1"},{"name":"Y","value":"2"}],"definition":["old"]}""");
        Assert.True(OrgPath.Get(raw, "a.b").GetBoolean());
        Assert.Equal("2", OrgPath.Get(raw, "values[name=Y].value").GetString());
        Assert.Equal("old", OrgPath.Get(raw, "definition.0").GetString());
        Assert.Equal(JsonValueKind.Undefined, OrgPath.Get(raw, "values[name=Z].value").ValueKind);

        var o = JsonNode.Parse(raw.GetRawText())!.AsObject();
        OrgPath.Set(o, "values[name=Y].value", "3");
        OrgPath.Set(o, "values[name=Z].value", "new");
        OrgPath.Set(o, "c.d.e", 5);
        OrgPath.Set(o, "definition.0", "fresh");
        Assert.Equal("""{"a":{"b":true},"values":[{"name":"X","value":"1"},{"name":"Y","value":"3"},{"name":"Z","value":"new"}],"definition":["fresh"],"c":{"d":{"e":5}}}""", o.ToJsonString());
        Assert.Equal("values", OrgPath.Top("values[name=Y].value"));
    }

    [Fact]
    public void Each_write_sends_what_its_service_needs_from_the_object_as_it_is_now()
    {
        var target = OrgChange.TargetFor("k", "1", "x");
        var now = J("""{"@odata.context":"c","id":"p","version":2,"Key":{"ScopeClass":"Global"},"isEnabled":false,"defaultUserRolePermissions":{"allowedToCreateApps":false,"allowedToReadOtherUsers":true},"values":[{"name":"A","value":"true"},{"name":"B","value":"x"}]}""");
        SetOrgSetting S(OrgWrite write, string path, JsonNode? value) => new(target, OrgApi.Graph, "u", write, "Settings", path, "L", value, "a", "b");

        // PATCH: the whole complex property, so Graph doesn't clear its other members.
        Assert.Equal("""{"defaultUserRolePermissions":{"allowedToCreateApps":true,"allowedToReadOtherUsers":true}}""",
            GraphChangeExecutor.SettingBody(S(OrgWrite.Patch, "defaultUserRolePermissions.allowedToCreateApps", true), now).ToJsonString());
        Assert.Equal("""{"values":[{"name":"A","value":"false"},{"name":"B","value":"x"}]}""",
            GraphChangeExecutor.SettingBody(S(OrgWrite.Patch, "values[name=A].value", "false"), now).ToJsonString());
        // PUT: the whole object, without Graph's housekeeping.
        var put = GraphChangeExecutor.SettingBody(S(OrgWrite.Put, "isEnabled", true), now);
        Assert.True((bool)put["isEnabled"]!);
        Assert.Null(put["@odata.context"]);
        Assert.Null(put["id"]);
        Assert.Null(put["version"]);
        // Teams: only the change, with the object's Key.
        Assert.Equal("""{"Key":{"ScopeClass":"Global"},"isEnabled":true}""", GraphChangeExecutor.SettingBody(S(OrgWrite.MergePut, "isEnabled", true), now).ToJsonString());
    }

    [Fact]
    public async Task User_consent_and_group_switches_are_sent_in_Microsofts_own_shapes()
    {
        var (pipeline, page, _) = await Page(OrgKinds.UserSettings);
        var card = Assert.Single(page.Cards);
        Assert.True(card.IsExpanded);
        var consent = Row(card, "defaultUserRolePermissions.permissionGrantPoliciesAssigned");
        consent.Choice = consent.Choices.Single(c => c.Value == "");
        var staged = Assert.IsType<SetOrgSetting>(Assert.Single(pipeline.Pending));
        Assert.Equal("[]", staged.Value!.ToJsonString());
        Assert.True(staged.IsCaution);

        var (groupPipeline, groups, _) = await Page(OrgKinds.GroupSettings);
        var creation = Row(Assert.Single(groups.Cards), "values[name=EnableGroupCreation].value");
        Assert.True(creation.IsOn);
        creation.IsOn = false;
        Assert.Equal("\"false\"", Assert.IsType<SetOrgSetting>(Assert.Single(groupPipeline.Pending)).Value!.ToJsonString());
        Assert.False(groups.CanCreate);
    }

    [Fact]
    public void Test_mode_refuses_organization_wide_settings_and_allows_test_items()
    {
        var real = new SetOrgSetting(OrgChange.TargetFor("usersettings", "", "User Settings"), OrgApi.Graph, "policies/authorizationPolicy", OrgWrite.Patch, "Settings", "allowedToUseSSPR", "Reset", false, "On", "Off");
        Assert.NotNull(WriteGuard.Refusal(real, testMode: true));
        var test = real with { Item = OrgChange.TargetFor("teams", "t", "zz-tenantry-test-team") };
        Assert.Null(WriteGuard.Refusal(test, testMode: true));
    }

    [Fact]
    public async Task A_test_teams_setting_applies_and_reads_back_and_its_channels_open_under_it()
    {
        var (pipeline, page, _) = await Page(OrgKinds.Teams);
        var team = page.Cards.Single(c => c.Name == "zz-tenantry-test-team");
        var gifs = Row(team, "funSettings.allowGiphy");
        gifs.IsOn = true;
        await pipeline.ApplyAsync(cancellationToken: TestContext.Current.CancellationToken);
        await page.Refresh();
        Assert.True(Row(page.Cards.Single(c => c.Name == "zz-tenantry-test-team"), "funSettings.allowGiphy").IsOn);

        var ops = page.Cards.Single(c => c.Name == "Operations");
        ops.IsExpanded = true;
        for (var i = 0; i < 20 && ops.Children?.IsLoaded != true; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(["General", "Planning"], ops.Children!.Cards.Select(c => c.Name));
        Assert.False(ops.Children.Cards[0].CanDelete);
        Assert.Contains(ops.Actions, a => a.Action.Key == "archive");
    }

    [Fact]
    public async Task Domains_show_their_records_offer_Verify_and_keep_the_default()
    {
        var (pipeline, page, _) = await Page(OrgKinds.Domains);
        var unverified = page.Cards.Single(c => c.Name == "zz-tenantry-test.example");
        unverified.IsExpanded = true;
        for (var i = 0; i < 20 && !unverified.HasExtras; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Contains(unverified.Extras, e => e.Title == "Records to Verify It" && e.Lines.Count > 0);
        unverified.Actions.Single(a => a.Action.Key == "verify").RunCommand.Execute(null);
        var verify = Assert.IsType<RunOrgAction>(Assert.Single(pipeline.Pending));
        Assert.Equal("domains/zz-tenantry-test.example/verify", verify.Url);
        Assert.False(page.Cards.Single(c => c.Name == "northgate.example").CanDelete);
        Assert.Empty(page.Cards.Single(c => c.Name == "northgate.example").Actions);
    }

    [Fact]
    public async Task Teams_policies_are_read_from_what_they_have_and_new_ones_are_put_by_name()
    {
        var (pipeline, page, _) = await Page(OrgKinds.MeetingPolicies);
        Assert.Contains(page.Cards, c => c.Name == "Global (Everyone Else)" && !c.CanDelete);
        Assert.True(page.Cards.Single(c => c.Name == "AllOn").IsReadOnly);
        var frontDesk = page.Cards.Single(c => c.Name == "Front Desk");
        Assert.Contains(frontDesk.Groups.SelectMany(g => g.Rows), r => r.Def.Key == "AllowCloudRecording");
        Assert.True(frontDesk.CanDelete);

        page.Create!.ToggleCommand.Execute(null);
        page.Create.Fields.Single().Text = "zz-tenantry-test-meetings";
        page.Create.AddCommand.Execute(null);
        var made = Assert.IsType<CreateOrgObject>(Assert.Single(pipeline.Pending));
        // POSTed to the collection with its Identity; Teams keeps it as Tag:name (a PUT to a new name answers 404, seen live).
        Assert.False(made.Put);
        Assert.Equal("Skype.Policy/configurations/TeamsMeetingPolicy", made.Url);
        Assert.Equal("zz-tenantry-test-meetings", (string?)made.Body["Identity"]);
        Assert.Equal("New Meeting Policy \"zz-tenantry-test-meetings\"", made.Label);
        Assert.Contains("$teamsToken", GraphChangeExecutor.OrgScript(made)!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Licenses_say_how_many_are_free_and_reports_become_lists()
    {
        var (_, page, records) = await Page(OrgKinds.Licenses);
        Assert.Contains(page.Cards, c => c.Subtitle == "46 of 50 given out · 4 free");
        Assert.All(page.Cards, c => Assert.True(c.IsReadOnly));

        var usage = new UsageReportsViewModel(records, null, () => DateTimeOffset.UtcNow);
        await usage.Load();
        Assert.NotNull(usage.List);
        Assert.Equal(5, usage.List!.Rows.Count);
        Assert.Equal("displayName", usage.List.Columns[0].Key);
        Assert.DoesNotContain(usage.List.Columns, c => c.Key == "reportRefreshDate");
    }
}
