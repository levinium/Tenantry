using System.Windows.Input;

namespace Tenantry.ViewModels;

/// <summary>
/// What shows while the startup tenant opens: a spinner and its name, instead of a home page
/// that flashes up and vanishes. If the sign-in fails or is cancelled, the home page replaces it
/// and says why.
/// </summary>
public sealed class StartingViewModel(WelcomeViewModel welcome, string tenantName)
{
    /// <summary>The home page to fall back to; it does the signing in.</summary>
    public WelcomeViewModel Welcome { get; } = welcome;

    public string Title => $"Opening {tenantName}…";

    public string Hint => "If your browser asks you to sign in, finish there and Tenantry will carry on.";

    /// <summary>Stops waiting for sign-in and shows the home page.</summary>
    public ICommand CancelCommand => Welcome.CancelSignInCommand;
}
