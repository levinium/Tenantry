using System.Globalization;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;
using F = Tenantry.Core.Directory.RetentionFields;

namespace Tenantry.ViewModels.Records
{
    /// <summary>Retention targets, drafts as they stand, and the lists (8f-3).</summary>
    public static class RetentionPages
    {
        public static ChangeTarget PolicyTarget(string name) => new(RetentionChange.PolicyKind, name, name, null);
        public static ChangeTarget LabelTarget(string name) => new(RetentionChange.LabelKind, name, name, null);
        public static ChangeTarget ScopeTarget(string name) => new(RetentionChange.ScopeKind, name, name, null);
        public static ChangeTarget EventTypeTarget(string name) => new(RetentionChange.EventTypeKind, name, name, null);
        public static ChangeTarget EventTarget(string name) => new(RetentionChange.EventKind, name, name, null);

        /// <summary>A policy as a draft, its places kept as Purview gave them (so unchanged ones compare equal).</summary>
        public static RetentionPolicyDraft DraftOf(RetentionPolicy p) => new(p.Kind, p.Comment,
            p.Locations.ToDictionary(l => l.Key, l => (IReadOnlyList<ChangeTarget>)[.. l.Value.Select(v => EDiscovery.IsAll(l.Value)
                ? EdChange.Everywhere(RetentionPlaces.PickKind(l.Key))
                : new ChangeTarget(RetentionPlaces.PickKind(l.Key), v, v, RetentionPlaces.PickKind(l.Key) == "site" ? SiteSummary.AliasOf(v) : v))]),
            p.Scopes, p.Setting, p.Labels, p.Query);

        public static RetentionLabelDraft DraftOf(RetentionLabel l) => new(l.Comment, l.Notes, l.Action, l.Days, l.BasedOn, l.IsRecord, l.EventType, l.Reviewers);

        static SavedView View(ListQuery sort, string id, string name, params Condition[] where) =>
            new(id, name, sort with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: true);

        static readonly ListQuery ByName = ListQuery.Empty with { Sort = [new SortLevel(F.Name, Descending: false)] };

        public static (string Text, BadgeKind Kind) PolicyBadge(RetentionPolicy p) =>
            p.IsPendingDeletion ? ("Being Deleted", BadgeKind.Neutral)
            : !p.Enabled ? (F.Off, BadgeKind.Neutral)
            : p.Distribution is "Pending" ? ("Being Put in Place", BadgeKind.Info)
            : p.Distribution is "Error" ? ("Not Everywhere", BadgeKind.Warning)
            : (F.On, BadgeKind.Success);

        public static ListDefinition<RetentionPolicy> Policies(Func<RetentionPolicy, object> preview) => new()
        {
            Key = "retentionpolicies",
            Title = "Retention Policies",
            Noun = "policies",
            Fields = F.Policies,
            Id = p => p.Id,
            Views =
            [
                View(ByName, "all", "All Policies"),
                View(ByName, "retention", "Keep or Delete", new ChoiceCondition(F.Kind, ["Retention"], [])),
                View(ByName, "labels", "Label Policies", new ChoiceCondition(F.Kind, ["Publishes Labels", "Applies a Label"], [])),
                View(ByName, "off", "Turned Off", new ChoiceCondition(F.Status, [F.Off], [])),
            ],
            SearchHint = "Search policies, what they do and where… or try kind:retention",
            Preview = preview,
            Columns =
            [
                new() { Key = F.Name, Header = "Policy", Cell = CellKind.Primary, MinWidth = 220, DefaultWidth = 280, Text = p => p.Name, Subtitle = p => p.Comment ?? "",
                    Initials = p => Display.Initials(p.Name) },
                new() { Key = F.Status, Header = "Status", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 150, Badge = PolicyBadge },
                new() { Key = F.Kind, Header = "Kind", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 150, Text = p => F.KindText(p.Kind) },
                new() { Key = F.What, Header = "What It Does", Cell = CellKind.Text, MinWidth = 200, DefaultWidth = 300, Text = p => p.WhatText },
                new() { Key = F.Places, Header = "Where", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 240, Text = p => p.PlacesText },
                new() { Key = F.Modified, Header = "Last Changed", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = p => p.Modified, DefaultVisible = false },
            ],
        };

