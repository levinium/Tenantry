using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Workflows;

namespace Tenantry.Tests;

/// <summary>The offboarding workflow (4d-2): what it plans for each person, in what order, and what it leaves out.</summary>
public sealed class OffboardingTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Index(IReadOnlyList<UserSummary> users, IReadOnlyList<GroupSummary> groups) : IDirectoryIndex
    {
        readonly Dictionary<string, UserSummary> _users = users.ToDictionary(u => u.Id);
        readonly Dictionary<string, GroupSummary> _groups = groups.ToDictionary(g => g.Id);
        public UserSummary? User(string id) => _users.GetValueOrDefault(id);
        public GroupSummary? Group(string id) => _groups.GetValueOrDefault(id);
        public IEnumerable<UserSummary> AllUsers => users;
        public IEnumerable<GroupSummary> AllGroups => groups;
    }

    static async Task<(OffboardingViewModel Workflow, ChangePipeline Pipeline, UserSummary[] People, Index Index)> Setup(int people = 1)
    {
        var directory = new DemoTenantDirectory();
        var records = new DemoUserRecords(directory);
        var users = await directory.GetUsersAsync(Ct);
        var groups = await directory.GetGroupsAsync(Ct);
        var index = new Index(users, groups);
        // Licensed members with a user mailbox who are in at least one group.
        var chosen = users.Where(u => u.Kind == UserKind.Member && u.AccountEnabled && u.Licenses.Count > 0 && u.Mailbox is { Type: "User" }
                                      && groups.Any(g => g.Members.Any(m => m.Id == u.Id)))
            .Take(people).ToArray();
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        return (new OffboardingViewModel(chosen, index, records, pipeline, "Northgate", () => { }), pipeline, chosen, index);
    }

    [Fact]
    public async Task The_default_checklist_locks_the_account_converts_before_removing_licenses_and_keeps_the_manager_out()
    {
        var (workflow, _, people, index) = await Setup();
        var person = people[0];

        var plan = await workflow.PlanAsync(Ct);

        var mine = plan.Changes.Where(c => c.Guarded.Any(t => t.Id == person.Id)).ToArray();
        Assert.IsType<SetSignInAllowed>(mine[0]);
        Assert.Contains(mine, c => c is RevokeSessions);
        Assert.False(Assert.Single(mine.OfType<ResetPassword>()).MustChangeAtNextSignIn);
        var convert = Array.FindIndex(mine, c => c is ConvertMailbox);
        var licenses = mine.OfType<SetLicenseAssigned>().ToArray();
        Assert.NotEmpty(licenses);
        Assert.All(licenses, l => Assert.Equal("mailboxtype", l.DependsOn));
        Assert.True(convert >= 0 && convert < Array.IndexOf(mine, licenses[0]));
        Assert.DoesNotContain(mine, c => c is SetMailboxAccess { Right: MailboxRight.FullAccess, New: true } or SetOneDriveAccess or RunDeviceAction);

        var reply = Assert.Single(mine.OfType<SetAutoReply>()).New;
        Assert.Contains(person.DisplayName, reply.Internal);
        Assert.Contains("Northgate", reply.Internal);

        // Every non-dynamic group they're in is left; dynamic ones are named in the notes.
        var groups = index.AllGroups.Where(g => !g.IsMailbox && g.Members.Any(m => m.Id == person.Id)).ToArray();
        Assert.Equal(groups.Count(g => !g.IsDynamic), mine.OfType<SetGroupMembership>().Count(m => m.Role == GroupRole.Member));
        foreach (var dynamic in groups.Where(g => g.IsDynamic))
            Assert.Contains(plan.Notes, n => n.Contains(dynamic.DisplayName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unticked_steps_plan_nothing_and_licenses_without_converting_carry_a_warning()
    {
        var (workflow, _, people, _) = await Setup();
        foreach (var step in workflow.Sections.SelectMany(s => s.Steps))
            step.IsOn = step.Key == "licenses";

        var plan = await workflow.PlanAsync(Ct);

        Assert.All(plan.Changes, c => Assert.IsType<SetLicenseAssigned>(c));
        Assert.All(plan.Changes, c => Assert.Null(c.DependsOn));
        Assert.Contains(plan.Notes, n => n.Contains("deleted 30 days", StringComparison.Ordinal) && n.StartsWith(people[0].DisplayName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Several_people_are_planned_together_and_staged_in_one_go()
    {
        var (workflow, pipeline, people, _) = await Setup(3);
        Assert.Equal(3, people.Length);

        await workflow.StageCommand.ExecuteAsync(null);

        Assert.True(workflow.IsDone || pipeline.Pending.Count > 0);
        Assert.All(people, p => Assert.Contains(pipeline.Pending, c => c is RevokeSessions && c.Target.Id == p.Id));
        Assert.Equal("Offboard 3 people", workflow.Heading);
    }

    [Fact]
    public void The_reply_fills_in_each_persons_details()
    {
        var person = new UserSummary("u1", "Pat Lee", "pat@contoso.example", null, null, null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, []);
        var workflow = new OffboardingViewModel([person], EmptyIndex.Instance, null,
            new ChangePipeline(new DemoChangeExecutor(new DemoTenantDirectory()), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin"),
            "Contoso", () => { });
        workflow.ReplyText = "{first name} has left {organization}; write to {manager}.";

        Assert.Equal("Pat has left Contoso; write to Sam Ortiz.", workflow.ReplyFor(person, "Sam Ortiz"));
        Assert.Equal("Pat has left Contoso; write to us.", workflow.ReplyFor(person, null));
    }
}
