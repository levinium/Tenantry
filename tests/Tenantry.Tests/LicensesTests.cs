using System.Net;
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

public class LicensesTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly LicenseSku E3 = new("sku-e3", "Microsoft 365 E3", 25, 20,
        [new("p-exo", "Exchange Online"), new("p-teams", "Microsoft Teams"), new("p-yammer", "Viva Engage Core")]);
    static readonly LicenseSku Basic = new("sku-basic", "Microsoft 365 Business Basic", 10, 10, [new("p-exo", "Exchange Online")]);
    static readonly LicenseSku Visio = new("sku-visio", "Visio Plan 2", 5, 1, [new("p-visio", "Visio")]);

    sealed class Records(UserLicensing licensing) : IUserRecords
    {
        public Task<UserDetails> GetDetailsAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetDomainsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UserLicensing> GetLicensingAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(licensing);

        public Task<IReadOnlyList<MailboxDelegate>> GetMailboxDelegatesAsync(string userId, string address, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MailDetails> GetMailAsync(string userId, string address, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    static UserSummary Ada => new("t1", "zz-tenantry-test-ada", "zz-tenantry-test-ada@contoso.example", null, null, null, true,
        UserKind.Member, DateTimeOffset.UnixEpoch, null, ["Microsoft 365 E3"]);

    static (LicensesSection Tab, ChangePipeline Pipeline) Tab(UserLicensing licensing, bool canEdit = true)
    {
        var pipeline = new ChangePipeline(new NoopExecutor(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var page = new UserDetailViewModel(Ada, pipeline: pipeline, canEdit: canEdit ? Availability.Yes : Availability.No("Requires the User Administrator role"),
            records: new Records(licensing));
        return (page.LicensesTab, pipeline);
    }

    sealed class NoopExecutor : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    [Fact]
    public async Task Licenses_show_where_they_come_from_and_inherited_ones_cant_be_removed_here()
    {
        var (tab, _) = Tab(new UserLicensing("GB",
        [
            new("sku-e3", "Microsoft 365 E3", null, null, null, ["p-yammer"]),
            new("sku-basic", "Microsoft 365 Business Basic", "g1", "All Staff", "CountViolation", []),
        ], [E3, Basic, Visio], []));

        await tab.LoadAsync(Ct);

        var e3 = tab.Licenses.Single(l => l.Name == "Microsoft 365 E3");
        Assert.Equal("Assigned directly", e3.Source);
        Assert.True(e3.CanRemove);
        Assert.Equal("2 of 3 apps on", e3.AppsSummary);
        var basic = tab.Licenses.Single(l => l.Name == "Microsoft 365 Business Basic");
        Assert.Equal("From group: All Staff", basic.Source);
        Assert.False(basic.CanRemove);
        Assert.Contains("All Staff", basic.RemoveTip);
        Assert.StartsWith("No licenses of this kind are left", basic.Error);
        // Business Basic is still offered (it's only inherited); full licenses are offered but disabled.
        Assert.Equal(["Visio Plan 2 · 4 of 5 left", "Microsoft 365 Business Basic · none left"], tab.AddOptions.Select(o => o.Label));
    }

    [Fact]
    public async Task Adding_removing_and_switching_apps_are_staged_and_shown_pending()
    {
        var (tab, pipeline) = Tab(new UserLicensing("GB", [new("sku-e3", "Microsoft 365 E3", null, null, null, [])], [E3, Visio], []));
        await tab.LoadAsync(Ct);

        tab.SelectedToAdd = tab.AddOptions.Single(o => o.Sku.SkuId == "sku-visio");
        tab.AddLicenseCommand.Execute(null);
        Assert.Equal("Adding", tab.Licenses.Single(l => l.Name == "Visio Plan 2").PendingText);

        tab.Licenses.Single(l => l.Name == "Microsoft 365 E3").Apps.Single(a => a.Name == "Viva Engage Core").IsOn = false;
        var apps = Assert.Single(pipeline.Pending.OfType<SetLicenseApps>());
        Assert.Equal("Microsoft 365 E3 Apps: All Apps On → Off: Viva Engage Core", apps.Summary);
        Assert.True(apps.IsCaution);

        tab.Licenses.Single(l => l.Name == "Microsoft 365 E3").RemoveCommand.Execute(null);
        Assert.Equal("Removing", tab.Licenses.Single(l => l.Name == "Microsoft 365 E3").PendingText);
        Assert.Contains(pipeline.Pending, p => p is SetLicenseAssigned { New: false, IsCaution: true });

        // Undo from the same button.
        tab.Licenses.Single(l => l.Name == "Visio Plan 2").RemoveCommand.Execute(null);
        Assert.DoesNotContain(pipeline.Pending, p => p is SetLicenseAssigned { New: true });
    }

    [Fact]
    public async Task Without_a_usage_location_licenses_cant_be_added()
    {
        var (tab, _) = Tab(new UserLicensing(null, [], [Visio], []));
        await tab.LoadAsync(Ct);

        Assert.True(tab.MissingUsageLocation);
        Assert.False(tab.CanAddLicenses);
        tab.SelectedToAdd = tab.AddOptions.Single();
        Assert.False(tab.AddLicenseCommand.CanExecute(null));
    }

    [Fact]
    public async Task Graph_assigns_and_reads_back_and_refreshes_counts()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "/users/t1/assignLicense", """{"id":"t1"}""")
            .On(HttpMethod.Get, "subscribedSkus", """{"value":[{"skuId":"sku-visio","skuPartNumber":"VISIOCLIENT","prepaidUnits":{"enabled":5},"consumedUnits":2}]}""")
            .On(HttpMethod.Get, "/users/t1?$select=id,assignedLicenses", """{"id":"t1","assignedLicenses":[{"skuId":"sku-visio","disabledPlans":["p-x"]}]}""");
        var store = new MemoryEntityStore();
        store.Apply(DirectorySync.Users, [new EntityChange("t1", JsonNode.Parse("""{"id":"t1","assignedLicenses":[]}""")!.AsObject())]);
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store, NoDelay.Instance);
        var ada = new ChangeTarget("user", "t1", "Ada", "zz-tenantry-test-ada@contoso.example");

        var added = await executor.ApplyAsync(new SetLicenseAssigned(ada, "sku-visio", "Visio Plan 2", false, true), Ct);
        var apps = await executor.ApplyAsync(new SetLicenseApps(ada, "sku-visio", "Visio Plan 2", [], ["p-x"], new Dictionary<string, string>()), Ct);

        Assert.Equal(ChangeOutcome.Applied, added.Outcome);
        Assert.Equal(ChangeOutcome.Applied, apps.Outcome);
        var bodies = http.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Body).ToArray();
        Assert.Equal("""{"addLicenses":[{"skuId":"sku-visio","disabledPlans":[]}],"removeLicenses":[]}""", bodies[0]);
        Assert.Equal("""{"addLicenses":[{"skuId":"sku-visio","disabledPlans":["p-x"]}],"removeLicenses":[]}""", bodies[1]);
        Assert.Contains("sku-visio", store.Get(DirectorySync.Users, "t1"));
        Assert.Contains("\"consumedUnits\":2", store.Get(DirectorySync.Skus, "sku-visio"));
    }
}
