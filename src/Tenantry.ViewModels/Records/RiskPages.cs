using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;
using F = Tenantry.Core.Directory.RiskFields;

namespace Tenantry.ViewModels.Records;

/// <summary>A button in a risk preview (Confirm Compromised, Dismiss Risk, Undo).</summary>
public sealed partial class RiskButton(string text, string tip, bool isDanger, Action act)
{
    public string Text => text;
    public string Tip => tip;
    public bool IsDanger => isDanger;

    [RelayCommand]
    void Act() => act();
}

/// <summary>A risky person, detection or app in the preview pane: its flags, facts, what was detected, and what can be done.</summary>
public sealed partial class RiskPreviewViewModel(string name, string? subtitle, IReadOnlyList<HealthChip> badges, IReadOnlyList<SummaryLine> facts,
    IReadOnlyList<SummaryLine> detections, IReadOnlyList<RiskButton> buttons, string? pending, string? openText, Action? open)
{
    public string Name => name;
    public string? Subtitle => subtitle;
    public bool HasSubtitle => !string.IsNullOrEmpty(subtitle);
    public IReadOnlyList<HealthChip> Badges => badges;
    public IReadOnlyList<SummaryLine> Facts => facts;
    public IReadOnlyList<SummaryLine> Detections => detections;
    public bool HasDetections => detections.Count > 0;
    public IReadOnlyList<RiskButton> Buttons => buttons;
    public bool HasButtons => buttons.Count > 0;
    public string? PendingText => pending;
    public bool HasPending => pending is not null;
    public string? OpenText => openText;
    public bool CanOpen => open is not null && openText is not null;

    [RelayCommand]
    void Open() => open?.Invoke();
}

/// <summary>The three Identity Protection lists (8d-2), their previews and their bulk actions.</summary>
public static class RiskLists
{
    static readonly ListQuery Newest = ListQuery.Empty with { Sort = [new SortLevel(F.Updated, Descending: true)] };
    static readonly ListQuery NewestDetections = ListQuery.Empty with { Sort = [new SortLevel(F.Detected, Descending: true)] };

    static SavedView View(ListQuery sort, string id, string name, params Condition[] where) =>
        new(id, name, sort with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: true);

    static readonly Condition AtRisk = new ChoiceCondition(F.State, ["At Risk"], []);

    public static (string Text, BadgeKind Kind) LevelBadge(string level) => level switch
    {
        "high" => ("High", BadgeKind.Danger),
        "medium" => ("Medium", BadgeKind.Warning),
        "low" => ("Low", BadgeKind.Info),
        _ => ("None", BadgeKind.Neutral),
    };

    static string Day(DateTimeOffset? when) => when is { } w ? w.ToLocalTime().ToString("d MMM yyyy h:mm tt", CultureInfo.CurrentCulture) : "Unknown";

    public static ChangeTarget UserTarget(RiskyUser u) => new("user", u.Id, u.Name, u.Upn);

    public static ChangeTarget AppTarget(RiskyApp a) => new(AppChange.Enterprise, a.Id, a.Name, null);

    // ---- Lists ----------------------------------------------------------------------------------

    public static ListDefinition<RiskyUser> Users(Func<RiskyUser, object> preview) => new()
    {
        Key = "riskyusers",
        Title = "Risky Users",
        Noun = "people",
        Fields = F.Users,
        Id = u => u.Id,
        Views =
        [
            View(Newest, "atrisk", "At Risk", AtRisk),
            View(Newest, "high", "High Risk", AtRisk, new ChoiceCondition(F.Level, ["High"], [])),
            View(Newest, "compromised", "Confirmed Compromised", new ChoiceCondition(F.State, ["Confirmed Compromised"], [])),
            View(Newest, "all", "Everyone With Risk History"),
        ],
        SearchHint = "Search people… or try level:high",
        Preview = preview,
        Columns =
        [
            new() { Key = F.Name, Header = "Person", Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 300, Text = u => u.Name, Subtitle = u => u.Upn ?? "",
                Initials = u => Display.Initials(u.Name) },
            new() { Key = F.Level, Header = "Risk Level", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 120, Badge = u => LevelBadge(u.Level) },
            new() { Key = F.State, Header = "Risk State", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 180, Text = u => Risk.StateName(u.State) },
            new() { Key = F.Updated, Header = "Last Updated", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = u => u.Updated },
            new() { Key = F.Detail, Header = "Detail", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 260, Text = u => u.Detail ?? "" },
        ],
    };

