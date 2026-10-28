using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.AppFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>App registrations and enterprise apps (8c): the two lists the Entra admin center keeps, with views for a review.</summary>
public static class AppList
{
    static readonly ListQuery ByName = ListQuery.Empty with { Sort = [new SortLevel(F.Name)] };

    static readonly HashSet<string> PinnedByDefault = ["all", "expired", "soon", "noowners", "enterprise", "thirdparty", "ours", "disabled"];

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, ByName with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: PinnedByDefault.Contains(id));

    public static IReadOnlyList<SavedView> RegistrationViews { get; } =
    [
        View("all", "All App Registrations"),
        // Apps whose sign-in has stopped working (or will soon): renew or remove the credential.
        View("expired", "Expired Secrets or Certificates", new ChoiceCondition(F.Credentials, [F.Expired, F.SomeExpired], [])),
        View("soon", "Expiring Within 30 Days", new ChoiceCondition(F.Credentials, [F.Soon], [])),
        View("noowners", "No Owners", new NumberCondition(F.OwnerCount, null, 0)),
        View("multitenant", "Other Organizations Can Sign In", new ChoiceCondition(F.Audience, [], [AppRegistration.AudienceText("AzureADMyOrg")])),
    ];

    public static IReadOnlyList<SavedView> EnterpriseViews { get; } =
    [
        // What Entra's Enterprise applications page shows by default.
        View("enterprise", "Enterprise Applications", new ChoiceCondition(F.Integrated, [F.Yes], [])),
        View("thirdparty", "Third-Party Apps", new ChoiceCondition(F.Source, [F.ThirdParty], [])),
        View("ours", "This Organization's Apps", new ChoiceCondition(F.Source, [F.Ours], [])),
        View("disabled", "Sign-In Turned Off", new ChoiceCondition(F.Enabled, [F.Off], [])),
        View("sso", "Single Sign-On Set Up", new ChoiceCondition(F.Sso, [], ["None"])),
        View("microsoft", "Microsoft Apps", new ChoiceCondition(F.Source, [F.Microsoft], [])),
        View("all", "All Service Principals"),
    ];

    public static (string Text, BadgeKind Kind) CredentialBadge(string state) => state switch
    {
        F.Expired => (F.Expired, BadgeKind.Danger),
        F.SomeExpired => (F.SomeExpired, BadgeKind.Warning),
        F.Soon => (F.Soon, BadgeKind.Warning),
        F.Current => (F.Current, BadgeKind.Success),
        _ => ("", BadgeKind.Neutral),
    };

    public static ListDefinition<AppRegistration> Registrations(Func<AppRegistration, object> preview, Func<DateTimeOffset> now) => new()
    {
        Key = "appregistrations",
        Title = "App Registrations",
        Noun = "apps",
        Fields = F.Registration(now),
        Id = a => a.Id,
        Views = RegistrationViews,
        SearchHint = "Search apps, their IDs and owners… or try credentials:expired",
        Preview = preview,
        Columns =
        [
            new() { Key = F.Name, Header = "App", Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 300,
                Text = a => a.Name, Subtitle = a => a.AppId, Initials = a => Display.Initials(a.Name) },
            new() { Key = F.Credentials, Header = "Secrets & Certificates", Cell = CellKind.Badge, MinWidth = 150, DefaultWidth = 170,
                Badge = a => CredentialBadge(a.CredentialState(now())) },
            new() { Key = F.NextExpiry, Header = "Next Expiry", Cell = CellKind.Date, MinWidth = 120, DefaultWidth = 130, Date = a => a.NextExpiry(now()) },
            new() { Key = F.Audience, Header = "Who Can Sign In", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 220, Text = a => AppRegistration.AudienceText(a.Audience) },
            new() { Key = F.Permissions, Header = "API Permissions", Cell = CellKind.Number, MinWidth = 110, DefaultWidth = 130, Number = a => a.Requested.Count },
            new() { Key = F.Owners, Header = "Owners", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 220, Text = a => a.Owners.Count == 0 ? "" : string.Join(", ", a.Owners.Select(o => o.Name)) },
            new() { Key = F.Created, Header = "Created", Cell = CellKind.Date, MinWidth = 120, DefaultWidth = 130, Date = a => a.Created },
            new() { Key = F.Secrets, Header = "Secrets", Cell = CellKind.Number, MinWidth = 80, DefaultWidth = 90, DefaultVisible = false, Number = a => a.Credentials.Count(c => c.IsSecret) },
            new() { Key = F.Certificates, Header = "Certificates", Cell = CellKind.Number, MinWidth = 100, DefaultWidth = 110, DefaultVisible = false,
                Number = a => a.Credentials.Count(c => !c.IsSecret) },
        ],
    };

    public static ListDefinition<EnterpriseApp> Enterprise(Func<EnterpriseApp, object> preview) => new()
    {
        Key = "enterpriseapps",
        Title = "Enterprise Apps",
        Noun = "apps",
        Fields = F.Enterprise,
        Id = a => a.Id,
        Views = EnterpriseViews,
        SearchHint = "Search apps, their IDs and publishers… or try from:\"third party\"",
        Preview = preview,
        Columns =
        [
            new() { Key = F.Name, Header = "App", Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 300,
                Text = a => a.Name, Subtitle = a => a.Publisher ?? a.AppId, Initials = a => Display.Initials(a.Name) },
            new() { Key = F.Source, Header = "From", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 160, Text = a => EnterpriseApp.SourceText(a.Source) },
            new() { Key = F.Enabled, Header = "Sign-In", Cell = CellKind.Badge, MinWidth = 100, DefaultWidth = 110,
                Badge = a => a.IsEnabled ? ("", BadgeKind.Neutral) : (F.Off, BadgeKind.Warning) },
            new() { Key = F.Assignment, Header = "Assignment", Cell = CellKind.Text, MinWidth = 110, DefaultWidth = 120, Text = a => a.AssignmentRequired ? F.Required : "" },
            new() { Key = F.Visible, Header = "Visible", Cell = CellKind.Text, MinWidth = 90, DefaultWidth = 100, Text = a => a.IsVisible ? "" : F.Hidden },
            new() { Key = F.Sso, Header = "Single Sign-On", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 130, Text = a => EnterpriseApp.SsoText(a.SsoMode) ?? "" },
            new() { Key = F.Created, Header = "Created", Cell = CellKind.Date, MinWidth = 120, DefaultWidth = 130, Date = a => a.Created },
            new() { Key = F.AppId, Header = "Application ID", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 290, DefaultVisible = false, Text = a => a.AppId },
            new() { Key = F.Type, Header = "Type", Cell = CellKind.Text, MinWidth = 110, DefaultWidth = 130, DefaultVisible = false, Text = a => a.Type },
        ],
    };
}
