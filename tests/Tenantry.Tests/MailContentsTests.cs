using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>The Mail tab's mailbox contents (4b-3): Exchange's formats, what each change sends, the pages, typed confirmation and converting.</summary>
public sealed class MailContentsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string BoAddress = "zz-tenantry-test-bo@contoso.example";
    static readonly ChangeTarget Bo = new("user", "u2", "zz-tenantry-test-bo", BoAddress);

    // ---- Formats ----------------------------------------------------------------------------

    [Fact]
    public void A_rule_keeps_its_64_bit_id_exactly_and_collects_every_forward()
    {
        // As Get-InboxRule returns it over REST (seen live): the ID is a JSON number past double precision.
        var row = JsonDocument.Parse("""
            {"Name":"Move it","RuleIdentity":17336492416353435649,"Enabled":true,"Priority":2,"Description":"If the message:\r\n\tall\r\n",
             "InError":false,"ForwardTo":["Ada [SMTP:ada@contoso.example]"],"ForwardAsAttachmentTo":null,"RedirectTo":["x@mail.example"],
             "DeleteMessage":false,"MoveToFolder":null}
            """).RootElement;

        var rule = GraphUserRecords.ToRule(row, BoAddress);

        Assert.Equal(BoAddress + "\\17336492416353435649", rule.Identity);
        Assert.Equal(["Ada [SMTP:ada@contoso.example]", "x@mail.example"], rule.ForwardsTo);
        Assert.Equal("If the message:\n  all", rule.Description);
    }

    // ---- What's sent ------------------------------------------------------------------------

    static GraphChangeExecutor Executor(FakeCmdlets exchange) =>
        new(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);

    [Theory]
    [InlineData(null, "Reviewer", "Add-MailboxFolderPermission")]
    [InlineData("Reviewer", "Editor", "Set-MailboxFolderPermission")]
    [InlineData("Editor", null, "Remove-MailboxFolderPermission")]
    public async Task Calendar_access_is_added_changed_or_removed_with_the_matching_cmdlet(string? old, string? @new, string cmdlet)
    {
        var exchange = new FakeCmdlets((c, _) => c == "Get-MailboxFolderPermission" && @new is not null ? $$"""[{"AccessRights":["{{@new}}"]}]""" : "[]");

        var result = await Executor(exchange).ApplyAsync(
            new SetFolderPermission(Bo, BoAddress + ":\\Calendar", "Calendar", "ada@contoso.example", "Ada", old, @new), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal(cmdlet, exchange.Calls[0].Cmdlet);
        Assert.Contains("\"User\":\"ada@contoso.example\"", exchange.Calls[0].Parameters);
    }

    [Fact]
    public async Task Blocking_a_phone_adds_its_id_to_the_blocked_list_and_a_work_data_wipe_says_so()
    {
        var exchange = new FakeCmdlets((c, _) => c == "Get-CASMailbox" ? """[{"ActiveSyncBlockedDeviceIDs":["APPL123"]}]""" : "[]");
        var executor = Executor(exchange);

        var block = await executor.ApplyAsync(new SetMobileDevice(Bo, "bo\\iPhone", "APPL123", "iPhone", DeviceAction.Block), Ct);
        var wipe = await executor.ApplyAsync(new SetMobileDevice(Bo, "bo\\iPhone", "APPL123", "iPhone", DeviceAction.WipeAccountOnly), Ct);

        Assert.Equal((ChangeOutcome.Applied, ChangeOutcome.Applied), (block.Outcome, wipe.Outcome));
        Assert.Contains("\"ActiveSyncBlockedDeviceIDs\":{\"@odata.type\":\"#Exchange.GenericHashTable\",\"Add\":[\"APPL123\"]}", exchange.Calls[0].Parameters);
        var clear = exchange.Calls.Single(c => c.Cmdlet == "Clear-MobileDevice");
        Assert.Contains("\"AccountOnly\":true", clear.Parameters);
    }

    [Fact]
    public async Task Converting_sends_the_type_and_reads_back_the_mailbox_kind()
    {
        var exchange = new FakeCmdlets((c, _) => c == "Get-Mailbox" ? """[{"ExternalDirectoryObjectId":"u2","RecipientTypeDetails":"SharedMailbox"}]""" : "[]");

        var result = await Executor(exchange).ApplyAsync(new ConvertMailbox(Bo, false, true), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Contains("\"Type\":\"Shared\"", exchange.Calls[0].Parameters);
    }

    [Fact]
    public async Task A_deleted_rule_counts_as_gone_when_exchange_no_longer_finds_it()
    {
        var exchange = new NotFoundAfterRemove();

        var result = await Executor(exchange.Runner).ApplyAsync(new RemoveInboxRule(Bo, BoAddress + "\\42", "Forward all", "On"), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
    }

    sealed class NotFoundAfterRemove
    {
        public FakeCmdlets Runner { get; } = new((c, _) => c == "Get-InboxRule"
            ? throw new Tenantry.Core.Exchange.CmdletException("Get-InboxRule", 404, "ManagementObjectNotFoundException|x|The rule wasn't found.")
            : "[]");
    }

    [Theory]
    [InlineData("Identity", "bo@contoso.example", "bo@contoso.example")]
    [InlineData("Identity", "bo@contoso.example:\\Calendar", "bo@contoso.example")]
    [InlineData("Mailbox", "bo@contoso.example", "bo@contoso.example")]
    [InlineData("Identity", "a1b2c3", null)]
    public void A_cmdlet_that_needs_its_mailbox_server_is_routed_on_that_mailbox(string parameter, string value, string? anchor) =>
        Assert.Equal(anchor, Tenantry.Exchange.RestCmdletRunner.ProxyAnchor(new Dictionary<string, object?> { [parameter] = value }));

    sealed class FailsConvert : IChangeExecutor
    {
        public List<ChangeOp> Applied { get; } = [];

        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default)
        {
            if (change is ConvertMailbox)
                return Task.FromResult(new ApplyResult(ChangeOutcome.Failed, "Cmdlet needs proxy."));
            Applied.Add(change);
            return Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
        }
    }

    [Fact]
    public async Task A_license_is_not_removed_when_converting_to_shared_fails()
    {
        // Seen live: the convert failed and the license removal ran anyway, leaving a user mailbox unlicensed.
        var executor = new FailsConvert();
        var pipeline = new ChangePipeline(executor, new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var convert = new ConvertMailbox(Bo, false, true);
        pipeline.StageAll([convert, new SetLicenseAssigned(Bo, "sku", "Business Basic", true, false) { DependsOn = convert.Key }]);

        var results = await pipeline.ApplyAsync(cancellationToken: Ct);

        Assert.Empty(executor.Applied);
        Assert.Contains("Mailbox Type", results[1].Result.Detail);
        Assert.Equal(2, pipeline.Pending.Count);
    }

    // ---- Typed confirmation -----------------------------------------------------------------

    sealed class Noop : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static ChangePipeline Pipeline() => new(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");

    [Fact]
    public async Task A_wipe_needs_the_device_name_typed_before_apply()
    {
        var pipeline = Pipeline();
        var tray = new PendingChangesViewModel(pipeline, "Contoso", showTestMode: true, () => Task.CompletedTask);
        pipeline.Stage(new SetMobileDevice(Bo, "bo\\iPhone", "APPL123", "Bo's iPhone", DeviceAction.Wipe));

        await tray.ApplyCommand.ExecuteAsync(null);
        Assert.True(tray.IsConfirming);
        var typed = Assert.Single(tray.TypedConfirms);
        Assert.False(tray.ApplyCommand.CanExecute(null));

        typed.Typed = "Bo's iphone";
        Assert.True(tray.ApplyCommand.CanExecute(null));
        await tray.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(pipeline.Pending);
    }

    // ---- The tab ----------------------------------------------------------------------------

    sealed class Records : IUserRecords, IMailboxContents
    {
        public List<InboxRule> Rules { get; } =
        [
            new(BoAddress + "\\1", "Tidy", true, 1, "move", false, [], false, "Archive"),
            new(BoAddress + "\\2", "Copy out", true, 2, "forward", false, ["Someone [SMTP:someone@mail.example]"], false, null),
        ];

        public MailDetails Mail { get; set; } = new(BoAddress, [], null, false, false, false, AutoReply.Off, 1L << 30, 50L << 30, 49L << 30, 10, ["contoso.example"]);

        public UserLicensing Licensing { get; set; } = new("US",
            [new LicenseAssignment("sku-basic", "Business Basic", null, null, null, []), new LicenseAssignment("sku-e3", "E3", "g1", "All staff", null, [])], [], []);

        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(Licensing);
        public Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailboxDelegate>>([]);
        public Task<MailDetails> GetMailAsync(string userId, string address, CancellationToken cancellationToken = default) => Task.FromResult(Mail);

        public Task<IReadOnlyList<InboxRule>> GetInboxRulesAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboxRule>>(Rules);
        public Task<FolderSharing> GetCalendarSharingAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FolderSharing(BoAddress + ":\\Calendar", "Calendar",
                [new FolderPermission("Default", "AvailabilityOnly", false), new FolderPermission("zz-tenantry-test-cy", "Reviewer", false)]));
        public Task<IReadOnlyList<MobileDevice>> GetMobileDevicesAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MobileDevice>>([new MobileDevice("bo\\iPhone", "APPL1", "Bo's iPhone", "iPhone", "iOS", "Outlook app", null, null, "Allowed", null)]);
        public Task<IReadOnlyList<DeletedItem>> GetDeletedItemsAsync(string address, string? subject, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeletedItem>>([new DeletedItem("e1", "Budget", "IPM.Note", "/Inbox", null)]);
        public Task<IReadOnlyList<TracedMessage>> TraceAsync(string address, TraceDirection direction, int days, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TracedMessage>>([]);
        public Task<IReadOnlyList<TraceEvent>> TraceDetailAsync(TracedMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TraceEvent>>([]);
    }

    sealed class Index(params UserSummary[] users) : IDirectoryIndex
    {
        public UserSummary? User(string id) => users.FirstOrDefault(u => u.Id == id);
        public GroupSummary? Group(string id) => null;
        public IEnumerable<UserSummary> AllUsers => users;
        public IEnumerable<GroupSummary> AllGroups => [];
    }

    static UserSummary Person(string id, MailboxInfo? mailbox = null, bool enabled = true) =>
        new(id, $"zz-tenantry-test-{id}", $"zz-tenantry-test-{id}@contoso.example", $"zz-tenantry-test-{id}@contoso.example", null, null, enabled,
            UserKind.Member, DateTimeOffset.UnixEpoch, null, [], Mailbox: mailbox);

    static readonly MailboxInfo Plain = new("User", BoAddress, [], ForwardingKind.None, null, false, false, false, false);

    static async Task<(MailSection Tab, ChangePipeline Pipeline, Records Records)> Tab(MailPage page)
    {
        var pipeline = Pipeline();
        var records = new Records();
        var bo = Person("bo", Plain) with { Id = "u2" };
        var view = new UserDetailViewModel(bo, pipeline: pipeline, canEdit: Availability.Yes, records: records, index: new Index(bo, Person("ada"), Person("cy")));
        var tab = view.Sections.OfType<MailSection>().Single();
        await tab.LoadAsync(Ct);
        tab.SelectedPage = page;
        for (var i = 0; i < 50 && (tab.RulesLoading || tab.SharingLoading || tab.DevicesLoading || tab.DeletedLoading); i++)
            await Task.Delay(10, Ct);
        return (tab, pipeline, records);
    }

    [Fact]
    public async Task Rules_show_forwarding_outside_and_switch_or_delete_through_pending_changes()
    {
        var (tab, pipeline, _) = await Tab(MailPage.RulesAndSharing);
        Assert.True(tab.HasForwardingRule);
        Assert.True(tab.Rules[1].ForwardsOutside);

        tab.Rules[0].IsEnabled = false;
        tab.Rules[1].DeleteCommand.Execute(null);

        Assert.Equal(2, pipeline.Pending.Count);
        Assert.Equal("Turning Off", tab.Rules[0].PendingText);
        Assert.True(tab.Rules[1].IsDeleting);
        Assert.True(pipeline.Pending.OfType<RemoveInboxRule>().Single().IsCaution);

        tab.Rules[1].DeleteCommand.Execute(null);
        Assert.Single(pipeline.Pending);
    }

    [Fact]
    public async Task Calendar_sharing_changes_roles_adds_people_and_keeps_the_built_in_entries()
    {
        var (tab, pipeline, _) = await Tab(MailPage.RulesAndSharing);
        Assert.Equal(["Everyone in the Organization", "zz-tenantry-test-cy"], tab.CalendarSharing.Select(r => r.Name));
        Assert.False(tab.CalendarSharing[0].CanRemove);

        tab.CalendarSharing[1].SelectedRole = SharingRow.Roles.Single(r => r.Role == "Editor");
        tab.SharingPicker.Query = "ada";
        tab.SharingPicker.PickCommand.Execute(tab.SharingPicker.Results.Single());

        var changes = pipeline.Pending.Cast<SetFolderPermission>().ToArray();
        Assert.Equal(("Reviewer", "Editor"), (changes[0].Old, changes[0].New));
        Assert.Equal(((string?)null, "Reviewer", "zz-tenantry-test-ada@contoso.example"), (changes[1].Old, changes[1].New, changes[1].Person));
        Assert.Equal("Adding", tab.CalendarSharing.Single(r => r.Name == "zz-tenantry-test-ada").PendingText);
    }

    [Fact]
    public async Task Device_actions_stage_and_pressing_again_takes_them_back()
    {
        var (tab, pipeline, _) = await Tab(MailPage.Devices);

        tab.Devices[0].ToggleBlockCommand.Execute(null);
        tab.Devices[0].WipeCommand.Execute(null);
        Assert.Equal(2, pipeline.Pending.Count);
        Assert.True(tab.Devices[0].IsBlocked);
        Assert.Equal("Bo's iPhone", pipeline.Pending.OfType<SetMobileDevice>().Single(d => d.Action == DeviceAction.Wipe).ConfirmPhrase);

        tab.Devices[0].WipeCommand.Execute(null);
        tab.Devices[0].ToggleBlockCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public async Task Restoring_a_deleted_item_stages_and_undoes()
    {
        var (tab, pipeline, _) = await Tab(MailPage.Recovery);

        tab.DeletedItems.Single().ToggleCommand.Execute(null);
        Assert.Equal("e1", Assert.IsType<RestoreDeletedItem>(pipeline.Pending.Single()).EntryId);
        Assert.Equal("Undo Restore", tab.DeletedItems.Single().RestoreText);

        tab.DeletedItems.Single().ToggleCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
    }

    // ---- Converting -------------------------------------------------------------------------

    static async Task<ConvertMailboxViewModel> Convert(Records records, ChangePipeline pipeline)
    {
        var convert = new ConvertMailboxViewModel(Person("bo", Plain) with { Id = "u2" }, records, pipeline, () => { });
        for (var i = 0; i < 50 && convert.IsLoading; i++)
            await Task.Delay(10, Ct);
        return convert;
    }

    [Fact]
    public async Task Converting_to_shared_goes_first_then_removes_direct_licenses_and_blocks_sign_in()
    {
        var pipeline = Pipeline();
        var convert = await Convert(new Records(), pipeline);
        Assert.True(convert.RemoveLicenses);
        Assert.Contains("All staff", convert.GroupLicensesText);

        convert.ConvertCommand.Execute(null);

        Assert.IsType<ConvertMailbox>(pipeline.Pending[0]);
        Assert.Equal("sku-basic", Assert.IsType<SetLicenseAssigned>(pipeline.Pending[1]).SkuId);
        Assert.IsType<SetSignInAllowed>(pipeline.Pending[2]);
        Assert.Equal(3, pipeline.Pending.Count);
    }

    [Fact]
    public async Task A_mailbox_that_still_needs_a_license_keeps_it_unless_the_admin_chooses()
    {
        var records = new Records();
        records.Mail = records.Mail with { UsedBytes = 60L << 30 };
        var pipeline = Pipeline();

        var convert = await Convert(records, pipeline);

        Assert.Contains("over 50 GB", Assert.Single(convert.Warnings));
        Assert.False(convert.RemoveLicenses);
        convert.ConvertCommand.Execute(null);
        Assert.DoesNotContain(pipeline.Pending, c => c is SetLicenseAssigned);
    }
}
