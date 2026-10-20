using Tenantry.Core.Directory;

namespace Tenantry.ViewModels;

/// <summary>A user's record page. Read-only until the change pipeline exists.</summary>
public sealed class UserDetailViewModel(UserSummary user, DateTimeOffset? now = null) : TabViewModel
{
    readonly UserRow _row = new(user, now ?? DateTimeOffset.Now);

    public UserSummary User { get; } = user;

    public override string Title => User.DisplayName;

    public string Initials => _row.Initials;

    public string StatusText => User.AccountEnabled ? "Sign-in allowed" : "Sign-in blocked";
    public BadgeKind StatusKind => _row.StatusKind;

    public string KindText => _row.KindText;
    public BadgeKind KindKind => _row.KindKind;

    public string Email => User.Mail ?? "No mailbox address";
    public string Department => User.Department ?? "Not set";
    public string JobTitle => User.JobTitle ?? "Not set";

    public string LastSignInText => _row.LastSignInText;
    public string LastSignInExact => _row.LastSignInExact;

    public string CreatedText => User.CreatedAt.ToLocalTime().ToString("MMMM d, yyyy");

    public IReadOnlyList<string> Licenses => User.Licenses;
    public bool HasLicenses => User.Licenses.Count > 0;
}