        public static ListDefinition<RetentionLabel> Labels(Func<RetentionLabel, object> preview) => new()
        {
            Key = "retentionlabels",
            Title = "Retention Labels",
            Noun = "labels",
            Fields = F.Labels,
            Id = l => l.Id,
            Views = [View(ByName, "all", "All Labels"), View(ByName, "records", "Records", new ChoiceCondition(F.Kind, ["Record", "Regulatory Record"], []))],
            SearchHint = "Search labels and what they do… or try kind:record",
            Preview = preview,
            Columns =
            [
                new() { Key = F.Name, Header = "Label", Cell = CellKind.Primary, MinWidth = 220, DefaultWidth = 260, Text = l => l.Name, Subtitle = l => l.Comment ?? "",
                    Initials = l => Display.Initials(l.Name) },
                new() { Key = F.Kind, Header = "Kind", Cell = CellKind.Badge, MinWidth = 110, DefaultWidth = 150,
                    Badge = l => (l.KindText, l.IsRecord ? BadgeKind.Info : BadgeKind.Neutral) },
                new() { Key = F.What, Header = "What It Does", Cell = CellKind.Text, MinWidth = 220, DefaultWidth = 360, Text = l => l.WhatText },
                new() { Key = F.Modified, Header = "Last Changed", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = l => l.Modified },
            ],
        };

        public static ListDefinition<AdaptiveScope> Scopes(Func<AdaptiveScope, object> preview) => new()
        {
            Key = "adaptivescopes",
            Title = "Adaptive Scopes",
            Noun = "scopes",
            Fields = F.Scopes,
            Id = s => s.Id,
            Views = [View(ByName, "all", "All Scopes")],
            SearchHint = "Search scopes and their queries…",
            Preview = preview,
            Columns =
            [
                new() { Key = F.Name, Header = "Scope", Cell = CellKind.Primary, MinWidth = 200, DefaultWidth = 260, Text = s => s.Name, Subtitle = s => s.Comment ?? "",
                    Initials = s => Display.Initials(s.Name) },
                new() { Key = F.Type, Header = "Of", Cell = CellKind.Text, MinWidth = 90, DefaultWidth = 110, Text = s => F.ScopeType(s.LocationType) },
                new() { Key = F.What, Header = "Query", Cell = CellKind.Text, MinWidth = 220, DefaultWidth = 400, Text = s => s.Query ?? "" },
                new() { Key = F.Created, Header = "Created", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = s => s.Created },
            ],
        };

        static string Day(DateTimeOffset? when) => when is { } w ? w.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "Unknown";

        public static AppPreviewViewModel PolicyPreview(RetentionPolicy p, Action<RetentionPolicy>? open)
        {
            var (text, kind) = PolicyBadge(p);
            return new AppPreviewViewModel(p.Name, p.Comment, [new HealthChip(text, kind, "Whether it's on and in place everywhere")],
                [new("Kind", F.KindText(p.Kind)), new("What It Does", p.WhatText), new("Where", p.PlacesText), new("Last Changed", Day(p.Modified))],
                "Open Policy", open is null ? null : () => open(p));
        }

        public static AppPreviewViewModel LabelPreview(RetentionLabel l, RetentionDirectory? directory, Action<RetentionLabel>? open) =>
            new(l.Name, l.Comment, [new HealthChip(l.KindText, l.IsRecord ? BadgeKind.Info : BadgeKind.Neutral, "Records can't be edited or deleted while labeled")],
            [
                new("What It Does", l.WhatText),
                .. l.EventType is { } e ? [new SummaryLine("Starts When", e)] : Array.Empty<SummaryLine>(),
                new("Used By", directory?.Policies.Where(p => p.Labels.Contains(l.Name)).Select(p => p.Name).ToArray() is { Length: > 0 } used ? string.Join(", ", used) : "No policy yet"),
            ],
            "Open Label", open is null ? null : () => open(l));

        public static AppPreviewViewModel ScopePreview(AdaptiveScope s, RetentionDirectory? directory, Action<AdaptiveScope>? open) =>
            new(s.Name, s.Comment, [new HealthChip(F.ScopeType(s.LocationType), BadgeKind.Neutral, "What it covers")],
            [
                new("Query", s.Query ?? ""),
                new("Used By", directory?.Policies.Where(p => p.Scopes.Contains(s.Name)).Select(p => p.Name).ToArray() is { Length: > 0 } used ? string.Join(", ", used) : "No policy yet"),
            ],
            "Open Scope", open is null ? null : () => open(s));
    }
}

