using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>
/// What another directory decides isn't edited here: people and groups synced from on-premises
/// Active Directory, and guests' passwords (their own organization's).
/// </summary>
public sealed class SyncedFromAdTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Noop : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static ChangePipeline Pipeline() => new(new Noop(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin@contoso.example");

    static UserSummary Person(bool synced) =>
        new("u1", "zz-tenantry-test-ada", "zz-tenantry-test-ada@contoso.example", null, "IT", null, true, UserKind.Member, DateTimeOffset.UnixEpoch, null, [],
            SyncedFromOnPremises: synced, UsageLocation: "US");

    [Fact]
    public async Task A_synced_persons_profile_is_read_only_but_usage_location_isnt()
    {
        var account = new AccountSection(Person(synced: true), null, Pipeline(), Availability.Yes);
        await account.LoadAsync(Ct);

        var fields = account.Groups.SelectMany(g => g.Fields).ToDictionary(f => f.Property);
        Assert.False(fields["displayName"].CanEdit);
        Assert.False(fields["userPrincipalName"].CanEdit);
        Assert.Equal(UserSummary.SyncedReason, fields["department"].EditTip);
        Assert.True(fields["usageLocation"].CanEdit);
        Assert.False(account.CanEdit);
        Assert.Equal(UserSummary.SyncedReason, account.EditReason);
        Assert.True(new AccountSection(Person(synced: false), null, Pipeline(), Availability.Yes).CanEdit);
    }

    [Fact]
    public void A_synced_person_shows_where_they_come_from()
    {
        var page = new UserDetailViewModel(Person(synced: true));

        Assert.Contains(page.Health, c => c.Text == "Synced from On-Premises AD");
        Assert.DoesNotContain(new UserDetailViewModel(Person(synced: false)).Health, c => c.Text == "Synced from On-Premises AD");
    }

    [Fact]
    public void A_guests_password_isnt_reset_here()
    {
        var guest = Person(synced: false) with { Kind = UserKind.Guest };

        var page = new UserDetailViewModel(guest, pipeline: Pipeline(), canEdit: Availability.Yes);

        Assert.True(page.CanEdit);
        Assert.False(page.CanResetPassword);
        Assert.Contains("own organization", page.ResetPasswordTip);
        Assert.True(new UserDetailViewModel(Person(synced: false), pipeline: Pipeline(), canEdit: Availability.Yes).CanResetPassword);
    }

    [Fact]
    public void A_synced_groups_members_are_changed_in_AD()
    {
        var group = new GroupSummary("g1", "zz-tenantry-test-group", null, null, GroupKind.Security, false, false, null, null, [], [],
            SyncedFromOnPremises: true);

        Assert.Contains("on-premises Active Directory", GroupAccess.LockedReason(group, GroupRole.Member));
        Assert.Null(GroupAccess.LockedReason(group, GroupRole.Owner));
        Assert.False(GroupAccess.MembersEditable(group));
    }
}
