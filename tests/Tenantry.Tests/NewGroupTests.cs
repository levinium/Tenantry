using System.Net;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>New Group: the four kinds, their settings and people, and how each is made.</summary>
public sealed class NewGroupTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly UserSummary Ada = new("u1", "Ada Lovelace", "ada@contoso.example", "ada@contoso.example", null, null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, []);
    static readonly UserSummary Bo = new("u2", "Bo Diddley", "bo@contoso.example", "bo@contoso.example", null, null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, []);
    static readonly UserSummary Cy = new("u3", "Cy Twombly", "cy@contoso.example", null, null, null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, []);
    static readonly GroupSummary Finance = new("g1", "Finance", "finance@contoso.example", null, GroupKind.Microsoft365, false, false, null, null, []);

    sealed class Index : IDirectoryIndex
    {
        public UserSummary? User(string id) => AllUsers.FirstOrDefault(u => u.Id == id);
        public GroupSummary? Group(string id) => AllGroups.FirstOrDefault(g => g.Id == id);
        public IEnumerable<UserSummary> AllUsers => [Ada, Bo, Cy];
        public IEnumerable<GroupSummary> AllGroups => [Finance];
    }

    sealed class Records : IUserRecords
    {
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

    static async Task<NewGroupViewModel> Form(ChangePipeline pipeline)
    {
        var form = new NewGroupViewModel(pipeline, new Records(), new Index(), () => { });
        await Task.Yield();
        return form;
    }

    static void Pick(Tenantry.ViewModels.Editing.PersonPicker picker, string query)
    {
        picker.Query = query;
        picker.PickCommand.Execute(picker.Results.Single());
    }

    [Theory]
    [InlineData("Finance & Accounts", "finance-accounts")]
    [InlineData("  Front Desk / Reception ", "front-desk-reception")]
    [InlineData("Café", "cafe")]
    public void Aliases_are_made_from_the_name(string name, string expected) => Assert.Equal(expected, NewGroupViewModel.AliasFor(name));

    [Fact]
    public async Task A_shared_mailbox_gets_an_address_and_people_with_access_but_no_owners()
    {
        var pipeline = Pipeline();
        var form = await Form(pipeline);
        form.Kind = NewGroupViewModel.Kinds.Single(k => k.Kind == GroupKind.SharedMailbox);
        form.Name = "Front Desk";
        Pick(form.MemberPicker, "ada");

        Assert.False(form.HasOwners);
        Assert.Equal("front-desk@contoso.example", form.Address);
        form.AddCommand.Execute(null);

        var create = Assert.IsType<CreateGroup>(Assert.Single(pipeline.Pending));
        Assert.Equal((GroupKind.SharedMailbox, "front-desk@contoso.example", "u1"), (create.Kind, create.Group.Address, Assert.Single(create.Members).Id));
        Assert.Empty(create.Owners);
    }

    [Fact]
    public async Task Names_and_addresses_already_used_are_refused()
    {
        var form = await Form(Pipeline());
        form.Name = "finance";
        Assert.Contains(form.Problems, p => p.Contains("already a group", StringComparison.Ordinal));
        form.Kind = NewGroupViewModel.Kinds.Single(k => k.Kind == GroupKind.Distribution);
        form.Name = "Billing";
        form.Alias = "ada";
        Assert.Contains(form.Problems, p => p.Contains("already in use", StringComparison.Ordinal));
        Assert.False(form.AddCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_distribution_list_only_takes_people_with_an_email_address()
    {
        var form = await Form(Pipeline());
        form.Name = "Board";
        Pick(form.OwnerPicker, "cy");
        Assert.DoesNotContain(form.Problems, p => p.Contains("no email address", StringComparison.Ordinal));

        form.Kind = NewGroupViewModel.Kinds.Single(k => k.Kind == GroupKind.Distribution);
        Assert.Contains("Cy Twombly has no email address, so Exchange can't add them to a distribution list.", form.Problems);
        form.MemberPicker.Query = "cy";
        Assert.Empty(form.MemberPicker.Results);
        form.MemberPicker.Query = "bo";
        Assert.Single(form.MemberPicker.Results);
    }

    [Fact]
    public async Task A_dynamic_security_group_needs_a_rule_and_takes_no_members()
    {
        var pipeline = Pipeline();
        var form = await Form(pipeline);
        form.Kind = NewGroupViewModel.Kinds.Single(k => k.Kind == GroupKind.Security);
        form.Name = "All Finance";
        Pick(form.MemberPicker, "bo");
        form.IsDynamic = true;
        Assert.False(form.ShowMembers);
        Assert.Contains(form.Problems, p => p.Contains("rule", StringComparison.Ordinal));
        form.MembershipRule = "(user.department -eq \"Finance\")";
        form.AddCommand.Execute(null);

        var create = Assert.IsType<CreateGroup>(Assert.Single(pipeline.Pending));
        Assert.Equal("(user.department -eq \"Finance\")", create.MembershipRule);
        Assert.Empty(create.Members);
        Assert.Null(create.Group.Address);
    }

    [Fact]
    public async Task A_distribution_list_can_also_be_a_security_group()
    {
        var pipeline = Pipeline();
        var form = await Form(pipeline);
        form.Kind = NewGroupViewModel.Kinds.Single(k => k.Kind == GroupKind.Distribution);
        form.Name = "Board";
        form.AlsoSecurity = true;
        form.OutsideSenders = true;
        Pick(form.OwnerPicker, "ada");
        form.AddCommand.Execute(null);

        var create = Assert.IsType<CreateGroup>(Assert.Single(pipeline.Pending));
        Assert.Equal((GroupKind.MailEnabledSecurity, true, "u1"), (create.Kind, create.OutsideSenders, Assert.Single(create.Owners).Id));
    }

    static ChangeTarget Person(UserSummary u) => new("user", u.Id, u.DisplayName, u.UserPrincipalName);

    [Fact]
    public async Task A_Microsoft_365_group_is_made_in_one_request_with_its_people()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "/v1.0/groups", """{"id":"ng1"}""")
            .On(HttpMethod.Get, "/groups/ng1?", """{"id":"ng1","displayName":"Team","groupTypes":["Unified"],"mailEnabled":true,"securityEnabled":false}""")
            .On(HttpMethod.Get, "/groups/ng1/members", """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u2"}]}""")
            .On(HttpMethod.Get, "/groups/ng1/owners", """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u1"}]}""");
        var store = new MemoryEntityStore();
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store, NoDelay.Instance);

        var result = await executor.ApplyAsync(new CreateGroup(new ChangeTarget("group", "team", "Team", null), GroupKind.Microsoft365, "team", "Our team", null,
            true, false, [Person(Ada)], [Person(Bo)]), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var body = http.Requests.First(r => r.Method == HttpMethod.Post).Json!;
        Assert.Equal(("Unified", "Private", true), (body["groupTypes"]![0]!.GetValue<string>(), body["visibility"]!.GetValue<string>(), body["mailEnabled"]!.GetValue<bool>()));
        Assert.EndsWith("/users/u1", body["owners@odata.bind"]![0]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.EndsWith("/users/u2", body["members@odata.bind"]![0]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotNull(store.Get(DirectorySync.Groups, "ng1"));
    }

    [Fact]
    public async Task A_shared_mailbox_is_made_in_Exchange_and_its_people_get_access_once_it_exists()
    {
        var grants = 0;
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet switch
        {
            "New-Mailbox" => """[{"ExternalDirectoryObjectId":"acct1","PrimarySmtpAddress":"desk@contoso.example"}]""",
            // The brand-new mailbox isn't found the first time.
            "Add-MailboxPermission" when grants++ == 0 => throw new Tenantry.Core.Exchange.CmdletException("Add-MailboxPermission", 404,
                "Ex|Microsoft.Exchange.Configuration.Tasks.ManagementObjectNotFoundException|The operation couldn't be performed because object 'desk' couldn't be found."),
            _ => "[]",
        });
        var http = new FakeHttp().On(HttpMethod.Get, "/users/acct1", """{"error":{"code":"Request_ResourceNotFound","message":"not yet"}}""", HttpStatusCode.NotFound);
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);

        var result = await executor.ApplyAsync(new CreateGroup(new ChangeTarget("group", "desk", "Front Desk", "desk@contoso.example"), GroupKind.SharedMailbox, "desk",
            null, null, false, false, [], [Person(Ada)]), Ct);

        Assert.Equal((ChangeOutcome.Applied, null), (result.Outcome, result.Detail));
        Assert.Contains(exchange.Calls, c => c.Cmdlet == "New-Mailbox" && c.Parameters.Contains("\"Shared\":true"));
        Assert.Equal(2, exchange.Calls.Count(c => c.Cmdlet == "Add-MailboxPermission"));
        Assert.Contains(exchange.Calls, c => c.Cmdlet == "Add-RecipientPermission");
    }

    [Fact]
    public async Task A_distribution_list_is_made_in_Exchange_with_members_and_owners()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "New-DistributionGroup" ? """[{"ExternalDirectoryObjectId":"dl1"}]""" : "[]");
        var http = new FakeHttp()
            .On(HttpMethod.Get, "/groups/dl1?", """{"id":"dl1","displayName":"Board","mail":"board@contoso.example","mailEnabled":true,"securityEnabled":false,"groupTypes":[]}""")
            .On(HttpMethod.Get, "/groups/dl1/members", """{"value":[]}""")
            .On(HttpMethod.Get, "/groups/dl1/owners", """{"value":[]}""");
        var store = new MemoryEntityStore();
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store, NoDelay.Instance, exchange);

        var result = await executor.ApplyAsync(new CreateGroup(new ChangeTarget("group", "board", "Board", "board@contoso.example"), GroupKind.Distribution, "board",
            "The board", null, false, false, [Person(Ada)], [Person(Bo)]), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        var call = Assert.Single(exchange.Calls, c => c.Cmdlet == "New-DistributionGroup");
        Assert.Contains("\"RequireSenderAuthenticationEnabled\":true", call.Parameters);
        Assert.Contains("bo@contoso.example", call.Parameters);
        Assert.Contains("\"ManagedBy\":[\"ada@contoso.example\"]", call.Parameters);
        Assert.NotNull(store.Get(DirectorySync.Groups, "dl1"));
    }
}
