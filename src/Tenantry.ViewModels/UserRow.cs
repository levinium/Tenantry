using Tenantry.Core.Directory;

namespace Tenantry.ViewModels;

/// <summary>A user as a list row: the record plus display-ready values, computed once per load.</summary>
public sealed class UserRow(UserSummary user, DateTimeOffset now)
{
    public UserSummary User { get; } = user;

    public string DisplayName => User.DisplayName;
    public string UserPrincipalName => User.UserPrincipalName;
    public string? Department => User.Department;
    public string? JobTitle => User.JobTitle;

    public string Initials { get; } = Display.Initials(user.DisplayName);

    /// <summary>
    /// The line under the name: a guest's real address rather than its mangled
    /// <c>name_domain#EXT#@tenant</c> sign-in name, which nobody can read at a glance.
    /// </summary>
    public string SecondaryText => User.Kind == UserKind.Guest && User.Mail is { Length: > 0 } mail
        ? mail
        : User.UserPrincipalName;

    public string StatusText => User.AccountEnabled ? "Active" : "Blocked";
    public BadgeKind StatusKind => User.AccountEnabled ? BadgeKind.Success : BadgeKind.Danger;

    public string KindText => User.Kind == UserKind.Guest ? "Guest" : "Member";
    public BadgeKind KindKind => User.Kind == UserKind.Guest ? BadgeKind.Info : BadgeKind.Neutral;

    public string LastSignInText { get; } = Display.Relative(user.LastSignIn, now);
    public string LastSignInExact { get; } = Display.Exact(user.LastSignIn);

    /// <summary>Sorts never-signed-in users first when ascending, which is what an audit wants.</summary>
    public DateTimeOffset LastSignInSort => User.LastSignIn ?? DateTimeOffset.MinValue;

    public string LicensesText => User.Licenses.Count == 0 ? "No license" : string.Join(", ", User.Licenses);
    public bool IsUnlicensed => User.Licenses.Count == 0;
}