namespace Tenantry.ViewModels
{
    /// <summary>What retention pages share.</summary>
    public sealed record RetentionContext(IDirectoryIndex Index, ChangePipeline? Pipeline, Availability CanEdit, Func<IReadOnlyList<SiteSummary>> Sites,
        Action<string, object> ShowDialog, Action CloseDialog, Func<RetentionDirectory?> Directory, Action<RetentionPolicy> OpenPolicy, Action<RetentionLabel> OpenLabel)
    {
        public bool Editable => Pipeline is not null && CanEdit.IsAvailable;

        public const string PurviewRetention = "https://purview.microsoft.com/datalifecyclemanagement";

        public static string Day(DateTimeOffset? when) => when is { } w ? w.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "Unknown";

        public void EditPolicy(RetentionPolicy? policy) => ShowDialog(policy is null ? "New Retention Policy" : $"Change {policy.Name}",
            new RetentionPolicyEditorViewModel(Pipeline!, Directory() ?? Empty, Index, Sites, CloseDialog, policy));

        public void EditLabel(RetentionLabel? label) => ShowDialog(label is null ? "New Retention Label" : $"Change {label.Name}",
            new RetentionLabelEditorViewModel(Pipeline!, Directory() ?? Empty, Index, CloseDialog, label));

        public void EditScope(AdaptiveScope? scope) => ShowDialog(scope is null ? "New Adaptive Scope" : $"Change {scope.Name}",
            new AdaptiveScopeEditorViewModel(Pipeline!, Directory() ?? Empty, CloseDialog, scope));

        public static readonly RetentionDirectory Empty = new([], [], [], [], []);
    }

    /// <summary>A retention policy's page: what it does, where, and its switches.</summary>
    public sealed partial class RetentionPolicyPageViewModel : CardPageViewModel
    {
        readonly RetentionContext _ctx;

        public RetentionPolicyPageViewModel(RetentionPolicy policy, RetentionContext ctx) : base(ctx.Pipeline)
        {
            Policy = policy;
            _ctx = ctx;
            Sections =
            [
                new AppSection("overview", "Overview", "Home", _ => Task.FromResult(Overview())),
                new AppSection("where", "Where", "Location", _ => Task.FromResult(Where())),
            ];
            SelectedSection = Sections[0];
        }

        public RetentionPolicy Policy { get; private set; }

        public override string Title => Policy.Name;
        public override string Subtitle => $"{F.KindText(Policy.Kind)} policy · changed {RetentionContext.Day(Policy.Modified)}";
        public override string Icon => "ShieldCheckmark";

        ChangeTarget Target => RetentionPages.PolicyTarget(Policy.Name);

        public override IReadOnlyList<HealthChip> Badges
        {
            get
            {
                var (text, kind) = RetentionPages.PolicyBadge(Policy);
                return
                [
                    new(text, kind, "Whether it's on and in place everywhere"),
                    .. Policy.IsLocked ? [new HealthChip("Preservation Lock", BadgeKind.Warning, "Locked: it can't be turned off, deleted or made less strict")] : Array.Empty<HealthChip>(),
                    .. Policy.IsTeams ? [new HealthChip("Teams", BadgeKind.Neutral, "Covers Teams messages")] : Array.Empty<HealthChip>(),
                ];
            }
        }

        protected override IEnumerable<ChangeOp> Pending =>
            Pipeline?.Pending.OfType<RetentionChange>().Where(c => c.Item.Kind == RetentionChange.PolicyKind && c.Item.Name == Policy.Name) ?? [];

        const string StillApplying = "Purview is still applying its last change everywhere.";

        /// <summary>
        /// Why it can't be changed now, or null. While its last change is still being put in place,
        /// Purview refuses any other ("being deployed", seen live), so nothing is offered until then.
        /// </summary>
        string? Frozen => Policy.IsPendingDeletion ? "It's being deleted."
            : Policy.IsLocked ? "It has Preservation Lock, so it can only be made stricter, in Purview."
            : Policy.Distribution is "Pending" ? StillApplying
            : null;

