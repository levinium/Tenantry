using Tenantry.Core.Tenancy;

namespace Tenantry.ViewModels;

/// <summary>A saved tenant as the welcome screen shows it.</summary>
public sealed class TenantCard(TenantProfile profile, DateTimeOffset now)
{
    public TenantProfile Profile { get; } = profile;

    public string DisplayName => Profile.DisplayName;
    public string PrimaryDomain => Profile.PrimaryDomain;
    public string Initials { get; } = Display.Initials(profile.DisplayName);

    public string AccountText => Profile.LastAccount is { Length: > 0 } a ? a : "No account yet";

    public string LastUsedText { get; } = profile.LastUsedAt is { } used
        ? $"Opened {Display.Relative(used, now)}"
        : "Not opened yet";
}
