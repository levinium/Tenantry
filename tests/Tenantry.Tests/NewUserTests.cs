using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>New User: the form fills itself sensibly, copies a colleague, refuses what Entra would, and creates the account with its extras.</summary>
public sealed class NewUserTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly UserSummary Ada = new("u1", "Ada Lovelace", "ada@contoso.example", "ada@contoso.example", "Finance", "Analyst", true, UserKind.Member,
        DateTimeOffset.UnixEpoch, null, ["Microsoft 365 Business Premium"]);

    static readonly GroupSummary FinanceTeam = new("g1", "Finance", "finance@contoso.example", null, GroupKind.Microsoft365, false, true, null, null, [new GroupMember("u1", "user")]);
    static readonly GroupSummary Auditors = new("g2", "Auditors", "auditors@contoso.example", null, GroupKind.Distribution, false, false, null, null, [new GroupMember("u1", "user")]);
    static readonly GroupSummary Everyone = new("g3", "Everyone (dynamic)", null, null, GroupKind.Security, true, false, null, null, [new GroupMember("u1", "user")]);

    sealed class Index : IDirectoryIndex
    {
        public UserSummary? User(string id) => AllUsers.FirstOrDefault(u => u.Id == id);
        public GroupSummary? Group(string id) => AllGroups.FirstOrDefault(g => g.Id == id);
        public IEnumerable<UserSummary> AllUsers => [Ada];
        public IEnumerable<GroupSummary> AllGroups => [FinanceTeam, Auditors, Everyone];
    }

    sealed class Records : IUserRecords
    {
        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserDetails(userId, new Dictionary<string, string?> { ["officeLocation"] = "Floor 3", ["usageLocation"] = "GB" }, "m1", "Grace Hopper", null, null));

        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["contoso.example", "contoso.onmicrosoft.example"]);

        public Task<IReadOnlyList<LicenseSku>> GetLicenseCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LicenseSku>>([new("sku-bp", "Microsoft 365 Business Premium", 10, 9, []), new("sku-e1", "Exchange Online (Plan 1)", 5, 5, [])]);

        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailDetails> GetMailAsync(string userId, string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    static ChangePipeline Pipeline() => new(new NothingExecutor(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");

    sealed class NothingExecutor : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static async Task<NewUserViewModel> Form(ChangePipeline pipeline, Action? close = null)
    {
        var form = new NewUserViewModel(pipeline, new Records(), new Index(), close ?? (() => { }));
        await Task.Yield();
        return form;
    }

    [Theory]
    [InlineData("Zoë", "O'Brien", "zoe.obrien")]
    [InlineData("Ada", "", "ada")]
    [InlineData("  Jean-Luc ", "Picard", "jeanluc.picard")]
    public void Sign_in_names_are_made_from_the_name(string first, string last, string expected) =>
        Assert.Equal(expected, NewUserViewModel.AliasFor(first, last));

    [Fact]
    public async Task Typing_a_name_fills_the_display_and_sign_in_names_until_they_are_edited()
    {
        var form = await Form(Pipeline());
        form.FirstName = "Pat";
        form.LastName = "Lee";
        Assert.Equal(("Pat Lee", "pat.lee", "pat.lee@contoso.example"), (form.DisplayName, form.Alias, form.SignInName));

        form.Alias = "plee";
        form.LastName = "Leeson";
        Assert.Equal(("Pat Leeson", "plee"), (form.DisplayName, form.Alias));
        Assert.Empty(form.Problems);
    }

    [Fact]
    public async Task What_Entra_would_refuse_is_said_first()
    {
        var form = await Form(Pipeline());
        form.FirstName = "Ada";
        form.LastName = "Lovelace";
        form.Alias = "ada";
        Assert.Contains(form.Problems, p => p.Contains("already someone's sign-in name", StringComparison.Ordinal));
        form.Alias = ".bad";
        Assert.Contains(form.Problems, p => p.Contains("can't start or end with a dot", StringComparison.Ordinal));
        form.Alias = "ada2";
        form.TypeOwn = true;
        Assert.Contains(form.Problems, p => p.Contains("Type a password", StringComparison.Ordinal));
        form.Password = "Str0ng!Passw0rd";
        form.Licenses.Single(l => l.Name.StartsWith("Exchange", StringComparison.Ordinal)).IsChosen = true;
        Assert.Contains(form.Problems, p => p.Contains("licenses are left", StringComparison.Ordinal));
        Assert.False(form.AddCommand.CanExecute(null));
    }

    [Fact]
    public async Task Copying_a_colleague_brings_their_department_manager_groups_and_licenses()
    {
        var form = await Form(Pipeline());
        form.CopyFrom.Query = "ada";
        form.CopyFrom.PickCommand.Execute(form.CopyFrom.Results.Single());
        await Task.Yield();

        Assert.Equal(("Finance", "Analyst", "Floor 3", "Grace Hopper", "GB"), (form.Department, form.JobTitle, form.Office, form.ManagerName, form.UsageLocation.Value));
        Assert.Equal(["Auditors", "Finance"], form.Groups.Select(g => g.Name).Order(StringComparer.Ordinal));
        Assert.True(form.Licenses.Single(l => l.Name == "Microsoft 365 Business Premium").IsChosen);
        Assert.Equal("Ada Lovelace", form.CopiedFrom);
    }

    [Fact]
    public async Task Adding_stages_one_change_and_closes()
    {
        var pipeline = Pipeline();
        var closed = false;
        var form = await Form(pipeline, () => closed = true);
        form.FirstName = "Pat";
        form.LastName = "Lee";
        form.JobTitle = "Clerk";
        form.Licenses.Single(l => l.Name == "Microsoft 365 Business Premium").IsChosen = true;
        form.GroupPicker.Query = "audit";
        form.GroupPicker.PickCommand.Execute(form.GroupPicker.Results.Single());

        form.AddCommand.Execute(null);

        var create = Assert.IsType<CreateUser>(Assert.Single(pipeline.Pending));
        Assert.True(closed);
        Assert.Equal(("pat.lee@contoso.example", "Pat Lee", "pat.lee"), (create.User.Address, create.User.Name, create.MailNickname));
        Assert.Equal("Clerk", create.Details["jobTitle"]);
        Assert.True(Assert.Single(create.Groups).ExchangeManaged);
        Assert.Null(create.Chosen);
        Assert.Contains("Microsoft 365 Business Premium", create.After);
    }

    [Fact]
    public async Task Test_mode_refuses_a_real_name_and_a_real_group()
    {
        var pipeline = new ChangePipeline(new NothingExecutor(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var test = new ChangeTarget("user", "zz-tenantry-test-new@contoso.example", "New", "zz-tenantry-test-new@contoso.example");
        pipeline.Stage(new CreateUser(test, "", "", "x", "US", true, true, [], [new NewUserGroup(new ChangeTarget("group", "g1", "Finance", "finance@contoso.example"), false)],
            null, null, new Dictionary<string, string>()));

        Assert.Equal(ChangeOutcome.Refused, Assert.Single(await pipeline.ApplyAsync(cancellationToken: Ct)).Result.Outcome);
    }

    [Fact]
    public async Task The_account_is_made_then_licensed_managed_and_added_to_groups()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "/v1.0/users", """{"id":"new1"}""")
            .On(HttpMethod.Post, "/users/new1/assignLicense", """{"id":"new1"}""")
            .On(HttpMethod.Get, "/subscribedSkus", """{"value":[]}""")
            .On(HttpMethod.Put, "/users/new1/manager/$ref", "{}")
            .On(HttpMethod.Post, "/groups/g1/members/$ref", "{}")
            .On(HttpMethod.Get, "/users/new1?", """{"id":"new1","displayName":"Pat Lee","userPrincipalName":"pat@contoso.example","accountEnabled":true,"assignedLicenses":[]}""")
            .On(HttpMethod.Get, "userRegistrationDetails/new1", """{"error":{"code":"NotFound","message":"not yet"}}""", System.Net.HttpStatusCode.NotFound);
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet is "Get-Mailbox" or "Get-CASMailbox"
            ? throw new Tenantry.Core.Exchange.CmdletException(cmdlet, 404, "Ex|ManagementObjectNotFoundException|The operation couldn't be performed because object couldn't be found.")
            : "[]");
        var store = new MemoryEntityStore();
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store, NoDelay.Instance, exchange);
        var create = new CreateUser(new ChangeTarget("user", "pat@contoso.example", "Pat Lee", "pat@contoso.example"), "Pat", "Lee", "pat", "US", true, true,
            [new NewUserLicense("sku-bp", "Business Premium")],
            [new NewUserGroup(new ChangeTarget("group", "g1", "Finance", null), false), new NewUserGroup(new ChangeTarget("group", "g2", "Auditors", null), true)],
            "m1", "Grace", new Dictionary<string, string> { ["jobTitle"] = "Clerk" });

        var result = await executor.ApplyAsync(create, Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Reveal));
        var made = http.Requests.First(r => r.Method == HttpMethod.Post && r.PathAndQuery.EndsWith("/users", StringComparison.Ordinal)).Json!;
        Assert.Equal(("pat@contoso.example", "US", "Clerk", true), (made["userPrincipalName"]!.GetValue<string>(), made["usageLocation"]!.GetValue<string>(),
            made["jobTitle"]!.GetValue<string>(), made["passwordProfile"]!["forceChangePasswordNextSignIn"]!.GetValue<bool>()));
        Assert.Contains(http.Requests, r => r.PathAndQuery.Contains("/manager/$ref", StringComparison.Ordinal));
        Assert.Contains(exchange.Calls, c => c.Cmdlet == "Add-DistributionGroupMember");
        Assert.NotNull(store.Get(DirectorySync.Users, "new1"));
    }

    [Fact]
    public async Task A_part_that_fails_after_the_account_is_made_is_named()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "/v1.0/users", """{"id":"new1"}""")
            .On(HttpMethod.Post, "/groups/g1/members/$ref", """{"error":{"code":"Request_BadRequest","message":"One or more added object references already exist"}}""", System.Net.HttpStatusCode.BadRequest)
            .On(HttpMethod.Get, "/users/new1?", """{"id":"new1"}""")
            .On(HttpMethod.Get, "userRegistrationDetails/new1", """{"error":{"code":"NotFound","message":"not yet"}}""", System.Net.HttpStatusCode.NotFound);
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);
        var create = new CreateUser(new ChangeTarget("user", "pat@contoso.example", "Pat Lee", "pat@contoso.example"), "", "", "pat", "US", true, true, [],
            [new NewUserGroup(new ChangeTarget("group", "g1", "Finance", null), false)], null, null, new Dictionary<string, string>(), Chosen: "Typed!Passw0rd");

        var result = await executor.ApplyAsync(create, Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Null(result.Reveal);
        Assert.Contains("Finance", result.Detail);
    }
}
