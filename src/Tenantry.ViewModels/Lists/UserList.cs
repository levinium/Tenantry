using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.UserFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>The Users list: its columns, views and actions. Everything else is the framework.</summary>
public static class UserList
{
    static readonly ListQuery ByName = ListQuery.Empty with { Sort = [new SortLevel(F.Name)] };

    // The first four start pinned as chips; the rest live under More views until pinned.
    static readonly HashSet<string> PinnedByDefault = ["all", "members", "guests", "blocked"];

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, ByName with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: PinnedByDefault.Contains(id));

    static ChoiceCondition Is(string field, string value) => new(field, [value], []);
    static ChoiceCondition IsNot(string field, string value) => new(field, [], [value]);

    public static IReadOnlyList<SavedView> Views { get; } =
    [
        View("all", "All Users"),
        View("members", "Members", Is(F.Type, F.Member)),
        View("guests", "Guests", Is(F.Type, F.Guest)),
        View("blocked", "Sign-In Blocked", Is(F.Status, F.Blocked)),
        // People only: shared, room and equipment mailbox accounts never sign in by design.
        View("inactive", "Inactive 90+ Days", Is(F.Status, F.Active), new ChoiceCondition(F.Type, [F.Member, F.Guest], []),
            ConditionGroup.Any(new DateCondition(F.LastSignIn, DateOp.OlderThanDays, 90), new DateCondition(F.LastSignIn, DateOp.Never))),
        View("licensed-blocked", "Licensed But Blocked", Is(F.Status, F.Blocked), IsNot(F.Licenses, F.Unlicensed)),
        View("unlicensed", "Unlicensed Members", Is(F.Type, F.Member), Is(F.Status, F.Active), Is(F.Licenses, F.Unlicensed)),
        View("guests-never", "Guests Never Signed In", Is(F.Type, F.Guest), new DateCondition(F.LastSignIn, DateOp.Never)),
        View("shared", "Shared Mailboxes", Is(F.Mailbox, F.SharedMailbox)),
        View("forwarding", "Forwarding On", new ChoiceCondition(F.Forwarding, [F.ForwardsExternally, F.ForwardsInternally], [])),
        View("forwarding-external", "Forwarding Outside the Organization", Is(F.Forwarding, F.ForwardsExternally)),
        View("nearly-full", "Mailbox Nearly Full", Is(F.Storage, F.NearlyFull)),
        View("auto-reply", "Automatic Reply On", new ChoiceCondition(F.AutoReply, [F.On, F.Scheduled], [])),
        View("hidden", "Hidden from the Address List", Is(F.AddressList, F.Hidden)),
        // Older ways in that often sign in without multi-factor sign-in: worth reviewing.
        View("pop-imap", "POP or IMAP On", new ChoiceCondition(F.EmailAppsOn, ["POP", "IMAP"], [])),
        View("hold", "Litigation Hold On", Is(F.LitigationHold, F.On)),
        View("archive", "Archive On", Is(F.Archive, F.On)),
        View("licensed-shared", "Licensed Shared Mailboxes", Is(F.Mailbox, F.SharedMailbox), IsNot(F.Licenses, F.Unlicensed)),
        // People who can sign in but haven't set up multi-factor sign-in: the first thing an attacker looks for.
        View("no-mfa", "No MFA Registered", Is(F.Status, F.Active), new ChoiceCondition(F.Type, [F.Member, F.Guest], []), Is(F.Mfa, F.MfaNotRegistered)),
        View("admins", "Admins", Is(F.AdminRole, F.IsAdmin)),
        View("admins-no-mfa", "Admins Without MFA", Is(F.AdminRole, F.IsAdmin), Is(F.Mfa, F.MfaNotRegistered)),
        // Microsoft 365 Apps activate on up to five computers per person: at four, the next new PC may be refused.
        View("office-near-limit", "Office on 4+ Computers", new NumberCondition(F.OfficeComputers, 4, null)),
    ];

    /// <summary>Member, Guest, or (from Exchange) Shared mailbox, Room or Equipment.</summary>
    public static (string Text, BadgeKind Kind) TypeBadge(UserSummary u) => F.TypeOf(u) switch
    {
        F.Guest => (F.Guest, BadgeKind.Info),
        F.Member => (F.Member, BadgeKind.Neutral),
        var mailbox => (mailbox, BadgeKind.Success),
    };

    /// <summary>Last sign-in reads amber after 90 days or never, red after 180.</summary>
    public static BadgeKind? SignInTone(TimeSpan? age) => age switch
    {
        null => BadgeKind.Warning,
        { TotalDays: > 180 } => BadgeKind.Danger,
        { TotalDays: > 90 } => BadgeKind.Warning,
        _ => null,
    };

    /// <param name="preview">Builds the preview pane's view model; a read-only record page when null.</param>
    public static ListDefinition<UserSummary> Definition(Func<DateTimeOffset> now, Func<UserSummary, object>? preview = null) => new()
    {
        Key = "users",
        Title = "Users",
        Noun = "users",
        Fields = F.All,
        Id = u => u.Id,
        Views = Views,
        SearchHint = "Search names, addresses, departments, titles, licenses… or try dept:finance -status:blocked",
        Preview = preview ?? (u => new UserDetailViewModel(u, now())),
        Actions =
        [
            new RowAction<UserSummary>("Open in Entra Admin Center", "Globe",
                u => Platform.OpenUrl($"https://entra.microsoft.com/#view/Microsoft_AAD_UsersAndTenants/UserProfileMenuBlade/~/overview/userId/{u.Id}")),
        ],
        Columns =
        [
            new() { Key = F.Name, Header = "Name", Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 290,
                Text = u => u.DisplayName,
                Subtitle = u => u.Kind == UserKind.Guest && u.Mail is { Length: > 0 } m ? m : u.UserPrincipalName,
                Initials = u => Display.Initials(u.DisplayName) },
            new() { Key = F.Department, Header = "Department", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 170, Text = u => u.Department },
            new() { Key = F.Title, Header = "Job Title", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 210, Text = u => u.JobTitle },
            new() { Key = F.Status, Header = "Status", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 120,
                Badge = u => u.AccountEnabled ? (F.Active, BadgeKind.Success) : (F.Blocked, BadgeKind.Danger) },
            new() { Key = F.Type, Header = "Type", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 120,
                Badge = TypeBadge },
            new() { Key = F.Licenses, Header = "Licenses", Cell = CellKind.Badges, MinWidth = 180, DefaultWidth = 250,
                Badges = u => u.Licenses, EmptyText = F.Unlicensed },
            new() { Key = F.LastSignIn, Header = "Last Sign-In", Cell = CellKind.Date, MinWidth = 140, DefaultWidth = 150,
                Date = u => u.LastSignIn, EmptyText = "Never", AgeTone = SignInTone },
            new() { Key = F.Created, Header = "Created", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 140, DefaultVisible = false,
                Date = u => u.CreatedAt },
            new() { Key = F.Email, Header = "Email", Cell = CellKind.Text, MinWidth = 200, DefaultWidth = 240, DefaultVisible = false, Text = u => u.Mail },
            new() { Key = F.SignInName, Header = "Sign-In Name", Cell = CellKind.Text, MinWidth = 220, DefaultWidth = 260, DefaultVisible = false, Text = u => u.UserPrincipalName },
            new() { Key = F.LicenseCount, Header = "License Count", Cell = CellKind.Number, MinWidth = 110, DefaultWidth = 120, DefaultVisible = false,
                Number = u => u.Licenses.Count },
            new() { Key = F.Mailbox, Header = "Mailbox", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 120, DefaultVisible = false,
                Badge = u => u.Mailbox is { } m ? (m.Type, m.Type == F.SharedMailbox ? BadgeKind.Info : BadgeKind.Neutral) : (F.NoMailbox, BadgeKind.Neutral) },
            new() { Key = F.Forwarding, Header = "Forwarding", Cell = CellKind.Badge, MinWidth = 150, DefaultWidth = 190, DefaultVisible = false,
                Badge = u => u.Mailbox?.Forwarding switch
                {
                    ForwardingKind.External => (F.ForwardsExternally, BadgeKind.Danger),
                    ForwardingKind.Internal => (F.ForwardsInternally, BadgeKind.Info),
                    _ => (F.NotForwarding, BadgeKind.Neutral),
                } },
            new() { Key = F.ForwardingTo, Header = "Forwards To", Cell = CellKind.Text, MinWidth = 180, DefaultWidth = 220, DefaultVisible = false,
                Text = u => u.Mailbox?.ForwardingTo },
            new() { Key = F.Addresses, Header = "Email Addresses", Cell = CellKind.Text, MinWidth = 220, DefaultWidth = 300, DefaultVisible = false,
                Text = u => u.Mailbox is { } m ? string.Join(", ", m.Aliases.Prepend(m.PrimaryAddress ?? "").Where(a => a.Length > 0)) : null },
            new() { Key = F.LitigationHold, Header = "Litigation Hold", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 130, DefaultVisible = false,
                Text = u => u.Mailbox is { } m ? (m.LitigationHold ? F.On : F.Off) : null },
            new() { Key = F.Archive, Header = "Archive", Cell = CellKind.Text, MinWidth = 100, DefaultWidth = 110, DefaultVisible = false,
                Text = u => u.Mailbox is { } m ? (m.ArchiveEnabled ? F.On : F.Off) : null },
            new() { Key = F.AddressList, Header = "Address List", Cell = CellKind.Text, MinWidth = 110, DefaultWidth = 120, DefaultVisible = false,
                Text = u => u.Mailbox is { } m ? (m.HiddenFromAddressList ? F.Hidden : F.Shown) : null },
            new() { Key = F.AutoReply, Header = "Automatic Replies", Cell = CellKind.Badge, MinWidth = 140, DefaultWidth = 150, DefaultVisible = false,
                Badge = u => u.Mailbox?.AutoReply switch
                {
                    AutoReplyState.On => (F.On, BadgeKind.Info),
                    AutoReplyState.Scheduled => (F.Scheduled, BadgeKind.Info),
                    AutoReplyState.Off => (F.Off, BadgeKind.Neutral),
                    _ => (u.Mailbox is null ? F.NoMailbox : F.NotReadYet, BadgeKind.Neutral),
                } },
            new() { Key = F.StorageUsed, Header = "Storage Used (%)", Cell = CellKind.Number, MinWidth = 130, DefaultWidth = 140, DefaultVisible = false,
                Number = u => u.Mailbox?.UsedPercent is { } p ? Math.Round(p) : null },
            new() { Key = F.Mfa, Header = "MFA", Cell = CellKind.Badge, MinWidth = 120, DefaultWidth = 140, DefaultVisible = false,
                Badge = u => u.Security is not { } s ? (F.NotReadYet, BadgeKind.Neutral)
                    : s.MfaRegistered ? (F.MfaRegistered, BadgeKind.Success) : (F.MfaNotRegistered, BadgeKind.Warning) },
            new() { Key = F.AdminRole, Header = "Admin Role", Cell = CellKind.Text, MinWidth = 110, DefaultWidth = 120, DefaultVisible = false,
                Text = u => u.Security is { } s ? (s.IsAdmin ? F.IsAdmin : F.NotAdmin) : null },
            new() { Key = F.OfficeComputers, Header = "Office on Computers", Cell = CellKind.Number, MinWidth = 140, DefaultWidth = 160, DefaultVisible = false,
                Number = u => OfficeActivations.Computers(u.Activations) },
            new() { Key = F.OfficeMobile, Header = "Office on Phones and Tablets", Cell = CellKind.Number, MinWidth = 160, DefaultWidth = 200, DefaultVisible = false,
                Number = u => OfficeActivations.Mobile(u.Activations) },
            new() { Key = F.OfficeLastActivated, Header = "Office Last Activated", Cell = CellKind.Date, MinWidth = 150, DefaultWidth = 170, DefaultVisible = false,
                Date = u => OfficeActivations.LastActivated(u.Activations) is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
                EmptyText = "Never" },
        ],
    };
}
