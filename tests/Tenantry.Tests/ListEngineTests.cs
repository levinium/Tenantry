using Tenantry.Core.Directory;
using Tenantry.Core.Lists;

namespace Tenantry.Tests;

public class ListEngineTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    static readonly ListEngine<UserSummary> Engine = new(UserFields.All);

    static UserSummary U(string name, string? dept = null, bool enabled = true, UserKind kind = UserKind.Member,
        int? signedInDaysAgo = null, params string[] licenses) =>
        new(name, name, $"{name.ToLowerInvariant()}@contoso.example", null, dept, null, enabled, kind,
            Now.AddDays(-400), signedInDaysAgo is { } d ? Now.AddDays(-d) : null, licenses);

    static readonly UserSummary[] People =
    [
        U("Ada", "Finance", signedInDaysAgo: 2, licenses: ["E3"]),
        U("Alan", "IT", signedInDaysAgo: 120, licenses: ["E5", "Visio"]),
        U("Grace", "IT", enabled: false, signedInDaysAgo: 200, licenses: ["E3"]),
        U("Guest", kind: UserKind.Guest),
        U("Linus", "IT", signedInDaysAgo: 40),
    ];

    static string[] Names(ListQuery query) =>
        Engine.Run(Engine.Prepare(People), query, Now).Select(u => u.DisplayName).ToArray();

    static ListQuery Where(params Condition[] conditions) => ListQuery.Empty with { Where = ConditionGroup.All(conditions) };

    [Fact]
    public void Excluding_a_choice_hides_those_rows()
    {
        Assert.Equal(["Ada", "Alan", "Guest", "Linus"], Names(Where(new ChoiceCondition(UserFields.Status, [], [UserFields.Blocked]))));
    }

    [Fact]
    public void Licenses_match_any_value_and_unlicensed_means_none()
    {
        Assert.Equal(["Alan"], Names(Where(new ChoiceCondition(UserFields.Licenses, ["e5"], []))));
        Assert.Equal(["Ada", "Alan", "Grace"], Names(Where(new ChoiceCondition(UserFields.Licenses, ["E3", "E5"], []))));
        Assert.Equal(["Guest", "Linus"], Names(Where(new ChoiceCondition(UserFields.Licenses, [UserFields.Unlicensed], []))));
        Assert.Equal(["Ada", "Grace", "Guest", "Linus"], Names(Where(new ChoiceCondition(UserFields.Licenses, [], ["Visio"]))));
    }

    [Fact]
    public void Date_conditions()
    {
        Assert.Equal(["Ada", "Linus"], Names(Where(new DateCondition(UserFields.LastSignIn, DateOp.WithinLastDays, 90))));
        Assert.Equal(["Alan", "Grace"], Names(Where(new DateCondition(UserFields.LastSignIn, DateOp.OlderThanDays, 90))));
        Assert.Equal(["Guest"], Names(Where(new DateCondition(UserFields.LastSignIn, DateOp.Never))));
        Assert.Equal(["Alan"], Names(Where(new DateCondition(UserFields.LastSignIn, DateOp.Between, From: Now.AddDays(-150), To: Now.AddDays(-100)))));
    }

    [Fact]
    public void Or_groups_express_inactive_including_never()
    {
        var inactive = ConditionGroup.Any(
            new DateCondition(UserFields.LastSignIn, DateOp.OlderThanDays, 90),
            new DateCondition(UserFields.LastSignIn, DateOp.Never));

        Assert.Equal(["Alan", "Grace", "Guest"], Names(Where(inactive)));
    }

    [Fact]
    public void Number_and_text_conditions()
    {
        Assert.Equal(["Alan"], Names(Where(new NumberCondition(UserFields.LicenseCount, 2, null))));
        Assert.Equal(["Guest"], Names(Where(new TextCondition(UserFields.Department, TextOp.IsEmpty))));
        Assert.Equal(["Ada", "Alan", "Grace", "Linus"], Names(Where(new TextCondition(UserFields.Department, TextOp.IsNotEmpty))));
        Assert.Equal(["Ada"], Names(Where(new TextCondition(UserFields.Name, TextOp.StartsWith, "ad"))));
    }

    [Fact]
    public void Multi_level_sort_with_empties_first()
    {
        var query = ListQuery.Empty with { Sort = [new SortLevel(UserFields.Department), new SortLevel(UserFields.Name, Descending: true)] };

        Assert.Equal(["Guest", "Ada", "Linus", "Grace", "Alan"], Names(query));
    }

    [Fact]
    public void Group_by_sorts_by_the_group_first()
    {
        var query = ListQuery.Empty with { Sort = [new SortLevel(UserFields.Name)], GroupBy = UserFields.Status };

        Assert.Equal(["Ada", "Alan", "Guest", "Linus", "Grace"], Names(query));
    }

    [Fact]
    public void Facets_count_against_the_other_filters_only()
    {
        var prepared = Engine.Prepare(People);
        var query = Where(
            new ChoiceCondition(UserFields.Department, ["IT"], []),
            new ChoiceCondition(UserFields.Status, [UserFields.Active], []));

        // Status counts ignore the status filter but respect the department one: IT has 2 active, 1 blocked.
        var status = Engine.Facet(prepared, query, UserFields.Status, Now);
        Assert.Equal([new FacetValue("Active", 2), new FacetValue("Blocked", 1)], status);

        // License counts respect both: Alan (E5, Visio) and Linus (none).
        var licenses = Engine.Facet(prepared, query, UserFields.Licenses, Now).ToDictionary(f => f.Value, f => f.Count);
        Assert.Equal(1, licenses["E5"]);
        Assert.Equal(1, licenses["Visio"]);
        Assert.Equal(1, licenses[UserFields.Unlicensed]);
        Assert.Equal(UserFields.Unlicensed, Engine.Facet(prepared, query, UserFields.Licenses, Now)[^1].Value);
    }

    [Fact]
    public void Free_text_search_spans_searchable_fields()
    {
        Assert.Equal(["Alan"], Names(ListQuery.Empty with { Search = "visio" }));
        Assert.Equal(["Alan", "Grace", "Linus"], Names(ListQuery.Empty with { Search = "it e" }));
        Assert.Equal(["Alan"], Names(ListQuery.Empty with { Search = "ALAN e5" }));
    }

    [Fact]
    public void Unknown_fields_are_ignored_not_fatal()
    {
        Assert.Equal(5, Names(Where(new ChoiceCondition("gone", ["x"], []))).Length);
    }
}
