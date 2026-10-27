using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>CSV import: the reader, the template, and what each row plans to do.</summary>
public sealed class ImportTests
{
    static readonly UserSummary Ada = new("u1", "Ada Lovelace", "ada@contoso.example", "ada@contoso.example", "Finance", "Analyst", true, UserKind.Member,
        DateTimeOffset.UnixEpoch, null, ["Business Basic"]);
    static readonly UserSummary Grace = new("u2", "Grace Hopper", "grace@contoso.example", "grace@contoso.example", "IT", "Lead", true, UserKind.Member,
        DateTimeOffset.UnixEpoch, null, []);
    static readonly GroupSummary Finance = new("g1", "Finance", "finance@contoso.example", null, GroupKind.Microsoft365, false, false, null, null, [new GroupMember("u1", "user")]);
    static readonly GroupSummary Board = new("g2", "Board", "board@contoso.example", null, GroupKind.Distribution, false, false, null, null, []);
    static readonly GroupSummary Everyone = new("g3", "Everyone", null, null, GroupKind.Security, true, false, null, null, []);

    sealed class Index : IDirectoryIndex
    {
        public UserSummary? User(string id) => AllUsers.FirstOrDefault(u => u.Id == id);
        public GroupSummary? Group(string id) => AllGroups.FirstOrDefault(g => g.Id == id);
        public IEnumerable<UserSummary> AllUsers => [Ada, Grace];
        public IEnumerable<GroupSummary> AllGroups => [Finance, Board, Everyone];
    }

