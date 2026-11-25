using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels.Editing;

/// <summary>
/// Just before Apply (after the local copy is brought up to date): whether something a staged
/// change was based on has since been changed by someone else, as far as the local copy shows.
/// Only the changes whose starting point the copy holds are checked; the rest return null.
/// </summary>
public static class StaleCheck
{
    /// <summary>What it is now when that differs from what the change started from ("now Facilities"), else null.</summary>
    public static string? Reason(ChangeOp change, IDirectoryIndex index) => change switch
    {
        SetUserText t when index.User(t.User.Id) is { } u && UserText(u, t.Property) is { } now && !Same(now.Value, t.Old) =>
            $"Changed since you staged this: {t.PropertyLabel} is now {Show(now.Value)}.",
        SetSignInAllowed s when index.User(s.User.Id) is { } u && u.AccountEnabled != s.Old =>
            $"Changed since you staged this: sign-in is now {(u.AccountEnabled ? "allowed" : "blocked")}.",
        SetLicenseAssigned l when index.User(l.User.Id) is { } u && u.Licenses.Contains(l.SkuName, StringComparer.OrdinalIgnoreCase) != l.Old =>
            $"Changed since you staged this: they {(l.Old ? "no longer have" : "now have")} {l.SkuName}.",
        SetGroupMembership m when index.Group(m.Group.Id) is { } g && In(m.Role == GroupRole.Owner ? g.OwnerList : g.Members, m.Person.Id) != m.Old =>
            $"Changed since you staged this: {m.Person.Name} is {(m.Old ? "no longer" : "now")} {(m.Role == GroupRole.Owner ? "an owner" : "a member")}.",
        SetMailboxAccess a when index.Group(a.Mailbox.Id) is { IsMailbox: true } box
                                && (box.Members.FirstOrDefault(p => p.Id == a.Person.Id) is { } entry && GroupAccess.RightsOf(entry.Access).Contains(a.Right)) != a.Old =>
            $"Changed since you staged this: {a.Person.Name} {(a.Old ? "no longer has" : "now has")} {SetMailboxAccess.RightLabel(a.Right)}.",
        _ => null,
    };

    /// <summary>The user's value for a property the local copy holds; null when it doesn't hold that one.</summary>
    static (string? Value, bool Held)? UserText(UserSummary u, string property) => property switch
    {
        "department" => (u.Department, true),
        "jobTitle" => (u.JobTitle, true),
        "displayName" => (u.DisplayName, true),
        "usageLocation" => (u.UsageLocation, true),
        _ => null,
    };

    static bool In(IReadOnlyList<GroupMember> list, string id) => list.Any(m => m.Id == id);

    static bool Same(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.Ordinal);

    static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "blank" : $"\"{value}\"";
}
