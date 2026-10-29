using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>Retention (8f-3): reading Purview, the changes and how they're sent, the guard, the editors and pages in the demo.</summary>
public sealed class RetentionTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ChangeTarget TestPolicy = RetentionPages.PolicyTarget("zz-tenantry-test-retention");
    static readonly ChangeTarget Bo = new("mailbox", "u2", "zz-tenantry-test-bo", "zz-tenantry-test-bo@contoso.example");
    static readonly ChangeTarget Real = new("mailbox", "u9", "Pat Example", "pat@contoso.example");

    static RetentionPolicyDraft Draft(RetentionPolicyKind kind = RetentionPolicyKind.Retention, RetentionSetting? setting = null, IReadOnlyList<string>? labels = null,
        params (string Kind, ChangeTarget[] Places)[] places) =>
        new(kind, null, places.ToDictionary(p => p.Kind, p => (IReadOnlyList<ChangeTarget>)p.Places), [],
            kind == RetentionPolicyKind.Retention ? setting ?? new RetentionSetting("Keep", 30, "CreationAgeInDays") : null, labels ?? [], null);

    [Theory]
    [InlineData("Keep", 2555, "CreationAgeInDays", "Keep for 7 years after it was created")]
    [InlineData("Keep", null, "CreationAgeInDays", "Keep forever")]
    [InlineData("Delete", 365, "ModificationAgeInDays", "Delete 1 year after it was last changed")]
    [InlineData("KeepAndDelete", 90, "EventAgeInDays", "Keep for 3 months after an event happens, then delete")]
    [InlineData("Keep", 10, "TaggedAgeInDays", "Keep for 10 days after it was labeled")]
    public void Settings_read_in_words(string action, int? days, string basedOn, string words) =>
        Assert.Equal(words, new RetentionSetting(action, days, basedOn).Text);

    [Fact]
    public void Unlimited_reads_as_forever() => Assert.Null(RetentionSetting.ParseDays("Unlimited"));

    [Fact]
    public async Task Policies_are_read_one_by_one_for_their_places_and_their_rules_say_what_they_do()
    {
        var exchange = new FakeCmdlets((cmdlet, p) => (cmdlet, p?.GetValueOrDefault("Identity") as string) switch
        {
            ("Get-RetentionCompliancePolicy", null) => """[{"Name":"Keep mail","Guid":"p1","ExchangeLocation":[]},{"Name":"Publish","Guid":"p2"}]""",
            ("Get-RetentionCompliancePolicy", "Keep mail") when p!.ContainsKey("DistributionDetail") => """[{"Name":"Keep mail","Guid":"p1","Enabled":true,"ExchangeLocation":["All"],"ExchangeLocationException":["Pat Example"],"DistributionStatus":"Success","Mode":"Enforce"}]""",
            ("Get-RetentionCompliancePolicy", "Publish") => """[{"Name":"Publish","Guid":"p2","Enabled":false,"SharePointLocation":["https://contoso.sharepoint.example/sites/x"],"Mode":"PendingDeletion"}]""",
            ("Get-RetentionComplianceRule", _) => """[{"Policy":"p1","RetentionComplianceAction":"KeepAndDelete","RetentionDuration":"2555","ExpirationDateOption":"ModificationAgeInDays","PublishComplianceTag":""},{"Policy":"p2","PublishComplianceTag":"5e0a0c1f-0d47-4f2a-9a3e-2b1c3d4e5f60,Invoice,Contract"}]""",
            ("Get-ComplianceTag", _) => """[{"Name":"Contract","Guid":"t1","RetentionAction":"KeepAndDelete","HasRetentionAction":true,"RetentionDuration":"3650","RetentionType":"EventAgeInDays","IsRecordLabel":false,"EventTypeId":"e1","ReviewerEmail":["a@contoso.example"]}]""",
            ("Get-ComplianceRetentionEventType", _) => """[{"Name":"Contract ended","Guid":"e1","CreatedBy":"Avery"},{"Name":"Employee activity","Guid":"e0","CreatedBy":""}]""",
            ("Get-AdaptiveScope", _) => throw new CmdletException("Get-AdaptiveScope", 400, "|x|This tenant is not licensed to be able to run this operation, you can start a trial to test this functionality."),
            _ => "[]",
        });
        var records = new GraphUserRecords(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), exchange);

        var r = await records.GetRetentionAsync(Ct);

        var keep = r.Policies.Single(p => p.Name == "Keep mail");
        Assert.Equal(RetentionPolicyKind.Retention, keep.Kind);
        Assert.Equal("Keep for 7 years after it was last changed, then delete", keep.WhatText);
        Assert.Equal("All mailboxes but 1", keep.PlacesText);
        var publish = r.Policies.Single(p => p.Name == "Publish");
        Assert.Equal(RetentionPolicyKind.PublishLabels, publish.Kind);
        Assert.Equal(["Invoice", "Contract"], publish.Labels);
        Assert.True(publish.IsPendingDeletion);
        var label = Assert.Single(r.Labels);
        Assert.Equal("Contract ended", label.EventType);
        Assert.Equal(["a@contoso.example"], label.Reviewers);
        Assert.True(r.EventTypes.Single(t => t.Name == "Employee activity").BuiltIn);
        Assert.Contains("E5", r.ScopesUnavailable, StringComparison.Ordinal);
    }

    static GraphChangeExecutor Executor(FakeCmdlets exchange) =>
        new(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);

    [Fact]
    public async Task A_publishing_rule_has_no_name_and_a_retention_rule_does()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-RetentionCompliancePolicy" ? """[{"Name":"x"}]""" : "[]");

        await Executor(exchange).ApplyAsync(new CreateRetentionPolicy(TestPolicy, Draft(RetentionPolicyKind.PublishLabels, labels: ["Invoice", "Contract"],
            places: (RetentionPlaces.Exchange, [Bo]))), Ct);
        await Executor(exchange).ApplyAsync(new CreateRetentionPolicy(TestPolicy, Draft(setting: new RetentionSetting("Keep", null, "CreationAgeInDays"),
            places: (RetentionPlaces.TeamsChats, [Bo]))), Ct);

        var rules = exchange.Calls.Where(c => c.Cmdlet == "New-RetentionComplianceRule").Select(c => c.Parameters).ToArray();
        Assert.Contains("\"PublishComplianceTag\":\"Invoice,Contract\"", rules[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"Name\"", rules[0], StringComparison.Ordinal);
        Assert.Contains("\"Name\":\"zz-tenantry-test-retention-rule\"", rules[1], StringComparison.Ordinal);
        Assert.Contains("\"RetentionDuration\":\"Unlimited\"", rules[1], StringComparison.Ordinal);
        // Teams rules take no expiration option.
        Assert.DoesNotContain("ExpirationDateOption", rules[1], StringComparison.Ordinal);
        Assert.Contains("\"TeamsChatLocation\":[\"zz-tenantry-test-bo@contoso.example\"]", exchange.Calls.Last(c => c.Cmdlet == "New-RetentionCompliancePolicy").Parameters, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_policy_whose_rule_fails_is_taken_away_again()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet switch
        {
            "Get-RetentionCompliancePolicy" => """[{"Name":"x"}]""",
            "New-RetentionComplianceRule" => throw new CmdletException("New-RetentionComplianceRule", 400, "|x|Bad rule"),
            _ => "[]",
        });

        var result = await Executor(exchange).ApplyAsync(new CreateRetentionPolicy(TestPolicy, Draft(places: (RetentionPlaces.Exchange, [Bo]))), Ct);

        Assert.Equal(ChangeOutcome.Failed, result.Outcome);
        Assert.Equal("Remove-RetentionCompliancePolicy", exchange.Calls[^1].Cmdlet);
    }

    [Fact]
    public async Task Changing_a_policy_sends_place_changes_and_its_rule_by_id()
    {
        var cy = Bo with { Id = "u3", Name = "zz-tenantry-test-cy", Address = "zz-tenantry-test-cy@contoso.example" };
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-RetentionComplianceRule" ? """[{"Guid":"rule-1","Name":"r"}]""" : "[]");

        var result = await Executor(exchange).ApplyAsync(new UpdateRetentionPolicy(TestPolicy,
            Draft(places: (RetentionPlaces.Exchange, [Bo])), Draft(setting: new RetentionSetting("Keep", 60, "CreationAgeInDays"), places: (RetentionPlaces.Exchange, [cy]))), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var set = exchange.Calls.First(c => c.Cmdlet == "Set-RetentionCompliancePolicy").Parameters;
        Assert.Contains("\"AddExchangeLocation\":[\"zz-tenantry-test-cy@contoso.example\"]", set, StringComparison.Ordinal);
        Assert.Contains("\"RemoveExchangeLocation\":[\"zz-tenantry-test-bo@contoso.example\"]", set, StringComparison.Ordinal);
        var rule = exchange.Calls.First(c => c.Cmdlet == "Set-RetentionComplianceRule").Parameters;
        Assert.Contains("\"Identity\":\"rule-1\"", rule, StringComparison.Ordinal);
        Assert.Contains("\"RetentionDuration\":\"60\"", rule, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_policy_still_being_deployed_says_so_plainly()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Set-RetentionCompliancePolicy"
            ? throw new CmdletException(cmdlet, 400, "|x|Previous changes to the policy 'p' are being deployed. Once deployed, additional actions can be performed.")
            : "[]");

        var result = await Executor(exchange).ApplyAsync(new SetRetentionPolicyEnabled(TestPolicy, true, false), Ct);

        Assert.Equal(ChangeOutcome.Failed, result.Outcome);
        Assert.StartsWith("Purview is still putting this policy's last change in place", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_guard_checks_a_policy_s_places_and_deletions_and_shorter_periods_are_cautions()
    {
        Assert.Null(WriteGuard.Refusal(new CreateRetentionPolicy(TestPolicy, Draft(places: (RetentionPlaces.Exchange, [Bo]))), testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new CreateRetentionPolicy(TestPolicy, Draft(places: (RetentionPlaces.Exchange, [Bo, Real]))), testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new CreateRetentionPolicy(TestPolicy, Draft(places: (RetentionPlaces.Exchange, [EdChange.Everywhere("mailbox")]))), testMode: true));
        Assert.NotNull(WriteGuard.Refusal(new CreateRetentionLabel(RetentionPages.LabelTarget("Real label"), new RetentionLabelDraft(null, null, "Keep", 1, "CreationAgeInDays", false, null, [])), testMode: true));
        Assert.True(new CreateRetentionPolicy(TestPolicy, Draft(setting: new RetentionSetting("Delete", 30, "CreationAgeInDays"), places: (RetentionPlaces.Exchange, [Bo]))).IsCaution);
        Assert.False(new CreateRetentionPolicy(TestPolicy, Draft(places: (RetentionPlaces.Exchange, [Bo]))).IsCaution);
        Assert.True(new SetRetentionPolicyEnabled(TestPolicy, true, false).IsCaution);
        Assert.Equal("zz-tenantry-test-retention", new DeleteRetentionPolicy(TestPolicy, "x").ConfirmPhrase);
        var same = Draft(places: (RetentionPlaces.Exchange, [Bo]));
        Assert.True(new UpdateRetentionPolicy(TestPolicy, same, same with { }).IsNoChange);
        Assert.True(new CreateRetentionEvent(RetentionPages.EventTarget("e"), "t", DateTimeOffset.UtcNow, null, null, null).IsCaution);
    }

    [Fact]
    public void Every_retention_change_survives_the_change_log()
    {
        var label = new RetentionLabelDraft("c", "n", "KeepAndDelete", 365, "CreationAgeInDays", true, null, ["a@contoso.example"]);
        ChangeOp[] changes =
        [
            new CreateRetentionPolicy(TestPolicy, Draft(places: (RetentionPlaces.Exchange, [Bo]))),
            new UpdateRetentionPolicy(TestPolicy, Draft(places: (RetentionPlaces.Exchange, [Bo])), Draft(RetentionPolicyKind.PublishLabels, labels: ["x"], places: (RetentionPlaces.SharePoint, [Bo]))),
            new SetRetentionPolicyEnabled(TestPolicy, true, false), new DeleteRetentionPolicy(TestPolicy, "x"),
            new CreateRetentionLabel(RetentionPages.LabelTarget("l"), label), new UpdateRetentionLabel(RetentionPages.LabelTarget("l"), label, label with { Days = 30 }),
            new DeleteRetentionLabel(RetentionPages.LabelTarget("l"), "x"),
            new CreateAdaptiveScope(RetentionPages.ScopeTarget("s"), "User", "Department -eq 'x'", null), new UpdateAdaptiveScope(RetentionPages.ScopeTarget("s"), "a", "b", null, "c"),
            new DeleteAdaptiveScope(RetentionPages.ScopeTarget("s"), "x"),
            new CreateRetentionEventType(RetentionPages.EventTypeTarget("t"), "c"), new DeleteRetentionEventType(RetentionPages.EventTypeTarget("t")),
            new CreateRetentionEvent(RetentionPages.EventTarget("e"), "t", DateTimeOffset.UtcNow, null, "ComplianceAssetId:1", null),
        ];
        foreach (var change in changes)
            Assert.Equal(change.Summary, JsonSerializer.Deserialize<ChangeOp>(JsonSerializer.Serialize(change))!.Summary);
    }

    // ---- Editors and pages, in the demo ------------------------------------------------------------

    static async Task<(DemoUserRecords Records, ChangePipeline Pipeline, RetentionContext Context, RetentionDirectory Directory, List<object> Dialogs)> Demo(bool premium = false)
    {
        var directory = new DemoTenantDirectory(userCount: 40);
        var records = new DemoUserRecords(directory, pim: premium);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var r = await records.GetRetentionAsync(Ct);
        var dialogs = new List<object>();
        var ctx = new RetentionContext(EmptyIndex.Instance, pipeline, Availability.Yes, () => [], (_, d) => dialogs.Add(d), () => { }, () => r, _ => { }, _ => { });
        return (records, pipeline, ctx, r, dialogs);
    }

    [Fact]
    public async Task The_policy_editor_refuses_mixing_Teams_with_other_places_and_publishing_with_no_labels()
    {
        var (_, pipeline, ctx, _, dialogs) = await Demo();
        ctx.EditPolicy(null);
        var editor = Assert.IsType<RetentionPolicyEditorViewModel>(Assert.Single(dialogs));
        editor.Name = "Mixed";
        editor.Places[0].Mode = editor.Places[0].Modes.Single(m => m.Value == PlacesEditor.All);
        editor.Places[5].Mode = editor.Places[5].Modes.Single(m => m.Value == PlacesEditor.All);
        Assert.Contains(editor.Check(), p => p.StartsWith("Teams messages need a policy of their own", StringComparison.Ordinal));

        editor.Places[5].Mode = editor.Places[5].Modes.Single(m => m.Value == PlacesEditor.None);
        editor.Kind = RetentionPolicyEditorViewModel.Kinds.Single(k => k.Value == nameof(RetentionPolicyKind.PublishLabels));
        Assert.Contains("Tick at least one label to publish.", editor.Check());
        editor.Labels[0].IsChecked = true;
        editor.SaveCommand.Execute(null);

        var made = Assert.IsType<CreateRetentionPolicy>(Assert.Single(pipeline.Pending));
        Assert.Equal(RetentionPolicyKind.PublishLabels, made.Draft.Kind);
        Assert.True(EdChange.IsEverywhere(made.Draft.Places[RetentionPlaces.Exchange]));
    }

    [Fact]
    public async Task Opening_and_saving_a_policy_unchanged_stages_nothing_and_a_new_period_stages_a_change()
    {
        var (_, pipeline, ctx, r, dialogs) = await Demo();
        var policy = r.Policies.Single(p => p.Name == "Keep mail seven years");
        ctx.EditPolicy(policy);
        ((RetentionPolicyEditorViewModel)dialogs[0]).SaveCommand.Execute(null);
        Assert.Empty(pipeline.Pending);

        ctx.EditPolicy(policy);
        var editor = (RetentionPolicyEditorViewModel)dialogs[1];
        editor.Setting.Amount = "10";
        editor.SaveCommand.Execute(null);
        var update = Assert.IsType<UpdateRetentionPolicy>(Assert.Single(pipeline.Pending));
        Assert.Equal(3650, update.New.Setting!.Days);
        await pipeline.ApplyAsync(cancellationToken: Ct);
    }

    [Fact]
    public async Task A_label_s_action_is_fixed_once_made_and_reviewers_need_a_deleting_label()
    {
        var (_, pipeline, ctx, r, dialogs) = await Demo();
        ctx.EditLabel(r.Labels.Single(l => l.Name == "Financial record"));
        var editor = (RetentionLabelEditorViewModel)dialogs[0];
        Assert.False(editor.Setting.CanChangeAction);
        editor.ReviewerPicker.Query = "x";
        editor.Reviewers.Add(new PlaceChip(new ChangeTarget("user", "a", "A", "a@northgate.example"), _ => { }));
        Assert.Contains("Reviewers only apply to labels that delete content.", editor.Check());

        ctx.EditLabel(null);
        var fresh = (RetentionLabelEditorViewModel)dialogs[1];
        fresh.Name = "Board minutes";
        fresh.Setting.BasedOn = fresh.Setting.BasedOnChoices.Single(b => b.Value == "EventAgeInDays");
        fresh.SaveCommand.Execute(null);
        var made = Assert.IsType<CreateRetentionLabel>(Assert.Single(pipeline.Pending));
        Assert.Equal("Employee activity", made.Draft.EventType);
    }

    [Fact]
    public async Task A_policy_page_turns_it_off_as_a_caution_and_a_locked_one_offers_nothing()
    {
        var (_, pipeline, ctx, r, _) = await Demo();
        var page = new RetentionPolicyPageViewModel(r.Policies.Single(p => p.Name == "Keep mail seven years"), ctx);
        page.Buttons.Single(b => b.Text == "Turn Off").ActCommand.Execute(null);
        Assert.True(Assert.IsType<SetRetentionPolicyEnabled>(Assert.Single(pipeline.Pending)).IsCaution);

        var locked = new RetentionPolicyPageViewModel(r.Policies[0] with { IsLocked = true }, ctx);
        Assert.Equal(["Open in Purview"], locked.Buttons.Select(b => b.Text));
    }

    [Fact]
    public async Task The_events_page_offers_deleting_only_unused_custom_types_and_records_events()
    {
        var (records, pipeline, ctx, r, dialogs) = await Demo();
        await pipeline.ApplyAsync(cancellationToken: Ct);
        var events = new RetentionEventsViewModel(ctx, () => DateTimeOffset.UtcNow, _ => Task.FromResult<RetentionDirectory?>(r));
        await events.Section.LoadAsync(Ct);
        var types = events.Section.Cards.Single(c => c.Title == "Event Types");
        Assert.All(types.RowItems, row => Assert.False(row.HasAction));

        ((AppButtonForm)events.Section.Cards.Single(c => c.Title == "Events").Form!).ActCommand.Execute(null);
        var form = Assert.IsType<NewRetentionEventViewModel>(Assert.Single(dialogs));
        form.Name = "Lease ended";
        form.AddCommand.Execute(null);
        var made = Assert.IsType<CreateRetentionEvent>(Assert.Single(pipeline.Pending));
        Assert.True(made.IsCaution);
    }

    [Fact]
    public async Task Adaptive_scopes_say_they_need_E5_without_it_and_are_listed_with_it()
    {
        var (_, _, _, plain, _) = await Demo();
        Assert.Contains("E5", plain.ScopesUnavailable, StringComparison.Ordinal);
        var (_, _, _, premium, _) = await Demo(premium: true);
        Assert.Null(premium.ScopesUnavailable);
        Assert.Single(premium.Scopes);
    }
}