        public override IReadOnlyList<PageButton> Buttons
        {
            get
            {
                var buttons = new List<PageButton> { new("Open in Purview", "Globe", "Open Data Lifecycle Management in the Microsoft Purview portal",
                    () => Platform.OpenUrl(RetentionContext.PurviewRetention)) };
                if (!_ctx.Editable || Frozen is not null)
                    return buttons;
                if (Pending.FirstOrDefault() is { } waiting)
                    return [.. buttons, new PageButton("Undo", "ArrowUndo", "Take the change back out of Pending Changes", () => Pipeline!.Unstage(waiting))];
                buttons.Add(new PageButton("Change Policy", "Edit", "Change where it applies and what it does (added to Pending Changes)", () => _ctx.EditPolicy(Policy)));
                buttons.Add(Policy.Enabled
                    ? new PageButton("Turn Off", "ToggleLeft", "Stop it keeping or deleting anything (added to Pending Changes)",
                        () => Pipeline!.Stage(new SetRetentionPolicyEnabled(Target, true, false)))
                    : new PageButton("Turn On", "ToggleRight", "Start it again (added to Pending Changes)", () => Pipeline!.Stage(new SetRetentionPolicyEnabled(Target, false, true))));
                buttons.Add(new PageButton("Delete Policy", "Delete", "Delete it (you type its name to confirm)",
                    () => Pipeline!.Stage(new DeleteRetentionPolicy(Target, Policy.WhatText)), isDanger: true));
                return buttons;
            }
        }

        public bool Update(RetentionDirectory directory)
        {
            if ((directory.Policies.FirstOrDefault(p => p.Id == Policy.Id) ?? directory.Policies.FirstOrDefault(p => p.Name == Policy.Name)) is not { } now)
                return false;
            Policy = now;
            Refresh();
            return true;
        }

        IReadOnlyList<AppCard> Overview() =>
        [
            .. Frozen is { } why && why != StillApplying ? [new AppCard("Can't Be Changed Here", why)] : Array.Empty<AppCard>(),
            .. Policy.Distribution is "Pending" ? [new AppCard("Being Put in Place",
                $"{StillApplying} Until it finishes, the policy can't be changed, turned off or deleted; this can take a while. Open it again later to see.")] : Array.Empty<AppCard>(),
            new AppCard("Details", Facts:
            [
                new("Kind", F.KindText(Policy.Kind)),
                new("What It Does", Policy.WhatText),
                new("Status", Policy.Enabled ? "On" : "Off"),
                .. Policy.Comment is { } c ? [new SummaryLine("Description", c)] : Array.Empty<SummaryLine>(),
                new("In Place", EDiscovery.DistributionText(Policy.Distribution)),
                new("Created", RetentionContext.Day(Policy.Created)),
                new("Last Changed", Policy.ModifiedBy is { } by ? $"{RetentionContext.Day(Policy.Modified)} by {by}" : RetentionContext.Day(Policy.Modified)),
            ]),
            .. Policy.Kind != RetentionPolicyKind.Retention ? [new AppCard(Policy.Kind == RetentionPolicyKind.PublishLabels ? "Labels It Publishes" : "Label It Applies",
                Rows: [.. Policy.Labels.Select(l => new AppRow
                {
                    Title = l,
                    Detail = _ctx.Directory()?.Labels.FirstOrDefault(x => x.Name == l)?.WhatText,
                    OpenAction = _ctx.Directory()?.Labels.FirstOrDefault(x => x.Name == l) is { } label ? () => _ctx.OpenLabel(label) : null,
                    OpenTip = "Open the label",
                })],
                EmptyText: "None.")] : Array.Empty<AppCard>(),
            .. Policy.Query is { } q ? [new AppCard("Applies To Content Matching", Rows: [new AppRow { Title = "Query", Code = q }])] : Array.Empty<AppCard>(),
        ];

        IReadOnlyList<AppCard> Where()
        {
            if (Policy.Scopes.Count > 0)
                return [new AppCard("Adaptive Scopes", "It follows these queries as people, groups and sites come and go.",
                    Rows: [.. Policy.Scopes.Select(s => new AppRow { Title = s, Detail = _ctx.Directory()?.Scopes.FirstOrDefault(x => x.Name == s)?.Query })])];
            return
            [
                new AppCard("Where It Applies", Rows: [.. RetentionPlaces.All.Where(Policy.Locations.ContainsKey).Select(k => new AppRow
                {
                    Title = RetentionPlaces.Label(k),
                    Detail = EDiscovery.IsAll(Policy.Locations[k]) ? "All of them" + (Policy.Exceptions.TryGetValue(k, out var except) ? $", except {except.Count}" : "") : null,
                    Chips = EDiscovery.IsAll(Policy.Locations[k]) ? Policy.Exceptions.GetValueOrDefault(k) ?? [] : Policy.Locations[k],
                })], EmptyText: "Nowhere yet."),
            ];
        }
    }

