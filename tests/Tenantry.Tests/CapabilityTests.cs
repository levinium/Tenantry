using Tenantry.Core.Capabilities;
using static Tenantry.Core.Capabilities.DirectoryRoles;

namespace Tenantry.Tests;

public class CapabilityTests
{
    static TenantContext Context(string[] roles, string[]? plans = null, bool windows = true) =>
        new(new HashSet<string>(roles), new HashSet<string>(plans ?? []), windows);

    [Fact]
    public void Global_administrator_satisfies_any_role_requirement()
    {
        var result = CapabilityEvaluator.Evaluate(Capabilities.ManageMailboxes, Context([GlobalAdministrator]));

        Assert.True(result.IsAvailable);
    }

    [Fact]
    public void Missing_role_names_the_roles_that_would_work()
    {
        var result = CapabilityEvaluator.Evaluate(Capabilities.ManageMailboxes, Context([UserAdministrator]));

        Assert.False(result.IsAvailable);
        Assert.Equal("Requires one of these roles: Exchange Administrator, Exchange Recipient Administrator", result.Reason);
    }

    [Fact]
    public void Single_role_reads_naturally()
    {
        var result = CapabilityEvaluator.Evaluate(Capabilities.EditUsers, Context([]));

        Assert.Equal("Requires the User Administrator role", result.Reason);
    }

    [Fact]
    public void Missing_license_is_named_even_for_a_global_admin()
    {
        var result = CapabilityEvaluator.Evaluate(Capabilities.ReadSignInLogs, Context([GlobalAdministrator], ["EXCHANGE_S_STANDARD"]));

        Assert.Equal("Requires Microsoft Entra ID P1 or P2", result.Reason);
    }

    [Fact]
    public void Either_entra_plan_unlocks_sign_in_logs()
    {
        Assert.True(CapabilityEvaluator.Evaluate(Capabilities.ReadSignInLogs, Context([ReportsReader], ["AAD_PREMIUM"])).IsAvailable);
        Assert.True(CapabilityEvaluator.Evaluate(Capabilities.ReadSignInLogs, Context([ReportsReader], ["AAD_PREMIUM_P2"])).IsAvailable);
    }

    [Fact]
    public void Platform_limits_are_reported_before_roles()
    {
        var windowsOnly = new Capability("x", "X", [ComplianceAdministrator], WindowsOnly: true);

        var result = CapabilityEvaluator.Evaluate(windowsOnly, Context([], windows: false));

        Assert.Equal("Requires Windows", result.Reason);
    }

    [Fact]
    public void Reading_users_needs_no_role()
    {
        Assert.True(CapabilityEvaluator.Evaluate(Capabilities.ReadUsers, Context([])).IsAvailable);
    }

    [Fact]
    public void Every_catalogued_role_has_a_display_name()
    {
        var unnamed = Capabilities.All.SelectMany(c => c.AnyOfRoles).Where(r => NameOf(r) == r).ToList();

        Assert.Empty(unnamed);
    }
}