    public static ListDefinition<RiskDetection> Detections(Func<RiskDetection, object> preview) => new()
    {
        Key = "riskdetections",
        Title = "Risk Detections",
        Noun = "detections",
        Fields = F.Detections,
        Id = d => d.Id,
        Views =
        [
            View(NewestDetections, "atrisk", "Still At Risk", AtRisk),
            View(NewestDetections, "high", "High Risk", new ChoiceCondition(F.Level, ["High"], [])),
            View(NewestDetections, "week", "Last 7 Days", new DateCondition(F.Detected, DateOp.WithinLastDays, 7)),
            View(NewestDetections, "all", "All Detections"),
        ],
        SearchHint = "Search detections, people, addresses and places… or try level:high",
        Preview = preview,
        Columns =
        [
            new() { Key = F.Event, Header = "Detection", Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 300, Text = d => Risk.EventName(d.EventType),
                Subtitle = d => d.UserName, Initials = d => Display.Initials(d.UserName) },
            new() { Key = F.Level, Header = "Risk Level", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 120, Badge = d => LevelBadge(d.Level) },
            new() { Key = F.Detected, Header = "Detected", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = d => d.Detected },
            new() { Key = F.Location, Header = "Location", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 200, Text = d => d.Location ?? "" },
            new() { Key = F.Ip, Header = "IP Address", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 150, Text = d => d.Ip ?? "" },
            new() { Key = F.State, Header = "Risk State", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 170, Text = d => Risk.StateName(d.State) },
            new() { Key = F.Activity, Header = "From", Cell = CellKind.Text, MinWidth = 100, DefaultWidth = 120, DefaultVisible = false,
                Text = d => d.Activity == "signin" ? "A Sign-In" : "The Account" },
        ],
    };

    public static ListDefinition<RiskyApp> Apps(Func<RiskyApp, object> preview) => new()
    {
        Key = "riskyapps",
        Title = "Risky Apps",
        Noun = "apps",
        Fields = F.Apps,
        Id = a => a.Id,
        Views = [View(Newest, "atrisk", "At Risk", AtRisk), View(Newest, "all", "All Apps With Risk History")],
        SearchHint = "Search apps… or try level:high",
        Preview = preview,
        Columns =
        [
            new() { Key = F.Name, Header = "App", Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 300, Text = a => a.Name, Subtitle = a => a.AppId,
                Initials = a => Display.Initials(a.Name) },
            new() { Key = F.Level, Header = "Risk Level", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 120, Badge = a => LevelBadge(a.Level) },
            new() { Key = F.State, Header = "Risk State", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 180, Text = a => Risk.StateName(a.State) },
            new() { Key = F.Updated, Header = "Last Updated", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = a => a.Updated },
        ],
    };

    // ---- Previews -------------------------------------------------------------------------------

    static IReadOnlyList<RiskButton> Buttons(ChangePipeline? pipeline, bool canEdit, bool atRisk, ChangeOp? waiting, Func<bool, ChangeOp> make, string who) =>
        !canEdit || pipeline is null ? []
        : waiting is not null ? [new RiskButton("Undo", "Take the change back out of Pending Changes", false, () => pipeline.Unstage(waiting))]
        :
        [
            new RiskButton("Confirm Compromised", $"Raise {who} risk to high, so risk-based policies act on it (added to Pending Changes)", true, () => pipeline.Stage(make(true))),
            .. atRisk ? [new RiskButton("Dismiss Risk", $"Clear {who} risk: it was safe (added to Pending Changes)", false, () => pipeline.Stage(make(false)))] : Array.Empty<RiskButton>(),
        ];

    public static RiskPreviewViewModel UserPreview(RiskyUser u, Func<RiskDirectory?> directory, ChangePipeline? pipeline, bool canEdit, Action<string>? openUser)
    {
        var waiting = pipeline?.Pending.OfType<SetUserRisk>().FirstOrDefault(r => r.User.Id == u.Id);
        var detections = directory()?.Detections.Where(d => d.UserId == u.Id).OrderByDescending(d => d.Detected).Take(8)
            .Select(d => new SummaryLine(Risk.EventName(d.EventType), string.Join(" · ", new[] { Risk.LevelName(d.Level), Day(d.Detected), d.Location }.OfType<string>())))
            .ToArray() ?? [];
        var (level, kind) = LevelBadge(u.Level);
        return new RiskPreviewViewModel(u.Name, u.Upn, [new HealthChip($"Risk {level}", kind, "Identity Protection's risk level"), new HealthChip(Risk.StateName(u.State), BadgeKind.Neutral, "Risk state")],
            [
                new("Last Updated", Day(u.Updated)),
                .. u.Detail is { } detail ? [new SummaryLine("Detail", detail)] : Array.Empty<SummaryLine>(),
            ],
            detections, Buttons(pipeline, canEdit, u.State == "atRisk", waiting, c => new SetUserRisk(UserTarget(u), c), "their"),
            waiting is null ? null : $"Pending: {waiting.After.ToLowerInvariant()}", openUser is null ? null : "Open Person", openUser is null ? null : () => openUser(u.Id));
    }

