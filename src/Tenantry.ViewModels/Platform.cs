namespace Tenantry.ViewModels;

/// <summary>
/// The few platform services view models need, set once by the app at startup. Tests leave
/// the defaults, which do nothing.
/// </summary>
public static class Platform
{
    /// <summary>Opens a web address in the user's browser.</summary>
    public static Action<string> OpenUrl { get; set; } = _ => { };
}
