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

/// <summary>Mail contacts: read into the local copy, named on lists, made, changed and deleted through Exchange.</summary>
public sealed class ContactTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly UserSummary Ada = new("u1", "Ada Lovelace", "ada@contoso.example", "ada@contoso.example", null, null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, []);

    static readonly ContactSummary Avery = new("c1", "Avery Quinn", "avery@fabrikam.example", "Avery", "Quinn", "Fabrikam", Hidden: false, ListIds: ["dl1"]);

    static readonly ContactSummary Drew = new("c2", "Drew Okafor", "drew@woodgrove.example", SyncedFromOnPremises: true, Hidden: false);

    static readonly GroupSummary Partners = new("dl1", "Partner Updates", "partners@contoso.example", null, GroupKind.Distribution, false, false, null, null,
        [new GroupMember("c1", "orgContact"), new GroupMember("u1", "user")]);

    static readonly GroupSummary Finance = new("g1", "Finance", "finance@contoso.example", null, GroupKind.Microsoft365, false, false, null, null, []);

    static readonly GroupSummary Kiosks = new("sg1", "Kiosk Access", null, null, GroupKind.Security, false, false, null, null, []);

    sealed class Index : IDirectoryIndex
    {
        public UserSummary? User(string id) => AllUsers.FirstOrDefault(u => u.Id == id);
        public GroupSummary? Group(string id) => AllGroups.FirstOrDefault(g => g.Id == id);
        public IEnumerable<UserSummary> AllUsers => [Ada];
        public IEnumerable<GroupSummary> AllGroups => [Partners, Finance, Kiosks];
        public ContactSummary? Contact(string id) => AllContacts.FirstOrDefault(c => c.Id == id);
        public IEnumerable<ContactSummary> AllContacts => [Avery, Drew];
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static ChangePipeline Pipeline() => new(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");

    static void Pick(Tenantry.ViewModels.Editing.PersonPicker picker, string query)
    {
        picker.Query = query;
        picker.PickCommand.Execute(picker.Results.Single());
    }

    [Fact]
    public void A_contact_on_a_list_is_named_and_opens_its_page()
    {
        var row = GroupAccess.Describe(new GroupMember("c1", "orgContact"), new Index());

        Assert.Equal(("Avery Quinn", "avery@fabrikam.example · Fabrikam", "Contact"), (row.Name, row.Detail, row.TypeText));
        Assert.Same(Avery, row.Contact);
        Assert.True(row.CanOpen);
        // As a change target it's named by its outside address, which the write guard judges.
        Assert.Equal(new ChangeTarget("contact", "c1", "Avery Quinn", "avery@fabrikam.example"), GroupAccess.PersonTarget(new GroupMember("c1", "orgContact"), row));
    }

    [Fact]
    public void Lists_and_security_groups_take_contacts_but_Microsoft_365_groups_do_not()
    {
        var index = new Index();

        Assert.Contains(GroupAccess.MemberCandidates(Partners, index), o => o.Id == "c2" && o.Type == "orgContact");
        Assert.Contains(GroupAccess.MemberCandidates(Kiosks, index), o => o.Id == "c2");
        Assert.DoesNotContain(GroupAccess.MemberCandidates(Finance, index), o => o.Type == "orgContact");
    }

    [Fact]
    public async Task Contacts_read_with_their_phones_and_Exchange_says_where_mail_goes_and_whether_they_are_hidden()
    {
        var store = new MemoryEntityStore();
        store.Apply(DirectorySync.Contacts, [new EntityChange("c1", JsonNode.Parse("""
            {"id":"c1","displayName":"Avery Quinn","givenName":"Avery","mail":"avery@fabrikam.example","companyName":"Fabrikam","onPremisesSyncEnabled":true,
             "phones":[{"type":"businessFax","number":"+1 425 555 0100"},{"type":"business","number":"+1 425 555 0141"},{"type":"mobile","number":"+1 425 555 0199"}]}
            """)!.AsObject())]);
        store.Apply(DirectorySync.Contacts, [new EntityChange("c2", JsonNode.Parse("""{"id":"c2","givenName":"Sam","surname":"Patel"}""")!.AsObject())]);
        store.Apply(DirectorySync.MailContacts, [new EntityChange("c1", new JsonObject
        {
            ["ExternalDirectoryObjectId"] = "c1", ["HiddenFromAddressListsEnabled"] = true, ["ExternalEmailAddress"] = "SMTP:avery.q@fabrikam.example",
        })]);

        var contacts = (await new CachedTenantDirectory(store, "Contoso").GetContactsAsync(Ct)).ToDictionary(c => c.Id);

        Assert.Equal(("Avery Quinn", "avery.q@fabrikam.example", "+1 425 555 0141", "+1 425 555 0199", true, true),
            (contacts["c1"].DisplayName, contacts["c1"].Email, contacts["c1"].Phone, contacts["c1"].Mobile, contacts["c1"].Hidden, contacts["c1"].SyncedFromOnPremises));
        // No display name: named from its parts. Exchange not read yet: hidden is unknown.
        Assert.Equal(("Sam Patel", (bool?)null), (contacts["c2"].DisplayName, contacts["c2"].Hidden));
    }

    [Fact]
    public void New_contact_guesses_the_name_and_puts_it_on_its_list()
    {
        var pipeline = Pipeline();
        var form = new NewContactViewModel(pipeline, new Index(), () => { }, "sam.patel@tailspin.example", Partners);

        Assert.Equal(("Sam", "Patel", "Sam Patel"), (form.FirstName, form.LastName, form.DisplayName));
        Pick(form.ListPicker, "kiosk");
        form.Company = "Tailspin Toys";
        form.Hidden = true;
        form.AddCommand.Execute(null);

        var create = Assert.IsType<CreateContact>(Assert.Single(pipeline.Pending));
        Assert.Equal(new ChangeTarget("contact", "sam.patel@tailspin.example", "Sam Patel", "sam.patel@tailspin.example"), create.Contact);
        Assert.Equal(("Tailspin Toys", true), (create.Company, create.Hidden));
        Assert.Equal([("dl1", true), ("sg1", false)], create.Lists.Select(l => (l.Group.Id, l.ExchangeManaged)));
        // The write guard judges the new address and every list.
        Assert.Equal(["sam.patel@tailspin.example", "partners@contoso.example", "Kiosk Access"], create.Guarded.Select(t => t.Address ?? t.Name));
    }

    [Theory]
    [InlineData("avery@fabrikam.example", "already a contact")]
    [InlineData("ada@contoso.example", "belongs to someone")]
    [InlineData("not an address", "looks like name@example.com")]
    public void New_contact_refuses_addresses_it_cannot_use(string email, string problem)
    {
        var form = new NewContactViewModel(Pipeline(), new Index(), () => { }) { DisplayName = "Someone", Email = email };

        Assert.Contains(form.Problems, p => p.Contains(problem, StringComparison.Ordinal));
        Assert.False(form.AddCommand.CanExecute(null));
    }

    [Fact]
    public void A_contact_page_stages_its_details_address_list_and_lists()
    {
        var pipeline = Pipeline();
        using var page = new ContactDetailViewModel(Avery, new Index(), pipeline, Availability.Yes);
        var company = page.Details.SelectMany(g => g.Fields).Single(f => f.Label == "Company");

        company.EditCommand.Execute(null);
        company.Draft = "Fabrikam Ltd";
        company.StageCommand.Execute(null);
        page.ShowsInAddressList = false;
        Pick(page.ListPicker, "kiosk");

        Assert.True(company.HasPending);
        Assert.Contains(pipeline.Pending, c => c is SetContactText { Parameter: "Company", Old: "Fabrikam", New: "Fabrikam Ltd" });
        Assert.Contains(pipeline.Pending, c => c is SetHiddenFromAddressList { User.Kind: "contact", Old: false, New: true });
        var join = Assert.Single(pipeline.Pending.OfType<SetGroupMembership>());
        Assert.Equal(("sg1", "orgContact", false, true), (join.Group.Id, join.PersonType, join.ExchangeManaged, join.New));
        Assert.Equal(["Kiosk Access", "Partner Updates"], page.Lists.Select(l => l.Name).Order());
        Assert.Equal("Adding", page.Lists.Single(l => l.Name == "Kiosk Access").PendingText);
    }

    [Fact]
    public void A_contact_synced_from_AD_keeps_its_details_but_can_still_join_lists()
    {
        var pipeline = Pipeline();
        using var page = new ContactDetailViewModel(Drew, new Index(), pipeline, Availability.Yes);

        Assert.False(page.CanEdit);
        Assert.All(page.Details.SelectMany(g => g.Fields), f => Assert.False(f.CanEdit));
        Assert.True(page.CanEditLists);
        page.DeleteCommand.Execute(null);
        Assert.False(page.HasPendingDelete);
    }

    [Fact]
    public async Task Details_go_to_Set_MailContact_or_Set_Contact_and_hiding_uses_the_contact()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet switch
        {
            "Get-Contact" => """[{"Company":"Fabrikam Ltd"}]""",
            "Get-MailContact" => """[{"DisplayName":"Avery Q","HiddenFromAddressListsEnabled":true,"ExternalDirectoryObjectId":"c1"}]""",
            _ => "[]",
        });
        var http = new FakeHttp().On(HttpMethod.Get, "/contacts/c1", """{"id":"c1","displayName":"Avery Q"}""");
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);
        var target = GroupAccess.Target(Avery);

        Assert.Equal(ChangeOutcome.Applied, (await executor.ApplyAsync(new SetContactText(target, "Company", "Company", "Fabrikam", "Fabrikam Ltd"), Ct)).Outcome);
        Assert.Equal(ChangeOutcome.Applied, (await executor.ApplyAsync(new SetContactText(target, "DisplayName", "Display Name", "Avery Quinn", "Avery Q"), Ct)).Outcome);
        Assert.Equal(ChangeOutcome.Applied, (await executor.ApplyAsync(new SetHiddenFromAddressList(target, false, true), Ct)).Outcome);

        var sets = exchange.Calls.Where(c => c.Cmdlet.StartsWith("Set-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["Set-Contact", "Set-MailContact", "Set-MailContact"], sets.Select(c => c.Cmdlet));
        Assert.Contains("\"Company\":\"Fabrikam Ltd\"", sets[0].Parameters, StringComparison.Ordinal);
        Assert.Contains("\"HiddenFromAddressListsEnabled\":true", sets[2].Parameters, StringComparison.Ordinal);
        Assert.DoesNotContain(exchange.Calls, c => c.Cmdlet == "Set-Mailbox");
    }

    [Fact]
    public async Task A_new_contact_is_made_then_put_on_its_lists_by_address_or_Entra_ID()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-MailContact"
            ? """[{"ExternalDirectoryObjectId":"nc1","HiddenFromAddressListsEnabled":true}]"""
            : "[]");
        var http = new FakeHttp()
            .On(HttpMethod.Post, "/groups/sg1/members/$ref", "")
            .On(HttpMethod.Get, "/contacts/nc1", """{"id":"nc1","displayName":"Sam Patel","mail":"sam@tailspin.example"}""")
            .On(HttpMethod.Get, "/groups/", """{"value":[]}""");
        var store = new MemoryEntityStore();
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store, NoDelay.Instance, exchange);

        var result = await executor.ApplyAsync(new CreateContact(new ChangeTarget("contact", "sam@tailspin.example", "Sam Patel", "sam@tailspin.example"),
            "Sam", "Patel", "Tailspin Toys", true,
            [new NewUserGroup(GroupAccess.Target(Partners), true), new NewUserGroup(GroupAccess.Target(Kiosks), false)]), Ct);

        Assert.Equal((ChangeOutcome.Applied, null), (result.Outcome, result.Detail));
        Assert.Equal(["New-MailContact", "Get-MailContact", "Set-Contact", "Set-MailContact", "Add-DistributionGroupMember"],
            exchange.Calls.Select(c => c.Cmdlet).Take(5));
        Assert.Contains("\"Member\":\"sam@tailspin.example\"", exchange.Calls.Single(c => c.Cmdlet == "Add-DistributionGroupMember").Parameters, StringComparison.Ordinal);
        Assert.Contains(http.Requests, r => r.Method == HttpMethod.Post && r.PathAndQuery.Contains("/groups/sg1/members/$ref", StringComparison.Ordinal)
                                             && r.Json!["@odata.id"]!.GetValue<string>().EndsWith("/nc1", StringComparison.Ordinal));
        Assert.NotNull(store.Get(DirectorySync.Contacts, "nc1"));
    }

    [Fact]
    public async Task The_demo_makes_and_deletes_contacts_on_its_lists()
    {
        var directory = new Tenantry.Demo.DemoTenantDirectory();
        var executor = new Tenantry.Demo.DemoChangeExecutor(directory);
        var partners = (await directory.GetGroupsAsync(Ct)).Single(g => g.Id == Tenantry.Demo.DemoTenantDirectory.PartnersListId);

        await executor.ApplyAsync(new CreateContact(new ChangeTarget("contact", "sam@tailspin.example", "Sam Patel", "sam@tailspin.example"), null, null, null, false,
            [new NewUserGroup(GroupAccess.Target(partners), true)]), Ct);
        var sam = (await directory.GetContactsAsync(Ct)).Single(c => c.Email == "sam@tailspin.example");
        Assert.Contains((await directory.GetGroupsAsync(Ct)).Single(g => g.Id == partners.Id).Members, m => m.Id == sam.Id && m.Type == "orgContact");

        await executor.ApplyAsync(new DeleteContact(GroupAccess.Target(sam), ""), Ct);
        Assert.DoesNotContain(await directory.GetContactsAsync(Ct), c => c.Id == sam.Id);
        Assert.DoesNotContain((await directory.GetGroupsAsync(Ct)).Single(g => g.Id == partners.Id).Members, m => m.Id == sam.Id);
    }
}
