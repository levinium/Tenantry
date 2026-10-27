using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.DeviceFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>The Devices list: Entra's devices joined with Intune's, with health views and bulk actions.</summary>
public static class DeviceList
{
    static readonly ListQuery ByName = ListQuery.Empty with { Sort = [new SortLevel(F.Name)] };

    static readonly HashSet<string> PinnedByDefault = ["all", "windows", "not-compliant", "stale"];

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, ByName with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: PinnedByDefault.Contains(id));

    static ChoiceCondition Is(string field, string value) => new(field, [value], []);

    public static IReadOnlyList<SavedView> Views { get; } =
    [
        View("all", "All Devices"),
        View("windows", "Windows", Is(F.Platform, "Windows")),
        View("macos", "macOS", Is(F.Platform, "macOS")),
        View("ios", "iOS/iPadOS", Is(F.Platform, "iOS/iPadOS")),
        View("android", "Android", Is(F.Platform, "Android")),
        View("not-compliant", "Not Compliant", Is(F.Compliance, F.NotCompliant)),
        // Laptops and desktops only: phones report encryption differently, and Entra-only devices not at all.
        View("not-encrypted", "Not Encrypted", Is(F.Encryption, F.NotEncrypted)),
        View("not-managed", "Not Managed by Intune", Is(F.Managed, F.ManagedNo)),
        // Candidates for clean-up: nothing has signed in on them or checked in for three months.
        View("stale", "Not Seen in 90+ Days",
            ConditionGroup.Any(new DateCondition(F.LastSeen, DateOp.OlderThanDays, 90), new DateCondition(F.LastSeen, DateOp.Never))),
        View("disabled", "Disabled", Is(F.Status, F.Disabled)),
        View("personal", "Personal Devices", Is(F.Ownership, F.Personal)),
        View("no-user", "No Primary User", Is(F.Managed, F.ManagedYes), new TextCondition(F.User, TextOp.IsEmpty, "")),
    ];

    /// <summary>A FluentIcons name for the kind of device.</summary>
    public static string IconFor(DeviceSummary d) => d.Platform switch
    {
        "iOS/iPadOS" when d.Os?.Contains("ipad", StringComparison.OrdinalIgnoreCase) == true => "Tablet",
        "iOS/iPadOS" or "Android" => "Phone",
        _ => "Laptop",
    };

    /// <summary>"Lenovo ThinkPad T14 · Windows 10.0.26100".</summary>
    public static string Describe(DeviceSummary d) => string.Join(" · ", new[]
    {
        string.Join(' ', new[] { d.Manufacturer, d.Model }.Where(s => !string.IsNullOrEmpty(s)).Distinct()),
        string.Join(' ', new[] { d.Os, d.OsVersion }.Where(s => !string.IsNullOrEmpty(s))),
    }.Where(s => s.Length > 0));

    /// <summary>Last seen reads amber after 90 days or never, red after 180 (the user list's tones).</summary>
    public static BadgeKind? SeenTone(TimeSpan? age) => UserList.SignInTone(age);

    public static ListDefinition<DeviceSummary> Definition(Func<DeviceSummary, object> preview) => new()
    {
        Key = "devices",
        Title = "Devices",
        Noun = "devices",
        Fields = F.All,
        Id = d => d.Key,
        Views = Views,
        SearchHint = "Search names, people, models, serial numbers… or try platform:windows compliance:\"not compliant\"",
        Preview = preview,
        Actions =
        [
            new RowAction<DeviceSummary>("Open in Intune Admin Center", "Globe",
                d => Platform.OpenUrl(d.ManagedId is { } managed
                    ? $"https://intune.microsoft.com/#view/Microsoft_Intune_Devices/DeviceSettingsMenuBlade/~/overview/mdmDeviceId/{managed}"
                    : $"https://entra.microsoft.com/#view/Microsoft_AAD_Devices/DeviceDetailsMenuBlade/~/Properties/objectId/{d.ObjectId}")),
        ],
        Columns =
        [
            new() { Key = F.Name, Header = "Name", Cell = CellKind.Primary, MinWidth = 220, DefaultWidth = 260,
                Text = d => d.Name,
                Subtitle = Describe,
                // The platform at a glance, not the name's initials ("AI" for "Abbey's iPhone").
                Initials = d => d.Platform switch { "Windows" => "W", "macOS" => "M", "iOS/iPadOS" => "i", "Android" => "A", "Linux" => "L", _ => "?" } },
            new() { Key = F.User, Header = "Primary User", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 190, Text = d => d.UserName, EmptyText = "No User" },
            new() { Key = F.Platform, Header = "Platform", Cell = CellKind.Text, MinWidth = 110, DefaultWidth = 120, Text = d => d.Platform },
            new() { Key = F.Join, Header = "Join Type", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 130, Text = d => d.JoinType },
            new() { Key = F.Compliance, Header = "Compliance", Cell = CellKind.Badge, MinWidth = 130, DefaultWidth = 140,
                Badge = d => d.Compliant switch
                {
                    true => (F.Compliant, BadgeKind.Success),
                    false => (F.NotCompliant, BadgeKind.Danger),
                    _ => (F.Unknown, BadgeKind.Neutral),
                } },
            new() { Key = F.LastSeen, Header = "Last Seen", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 140,
                Date = d => d.LastSeen, EmptyText = "Never", AgeTone = SeenTone },
            new() { Key = F.Managed, Header = "Intune", Cell = CellKind.Badge, MinWidth = 140, DefaultWidth = 160,
                Badge = d => d.IsManaged ? (F.ManagedYes, BadgeKind.Info) : (F.ManagedNo, BadgeKind.Neutral) },
            new() { Key = F.Encryption, Header = "Encryption", Cell = CellKind.Badge, MinWidth = 120, DefaultWidth = 130,
                Badge = d => d.Encrypted switch
                {
                    true => (F.Encrypted, BadgeKind.Success),
                    false => (F.NotEncrypted, BadgeKind.Warning),
                    _ => (F.Unknown, BadgeKind.Neutral),
                } },
            new() { Key = F.Status, Header = "Status", Cell = CellKind.Badge, MinWidth = 100, DefaultWidth = 110, DefaultVisible = false,
                Badge = d => d.Enabled == false ? (F.Disabled, BadgeKind.Danger) : (F.Enabled, BadgeKind.Success) },
            new() { Key = F.Ownership, Header = "Ownership", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 140, DefaultVisible = false, Text = d => d.Ownership },
            new() { Key = F.OsVersion, Header = "OS Version", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 130, DefaultVisible = false, Text = d => d.OsVersion },
            new() { Key = F.Model, Header = "Model", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 200, DefaultVisible = false,
                Text = d => string.Join(' ', new[] { d.Manufacturer, d.Model }.Where(s => !string.IsNullOrEmpty(s)).Distinct()) },
            new() { Key = F.Serial, Header = "Serial Number", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 160, DefaultVisible = false, Text = d => d.Serial },
            new() { Key = F.LastCheckIn, Header = "Last Intune Check-In", Cell = CellKind.Date, MinWidth = 160, DefaultWidth = 170, DefaultVisible = false,
                Date = d => d.LastCheckIn, EmptyText = "Never" },
            new() { Key = F.Registered, Header = "Registered", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 140, DefaultVisible = false, Date = d => d.Registered },
            new() { Key = F.StorageFree, Header = "Storage Free (GB)", Cell = CellKind.Number, MinWidth = 130, DefaultWidth = 150, DefaultVisible = false,
                Number = d => d.StorageFree is { } free ? Math.Round(free / (double)(1L << 30), 1) : null },
        ],
    };
}
