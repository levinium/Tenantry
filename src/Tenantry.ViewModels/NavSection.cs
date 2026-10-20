namespace Tenantry.ViewModels;

/// <summary>
/// A sidebar entry. Unavailable sections stay visible, disabled, with the reason shown, so an
/// admin can see what exists and why they can't use it yet.
/// </summary>
public sealed record NavSection(string Key, string Title, string? UnavailableReason = null)
{
    public bool IsAvailable => UnavailableReason is null;
}