    /// <summary>A retention label's page: what it does and which policies use it.</summary>
    public sealed partial class RetentionLabelPageViewModel : CardPageViewModel
    {
        readonly RetentionContext _ctx;

        public RetentionLabelPageViewModel(RetentionLabel label, RetentionContext ctx) : base(ctx.Pipeline)
        {
            Label = label;
            _ctx = ctx;
            Sections = [new AppSection("overview", "Overview", "Home", _ => Task.FromResult(Overview()))];
            SelectedSection = Sections[0];
        }

        public RetentionLabel Label { get; private set; }

        public override string Title => Label.Name;
        public override string Subtitle => $"Retention {Label.KindText.ToLowerInvariant()} · changed {RetentionContext.Day(Label.Modified)}";
        public override string Icon => "Tag";

        public override IReadOnlyList<HealthChip> Badges =>
        [
            new(Label.KindText, Label.IsRecord ? BadgeKind.Info : BadgeKind.Neutral, "Records can't be edited or deleted while labeled"),
            .. Label.IsPendingDeletion ? [new HealthChip("Being Deleted", BadgeKind.Neutral, "Purview is taking it away")] : Array.Empty<HealthChip>(),
        ];

        protected override IEnumerable<ChangeOp> Pending =>
            Pipeline?.Pending.OfType<RetentionChange>().Where(c => c.Item.Kind == RetentionChange.LabelKind && c.Item.Name == Label.Name) ?? [];

        public override IReadOnlyList<PageButton> Buttons
        {
            get
            {
                var buttons = new List<PageButton> { new("Open in Purview", "Globe", "Open Data Lifecycle Management in the Microsoft Purview portal",
                    () => Platform.OpenUrl(RetentionContext.PurviewRetention)) };
                if (!_ctx.Editable || Label.IsPendingDeletion || Label.Regulatory)
                    return buttons;
                if (Pending.FirstOrDefault() is { } waiting)
                    return [.. buttons, new PageButton("Undo", "ArrowUndo", "Take the change back out of Pending Changes", () => Pipeline!.Unstage(waiting))];
                buttons.Add(new PageButton("Change Label", "Edit", "Change its description, period and reviewers (added to Pending Changes)", () => _ctx.EditLabel(Label)));
                buttons.Add(new PageButton("Delete Label", "Delete", "Delete it; no policy may still use it (you type its name to confirm)",
                    () => Pipeline!.Stage(new DeleteRetentionLabel(RetentionPages.LabelTarget(Label.Name), Label.WhatText)), isDanger: true));
                return buttons;
            }
        }

        public bool Update(RetentionDirectory directory)
        {
            if ((directory.Labels.FirstOrDefault(l => l.Id == Label.Id) ?? directory.Labels.FirstOrDefault(l => l.Name == Label.Name)) is not { } now)
                return false;
            Label = now;
            Refresh();
            return true;
        }

        IReadOnlyList<AppCard> Overview()
        {
            var policies = _ctx.Directory()?.Policies.Where(p => p.Labels.Contains(Label.Name)).ToArray() ?? [];
            return
            [
                new AppCard("Details", Facts:
                [
                    new("What It Does", Label.WhatText),
                    new("Kind", Label.KindText),
                    .. Label.EventType is { } e ? [new SummaryLine("Starts When", $"A {e} event is recorded")] : Array.Empty<SummaryLine>(),
                    .. Label.Comment is { } c ? [new SummaryLine("Description", c)] : Array.Empty<SummaryLine>(),
                    .. Label.Notes is { } n ? [new SummaryLine("Admin Notes", n)] : Array.Empty<SummaryLine>(),
                    new("Reviewed Before Deletion", Label.Reviewers.Count == 0 ? "No" : string.Join(", ", Label.Reviewers)),
                    new("Last Changed", Label.ModifiedBy is { } by ? $"{RetentionContext.Day(Label.Modified)} by {by}" : RetentionContext.Day(Label.Modified)),
                ]),
                new AppCard("Used By", "Policies that publish it (so people can choose it) or apply it.",
                    Rows: [.. policies.Select(p => new AppRow
                    {
                        Title = p.Name,
                        Detail = $"{F.KindText(p.Kind)} · {p.PlacesText}",
                        OpenAction = () => _ctx.OpenPolicy(p),
                        OpenTip = "Open the policy",
                    })],
                    EmptyText: "No policy publishes or applies it yet, so nobody can use it."),
            ];
        }
    }

