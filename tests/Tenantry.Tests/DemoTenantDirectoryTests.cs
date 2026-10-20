using Tenantry.Core.Directory;
using Tenantry.Demo;

namespace Tenantry.Tests;

public class DemoTenantDirectoryTests
{
    [Fact]
    public async Task Same_seed_produces_identical_users()
    {
        var a = await new DemoTenantDirectory(100, seed: 7).GetUsersAsync(TestContext.Current.CancellationToken);
        var b = await new DemoTenantDirectory(100, seed: 7).GetUsersAsync(TestContext.Current.CancellationToken);

        Assert.Equal(a.Select(u => (u.Id, u.UserPrincipalName, u.LastSignIn)), b.Select(u => (u.Id, u.UserPrincipalName, u.LastSignIn)));
    }

    // Demo data feeds screenshots in a public repo, so every address must sit in a reserved
    // RFC 2606 domain that can never belong to a real organisation.
    [Fact]
    public async Task Every_address_uses_a_reserved_example_domain()
    {
        var users = await new DemoTenantDirectory(500).GetUsersAsync(TestContext.Current.CancellationToken);

        foreach (var u in users)
        {
            Assert.EndsWith(".example", u.UserPrincipalName.Split('@')[^1], StringComparison.Ordinal);
            if (u.Mail is not null)
                Assert.EndsWith(".example", u.Mail.Split('@')[^1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Sign_in_names_are_unique()
    {
        var users = await new DemoTenantDirectory(2000).GetUsersAsync(TestContext.Current.CancellationToken);

        Assert.Equal(users.Count, users.Select(u => u.UserPrincipalName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Includes_the_variety_the_ui_needs_to_show()
    {
        var users = await new DemoTenantDirectory().GetUsersAsync(TestContext.Current.CancellationToken);

        Assert.Contains(users, u => u.Kind == UserKind.Guest);
        Assert.Contains(users, u => !u.AccountEnabled);
        Assert.Contains(users, u => u.LastSignIn is null);
        Assert.All(users.Where(u => u.Kind == UserKind.Guest), u => Assert.Contains("#EXT#", u.UserPrincipalName));
    }
}