    sealed class Records : IUserRecords
    {
        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserDetails(userId, new Dictionary<string, string?> { ["officeLocation"] = "Floor 1", ["usageLocation"] = "US" },
                userId == "u1" ? "u2" : null, userId == "u1" ? "Grace Hopper" : null, null, null));

        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["contoso.example"]);

        public Task<IReadOnlyList<LicenseSku>> GetLicenseCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LicenseSku>>([new("sku-b", "Business Basic", 10, 9, []), new("sku-p", "Business Premium", 10, 8, [])]);

        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailDetails> GetMailAsync(string userId, string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static ChangePipeline Pipeline() => new(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");

    [Fact]
    public void Csv_reads_quotes_commas_line_breaks_and_semicolon_files()
    {
        var rows = Csv.Read("﻿Name,Note\r\n\"Lee, Pat\",\"said \"\"hi\"\"\nthen left\"\r\n\r\nBo,\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Lee, Pat", "said \"hi\"\nthen left"], rows[1]);
        Assert.Equal(["Bo", ""], rows[2]);
        Assert.Equal(["a", "b"], Csv.Read("x;y\na;b")[1]);
        Assert.Equal("\"Lee, Pat\",x\r\n", Csv.Write([["Lee, Pat", "x"]]));
    }

    static async Task<ImportUsersViewModel> Import(string csv, ChangePipeline? pipeline = null)
    {
        var import = new ImportUsersViewModel(pipeline ?? Pipeline(), new Records(), new Index(), () => { });
        await import.LoadAsync("people.csv", csv);
        return import;
    }

    [Fact]
    public async Task Columns_outside_the_template_are_refused()
    {
        var import = await Import("Sign-In Name,Favourite Colour\nada@contoso.example,blue");
        Assert.Contains("Favourite Colour", import.FileProblem);
        Assert.Empty(import.Rows);
        Assert.Contains("needs a \"Sign-In Name\" column", (await Import("Department\nFinance")).FileProblem);
    }

    [Fact]
    public async Task A_new_person_is_created_with_their_licenses_groups_and_manager()
    {
        var import = await Import("""
            Sign-In Name,First Name,Last Name,Job Title,Licenses,Groups,Manager,Country,Must Change Password
            pat.lee@contoso.example,Pat,Lee,Clerk,Business Premium,Finance; board@contoso.example,grace@contoso.example,GB,No
            """);

        var row = Assert.Single(import.Rows);
        Assert.Equal((ImportOutcome.New, "Pat Lee"), (row.Outcome, row.Name));
        var create = Assert.IsType<CreateUser>(Assert.Single(row.Ops));
        Assert.Equal(("pat.lee", "GB", false, "u2", "Clerk"), (create.MailNickname, create.UsageLocation, create.MustChangePassword, create.ManagerId, create.Details["jobTitle"]));
        Assert.Equal(["Business Premium"], create.Licenses.Select(l => l.Name));
        Assert.Equal([("g1", false), ("g2", true)], create.Groups.Select(g => (g.Group.Id, g.ExchangeManaged)));
        Assert.Null(create.Chosen);
    }

    [Fact]
    public async Task An_existing_person_gets_only_what_differs_and_nothing_is_removed()
    {
        var import = await Import("""
            Sign-In Name,Department,Job Title,Office,Licenses,Groups,Manager
            ada@contoso.example,Operations,Analyst,Floor 1,Business Basic; Business Premium,Finance;Board,grace@contoso.example
            grace@contoso.example,IT,,,,,
            """);

        var ada = import.Rows[0];
        Assert.Equal(ImportOutcome.Update, ada.Outcome);
        Assert.Equal(["Department: Finance → Operations", "License: add Business Premium", "Join Board"], ada.Changes);
        Assert.Contains(ada.Ops, o => o is SetUserText { Property: "department", New: "Operations" });
        Assert.Contains(ada.Ops, o => o is SetGroupMembership { Group.Id: "g2", ExchangeManaged: true });
        Assert.Equal(ImportOutcome.NoChange, import.Rows[1].Outcome);
        Assert.Equal("Add 3 Changes to Pending Changes", import.AddText);
    }

    [Fact]
    public async Task Problems_are_named_and_those_rows_are_left_out()
    {
        var import = await Import("""
            Sign-In Name,Display Name,Licenses,Groups,Manager,Password,Sign-In Allowed,Country
            new1@elsewhere.example,New One,,,,,,
            new2@contoso.example,New Two,Gold Plan,,,,,
            new3@contoso.example,New Three,,Everyone,,,,
            new4@contoso.example,New Four,,,nobody@contoso.example,,,
            new5@contoso.example,New Five,,,,short,,
            new6@contoso.example,New Six,,,,,maybe,
            new7@contoso.example,New Seven,,,,,,Narnia
            new7@contoso.example,New Seven Again,,,,,,
            """);

        string Problem(int i) => Assert.Single(import.Rows[i].Problems);
        Assert.Contains("isn't one of this tenant's domains", Problem(0));
        Assert.Contains("no license called \"Gold Plan\"", Problem(1));
        Assert.Contains("picks its members by a rule", Problem(2));
        Assert.Contains("isn't anyone in the directory", Problem(3));
        Assert.StartsWith("Password:", Problem(4));
        Assert.Contains("Yes or No", Problem(5));
        Assert.Contains("Narnia", Problem(6));
        Assert.Contains("more than once", Problem(7));
        Assert.All(import.Rows, r => Assert.Equal(ImportOutcome.Problem, r.Outcome));
        Assert.All(import.Rows, r => Assert.Empty(r.Ops));
        Assert.Equal("Nothing to Add", import.AddText);
    }

    [Fact]
    public async Task Seats_are_counted_across_the_whole_file()
    {
        var import = await Import("""
            Sign-In Name,Display Name,Licenses
            a@contoso.example,A,Business Basic
            b@contoso.example,B,Business Basic
            """);
        Assert.Equal(ImportOutcome.New, import.Rows[0].Outcome);
        Assert.Contains("No Business Basic licenses are left", Assert.Single(import.Rows[1].Problems));
    }

    [Fact]
    public async Task A_password_in_the_file_is_used_and_resets_an_existing_person()
    {
        var import = await Import("""
            Sign-In Name,Display Name,Password
            pat@contoso.example,Pat,Str0ng!Passw0rd
            ada@contoso.example,,Str0ng!Passw0rd
            """);
        Assert.Equal("Str0ng!Passw0rd", Assert.IsType<CreateUser>(Assert.Single(import.Rows[0].Ops)).Chosen);
        Assert.Equal("Str0ng!Passw0rd", Assert.IsType<ResetPassword>(Assert.Single(import.Rows[1].Ops)).Chosen);
    }

    [Fact]
    public async Task Adding_stages_every_ready_change_at_once_and_closes()
    {
        var pipeline = Pipeline();
        var closed = false;
        var import = new ImportUsersViewModel(pipeline, new Records(), new Index(), () => closed = true);
        await import.LoadAsync("people.csv", "Sign-In Name,Display Name,Department\npat@contoso.example,Pat,\nada@contoso.example,,Operations\nbad,,");
        import.AddCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal(2, pipeline.Pending.Count);
    }

    [Fact]
    public void Members_from_a_file_are_added_once_and_problems_named()
    {
        var pipeline = Pipeline();
        var import = new ImportMembersViewModel(Board, pipeline, new Index(), () => { });
        import.Load("board.csv", "Sign-In Name\nada@contoso.example\ngrace@contoso.example\nada@contoso.example\nnobody@contoso.example\n");

        Assert.Equal([ImportOutcome.Update, ImportOutcome.Update, ImportOutcome.NoChange, ImportOutcome.Problem], import.Rows.Select(r => r.Outcome));
        Assert.Equal("Add 2 Members to Pending Changes", import.AddText);
        import.AddCommand.Execute(null);
        Assert.All(pipeline.Pending, c => Assert.True(c is SetGroupMembership { New: true, ExchangeManaged: true }));
    }
}
