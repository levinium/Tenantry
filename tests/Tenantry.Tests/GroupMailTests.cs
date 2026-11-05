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

/// <summary>A group's mail settings (G2): read from Exchange, edited as staged changes, sent as Set-UnifiedGroup or Set-DistributionGroup.</summary>
public sealed class GroupMailTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly UserSummary Ada = new("u1", "Ada Lovelace", "ada@contoso.example", "ada@contoso.example", null, null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, []);
    static readonly UserSummary Bo = new("u2", "Bo Diddley", "bo@contoso.example", "bo@contoso.example", null, null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, []);

    static readonly GroupSummary Team = new("g1", "Finance Team", "finance@contoso.example", null, GroupKind.Microsoft365, false, false, "Private", null, []);
    static readonly GroupSummary Board = new("dl1", "Board", "board@contoso.example", null, GroupKind.Distribution, false, false, null, null, []);

    sealed class Index : IDirectoryIndex
    {
        public UserSummary? User(string id) => AllUsers.FirstOrDefault(u => u.Id == id);
        public GroupSummary? Group(string id) => AllGroups.FirstOrDefault(g => g.Id == id);
        public IEnumerable<UserSummary> AllUsers => [Ada, Bo];
        public IEnumerable<GroupSummary> AllGroups => [Team, Board];
        public UserSummary? UserByUpn(string upn) => AllUsers.FirstOrDefault(u => string.Equals(u.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase));
    }

    sealed class Records(GroupMail mail) : IGroupRecords, IUserRecords
    {
        public Task<IReadOnlyList<DeletedGroup>> GetDeletedGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeletedGroup>>([]);
        public Task<RuleTest> TestRuleAsync(string rule, string personId, string personName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GroupMail> GetGroupMailAsync(GroupSummary group, CancellationToken cancellationToken = default) => Task.FromResult(mail);
        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["contoso.example"]);
        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailDetails> GetMailAsync(string userId, string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static ChangePipeline Pipeline() => new(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");

    static GroupMail Mail(IReadOnlyList<string>? accept = null, IReadOnlyList<string>? moderators = null) =>
        new("finance@contoso.example", ["SMTP:finance@contoso.example", "smtp:fin@contoso.example", "SPO:SPO_x@SPO_y"], false, false,
            accept ?? [], [], false, moderators ?? [], [], "Always", "Ask first", ["bo@contoso.example"], [], false, false, true, true);

    static async Task<GroupMailEditor> Editor(GroupSummary group, ChangePipeline pipeline, GroupMail mail)
    {
        var records = new Records(mail);
        var editor = new GroupMailEditor(group, new Index(), pipeline, Availability.Yes, records, records);
        await editor.LoadAsync();
        return editor;
    }

    [Fact]
    public async Task Addresses_show_the_primary_and_aliases_but_not_SharePoints()
    {
        var editor = await Editor(Team, Pipeline(), Mail());

        Assert.Equal([("finance@contoso.example", true), ("fin@contoso.example", false)], editor.Addresses.Select(a => (a.Address, a.IsPrimary)));
        Assert.False(editor.Addresses[0].CanRemove);
    }

    [Fact]
    public async Task Addresses_are_added_checked_removed_and_made_primary()
    {
        var pipeline = Pipeline();
        var editor = await Editor(Team, pipeline, Mail());

        editor.StartAddAddressCommand.Execute(null);
        editor.NewAlias = "fin";
        Assert.Contains("already in use", editor.AddressProblem, StringComparison.Ordinal);
        editor.NewAlias = "accounts";
        editor.AddAddressCommand.Execute(null);
        editor.Addresses.Single(a => a.Address == "fin@contoso.example").RemoveCommand.Execute(null);
        editor.Addresses.Single(a => a.Address == "accounts@contoso.example").MakePrimaryCommand.Execute(null);

        Assert.Contains(pipeline.Pending, c => c is SetGroupAddress { Address: "accounts@contoso.example", New: true });
        Assert.Contains(pipeline.Pending, c => c is SetGroupAddress { Address: "fin@contoso.example", New: false, IsCaution: true });
        Assert.Contains(pipeline.Pending, c => c is SetGroupPrimaryAddress { New: "accounts@contoso.example" });
    }

    [Fact]
    public async Task A_list_of_senders_is_staged_whole_as_Entra_IDs_and_named_from_the_local_copy()
    {
        var pipeline = Pipeline();
        var editor = await Editor(Board, pipeline, Mail(accept: ["u1"]));

        Assert.Equal("Ada Lovelace", editor.AcceptList.Rows.Single().Name);
        editor.AcceptList.Picker.Query = "bo";
        editor.AcceptList.Picker.PickCommand.Execute(editor.AcceptList.Picker.Results.Single());
        editor.AcceptList.Rows.Single(r => r.Name == "Ada Lovelace").RemoveCommand.Execute(null);

        var change = Assert.Single(pipeline.Pending.OfType<SetGroupMailValue>());
        Assert.Equal(("AcceptMessagesOnlyFromSendersOrMembers", "Ada Lovelace", "Bo Diddley"), (change.Parameter, change.Old, change.New));
        Assert.Equal(["u2"], change.Value!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal([("Ada Lovelace", "Removing"), ("Bo Diddley", "Adding")], editor.AcceptList.Rows.Select(r => (r.Name, r.PendingText!)).Order());
        // Back as it was: the change leaves Pending Changes.
        editor.AcceptList.Rows.Single(r => r.Name == "Ada Lovelace").RemoveCommand.Execute(null);
        editor.AcceptList.Rows.Single(r => r.Name == "Bo Diddley").RemoveCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public async Task Switches_stage_their_parameter_and_outside_senders_is_inverted()
    {
        var pipeline = Pipeline();
        var editor = await Editor(Team, pipeline, Mail());

        Assert.Equal(["Hide from the Address List", "Accept Mail from Outside", "Hide from Outlook", "Send Copies to Members' Inboxes", "Send Calendar Events to Members",
            "Welcome Message"], editor.Delivery.Select(t => t.Label));
        editor.Delivery.Single(t => t.Label == "Accept Mail from Outside").IsOn = true;
        editor.Delivery.Single(t => t.Label == "Welcome Message").IsOn = false;

        var outside = pipeline.Pending.OfType<SetGroupMailValue>().Single(c => c.Parameter == "RequireSenderAuthenticationEnabled");
        Assert.Equal((false, true), (outside.Value!.GetValue<bool>(), outside.IsCaution));
        Assert.Contains(pipeline.Pending, c => c is SetGroupMailValue { Parameter: "UnifiedGroupWelcomeMessageEnabled" } w && !w.Value!.GetValue<bool>());
        Assert.True(editor.Delivery.Single(t => t.Label == "Accept Mail from Outside").IsPending);
    }

    [Fact]
    public async Task Approval_on_a_Microsoft_365_group_waits_for_a_moderator()
    {
        var pipeline = Pipeline();
        var editor = await Editor(Team, pipeline, Mail());

        Assert.False(editor.Moderation!.CanEdit);
        editor.Moderators.Picker.Query = "ada";
        editor.Moderators.Picker.PickCommand.Execute(editor.Moderators.Picker.Results.Single());
        Assert.True(editor.Moderation!.CanEdit);
        // A list falls back to its owners, so it can be turned on straight away.
        Assert.True((await Editor(Board, Pipeline(), Mail())).Moderation!.CanEdit);
    }

    [Fact]
    public async Task The_MailTip_is_edited_and_cleared_as_text()
    {
        var pipeline = Pipeline();
        var editor = await Editor(Team, pipeline, Mail());
        var tip = editor.TextSettings.Single();

        tip.EditCommand.Execute(null);
        tip.Draft = "";
        tip.StageCommand.Execute(null);

        var change = Assert.Single(pipeline.Pending.OfType<SetGroupMailValue>());
        Assert.Equal(("MailTip", "Ask first", "(none)", null), (change.Parameter, change.Old, change.New, change.Value));
        Assert.True(tip.HasPending);
    }

    [Fact]
    public void A_MailTip_stored_as_HTML_reads_as_its_text() =>
        Assert.Equal("Test tip & more", GroupMail.PlainTip("<html>\r\n<body>\r\nTest tip &amp; more\r\n</body>\r\n</html>\r\n"));

    [Fact]
    public async Task Exchange_is_read_and_written_with_the_cmdlet_for_the_kind()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet switch
        {
            "Get-DistributionGroup" => """
                [{"PrimarySmtpAddress":"board@contoso.example","EmailAddresses":["SMTP:board@contoso.example"],"RequireSenderAuthenticationEnabled":true,
                  "AcceptMessagesOnlyFromSendersOrMembers":["u2"],"ModeratedBy":[],"MailTip":"<html><body>Board only</body></html>","GrantSendOnBehalfTo":[],
                  "SendModerationNotifications":"Internal"}]
                """,
            "Get-RecipientPermission" => """[{"Trustee":"NT AUTHORITY\\SELF","AccessRights":["SendAs"]},{"Trustee":"ada@contoso.example","AccessRights":["SendAs"]}]""",
            _ => "[]",
        });
        var store = new MemoryEntityStore();
        var records = new GraphUserRecords(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), store, exchange);

        var mail = await records.GetGroupMailAsync(Board, Ct);

        Assert.Equal((false, "Board only", "Internal"), (mail.OutsideSenders, mail.MailTip, mail.ModerationNotices));
        Assert.Equal(["u2"], mail.AcceptFrom);
        Assert.Equal(["ada@contoso.example"], mail.SendAs);
        Assert.Null(mail.WelcomeMessage);

        var executor = new GraphChangeExecutor(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), store, NoDelay.Instance, exchange);
        var result = await executor.ApplyAsync(new SetGroupMailValue(GroupAccess.Target(Board), GroupKind.Distribution, "AcceptMessagesOnlyFromSendersOrMembers",
            "Who Can Send", "Bo", "Bo", new JsonArray("u2")), Ct);
        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var set = exchange.Calls.Single(c => c.Cmdlet == "Set-DistributionGroup");
        Assert.Contains("\"AcceptMessagesOnlyFromSendersOrMembers\":[\"u2\"]", set.Parameters, StringComparison.Ordinal);
        Assert.Contains("\"BypassSecurityGroupManagerCheck\":true", set.Parameters, StringComparison.Ordinal);
        Assert.Equal("Set-UnifiedGroup", new SetGroupMailValue(GroupAccess.Target(Team), GroupKind.Microsoft365, "MailTip", "MailTip", "", "x", "x").Cmdlet);
    }
}
