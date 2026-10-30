using Tenantry.Core.Directory;
using Tenantry.ViewModels;

namespace Tenantry.Tests;

public class DisplayAndChipTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Ada Lovelace", "AL")]
    [InlineData("Grace Brewster Hopper", "GH")]
    [InlineData("cher", "C")]
    [InlineData("  ", "?")]
    [InlineData(null, "?")]
    [InlineData("(Shared) Front Desk", "FD")]
    public void Initials(string? name, string expected) => Assert.Equal(expected, Display.Initials(name));

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(5, "5 minutes ago")]
    [InlineData(90, "1 hour ago")]
    [InlineData(60 * 5, "5 hours ago")]
    [InlineData(60 * 30, "yesterday")]
    [InlineData(60 * 24 * 12, "12 days ago")]
    public void Relative_times(int minutesAgo, string expected) =>
        Assert.Equal(expected, Display.Relative(Now.AddMinutes(-minutesAgo), Now));

    [Fact]
    public void Old_and_missing_dates()
    {
        Assert.Equal("Never", Display.Relative(null, Now));
        Assert.Matches(@"^\d{1,2} [A-Z][a-z]{2} 2025$", Display.Relative(new DateTimeOffset(2025, 3, 4, 12, 0, 0, TimeSpan.Zero), Now));
    }

    [Fact]
    public void Rows_read_at_a_glance()
    {
        var row = new UserRow(FakeDirectory.User("1", "Grace Hopper", enabled: false, kind: UserKind.Guest), Now);

        Assert.Equal(("Blocked", BadgeKind.Danger), (row.StatusText, row.StatusKind));
        Assert.Equal(("Guest", BadgeKind.Info), (row.KindText, row.KindKind));
        Assert.Equal("Never", row.LastSignInText);
        Assert.True(row.IsUnlicensed);
    }

    [Fact]
    public void Guests_show_their_real_address_not_the_ext_sign_in_name()
    {
        var guest = new UserSummary("g", "Pat Guest", "pat_partner.example#EXT#@contoso.example", "pat@partner.example",
            null, null, true, UserKind.Guest, Now, null, []);
        var member = FakeDirectory.User("m", "Ada Lovelace");

        Assert.Equal("pat@partner.example", new UserRow(guest, Now).SecondaryText);
        Assert.Equal(member.UserPrincipalName, new UserRow(member, Now).SecondaryText);
    }
}
