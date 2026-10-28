using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>Named locations and authentication strengths (8b-3).</summary>
public sealed class NamedLocationTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("203.0.113.0/24", true)]
    [InlineData("2001:db8::/32", true)]
    [InlineData("203.0.113.0/33", false)]
    [InlineData("203.0.113.0", false)]
    [InlineData("office", false)]
    public void IP_ranges_need_an_address_and_a_prefix_that_fits(string text, bool ok) => Assert.Equal(ok, CaPlaces.IsCidr(text));

    [Fact]
    public void Locations_are_sent_in_Graphs_shape()
    {
        var ip = CaPlaces.Json("Office", true, true, ["203.0.113.0/24", "2001:db8::/32"], false, false);
        var countries = CaPlaces.Json("Home", false, false, ["us", "CA"], true, true);

        Assert.Equal("#microsoft.graph.ipNamedLocation", ip["@odata.type"]!.GetValue<string>());
        Assert.Equal("#microsoft.graph.iPv6CidrRange", ip["ipRanges"]![1]!["@odata.type"]!.GetValue<string>());
        Assert.True(ip["isTrusted"]!.GetValue<bool>());
        Assert.Equal(["US", "CA"], countries["countriesAndRegions"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("authenticatorAppGps", countries["countryLookupMethod"]!.GetValue<string>());
    }

    [Fact]
    public void Bad_places_and_names_say_whats_wrong()
    {
        var problems = CaPlaces.Problems("Head Office", true, ["203.0.113.0/24", "nope"], [new NamedLocation("x", "Head Office", false, NamedLocation.IpRanges, [])], null);

        Assert.Contains(problems, p => p.Contains("already a location", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("\"nope\"", StringComparison.Ordinal));
    }

    static async Task<(SecurityAreaViewModel Area, ChangePipeline Pipeline, DemoUserRecords Records)> Demo()
    {
        var directory = new DemoTenantDirectory(userCount: 60);
        var records = new DemoUserRecords(directory);
        var pipeline = new ChangePipeline(new DemoChangeExecutor(directory, records), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin");
        var area = new SecurityAreaViewModel(records, pipeline, null, () => DateTimeOffset.Now, _ => { }, null, null, ca: records, newPolicy: () => { });
        area.ReadPolicies();
        for (var i = 0; i < 50 && area.CaDirectory is null; i++)
            await Task.Delay(5, Ct);
        return (area, pipeline, records);
    }

    [Fact]
    public async Task A_location_a_policy_uses_cant_be_deleted_and_one_that_isnt_can()
    {
        var (area, pipeline, _) = await Demo();
        var office = area.NamedLocations.Locations.Single(l => l.Name == "Head Office");
        var home = area.NamedLocations.Locations.Single(l => l.Name == "Home Countries");

        Assert.False(office.CanDelete);
        Assert.Contains("Block sign-ins away from the office", office.UsedText, StringComparison.Ordinal);
        home.DeleteCommand.Execute(null);

        Assert.IsType<DeleteNamedLocation>(Assert.Single(pipeline.Pending));
        Assert.Equal("Deleting", area.NamedLocations.Locations.Single(l => l.Name == "Home Countries").StateText);
    }

    [Fact]
    public async Task Making_a_location_trusted_is_a_caution_and_applies_in_the_demo()
    {
        var (area, pipeline, records) = await Demo();
        var page = area.NamedLocations;
        page.Locations.Single(l => l.Name == "Home Countries").EditCommand.Execute(null);
        page.Kind = LocationsViewModel.Kinds.Single(k => k.Value == NamedLocation.IpRanges);
        page.PlacesText = "198.51.100.0/24";
        page.Trusted = true;
        page.SaveCommand.Execute(null);

        var update = Assert.IsType<UpdateNamedLocation>(Assert.Single(pipeline.Pending));
        Assert.True(update.IsCaution);
        await pipeline.ApplyAsync(cancellationToken: Ct);
        var after = (await records.GetPoliciesAsync(Ct)).Locations.Single(l => l.Name == "Home Countries");
        Assert.True(after.IsTrusted && after.IsIp);
    }

    [Fact]
    public async Task Only_ticking_include_unknown_countries_is_still_a_change()
    {
        var (area, pipeline, _) = await Demo();
        var page = area.NamedLocations;
        page.Locations.Single(l => l.Name == "Home Countries").EditCommand.Execute(null);
        page.IncludeUnknown = true;
        page.SaveCommand.Execute(null);

        Assert.Contains("unknown countries", Assert.IsType<UpdateNamedLocation>(Assert.Single(pipeline.Pending)).NewSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_location_reads_back_from_the_json_it_was_sent_as()
    {
        var back = CaPlaces.Read("l1", CaPlaces.Json("Home", false, false, ["US"], true, true).ToJsonString());

        Assert.Equal(new NamedLocation("l1", "Home", false, NamedLocation.Countries, back.Places, true, true), back);
        Assert.Equal(["US"], back.Places);
    }

    [Fact]
    public async Task A_change_is_patched_with_its_type_and_read_back()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Patch, "namedLocations/l1", "{}")
            .On(HttpMethod.Get, "namedLocations/l1", """{"displayName":"Office"}""");
        var executor = new GraphChangeExecutor(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance);
        var json = CaPlaces.Json("Office", true, false, ["203.0.113.0/24"], false, false).ToJsonString();

        var result = await executor.ApplyAsync(new UpdateNamedLocation(new ChangeTarget("location", "l1", "Office", null), json, "a", "b", false), Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.Equal("#microsoft.graph.ipNamedLocation", http.Requests[0].Json!["@odata.type"]!.GetValue<string>());
    }

    [Fact]
    public void Authentication_methods_read_in_words() =>
        Assert.Equal("Password and Authenticator notification", CaPlaces.Combination("password,microsoftAuthenticatorPush"));
}
