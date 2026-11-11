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
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.Tests;

/// <summary>The generic Intune pages (13c-13f): apps, app protection, updates, scripts, enrollment, tenant administration.</summary>
public sealed class IntuneObjectTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_new_objects_boxes_fill_nested_paths_and_arrays()
    {
        var body = new JsonObject();
        IntuneKinds.Set(body, "installExperience.runAsAccount", "system");
        IntuneKinds.Set(body, "rolePermissions.0.resourceActions.0.allowedResourceActions", new JsonArray("a", "b"));
        Assert.Equal("system", (string?)body["installExperience"]!["runAsAccount"]);
        Assert.Equal("b", (string?)body["rolePermissions"]![0]!["resourceActions"]![0]!["allowedResourceActions"]![1]);

        var script = new CreateFieldRow(new CreateField("scriptContent", "Script", SettingKind.Script)) { Text = "Write-Output 'hi'" };
        Assert.Equal("Write-Output 'hi'", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(script.Read().Value!.GetValue<string>())));
        Assert.NotNull(new CreateFieldRow(new CreateField("displayName", "Name")).Read().Problem);
        Assert.Null(new CreateFieldRow(new CreateField("x", "X", Required: false)).Read().Value);
    }

    [Fact]
    public void Every_kind_names_its_collection_and_every_New_body_has_a_type_or_needs_none()
    {
        foreach (var kind in IntuneKinds.All)
        {
            Assert.DoesNotContain("?", kind.Collection, StringComparison.Ordinal);
            Assert.Equal($"{kind.Collection}/x1", kind.ItemUrl("x1"));
            foreach (var option in kind.Creates ?? [])
                Assert.Contains(option.Fields, f => f.Path == kind.NameProperty || f.Path == "displayName");
        }
        Assert.True(IntuneKinds.IsUpdateType("#microsoft.graph.windowsUpdateForBusinessConfiguration"));
        Assert.Equal(IntuneKinds.All.Count, IntuneKinds.All.Select(k => k.Key).Distinct().Count());
    }

    [Fact]
    public async Task A_new_object_is_posted_then_its_follow_up_and_a_remediations_assignments_carry_its_schedule()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "notificationMessageTemplates/n1/localizedNotificationMessages", "{}")
            .On(HttpMethod.Post, "notificationMessageTemplates", """{"id":"n1"}""");
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

        var result = await executor.ApplyAsync(new CreateIntuneObject(IntuneChange.TargetFor("new:n:zz-tenantry-test-n", "zz-tenantry-test-n"), IntuneKinds.Notifications.Collection,
            "Notification Message", new JsonObject { ["displayName"] = "zz-tenantry-test-n" }, "x", "localizedNotificationMessages", new JsonObject { ["subject"] = "Hello" }), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("Hello", (string?)http.Requests.Single(r => r.Url.ToString().Contains("localizedNotificationMessages")).Json!["subject"]);

        var body = GraphChangeExecutor.AssignBody(new SetIntuneAssignments(IntuneChange.TargetFor(IntuneKinds.Remediations.ItemUrl("r1"), "zz-tenantry-test-r"), "Remediation", [],
            [new IntuneAssignment(AssignmentKind.Group, "g1", "zz-tenantry-test-group")], IntuneKinds.Remediations.AssignProperty, IntuneKinds.Remediations.AssignItem));
        var item = body["deviceHealthScriptAssignments"]![0]!;
        Assert.Equal("09:00:00", (string?)item["runSchedule"]!["time"]);
        Assert.Equal("g1", (string?)item["target"]!["groupId"]);
        var apps = GraphChangeExecutor.TargetAppsBody(new SetProtectedApps(IntuneChange.TargetFor("p", "zz-tenantry-test-p"), "ios", [], ["com.microsoft.Office.Outlook"]));
        Assert.Equal("com.microsoft.Office.Outlook", (string?)apps["apps"]![0]!["mobileAppIdentifier"]!["bundleId"]);
    }

    static async Task<(DemoUserRecords Records, ChangePipeline Pipeline, IntuneObjectsViewModel Page)> Page(IntuneKind kind)
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var page = new IntuneObjectsViewModel(kind, records, pipeline, Availability.Yes, EmptyIndex.Instance, records);
        await page.Refresh();
        return (records, pipeline, page);
    }

    static async Task Open(IntuneObjectCard card, CancellationToken ct)
    {
        card.IsExpanded = true;
        for (var i = 0; i < 20 && card.Groups.Count == 0; i++)
            await Task.Delay(10, ct);
        await Task.Delay(20, ct);
    }

    static SettingRow Row(IntuneObjectCard card, string parameter) => card.Groups.SelectMany(g => g.Rows).Single(r => r.Def.Parameter == parameter);

    [Fact]
    public async Task An_apps_settings_inner_settings_and_installs_show_and_a_setting_applies()
    {
        var (records, pipeline, page) = await Page(IntuneKinds.Apps);
        var portal = page.Cards.Single(c => c.Name == "Company Portal");
        await Open(portal, Ct);

        Assert.NotNull(portal.InstallText);
        Assert.Equal("Install As", Row(portal, "installExperience.runAsAccount").Label);
        Assert.Equal("Available: All Users", portal.Assignments.Summary);
        Row(portal, "isFeatured").IsOn = true;
        var change = (SetIntuneSetting)Assert.Single(pipeline.Pending);
        Assert.Equal(("#microsoft.graph.winGetApp", "isFeatured"), (change.ODataType, change.Property));
        var runAs = Row(portal, "installExperience.runAsAccount");
        runAs.Choice = runAs.Choices.Single(c => c.Value == "system");
        await pipeline.ApplyAsync(cancellationToken: Ct);
        var after = await records.GetObjectAsync(IntuneKinds.Apps, portal.Item.Id, Ct);
        Assert.True(after.GetProperty("isFeatured").GetBoolean());
        Assert.Equal("system", after.GetProperty("installExperience").GetProperty("runAsAccount").GetString());
    }

    [Fact]
    public async Task New_makes_a_web_link_and_a_scripts_content_edits_as_text()
    {
        var (records, pipeline, page) = await Page(IntuneKinds.Apps);
        page.ToggleAddCommand.Execute(null);
        page.CreateOption = page.CreateOptions.Single(o => o.Key == "web");
        page.Fields.Single(f => f.Field.Path == "displayName").Text = "zz-tenantry-test-link";
        page.Fields.Single(f => f.Field.Path == "publisher").Text = "Test";
        page.AddCommand.Execute(null);
        Assert.Contains("Web Address", page.AddProblem);
        page.Fields.Single(f => f.Field.Path == "appUrl").Text = "https://test.example";
        page.AddCommand.Execute(null);
        var made = (CreateIntuneObject)Assert.Single(pipeline.Pending);
        Assert.Equal(("#microsoft.graph.webApp", "https://test.example"), ((string?)made.Body["@odata.type"], (string?)made.Body["appUrl"]));
        await pipeline.ApplyAsync(cancellationToken: Ct);
        Assert.Contains((await records.GetObjectsAsync(IntuneKinds.Apps, Ct)), a => a.Name == "zz-tenantry-test-link");

        var (_, scriptPipeline, scripts) = await Page(IntuneKinds.Scripts);
        var card = scripts.Cards.Single();
        await Open(card, Ct);
        var content = Row(card, "scriptContent");
        Assert.True(content.IsLongText);
        Assert.StartsWith("Set-TimeZone", content.TextValue);
        content.TextValue = "Set-TimeZone -Id 'Pacific Standard Time'";
        content.CommitTextCommand.Execute(null);
        var edit = (SetIntuneSetting)Assert.Single(scriptPipeline.Pending);
        Assert.Equal("Set-TimeZone -Id 'Pacific Standard Time'", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(edit.Value!.GetValue<string>())));
    }

    [Fact]
    public async Task Defaults_and_Microsofts_items_keep_still_and_protected_apps_are_one_change()
    {
        var (_, _, enrollment) = await Page(IntuneKinds.Enrollment);
        var defaults = enrollment.Cards.Where(c => c.IsDefault).ToArray();
        Assert.Equal(3, defaults.Length);
        Assert.All(defaults, d => Assert.False(d.CanDelete || d.HasAssignments));
        Assert.Equal("Staff Device Limit", enrollment.Cards[0].Name);

        var (_, _, roles) = await Page(IntuneKinds.Roles);
        Assert.All(roles.Cards.Where(c => c.Name != "Front Office Support"), r => Assert.False(r.CanEdit));

        var (_, pipeline, mam) = await Page(IntuneKinds.IosProtection);
        var policy = mam.Cards.Single();
        await Open(policy, Ct);
        Assert.Equal(["com.microsoft.Office.Outlook", "com.microsoft.skype.teams"], policy.ProtectedApps);
        policy.ProtectedText = "com.microsoft.Office.Outlook";
        policy.CommitProtectedCommand.Execute(null);
        Assert.True(((SetProtectedApps)Assert.Single(pipeline.Pending)).IsCaution);
        Assert.Equal("Copy and Paste To", Row(policy, "allowedOutboundClipboardSharingLevel").Label);
    }

    [Fact]
    public async Task A_notification_message_is_made_with_its_text_and_a_remediation_reaches_a_group_on_its_schedule()
    {
        var (records, pipeline, page) = await Page(IntuneKinds.Notifications);
        page.ToggleAddCommand.Execute(null);
        page.Fields.Single(f => f.Field.Path == "displayName").Text = "zz-tenantry-test-notice";
        page.Fields.Single(f => f.Field.Path == "Then.subject").Text = "Please update";
        page.Fields.Single(f => f.Field.Path == "Then.messageTemplate").Text = "Your device is out of date.";
        page.AddCommand.Execute(null);
        var made = (CreateIntuneObject)Assert.Single(pipeline.Pending);
        Assert.Equal(("localizedNotificationMessages", "Please update", "Your device is out of date."), (made.ThenPath, (string?)made.ThenBody!["subject"], (string?)made.ThenBody["messageTemplate"]));
        Assert.False(made.Body.ContainsKey("Then"));

        var (_, rPipeline, remediations) = await Page(IntuneKinds.Remediations);
        remediations.Cards.Single().Assignments.AddAllDevicesCommand.Execute(null);
        var assign = (SetIntuneAssignments)Assert.Single(rPipeline.Pending);
        Assert.Equal("deviceHealthScriptAssignments", assign.ListProperty);
        Assert.NotNull(assign.ItemTemplate);
    }

    [Fact]
    public void Intunes_refusals_read_as_what_to_do()
    {
        Assert.Contains("permission", Friendly.Message(new InvalidOperationException("Graph 403 Forbidden: {\"Message\": \"Application is not authorized to perform this operation.\"}")));
        Assert.Equal("Invalid property values", Friendly.Message(new InvalidOperationException("{ \"_version\": 3, \"Message\": \"Invalid property values - Operation ID (for customer support): 0 - Activity ID: 1\" }")));
        Assert.Contains("without saying why", Friendly.Message(new InvalidOperationException("{ \"Message\": \"An error has occurred - Operation ID (for customer support): 0\" }")));
    }
}