    /// <summary>An adaptive scope's page: its query and the policies that follow it.</summary>
    public sealed partial class AdaptiveScopePageViewModel : CardPageViewModel
    {
        readonly RetentionContext _ctx;

        public AdaptiveScopePageViewModel(AdaptiveScope scope, RetentionContext ctx) : base(ctx.Pipeline)
        {
            Scope = scope;
            _ctx = ctx;
            Sections = [new AppSection("overview", "Overview", "Home", _ => Task.FromResult(Overview()))];
            SelectedSection = Sections[0];
        }

        public AdaptiveScope Scope { get; private set; }

        public override string Title => Scope.Name;
        public override string Subtitle => $"Adaptive scope of {F.ScopeType(Scope.LocationType).ToLowerInvariant()}";
        public override string Icon => "Filter";
        public override IReadOnlyList<HealthChip> Badges => [new(F.ScopeType(Scope.LocationType), BadgeKind.Neutral, "What it covers")];

        protected override IEnumerable<ChangeOp> Pending =>
            Pipeline?.Pending.OfType<RetentionChange>().Where(c => c.Item.Kind == RetentionChange.ScopeKind && c.Item.Name == Scope.Name) ?? [];

        public override IReadOnlyList<PageButton> Buttons
        {
            get
            {
                var buttons = new List<PageButton> { new("Open in Purview", "Globe", "Open Data Lifecycle Management in the Microsoft Purview portal",
                    () => Platform.OpenUrl(RetentionContext.PurviewRetention)) };
                if (!_ctx.Editable)
                    return buttons;
                if (Pending.FirstOrDefault() is { } waiting)
                    return [.. buttons, new PageButton("Undo", "ArrowUndo", "Take the change back out of Pending Changes", () => Pipeline!.Unstage(waiting))];
                buttons.Add(new PageButton("Change Scope", "Edit", "Change its query (added to Pending Changes)", () => _ctx.EditScope(Scope)));
                buttons.Add(new PageButton("Delete Scope", "Delete", "Delete it; no policy may still use it (added to Pending Changes)",
                    () => Pipeline!.Stage(new DeleteAdaptiveScope(RetentionPages.ScopeTarget(Scope.Name), Scope.Query ?? "")), isDanger: true));
                return buttons;
            }
        }

        public bool Update(RetentionDirectory directory)
        {
            if ((directory.Scopes.FirstOrDefault(s => s.Id == Scope.Id) ?? directory.Scopes.FirstOrDefault(s => s.Name == Scope.Name)) is not { } now)
                return false;
            Scope = now;
            Refresh();
            return true;
        }

        IReadOnlyList<AppCard> Overview() =>
        [
            new AppCard("Query", Scope.LocationType == "Site" ? "A keyword query on site properties." : "A filter on their directory properties.",
                Rows: [new AppRow { Title = "Covers", Code = Scope.Query }]),
            new AppCard("Details", Facts:
            [
                .. Scope.Comment is { } c ? [new SummaryLine("Description", c)] : Array.Empty<SummaryLine>(),
                new("Created", RetentionContext.Day(Scope.Created)),
            ]),
            new AppCard("Used By", Rows: [.. (_ctx.Directory()?.Policies.Where(p => p.Scopes.Contains(Scope.Name)) ?? []).Select(p => new AppRow
            {
                Title = p.Name,
                Detail = p.WhatText,
                OpenAction = () => _ctx.OpenPolicy(p),
                OpenTip = "Open the policy",
            })], EmptyText: "No policy uses it yet."),
        ];
    }

