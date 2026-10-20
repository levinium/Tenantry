using System.Text.Json;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;

namespace Tenantry.Tests;

public class SearchSyntaxTests
{
    static SearchParse Parse(string text) => SearchSyntax.Parse(text, UserFields.All);

    [Fact]
    public void Shortcuts_become_conditions_and_the_rest_stays_free_text()
    {
        var parse = Parse("smith dept:finance -status:blocked license:E5");

        Assert.Equal("smith", parse.FreeText);
        Assert.Equal(
        [
            new ChoiceCondition(UserFields.Department, ["finance"], []),
            new ChoiceCondition(UserFields.Status, [], ["blocked"]),
            new ChoiceCondition(UserFields.Licenses, ["E5"], []),
        ], parse.Conditions.Cast<ChoiceCondition>().Select(c => c with { Include = c.Include.ToArray(), Exclude = c.Exclude.ToArray() }),
            new ChoiceComparer());
    }

    [Theory]
    [InlineData("signin:>90d", DateOp.OlderThanDays, 90)]
    [InlineData("signin:<7d", DateOp.WithinLastDays, 7)]
    [InlineData("lastsignin:>2w", DateOp.OlderThanDays, 14)]
    [InlineData("login:>6m", DateOp.OlderThanDays, 180)]
    [InlineData("signin:never", DateOp.Never, 0)]
    [InlineData("-signin:never", DateOp.Any, 0)]
    public void Date_shortcuts(string text, DateOp op, int days)
    {
        var condition = Assert.IsType<DateCondition>(Parse(text).Conditions.Single());

        Assert.Equal((op, days), (condition.Op, condition.Days));
    }

    [Theory]
    [InlineData("lc:>=2", 2.0, null)]
    [InlineData("lc:>2", 3.0, null)]
    [InlineData("lc:<2", null, 1.0)]
    [InlineData("lc:1..3", 1.0, 3.0)]
    [InlineData("lc:0", 0.0, 0.0)]
    public void Number_shortcuts(string text, double? min, double? max)
    {
        var condition = Assert.IsType<NumberCondition>(Parse(text).Conditions.Single());

        Assert.Equal((min, max), (condition.Min, condition.Max));
    }

    [Fact]
    public void Quoted_values_none_and_unknown_keys()
    {
        var parse = Parse("dept:\"human resources\" license:none re:budget title:none");

        Assert.Equal("re:budget", parse.FreeText);
        Assert.Equal(["human resources"], ((ChoiceCondition)parse.Conditions[0]).Include);
        Assert.Equal([UserFields.Unlicensed], ((ChoiceCondition)parse.Conditions[1]).Include);
        Assert.Equal(TextOp.IsEmpty, ((TextCondition)parse.Conditions[2]).Op);
    }

    [Fact]
    public void Nonsense_values_are_reported()
    {
        var parse = Parse("signin:soon");

        Assert.Empty(parse.Conditions);
        Assert.Single(parse.Problems);
    }

    [Fact]
    public void Queries_round_trip_through_json()
    {
        var query = new ListQuery("x", ConditionGroup.All(
            new ChoiceCondition(UserFields.Status, [], [UserFields.Blocked]),
            ConditionGroup.Any(new DateCondition(UserFields.LastSignIn, DateOp.OlderThanDays, 90), new DateCondition(UserFields.LastSignIn, DateOp.Never)),
            new NumberCondition(UserFields.LicenseCount, 1, null),
            new TextCondition(UserFields.Title, TextOp.Contains, "dir")),
            [new SortLevel(UserFields.Name, true)], UserFields.Department);

        var json = JsonSerializer.Serialize(query);
        var back = JsonSerializer.Deserialize<ListQuery>(json)!;

        Assert.Equal(json, JsonSerializer.Serialize(back));
        Assert.Contains("\"type\":\"group\"", json);
    }

    sealed class ChoiceComparer : IEqualityComparer<ChoiceCondition>
    {
        public bool Equals(ChoiceCondition? a, ChoiceCondition? b) =>
            a!.Field == b!.Field && a.Include.SequenceEqual(b.Include) && a.Exclude.SequenceEqual(b.Exclude);

        public int GetHashCode(ChoiceCondition c) => c.Field.GetHashCode();
    }
}
