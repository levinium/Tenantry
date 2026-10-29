using System.Globalization;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.LogFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>The archived history lists (10): sign-ins, directory audits and Exchange admin actions.</summary>
public static class LogLists
{
    static readonly ListQuery Newest = ListQuery.Empty with { Sort = [new SortLevel(F.When, Descending: true)] };

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, Newest with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: true);

    static readonly Condition Failed = new ChoiceCondition(F.Result, [F.Failed], []);

    public static (string Text, BadgeKind Kind) ResultBadge(bool succeeded) => succeeded ? (F.Success, BadgeKind.Success) : (F.Failed, BadgeKind.Danger);

    public static ListDefinition<SignInEntry> SignIns(Func<SignInEntry, object> preview) => new()
    {
        Key = "signinhistory",
        Title = "Sign-In History",
        Noun = "sign-ins",
        Fields = F.SignIns,
        Id = s => s.Id,
        Views =
        [
            View("all", "All Sign-Ins"),
            View("failed", "Failed", Failed),
            View("single", "Single-Factor", new ChoiceCondition(F.Auth, ["Single-factor"], [])),
            View("legacy", "Older Mail Apps", new ChoiceCondition(F.Client, ["IMAP4", "POP3", "Authenticated SMTP", "Exchange ActiveSync", "Other clients"], [])),
        ],
        SearchHint = "Search people, apps, addresses and places… or try result:failed",
        Preview = preview,
        Columns =
        [
            new() { Key = F.When, Header = "When", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 160, Date = s => s.At },
            new() { Key = F.Who, Header = "Person", Cell = CellKind.Primary, MinWidth = 200, DefaultWidth = 240, Text = s => s.User, Subtitle = s => s.Upn ?? "",
                Initials = s => Display.Initials(s.User) },
            new() { Key = F.App, Header = "App", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 200, Text = s => s.App },
            new() { Key = F.Result, Header = "Result", Cell = CellKind.Badge, MinWidth = 100, DefaultWidth = 110, Badge = s => ResultBadge(s.Succeeded) },
            new() { Key = F.Location, Header = "Location", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 200, Text = s => s.Location ?? "" },
            new() { Key = F.Ip, Header = "IP Address", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 150, Text = s => s.Ip ?? "" },
            new() { Key = F.Auth, Header = "Sign-In Step", Cell = CellKind.Text, MinWidth = 110, DefaultWidth = 130, Text = s => s.Auth ?? "" },
            new() { Key = F.Client, Header = "Client", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 150, Text = s => s.Client ?? "", DefaultVisible = false },
            new() { Key = F.Device, Header = "Device", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 220,
                Text = s => string.Join(" · ", new[] { s.Device, s.Os, s.Browser }.OfType<string>()), DefaultVisible = false },
            new() { Key = F.Kind, Header = "Kind", Cell = CellKind.Text, MinWidth = 110, DefaultWidth = 130, Text = s => s.Interactive ? "Interactive" : "Non-Interactive", DefaultVisible = false },
        ],
    };

    public static ListDefinition<DirectoryAuditEntry> Audits(Func<DirectoryAuditEntry, object> preview) => new()
    {
        Key = "audithistory",
        Title = "Directory Audits",
        Noun = "events",
        Fields = F.Audits,
        Id = a => a.Id,
        Views =
        [
            View("all", "All Events"),
            View("failed", "Failed", Failed),
            View("roles", "Admin Roles", new ChoiceCondition(F.Category, ["RoleManagement"], [])),
            View("apps", "Apps and Consents", new ChoiceCondition(F.Category, ["ApplicationManagement"], [])),
            View("users", "User Changes", new ChoiceCondition(F.Category, ["UserManagement"], [])),
        ],
        SearchHint = "Search activities, who did them and what they changed…",
        Preview = preview,
        Columns =
        [
            new() { Key = F.When, Header = "When", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 160, Date = a => a.At },
            new() { Key = F.Activity, Header = "Activity", Cell = CellKind.Primary, MinWidth = 200, DefaultWidth = 260, Text = a => a.Activity, Subtitle = a => a.Category,
                Initials = a => Display.Initials(a.Activity) },
            new() { Key = F.Who, Header = "By", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 220, Text = a => a.By ?? "" },
            new() { Key = F.Target, Header = "Target", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 240, Text = a => string.Join(", ", a.Targets) },
            new() { Key = F.Result, Header = "Result", Cell = CellKind.Badge, MinWidth = 100, DefaultWidth = 110, Badge = a => ResultBadge(a.Succeeded) },
            new() { Key = F.Service, Header = "Service", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 160, Text = a => a.Service ?? "", DefaultVisible = false },
        ],
    };

    public static ListDefinition<ExchangeAdminEntry> ExchangeAdmin(Func<ExchangeAdminEntry, object> preview) => new()
    {
        Key = "exchangeadminhistory",
        Title = "Exchange Admin",
        Noun = "actions",
        Fields = F.ExchangeAdmin,
        Id = e => e.Id,
        Views = [View("all", "All Actions"), View("failed", "Failed", Failed)],
        SearchHint = "Search commands, who ran them, objects and parameters…",
        Preview = preview,
        Columns =
        [
            new() { Key = F.When, Header = "When", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 160, Date = e => e.At },
            new() { Key = F.Operation, Header = "Command", Cell = CellKind.Primary, MinWidth = 200, DefaultWidth = 240, Text = e => e.Operation, Subtitle = e => e.Object ?? "",
                Initials = e => Display.Initials(e.Operation) },
            new() { Key = F.Who, Header = "By", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 220, Text = e => e.By ?? "" },
            new() { Key = F.Parameters, Header = "Parameters", Cell = CellKind.Text, MinWidth = 200, DefaultWidth = 360, Text = e => e.Parameters ?? "" },
            new() { Key = F.Result, Header = "Result", Cell = CellKind.Badge, MinWidth = 100, DefaultWidth = 110, Badge = e => ResultBadge(e.Succeeded) },
        ],
    };

    static string Day(DateTimeOffset when) => when.ToLocalTime().ToString("d MMM yyyy h:mm:ss tt", CultureInfo.CurrentCulture);

    public static Records.AppPreviewViewModel SignInPreview(SignInEntry s, Action<string>? openUser) =>
        new(s.User, Day(s.At), [new Records.HealthChip(s.Succeeded ? F.Success : F.Failed, s.Succeeded ? BadgeKind.Success : BadgeKind.Danger, "Whether the sign-in worked")],
        [
            new("Sign-In Name", s.Upn ?? ""),
            new("App", s.Resource is { } r && r != s.App ? $"{s.App} (to {r})" : s.App),
            .. s.Failure is { } why ? [new Records.SummaryLine("Why It Failed", why)] : Array.Empty<Records.SummaryLine>(),
            new("Where", string.Join(" · ", new[] { s.Location, s.Ip }.OfType<string>())),
            new("Device", string.Join(" · ", new[] { s.Device, s.Os, s.Browser }.OfType<string>()) is { Length: > 0 } d ? d : "Not given"),
            new("Client", s.Client ?? "Not given"),
            new("Sign-In Step", s.Auth ?? "Not given"),
            new("Conditional Access", s.ConditionalAccess ?? "Not given"),
            .. s.Risk is { } risk ? [new Records.SummaryLine("Risk", risk)] : Array.Empty<Records.SummaryLine>(),
        ],
        "Open Person", openUser is null || s.UserId is null ? null : () => openUser(s.UserId));

    public static Records.AppPreviewViewModel AuditPreview(DirectoryAuditEntry a) =>
        new(a.Activity, Day(a.At), [new Records.HealthChip(a.Succeeded ? F.Success : F.Failed, a.Succeeded ? BadgeKind.Success : BadgeKind.Danger, "Whether it worked")],
        [
            new("By", a.By ?? "Not given"),
            new("Target", a.Targets.Count == 0 ? "None given" : string.Join(", ", a.Targets)),
            new("Category", a.Category),
            new("Service", a.Service ?? "Not given"),
            .. a.Reason is { } why ? [new Records.SummaryLine("Reason", why)] : Array.Empty<Records.SummaryLine>(),
        ],
        "Open", null);

    public static Records.AppPreviewViewModel ExchangePreview(ExchangeAdminEntry e) =>
        new(e.Operation, Day(e.At), [new Records.HealthChip(e.Succeeded ? F.Success : F.Failed, e.Succeeded ? BadgeKind.Success : BadgeKind.Danger, "Whether it worked")],
        [
            new("By", e.By ?? "Not given"),
            new("Object", e.Object ?? "Not given"),
            new("Parameters", e.Parameters ?? "None"),
        ],
        "Open", null);
}
