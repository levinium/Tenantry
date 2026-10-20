using Tenantry.Core.Directory;

namespace Tenantry.ViewModels;

/// <summary>A user's record page. Read-only until the change pipeline exists.</summary>
public sealed class UserDetailViewModel(UserSummary user) : TabViewModel
{
    public UserSummary User { get; } = user;

    public override string Title => User.DisplayName;

    public string Status => User.AccountEnabled ? "Enabled" : "Sign-in blocked";

    public string KindText => User.Kind == UserKind.Guest ? "Guest" : "Member";

    public string LastSignInText => User.LastSignIn?.ToLocalTime().ToString("g") ?? "Never";

    public string CreatedText => User.CreatedAt.ToLocalTime().ToString("d");

    public string LicensesText => User.Licenses.Count == 0 ? "None" : string.Join(", ", User.Licenses);
}
