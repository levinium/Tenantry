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
using Tenantry.ViewModels.Intune;

namespace Tenantry.Tests;

/// <summary>Configuration profiles (13b): the settings catalog's definitions, defaults, changes and the editor.</summary>
public sealed class SettingsCatalogTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Definitions_read_their_options_limits_defaults_and_inner_settings()
    {
        var choice = CatalogDefinition.From(Json("""
            {"@odata.type":"#microsoft.graph.deviceManagementConfigurationChoiceSettingDefinition","id":"c","displayName":"Encryption","defaultOptionId":"c_1",
             "options":[{"itemId":"c_0","displayName":"Disabled"},{"itemId":"c_1","displayName":"Enabled","dependedOnBy":[{"dependedOnBy":"c_child","required":true}]}]}
            """));
        Assert.Equal((CatalogKind.Choice, "c_1"), (choice.Kind, choice.DefaultOptionId));
        Assert.Equal(["c_child"], choice.Options[1].Children);
        var number = CatalogDefinition.From(Json("""
            {"@odata.type":"#microsoft.graph.deviceManagementConfigurationSimpleSettingDefinition","id":"n","displayName":"Days",
             "valueDefinition":{"@odata.type":"#microsoft.graph.deviceManagementConfigurationIntegerSettingValueDefinition","minimumValue":0,"maximumValue":90},"defaultValue":{"value":7}}
            """));
        Assert.Equal((CatalogKind.Simple, true, 0L, 90L, "7"), (number.Kind, number.IsInteger, number.Min, number.Max, number.DefaultValue));
        var group = CatalogDefinition.From(Json("""{"@odata.type":"#microsoft.graph.deviceManagementConfigurationSettingGroupCollectionDefinition","id":"g","childIds":["a","b"],"maximumCount":5}"""));
        Assert.Equal((CatalogKind.GroupCollection, 5), (group.Kind, group.MaxCount));
        Assert.Equal(["a", "b"], group.ChildIds);
    }

    [Fact]
    public void A_new_setting_starts_at_its_default_option_with_that_options_settings()
    {
        var child = new CatalogDefinition("c_child", CatalogKind.Simple, "Key length", null, null, [], null, "integer", 128, 256, "256", [], null);
        var parent = new CatalogDefinition("c", CatalogKind.Choice, "Encryption", null, null,
            [new CatalogOption("c_0", "Disabled", null, []), new CatalogOption("c_1", "Enabled", null, ["c_child"])], "c_1", "string", null, null, null, [], null);
        var defs = new Dictionary<string, CatalogDefinition> { ["c"] = parent, ["c_child"] = child };

        var instance = SettingsCatalog.Default(parent, defs);

        Assert.Equal("c_1", (string?)instance["choiceSettingValue"]!["value"]);
        var inner = instance["choiceSettingValue"]!["children"]![0]!;
        Assert.Equal(256, inner["simpleSettingValue"]!["value"]!.GetValue<long>());
        Assert.Contains("Integer", (string?)inner["simpleSettingValue"]!["@odata.type"]);
        Assert.Equal("Enabled (Key length: 256)", SettingsCatalog.Words(instance, defs));
        Assert.Equal("Windows, macOS", SettingsCatalog.PlatformText("windows10,macOS"));
        Assert.Equal("Windows 10 General Configuration", SettingsCatalog.Words("windows10GeneralConfiguration"));
    }

    [Fact]
    public void Pending_Changes_names_the_settings_that_changed_were_added_or_removed()
    {
        var a = new CatalogDefinition("a", CatalogKind.Choice, "Firewall", null, null, [new("a_0", "Off", null, []), new("a_1", "On", null, [])], null, "string", null, null, null, [], null);
        var b = new CatalogDefinition("b", CatalogKind.Simple, "Message", null, null, [], null, "string", null, null, null, [], null);
        var defs = new Dictionary<string, CatalogDefinition> { ["a"] = a, ["b"] = b };
        var old = new JsonArray(SettingsCatalog.Default(a, defs));
        var changed = SettingsCatalog.Default(a, defs);
        changed["choiceSettingValue"]!["value"] = "a_1";
        var @new = new JsonArray(changed, SettingsCatalog.Default(b, defs));

        var (before, after) = SettingsCatalog.Describe(old, @new, defs);

        Assert.Equal("Firewall: Off", before);
        Assert.Equal("Firewall: On; added Message: ", after);
    }

    [Fact]
    public async Task Settings_go_back_whole_with_the_profiles_current_name()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "select=name,description", """{"name":"zz-tenantry-test-renamed","description":"d"}""")
            .On(HttpMethod.Put, "configurationPolicies/p1", "{}");
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);
        var instance = new JsonObject { ["@odata.type"] = "#microsoft.graph.deviceManagementConfigurationSimpleSettingInstance", ["settingDefinitionId"] = "x" };

        var result = await executor.ApplyAsync(new SetCatalogSettings(IntuneChange.TargetFor(SettingsCatalog.Beta + "configurationPolicies/p1", "zz-tenantry-test-p"),
            new JsonObject { ["name"] = "zz-tenantry-test-p", ["platforms"] = "windows10", ["technologies"] = "mdm" }, [], [instance], "0 settings", "x"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var put = http.Requests.Single(r => r.Method == HttpMethod.Put).Json!;
        Assert.Equal("zz-tenantry-test-renamed", (string?)put["name"]);
        Assert.Equal("x", (string?)put["settings"]![0]!["settingInstance"]!["settingDefinitionId"]);
        Assert.Equal("#microsoft.graph.deviceManagementConfigurationSetting", (string?)put["settings"]![0]!["@odata.type"]);
    }

    static async Task<(DemoUserRecords Records, ChangePipeline Pipeline, ConfigurationProfilesViewModel Page)> Page()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var page = new ConfigurationProfilesViewModel(records, pipeline, Availability.Yes, EmptyIndex.Instance, records);
        await page.Refresh();
        return (records, pipeline, page);
    }

    [Fact]
    public async Task A_choice_brings_or_drops_its_inner_settings_and_setting_it_back_drops_the_change()
    {
        var (records, pipeline, page) = await Page();
        var card = page.Cards.Single(c => c.Name == "BitLocker for Windows");
        await card.LoadAsync();
        var method = card.Roots.Single(r => r.Id == "bitlocker_encryptionmethodbydrivetype");
        var inner = Assert.Single(method.Children);

        inner.Option = inner.Options.Single(o => o.Name == "XTS-AES 128-bit");
        await Task.Delay(20, Ct);
        var change = (SetCatalogSettings)Assert.Single(pipeline.Pending);
        Assert.Contains("XTS-AES 128-bit", change.After);
        card.Roots.Single(r => r.Id == "bitlocker_encryptionmethodbydrivetype").Children.Single().Option =
            card.Roots.Single(r => r.Id == "bitlocker_encryptionmethodbydrivetype").Children.Single().Options.Single(o => o.Name == "XTS-AES 256-bit");
        await Task.Delay(20, Ct);
        Assert.Empty(pipeline.Pending);

        method = card.Roots.Single(r => r.Id == "bitlocker_encryptionmethodbydrivetype");
        method.Option = method.Options.Single(o => o.Name == "Disabled");
        await Task.Delay(20, Ct);
        Assert.Empty(card.Roots.Single(r => r.Id == "bitlocker_encryptionmethodbydrivetype").Children);
        await pipeline.ApplyAsync(cancellationToken: Ct);
        var (settings, _) = await records.GetProfileSettingsAsync("dp-bitlocker", Ct);
        Assert.Equal("bitlocker_encryptionmethodbydrivetype_0", (string?)settings.OfType<JsonObject>().Single(s => (string?)s["settingDefinitionId"] == "bitlocker_encryptionmethodbydrivetype")["choiceSettingValue"]!["value"]);
    }

    [Fact]
    public async Task A_setting_is_found_added_with_a_group_item_and_another_item_added_then_one_removed()
    {
        var (_, pipeline, page) = await Page();
        var card = page.Cards.Single(c => c.Name == "zz-tenantry-test-profile");
        await card.LoadAsync();

        card.SearchText = "printers";
        await card.SearchCommand.ExecuteAsync(null);
        await card.Found.Single(f => f.Name == "Printers").AddCommand.ExecuteAsync(null);
        var printers = card.Roots.Single(r => r.Id == "printers_list");
        Assert.Single(printers.Items);
        await printers.AddItemCommand.ExecuteAsync(null);
        Assert.Equal(2, card.Roots.Single(r => r.Id == "printers_list").Items.Count);
        var address = card.Roots.Single(r => r.Id == "printers_list").Items[0].Children.Single(c => c.Id == "printers_list_name");
        address.Text = @"\\print02\hall";
        address.CommitTextCommand.Execute(null);
        Assert.Contains("added Printers: 2 items", ((SetCatalogSettings)Assert.Single(pipeline.Pending)).After);

        card.Roots.Single(r => r.Id == "update_allowautoupdate").RemoveCommand.Execute(null);
        // Taking out the last one is refused: Intune won't keep a profile with no settings.
        card.Roots.Single(r => r.Id == "printers_list").RemoveCommand.Execute(null);
        Assert.Contains("at least one setting", card.Note);
        Assert.Contains(card.Roots, r => r.Id == "printers_list");
        Assert.True(((SetCatalogSettings)Assert.Single(pipeline.Pending)).IsCaution);
        Assert.DoesNotContain(card.Roots, r => r.Id == "update_allowautoupdate");

        var days = page.Cards.Single(c => c.Name == "Defender Antivirus");
        await days.LoadAsync();
        var retain = days.Roots.Single(r => r.Id == "defender_daystoretaincleanedmalware");
        retain.Text = "500";
        retain.CommitTextCommand.Execute(null);
        Assert.NotNull(retain.Problem);
    }

    [Fact]
    public async Task A_new_profile_is_staged_and_the_old_template_profiles_read_as_plain_settings()
    {
        var (_, pipeline, page) = await Page();
        page.NewName = "zz-tenantry-test-new";
        page.NewPlatform = ConfigurationProfilesViewModel.Platforms.Single(p => p.Label == "macOS");
        await page.AddCommand.ExecuteAsync(null);
        Assert.Contains("first setting", page.AddProblem);
        page.NewSearchText = "drive encryption method";
        await page.SearchNewCommand.ExecuteAsync(null);
        await page.NewFound.Single().AddCommand.ExecuteAsync(null);
        await page.AddCommand.ExecuteAsync(null);
        var made = (CreateConfigurationProfile)Assert.Single(pipeline.Pending);
        Assert.Equal(("macOS", "mdm,appleRemoteManagement"), (made.Platforms, made.Technologies));
        Assert.Single(made.Settings);

        var template = Assert.Single(page.Templates);
        var rows = template.Groups.SelectMany(g => g.Rows).ToArray();
        Assert.Contains(rows, r => r.Label == "Lock Screen Timeout In Seconds" && r.IsNumber);
        Assert.Contains(rows, r => r.Def.Parameter == "edgeHomepageUrls" && r.IsList);
    }
}
