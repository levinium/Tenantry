using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.UserFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>The Users list: its columns, views and actions. Everything else is the framework.</summary>
public static class UserList
{
    static readonly ListQuery ByName = ListQuery.Empty with { Sort = [new SortLevel(F.Name)] };

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, ByName with { Where = ConditionGroup.All(where) }, BuiltIn: true);

    static ChoiceCondition Is(string field, string value) => new(field, [value], []);
    static ChoiceCondition IsNot(string field, string value) => new(field, [], [value]);

    public static IReadOnlyList<SavedView> Views { get; } =
    [
        View("all", "All users"),
        View("members", "Members", Is(F.Type, F.Member)),
        View("guests", "Guests", Is(F.Type, F.Guest)),
        View("blocked", "Sign-in blocked", Is(F.Status, F.Blocked)),
        View("inactive", "Inactive 90+ days", Is(F.Status, F.Active),
            ConditionGroup.Any(new DateCondition(F.LastSignIn, DateOp.OlderThanDays, 90), new DateCondition(F.LastSignIn, DateOp.Never))),
        View("licensed-blocked", "Licensed but blocked", Is(F.Status, F.Blocked), IsNot(F.Licenses, F.Unlicensed)),
        View("unlicensed", "Unlicensed members", Is(F.Type, F.Member), Is(F.Status, F.Active), Is(F.Licenses, F.Unlicensed)),
        View("guests-never", "Guests never signed in", Is(F.Type, F.Guest), new DateCondition(F.LastSignIn, DateOp.Never)),
    ];

    /// <summary>Last sign-in reads amber after 90 days or never, red after 180.</summary>
    public static BadgeKind? SignInTone(TimeSpan? age) => age switch
    {
        null => BadgeKind.Warning,
        { TotalDays: > 180 } => BadgeKind.Danger,
        { TotalDays: > 90 } => BadgeKind.Warning,
        _ => null,
    };

    public static ListDefinition<UserSummary> Definition(Func<DateTimeOffset> now) => new()
    {
        Key = "users",
        Title = "Users",
        Noun = "users",
        Fields = F.All,
        Id = u => u.Id,
        Views = Views,
        SearchHint = "Search names, addresses, departments, titles, licenses… or try dept:finance -status:blocked",
        Preview = u => new UserDetailViewModel(u, now()),
        Actions =
        [
            new RowAction<UserSummary>("Open in Entra admin center", "Globe",
                u => Platform.OpenUrl($"https://entra.microsoft.com/#view/Microsoft_AAD_UsersAndTenants/UserProfileMenuBlade/~/overview/userId/{u.Id}")),
        ],
        Columns =
        [
            new() { Key = F.Name, Header = "Name", Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 290,
                Text = u => u.DisplayName,
                Subtitle = u => u.Kind == UserKind.Guest && u.Mail is { Length: > 0 } m ? m : u.UserPrincipalName,
                Initials = u => Display.Initials(u.DisplayName) },
            new() { Key = F.Department, Header = "Department", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 170, Text = u => u.Department },
            new() { Key = F.Title, Header = "Job title", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 210, Text = u => u.JobTitle },
            new() { Key = F.Status, Header = "Status", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 120,
                Badge = u => u.AccountEnabled ? (F.Active, BadgeKind.Success) : (F.Blocked, BadgeKind.Danger) },
            new() { Key = F.Type, Header = "Type", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 120,
                Badge = u => u.Kind == UserKind.Guest ? (F.Guest, BadgeKind.Info) : (F.Member, BadgeKind.Neutral) },
            new() { Key = F.Licenses, Header = "Licenses", Cell = CellKind.Badges, MinWidth = 180, DefaultWidth = 250,
                Badges = u => u.Licenses, EmptyText = F.Unlicensed },
            new() { Key = F.LastSignIn, Header = "Last sign-in", Cell = CellKind.Date, MinWidth = 140, DefaultWidth = 150,
                Date = u => u.LastSignIn, EmptyText = "Never", AgeTone = SignInTone },
            new() { Key = F.Created, Header = "Created", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 140, DefaultVisible = false,
                Date = u => u.CreatedAt },
            new() { Key = F.Email, Header = "Email", Cell = CellKind.Text, MinWidth = 200, DefaultWidth = 240, DefaultVisible = false, Text = u => u.Mail },
            new() { Key = F.SignInName, Header = "Sign-in name", Cell = CellKind.Text, MinWidth = 220, DefaultWidth = 260, DefaultVisible = false, Text = u => u.UserPrincipalName },
            new() { Key = F.LicenseCount, Header = "License count", Cell = CellKind.Number, MinWidth = 110, DefaultWidth = 120, DefaultVisible = false,
                Number = u => u.Licenses.Count },
        ],
    };
}
