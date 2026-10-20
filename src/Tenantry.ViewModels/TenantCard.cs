using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Tenancy;

namespace Tenantry.ViewModels;

/// <summary>A saved tenant as the home screen lists it.</summary>
public sealed partial class TenantCard(TenantProfile profile, DateTimeOffset now) : ObservableObject
{
    public TenantProfile Profile { get; } = profile;

    public string DisplayName => Profile.DisplayName;
    public string PrimaryDomain => Profile.PrimaryDomain;
    public string Initials { get; } = Display.Initials(profile.DisplayName);

    public string AccountText => Profile.LastAccount is { Length: > 0 } a ? a : "No account yet";

    public string LastUsedText { get; } = profile.LastUsedAt is { } used
        ? $"Opened {Display.Relative(used, now)}"
        : "Not opened yet";

    /// <summary>Opens automatically when Tenantry starts.</summary>
    [ObservableProperty]
    public partial bool IsDefault { get; set; } = false;

    /// <summary>True while the row asks "Remove this tenant from this computer?".</summary>
    [ObservableProperty]
    public partial bool IsConfirmingRemove { get; set; }

    public string RemoveQuestion =>
        $"Remove {DisplayName} from Tenantry on this computer? This deletes its saved sign-in and list views here. Nothing changes in the tenant itself, and you can add it again later.";
}