    public static RiskPreviewViewModel DetectionPreview(RiskDetection d, Action<string>? openUser) =>
        new(Risk.EventName(d.EventType), d.UserName, [new HealthChip($"Risk {LevelBadge(d.Level).Text}", LevelBadge(d.Level).Kind, "This detection's risk level")],
            [
                new("Person", d.Upn ?? d.UserName),
                new("Detected", Day(d.Detected)),
                new("From", d.Activity == "signin" ? "A sign-in" : "The account itself (found later, offline)"),
                .. d.Ip is { } ip ? [new SummaryLine("IP Address", ip)] : Array.Empty<SummaryLine>(),
                .. d.Location is { } where ? [new SummaryLine("Location", where)] : Array.Empty<SummaryLine>(),
                new("Risk State", Risk.StateName(d.State)),
                .. d.Timing is { } timing ? [new SummaryLine("Found", timing == "realtime" ? "As it happened" : timing == "offline" ? "Afterwards" : timing)] : Array.Empty<SummaryLine>(),
            ],
            [], [], null, openUser is null || d.UserId is null ? null : "Open Person", openUser is null || d.UserId is null ? null : () => openUser(d.UserId));

    public static RiskPreviewViewModel AppPreview(RiskyApp a, ChangePipeline? pipeline, bool canEdit, Action<string>? openApp)
    {
        var waiting = pipeline?.Pending.OfType<SetAppRisk>().FirstOrDefault(r => r.App.Id == a.Id);
        var (level, kind) = LevelBadge(a.Level);
        return new RiskPreviewViewModel(a.Name, a.AppId, [new HealthChip($"Risk {level}", kind, "Identity Protection's risk level"), new HealthChip(Risk.StateName(a.State), BadgeKind.Neutral, "Risk state")],
            [new("Last Updated", Day(a.Updated)), .. a.Detail is { } detail ? [new SummaryLine("Detail", detail)] : Array.Empty<SummaryLine>()],
            [], Buttons(pipeline, canEdit, a.State == "atRisk", waiting, c => new SetAppRisk(AppTarget(a), c), "its"),
            waiting is null ? null : $"Pending: {waiting.After.ToLowerInvariant()}", openApp is null ? null : "Open App", openApp is null ? null : () => openApp(a.Id));
    }

    // ---- Bulk -----------------------------------------------------------------------------------

    static IReadOnlyList<BulkAction> Actions<T>(Func<IReadOnlyList<T>> selected, Func<T, bool, ChangeOp> make, Func<T, bool> atRisk, Func<T, string> name, string whose) =>
    [
        new("Confirm Compromised", $"Raises {whose} risk to high, so risk-based Conditional Access policies act on it.", "ShieldError",
            (_, _) => Task.FromResult(new BulkPlan([.. selected().Select(r => make(r, true))], [])), isCaution: true),
        new("Dismiss Risk", "Clears the risk: what was detected was safe.", "ShieldCheckmark",
            (_, _) => Task.FromResult(new BulkPlan([.. selected().Where(atRisk).Select(r => make(r, false))],
                [.. selected().Where(r => !atRisk(r)).Select(r => $"{name(r)}: not at risk now")]))),
    ];

    public static SelectionPanelViewModel UsersPanel(IReadOnlyList<object> rows, Action<object> uncheck, ChangePipeline? pipeline, Availability canEdit) =>
        new(rows, uncheck, "person", "people", r => (((RiskyUser)r).Name, ((RiskyUser)r).Upn ?? ""),
            all => [new("Risk Levels", string.Join(" · ", all.Cast<RiskyUser>().GroupBy(u => Risk.LevelName(u.Level)).Select(g => $"{g.Count():N0} {g.Key.ToLowerInvariant()}")))],
            current => Actions(() => current().Cast<RiskyUser>().ToArray(), (u, c) => new SetUserRisk(UserTarget(u), c), u => u.State == "atRisk", u => u.Name, "their"),
            pipeline, canEdit);

    public static SelectionPanelViewModel AppsPanel(IReadOnlyList<object> rows, Action<object> uncheck, ChangePipeline? pipeline, Availability canEdit) =>
        new(rows, uncheck, "app", "apps", r => (((RiskyApp)r).Name, ((RiskyApp)r).AppId),
            all => [new("Risk Levels", string.Join(" · ", all.Cast<RiskyApp>().GroupBy(a => Risk.LevelName(a.Level)).Select(g => $"{g.Count():N0} {g.Key.ToLowerInvariant()}")))],
            current => Actions(() => current().Cast<RiskyApp>().ToArray(), (a, c) => new SetAppRisk(AppTarget(a), c), a => a.State == "atRisk", a => a.Name, "its"),
            pipeline, canEdit);
}
