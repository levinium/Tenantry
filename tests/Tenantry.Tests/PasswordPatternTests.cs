using System.Text.RegularExpressions;
using Tenantry.Core.Changes;
using Tenantry.ViewModels;

namespace Tenantry.Tests;

/// <summary>Password patterns: written as short text, checked against Microsoft 365's rules, and every password made follows the pattern.</summary>
public sealed class PasswordPatternTests
{
    [Theory]
    [InlineData("L5+1U D3 S2")]
    [InlineData("U2 l4 D2-4 '-' S1")]
    [InlineData("A8+1U+1l P2")]
    public void Patterns_read_and_write_back_the_same(string text)
    {
        var (parts, problem) = PasswordPattern.Parse(text);
        Assert.Null(problem);
        Assert.Equal(text, PasswordPattern.ToText(parts));
    }

    [Theory]
    [InlineData("L5+1U X2", "isn't a part")]
    [InlineData("L", "Say how many")]
    [InlineData("D8-6", "smaller number")]
    [InlineData("D3+1U", "Only L")]
    [InlineData("L2+2U+1l", "more capitals and small letters")]
    [InlineData("'-", "Close the exact text")]
    public void Mistakes_in_the_text_are_explained(string text, string expected) =>
        Assert.Contains(expected, PasswordPattern.Parse(text).Problem);

    [Theory]
    [InlineData("L5+1U D2", "needs at least 8")]
    [InlineData("L6 D4", "three kinds")]
    [InlineData("U4 D4", "three kinds")]
    public void Patterns_Microsoft_would_refuse_are_said_to_be(string text, string expected) =>
        Assert.Contains(expected, PasswordPattern.Problem(PasswordPattern.Parse(text).Parts));

    [Fact]
    public void A_letter_rule_counts_toward_the_three_kinds() =>
        Assert.Null(PasswordPattern.Problem(PasswordPattern.Parse("L5+1U D3 S2").Parts));

    [Theory]
    [InlineData("L5+1U D3 S2", @"^[A-Za-z]{5}[0-9]{3}[!@#$%&*?+=]{2}$")]
    [InlineData("U2 l4 D2-4 '-' S1", @"^[A-Z]{2}[a-z]{4}[0-9]{2,4}-[!@#$%&*?+=]$")]
    public void Every_password_follows_its_pattern(string text, string regex)
    {
        var parts = PasswordPattern.Parse(text).Parts;
        for (var i = 0; i < 300; i++)
        {
            var password = PasswordPattern.Create(parts, avoidLookAlikes: true);
            Assert.Matches(regex, password);
            Assert.DoesNotMatch("[0Oo1lI5S2Z]", password.Where(char.IsLetterOrDigit).Aggregate("", (s, c) => s + c));
            if (text.StartsWith("L5+1U", StringComparison.Ordinal))
                Assert.Matches("[A-Z]", password[..5]);
        }
    }

    [Fact]
    public void The_style_uses_the_pattern_when_set_and_says_what_it_makes()
    {
        var style = PasswordStyle.Default with { Pattern = "L5+1U D3 S2" };
        Assert.Null(style.Problem);
        Assert.Equal("5 letters (at least 1 capital), then 3 digits, then 2 symbols that are easy to type on a phone.", style.Description);
        Assert.Matches(@"^[A-Za-z]{5}\d{3}\W{2}$", style.Create());
        // An unreadable pattern is reported, and passwords fall back to the standard style.
        var broken = PasswordStyle.Default with { Pattern = "Q" };
        Assert.NotNull(broken.Problem);
        Assert.Matches(@"^\w{4}-\w{4}-\w{4}-\w{4}$", broken.Create());
    }

    [Fact]
    public void A_pattern_is_kept_with_the_tenant()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tenantry-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonPasswordStyleStore(folder);
            store.Save(PasswordStyle.Default with { Pattern = "U2 l4 D2-4 '-' S1" });
            Assert.Equal("U2 l4 D2-4 '-' S1", new JsonPasswordStyleStore(folder).Load().Pattern);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_rows_and_the_text_follow_each_other()
    {
        var store = new MemoryPasswordStyleStore();
        var settings = new PasswordStyleViewModel(store) { UsePattern = true };
        Assert.Equal(PasswordStyleViewModel.StarterPattern, store.Load().Pattern);

        settings.PatternText = "U2 l4 D3 S1";
        Assert.Equal(4, settings.Parts.Count);
        Assert.Equal("U2 l4 D3 S1", store.Load().Pattern);

        settings.Parts[2].Count = 5;
        Assert.Equal("U2 l4 D5 S1", settings.PatternText);
        settings.Parts[3].MoveUpCommand.Execute(null);
        Assert.Equal("U2 l4 S1 D5", settings.PatternText);
        settings.AddPartCommand.Execute(null);
        settings.Parts[^1].Kind = PatternPartRow.Kinds.Single(k => k.Value == PatternKind.Exact);
        settings.Parts[^1].Text = "-";
        Assert.Equal("U2 l4 S1 D5 '-'", settings.PatternText);
        Assert.Equal(3, settings.Examples.Count);

        settings.PatternText = "U2 X";
        Assert.True(settings.HasPatternTextProblem);
        Assert.Equal(5, settings.Parts.Count);
        Assert.Equal("U2 l4 S1 D5 '-'", store.Load().Pattern);

        settings.UseSimple = true;
        Assert.Null(store.Load().Pattern);
    }
}
