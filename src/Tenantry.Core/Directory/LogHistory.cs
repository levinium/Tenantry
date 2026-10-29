using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>The logs Tenantry archives on this computer (10), by their kind in the archive.</summary>
public static class LogKinds
{
    public const string SignIns = "log.signin";
    public const string NonInteractive = "log.signin.background";
    public const string Audits = "log.audit";
    public const string ExchangeAdmin = "log.exchangeadmin";

    /// <summary>How far back Microsoft keeps each log (Entra ID P1), so how far a first read can reach.</summary>
    public const int MicrosoftKeepsDays = 30;

    /// <summary>Non-interactive sign-ins are many times the rest, so they're kept only this long.</summary>
    public const int BackgroundKeepDays = 30;

    static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>An entry as the archive stores it.</summary>
    public static string Write(object entry) => System.Text.Json.JsonSerializer.Serialize(entry, entry.GetType(), Json);

    /// <summary>An archived event back as its entry, or null if it can't be read.</summary>
    public static T? Read<T>(Tenantry.Core.Cache.ArchivedEvent e) where T : class
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(e.Json, Json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>One sign-in, trimmed to what the history shows.</summary>
/// <param name="Failure">Why it failed, or null when it succeeded.</param>
/// <param name="Auth">"Single-factor" or "Multifactor".</param>
public sealed record SignInEntry(string Id, DateTimeOffset At, string User, string? Upn, string? UserId, string App, string? Resource, string? Ip,
    string? Location, string? Client, string? Failure, string? ConditionalAccess, string? Auth, string? Device, string? Os, string? Browser, string? Risk,
    bool Interactive)
{
    public bool Succeeded => Failure is null;
}

/// <summary>One directory audit event: who did what to what.</summary>
public sealed record DirectoryAuditEntry(string Id, DateTimeOffset At, string Activity, string Category, bool Succeeded, string? Reason, string? By,
    IReadOnlyList<string> Targets, string? Service);

/// <summary>One Exchange admin action (from the unified audit log): a cmdlet someone ran.</summary>
public sealed record ExchangeAdminEntry(string Id, DateTimeOffset At, string Operation, string? By, string? Object, string? Parameters, bool Succeeded);

/// <summary>The history lists' fields.</summary>
public static class LogFields
{
    public const string When = "when";
    public const string Who = "who";
    public const string App = "app";
    public const string Result = "result";
    public const string Ip = "ip";
    public const string Location = "location";
    public const string Client = "client";
    public const string Auth = "auth";
    public const string Device = "device";
    public const string Activity = "activity";
    public const string Category = "category";
    public const string Target = "target";
    public const string Service = "service";
    public const string Operation = "operation";
    public const string Parameters = "parameters";
    public const string Kind = "kind";

    public const string Success = "Succeeded";
    public const string Failed = "Failed";

    public static IReadOnlyList<Field<SignInEntry>> SignIns { get; } =
    [
        new() { Key = When, Label = "When", Kind = FieldKind.Date, Date = s => s.At },
        new() { Key = Who, Label = "Person", Kind = FieldKind.Text, Searchable = true, Aliases = ["user"], Text = s => $"{s.User} {s.Upn}" },
        new() { Key = App, Label = "App", Kind = FieldKind.Choice, Searchable = true, Text = s => s.App },
        new() { Key = Result, Label = "Result", Kind = FieldKind.Choice, ChoiceOrder = [Success, Failed], Text = s => s.Succeeded ? Success : Failed },
        new() { Key = Ip, Label = "IP Address", Kind = FieldKind.Text, Searchable = true, Text = s => s.Ip },
        new() { Key = Location, Label = "Location", Kind = FieldKind.Text, Searchable = true, Aliases = ["where", "country"], Text = s => s.Location },
        new() { Key = Client, Label = "Client", Kind = FieldKind.Choice, Text = s => s.Client },
        new() { Key = Auth, Label = "Sign-In Step", Kind = FieldKind.Choice, Text = s => s.Auth },
        new() { Key = Device, Label = "Device", Kind = FieldKind.Text, Searchable = true, Text = s => string.Join(' ', new[] { s.Device, s.Os, s.Browser }.OfType<string>()) },
        new() { Key = Kind, Label = "Kind", Kind = FieldKind.Choice, ChoiceOrder = ["Interactive", "Non-Interactive"], Text = s => s.Interactive ? "Interactive" : "Non-Interactive" },
    ];

    public static IReadOnlyList<Field<DirectoryAuditEntry>> Audits { get; } =
    [
        new() { Key = When, Label = "When", Kind = FieldKind.Date, Date = a => a.At },
        new() { Key = Activity, Label = "Activity", Kind = FieldKind.Choice, Searchable = true, Text = a => a.Activity },
        new() { Key = Category, Label = "Category", Kind = FieldKind.Choice, Text = a => a.Category },
        new() { Key = Who, Label = "By", Kind = FieldKind.Text, Searchable = true, Aliases = ["user"], Text = a => a.By },
        new() { Key = Target, Label = "Target", Kind = FieldKind.Text, Searchable = true, Text = a => string.Join(", ", a.Targets) },
        new() { Key = Result, Label = "Result", Kind = FieldKind.Choice, ChoiceOrder = [Success, Failed], Text = a => a.Succeeded ? Success : Failed },
        new() { Key = Service, Label = "Service", Kind = FieldKind.Choice, Text = a => a.Service },
    ];

    public static IReadOnlyList<Field<ExchangeAdminEntry>> ExchangeAdmin { get; } =
    [
        new() { Key = When, Label = "When", Kind = FieldKind.Date, Date = e => e.At },
        new() { Key = Operation, Label = "Command", Kind = FieldKind.Choice, Searchable = true, Text = e => e.Operation },
        new() { Key = Who, Label = "By", Kind = FieldKind.Text, Searchable = true, Aliases = ["user"], Text = e => e.By },
        new() { Key = Target, Label = "Object", Kind = FieldKind.Text, Searchable = true, Text = e => e.Object },
        new() { Key = Parameters, Label = "Parameters", Kind = FieldKind.Text, Searchable = true, Text = e => e.Parameters },
        new() { Key = Result, Label = "Result", Kind = FieldKind.Choice, ChoiceOrder = [Success, Failed], Text = e => e.Succeeded ? Success : Failed },
    ];
}
