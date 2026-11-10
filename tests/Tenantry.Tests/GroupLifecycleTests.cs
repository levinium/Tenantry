using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>A group's lifecycle (G3): expiry and renewal, licenses for members, upgrading a list, joining and leaving.</summary>
public sealed class GroupLifecycleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    static readonly GroupSummary Team = new("g1", "Finance Team", "finance@contoso.example", null, GroupKind.Microsoft365, false, false, "Private", null, []);
    static readonly GroupSummary Printers = new("sg1", "Printer Access", null, null, GroupKind.Security, false, false, null, null, []);
    static readonly GroupSummary Board = new("dl1", "Board", "board@contoso.example", null, GroupKind.Distribution, false, false, null, null, []);
    static readonly LicenseSku E3 = new("sku-e3", "Office 365 E3", 10, 4, []);
    static readonly LicenseSku Basic = new("sku-basic", "Microsoft 365 Business Basic", 5, 5, []);

    sealed class Records(GroupLifecycle lifecycle) : IGroupLifecycleRecords
    {
        public Task<GroupLifecycle> GetGroupLifecycleAsync(GroupSummary group, CancellationToken cancellationToken = default) => Task.FromResult(lifecycle);
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    static ChangePipeline Pipeline() => new(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");

    static GroupLifecycle Lifecycle(int? days = 180, bool canUpgrade = false, string? blocked = null, IReadOnlyList<GroupLicense>? licenses = null) =>
        new(days is null ? null : Now.AddDays(40), Now.AddDays(-140), days, null, licenses ?? [], [E3, Basic], licenses is { Count: > 0 } ? "ProcessingComplete" : null,
            licenses is { Count: > 0 } ? 2 : null, canUpgrade, blocked);

    static async Task<GroupLifecycleEditor> Editor(GroupSummary group, ChangePipeline pipeline, GroupLifecycle lifecycle)
    {
        var editor = new GroupLifecycleEditor(group, pipeline, Availability.Yes, new Records(lifecycle), () => Now);
        await editor.LoadAsync();
        return editor;
    }

    [Fact]
    public async Task A_group_under_the_expiration_policy_says_when_and_can_be_renewed()
    {
        var pipeline = Pipeline();
        var editor = await Editor(Team, pipeline, Lifecycle());

        Assert.StartsWith("Expires 15 Dec 2026", editor.ExpiryText, StringComparison.Ordinal);
        editor.RenewCommand.Execute(null);
        Assert.IsType<RenewGroup>(Assert.Single(pipeline.Pending));
        Assert.True(editor.HasPendingRenew);
        editor.RenewCommand.Execute(null);
        Assert.Empty(pipeline.Pending);

        var never = await Editor(Team, Pipeline(), Lifecycle(days: null));
        Assert.StartsWith("Doesn't expire", never.ExpiryText, StringComparison.Ordinal);
        Assert.False(never.CanRenew);
    }

    [Fact]
    public async Task Licenses_for_members_are_added_from_the_tenants_and_removed()
    {
        var pipeline = Pipeline();
        var editor = await Editor(Printers, pipeline, Lifecycle(licenses: [new GroupLicense("sku-e3", "Office 365 E3", [])]));

        Assert.Equal(["Office 365 E3"], editor.Licenses.Select(l => l.Name));
        Assert.Contains("2 members couldn't get them", editor.LicenseStateText, StringComparison.Ordinal);
        editor.LicensePicker.Query = "";
        editor.LicensePicker.Browse();
        Assert.Equal(["Microsoft 365 Business Basic"], editor.LicensePicker.Rows.Select(r => r.Option.Name));
        editor.LicensePicker.Choose(editor.LicensePicker.Rows.Single(), false);
        editor.LicensePicker.AddTickedCommand.Execute(null);
        editor.Licenses.Single(l => l.Name == "Office 365 E3").RemoveCommand.Execute(null);

        var changes = pipeline.Pending.OfType<SetGroupLicense>().ToDictionary(l => l.SkuId);
        Assert.True(changes["sku-basic"].New);
        Assert.True(!changes["sku-e3"].New && changes["sku-e3"].IsCaution);
        Assert.Equal("""{"addLicenses":[],"removeLicenses":["sku-e3"]}""", GraphChangeExecutor.GroupLicenseBody(changes["sku-e3"]).ToJsonString());
    }

    [Fact]
    public async Task A_list_is_upgraded_only_when_Microsoft_allows_and_its_name_is_typed()
    {
        var blocked = await Editor(Board, Pipeline(), Lifecycle(days: null, blocked: "Microsoft won't upgrade this list."));
        Assert.False(blocked.CanUpgrade);
        Assert.Equal("Microsoft won't upgrade this list.", blocked.UpgradeText);

        var pipeline = Pipeline();
        var editor = await Editor(Board, pipeline, Lifecycle(days: null, canUpgrade: true));
        editor.UpgradeCommand.Execute(null);
        var upgrade = Assert.IsType<UpgradeList>(Assert.Single(pipeline.Pending));
        Assert.Equal(("Board", true), (upgrade.ConfirmPhrase, upgrade.IsCaution));
        Assert.Equal("Upgrade-DistributionGroup", GraphChangeExecutor.ScriptFor(upgrade)!.Text.Split(' ')[0]);
    }

    [Fact]
    public async Task Renewing_and_licensing_go_through_Graph()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Post, "/groups/g1/renew", "{}")
            .On(HttpMethod.Post, "/groups/sg1/assignLicense", "{}")
            .On(HttpMethod.Get, "/groups/sg1?$select=assignedLicenses", """{"assignedLicenses":[{"skuId":"sku-e3","disabledPlans":[]}]}""");
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);

        var renew = await executor.ApplyAsync(new RenewGroup(GroupAccess.Target(Team), GroupKind.Microsoft365, "Expires soon"), Ct);
        var license = await executor.ApplyAsync(new SetGroupLicense(GroupAccess.Target(Printers), GroupKind.Security, "sku-e3", "Office 365 E3", false, true), Ct);

        Assert.Equal((ChangeOutcome.Applied, ChangeOutcome.Applied), (renew.Outcome, license.Outcome));
        Assert.Contains("\"skuId\":\"sku-e3\"", http.Requests.Single(r => r.PathAndQuery.EndsWith("assignLicense", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
    }
}
