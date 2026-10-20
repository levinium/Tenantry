namespace Tenantry.Core.Capabilities;

/// <summary>
/// A feature, and what the signed-in admin and the tenant need for it to work. Features check
/// this before offering an action, so the UI can show it disabled with the reason instead of
/// letting the API refuse it.
/// </summary>
/// <param name="AnyOfRoles">Role template IDs; any one is enough. Global Administrator always
/// qualifies. Empty means any signed-in user.</param>
/// <param name="AnyOfServicePlans">Service plan names (from subscribedSkus); any one is
/// enough. Empty means no license requirement.</param>
/// <param name="LicenseLabel">How the license requirement reads to a person.</param>
/// <param name="WindowsOnly">True when only a Windows-only path can do it.</param>
public sealed record Capability(
    string Key,
    string Title,
    IReadOnlyList<string> AnyOfRoles,
    IReadOnlyList<string>? AnyOfServicePlans = null,
    string? LicenseLabel = null,
    bool WindowsOnly = false);

/// <summary>What is known about the signed-in admin and the tenant.</summary>
public sealed record TenantContext(
    IReadOnlySet<string> RoleTemplateIds,
    IReadOnlySet<string> ServicePlans,
    bool IsWindows);

public sealed record Availability(bool IsAvailable, string? Reason)
{
    public static readonly Availability Yes = new(true, null);
    public static Availability No(string reason) => new(false, reason);
}

public static class CapabilityEvaluator
{
    public static Availability Evaluate(Capability capability, TenantContext context)
    {
        // Platform first: no role or license fixes it, so it's the most useful thing to say.
        if (capability.WindowsOnly && !context.IsWindows)
            return Availability.No("Requires Windows");

        if (capability.AnyOfRoles.Count > 0
            && !context.RoleTemplateIds.Contains(DirectoryRoles.GlobalAdministrator)
            && !capability.AnyOfRoles.Any(context.RoleTemplateIds.Contains))
        {
            var names = capability.AnyOfRoles.Select(DirectoryRoles.NameOf);
            return Availability.No(capability.AnyOfRoles.Count == 1
                ? $"Requires the {names.Single()} role"
                : $"Requires one of these roles: {string.Join(", ", names)}");
        }

        if (capability.AnyOfServicePlans is { Count: > 0 } plans && !plans.Any(context.ServicePlans.Contains))
            return Availability.No($"Requires {capability.LicenseLabel ?? string.Join(" or ", plans)}");

        return Availability.Yes;
    }
}