    /// <summary>
    /// Retention Events (8f-3): the kinds of event labels can wait for, and the events recorded.
    /// Recording an event starts the retention period for content waiting on its type.
    /// </summary>
    public sealed partial class RetentionEventsViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        readonly RetentionContext _ctx;
        readonly Func<DateTimeOffset> _now;

        /// <param name="read">Reads retention (or hands back the read already made).</param>
        public RetentionEventsViewModel(RetentionContext ctx, Func<DateTimeOffset> now, Func<CancellationToken, Task<RetentionDirectory?>> read)
        {
            _ctx = ctx;
            _now = now;
            Section = new AppSection("events", "Retention Events", "CalendarClock", async ct => Cards(await read(ct)), "Reading retention events…");
        }

        public AppSection Section { get; }

        IReadOnlyList<AppCard> Cards(RetentionDirectory? read)
        {
            var directory = read ?? RetentionContext.Empty;
            var pending = _ctx.Pipeline?.Pending.OfType<RetentionChange>().ToArray() ?? [];
            var used = directory.Labels.Select(l => l.EventType).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var typeRows = directory.EventTypes.OrderBy(t => t.Name, StringComparer.CurrentCulture).Select(t =>
            {
                var row = new AppRow
                {
                    Title = t.Name,
                    Detail = t.Comment,
                    Badges = t.BuiltIn ? [new HealthChip("Built In", BadgeKind.Neutral, "Microsoft's own")] : [],
                };
                if (!_ctx.Editable || t.BuiltIn || used.Contains(t.Name))
                    return row;
                var waiting = pending.OfType<DeleteRetentionEventType>().FirstOrDefault(d => d.EventType.Name == t.Name);
                return waiting is not null
                    ? row.With("Deleting", "Undo", "Take the change back out of Pending Changes", () => _ctx.Pipeline!.Unstage(waiting))
                    : row.With(null, "Delete", "Delete it (added to Pending Changes)", () => _ctx.Pipeline!.Stage(new DeleteRetentionEventType(RetentionPages.EventTypeTarget(t.Name))));
            }).ToList();
            typeRows.AddRange(pending.OfType<CreateRetentionEventType>().Select(c => new AppRow { Title = c.EventType.Name, Detail = c.Comment }
                .With("Adding", "Undo", "Don't add it", () => _ctx.Pipeline!.Unstage(c))));
            var eventRows = directory.Events.OrderByDescending(e => e.When).Select(e => new AppRow
            {
                Title = e.Name,
                Detail = string.Join(" · ", new[] { e.EventType, $"happened {RetentionContext.Day(e.When)}", e.Comment }.OfType<string>().Where(x => x.Length > 0)),
                Code = string.Join(Environment.NewLine, new[] { e.SharePointQuery, e.ExchangeQuery }.OfType<string>().Where(x => x.Length > 0)) is { Length: > 0 } q ? q : null,
            }).ToList();
            eventRows.AddRange(pending.OfType<CreateRetentionEvent>().Select(c => new AppRow { Title = c.Event.Name, Detail = c.After }
                .With("Adding", "Undo", "Don't record it", () => _ctx.Pipeline!.Unstage(c))));
            return
            [
                .. directory.EventsUnavailable is { } why ? [new AppCard("Events", why)] : Array.Empty<AppCard>(),
                new AppCard("Event Types", "Labels can start their period when an event of one of these types is recorded, instead of when content was made or changed.",
                    Rows: typeRows, EmptyText: "None.",
                    Form: _ctx.Editable ? new AppButtonForm("New Event Type", "Add a kind of event, like a contract ending.", "Added to Pending Changes",
                        () => _ctx.ShowDialog("New Event Type", new NewEventTypeViewModel(_ctx.Pipeline!, directory.EventTypes.Select(t => t.Name), _ctx.CloseDialog))) : null),
                new AppCard("Events", "Recording an event starts the retention period for content labeled for its type (and matching its asset IDs). It can't be undone.",
                    Rows: eventRows, EmptyText: "No events recorded.",
                    Form: _ctx.Editable && directory.EventTypes.Count > 0
                        ? new AppButtonForm("New Event", "Record that something happened, on a date.", "Added to Pending Changes as a caution change",
                            () => _ctx.ShowDialog("New Event", new NewRetentionEventViewModel(_ctx.Pipeline!, directory, _now, _ctx.CloseDialog)))
                        : null),
            ];
        }

        public void Rebuild() => Section.Rebuild();
    }
}
