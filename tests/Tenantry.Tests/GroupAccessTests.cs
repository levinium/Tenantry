using System.Net;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;

namespace Tenantry.Tests;

/// <summary>Group membership, mailbox access and manager changes: what is sent, and the read-back.</summary>
public sealed class GroupAccessTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ChangeTarget Group = new("group", "g1", "zz-tenantry-test-group", null);
    static readonly ChangeTarget List = new("group", "dl1", "zz-tenantry-test-list", "zz-tenantry-test-list@contoso.example");
    static readonly ChangeTarget Shared = new("mailbox", "m1", "zz-tenantry-test-shared", "zz-tenantry-test-shared@contoso.example");
    static readonly ChangeTarget Ada = new("user", "u1", "zz-tenantry-test-ada", "zz-tenantry-test-ada@contoso.example");
    static readonly ChangeTarget Bo = new("user", "u2", "zz-tenantry-test-bo", "zz-tenantry-test-bo@contoso.example");

    static GraphChangeExecutor Executor(FakeHttp http, IEntityStore store, FakeCmdlets? exchange = null) =>
        new(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store, NoDelay.Instance, exchange);

    static MemoryEntityStore StoreWithGroup(string id, string members = "{}")
    {
        var store = new MemoryEntityStore();
        store.Apply(DirectorySync.Groups, [new EntityChange(id, JsonNode.Parse($$"""{"id":"{{id}}","displayName":"Test","members":{{members}}}""")!.AsObject())]);
        return store;
    }

    [Fact]
    public async Task Adding_a_member_posts_a_reference_and_reads_the_list_back()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "/groups/g1/members/$ref", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            .On(HttpMethod.Get, "/groups/g1/members?$select=id", """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u1"}]}""");
        var store = StoreWithGroup("g1");

        var result = await Executor(http, store).ApplyAsync(new SetGroupMembership(Group, Ada, "user", GroupRole.Member, false, false, true), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("https://graph.microsoft.com/v1.0/directoryObjects/u1", (string?)http.Requests[0].Json!["@odata.id"]);
        Assert.Equal("user", (string?)JsonNode.Parse(store.Get(DirectorySync.Groups, "g1")!)!["members"]!["u1"]);
    }

    [Fact]
    public async Task Removing_an_owner_deletes_the_reference()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Delete, "/groups/g1/owners/u1/$ref", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            .On(HttpMethod.Get, "/groups/g1/owners?$select=id", """{"value":[]}""");
        var store = StoreWithGroup("g1");

        var result = await Executor(http, store).ApplyAsync(new SetGroupMembership(Group, Ada, "user", GroupRole.Owner, false, true, false), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Empty(JsonNode.Parse(store.Get(DirectorySync.Groups, "g1")!)!["owners"]!.AsObject());
    }

    [Fact]
    public async Task Distribution_lists_change_through_exchange_by_address()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/groups/dl1/members?$select=id", """{"value":[]}""")
            .On(HttpMethod.Get, "/groups/dl1/owners?$select=id", """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u2"}]}""");
        var exchange = new FakeCmdlets();
        var executor = Executor(http, StoreWithGroup("dl1"), exchange);

        var removed = await executor.ApplyAsync(new SetGroupMembership(List, Ada, "user", GroupRole.Member, true, true, false), Ct);
        var owner = await executor.ApplyAsync(new SetGroupMembership(List, Bo, "user", GroupRole.Owner, true, false, true), Ct);

        Assert.Equal(ChangeOutcome.Applied, removed.Outcome);
        Assert.Equal(ChangeOutcome.Applied, owner.Outcome);
        Assert.Equal("Remove-DistributionGroupMember", exchange.Calls[0].Cmdlet);
        Assert.Contains("\"Member\":\"zz-tenantry-test-ada@contoso.example\"", exchange.Calls[0].Parameters);
        Assert.Contains("\"Confirm\":false", exchange.Calls[0].Parameters);
        Assert.Equal("Set-DistributionGroup", exchange.Calls[1].Cmdlet);
        Assert.Contains("\"ManagedBy\":{\"@odata.type\":\"#Exchange.GenericHashTable\",\"Add\":[\"zz-tenantry-test-bo@contoso.example\"]}", exchange.Calls[1].Parameters);
        Assert.DoesNotContain(http.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task Full_access_is_granted_then_read_back_into_the_local_copy()
    {
        var granted = false;
        var exchange = new FakeCmdlets((cmdlet, _) =>
        {
            if (cmdlet == "Add-MailboxPermission")
                granted = true;
            return cmdlet == "Get-MailboxPermission" && granted
                ? """[{"User":"zz-tenantry-test-ada@contoso.example","AccessRights":["FullAccess"],"IsInherited":false,"Deny":false}]"""
                : "[]";
        });
        var store = new MemoryEntityStore();

        var result = await Executor(new FakeHttp(), store, exchange).ApplyAsync(new SetMailboxAccess(Shared, Ada, MailboxRight.FullAccess, false, true), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Contains("\"AccessRights\":[\"FullAccess\"]", exchange.Calls[0].Parameters);
        Assert.Contains("\"InheritanceType\":\"All\"", exchange.Calls[0].Parameters);
        Assert.Contains("zz-tenantry-test-ada@contoso.example", store.Get(DirectorySync.MailboxAccess, "m1"));
    }

    [Fact]
    public async Task Send_on_behalf_removes_through_a_hashtable_and_matches_by_name()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-Mailbox"
            ? """[{"ExternalDirectoryObjectId":"m1","GrantSendOnBehalfTo":["zz-tenantry-test-bo"]}]"""
            : "[]");

        var result = await Executor(new FakeHttp(), new MemoryEntityStore(), exchange)
            .ApplyAsync(new SetMailboxAccess(Shared, Ada, MailboxRight.SendOnBehalf, true, false), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("Set-Mailbox", exchange.Calls[0].Cmdlet);
        Assert.Contains("\"Remove\":[\"zz-tenantry-test-ada@contoso.example\"]", exchange.Calls[0].Parameters);
    }

    [Fact]
    public async Task Exchange_errors_lose_their_internal_prefix_and_missing_recipients_are_explained()
    {
        var exchange = new ThrowingCmdlets(new Tenantry.Core.Exchange.CmdletException("Set-Mailbox", 400,
            "Ex94914C|Microsoft.Exchange.Configuration.Tasks.ManagementObjectNotFoundException|Couldn't find object \"zz-tenantry-test-ada@contoso.example\"."));

        var onBehalf = await Executor(new FakeHttp(), new MemoryEntityStore(), exchange.Runner)
            .ApplyAsync(new SetMailboxAccess(Shared, Ada, MailboxRight.SendOnBehalf, false, true), Ct);
        var member = await Executor(new FakeHttp(), new MemoryEntityStore(), exchange.Runner)
            .ApplyAsync(new SetGroupMembership(List, Ada, "user", GroupRole.Member, true, false, true), Ct);

        Assert.Contains("only be given to someone with a mailbox", onBehalf.Detail);
        Assert.StartsWith("Exchange couldn't find zz-tenantry-test-ada.", member.Detail);
        Assert.DoesNotContain("Ex94914C", member.Detail);
    }

    sealed class ThrowingCmdlets(Exception error)
    {
        public FakeCmdlets Runner { get; } = new((_, _) => throw error);
    }

    [Fact]
    public async Task Exchange_changes_without_exchange_fail_with_a_reason()
    {
        var result = await Executor(new FakeHttp(), new MemoryEntityStore()).ApplyAsync(new SetMailboxAccess(Shared, Ada, MailboxRight.SendAs, false, true), Ct);

        Assert.Equal(ChangeOutcome.Failed, result.Outcome);
        Assert.Contains("Exchange", result.Detail);
    }

    [Fact]
    public async Task Setting_a_manager_puts_a_reference_and_clearing_deletes_it()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Put, "/users/u1/manager/$ref", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            .Once(HttpMethod.Get, "/users/u1/manager?$select=id", """{"id":"u2"}""")
            .On(HttpMethod.Delete, "/users/u1/manager/$ref", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            .On(HttpMethod.Get, "/users/u1/manager?$select=id", """{"error":{"code":"Request_ResourceNotFound","message":"none"}}""", HttpStatusCode.NotFound);
        var executor = Executor(http, new MemoryEntityStore());

        var set = await executor.ApplyAsync(new SetManager(Ada, null, null, "u2", "Bo"), Ct);
        var cleared = await executor.ApplyAsync(new SetManager(Ada, "u2", "Bo", null, null), Ct);

        Assert.Equal(ChangeOutcome.Applied, set.Outcome);
        Assert.Equal(ChangeOutcome.Applied, cleared.Outcome);
    }

    [Fact]
    public async Task The_guard_refuses_adding_a_real_person_to_a_test_group()
    {
        var http = new FakeHttp();
        var pipeline = new ChangePipeline(Executor(http, new MemoryEntityStore()), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var real = new ChangeTarget("user", "r1", "A real person", "someone@contoso.example");

        pipeline.Stage(new SetGroupMembership(Group, real, "user", GroupRole.Member, false, false, true));
        var results = await pipeline.ApplyAsync(cancellationToken: Ct);

        Assert.Equal(ChangeOutcome.Refused, results.Single().Result.Outcome);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void Membership_changes_read_as_people_and_removals_need_a_second_look()
    {
        var remove = new SetGroupMembership(Group, Ada, "user", GroupRole.Owner, false, true, false);
        var add = new SetMailboxAccess(Shared, Bo, MailboxRight.SendAs, false, true);

        Assert.Equal("Owner zz-tenantry-test-ada: Owner → Removed", remove.Summary);
        Assert.True(remove.IsCaution);
        Assert.Equal("Send As for zz-tenantry-test-bo: Not Allowed → Allowed", add.Summary);
        Assert.False(add.IsCaution);
        Assert.NotEqual(new SetGroupMembership(Group, Ada, "user", GroupRole.Member, false, false, true).Key, remove.Key);
    }
}
