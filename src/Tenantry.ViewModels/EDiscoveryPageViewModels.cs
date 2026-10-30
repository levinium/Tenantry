using System.Globalization;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>What eDiscovery pages share: the reader, the local copy, Pending changes, and how to open things.</summary>
/// <param name="ShowDialog">Shows a popup (title, content).</param>
/// <param name="Directory">The cases and searches as last read (for names already taken).</param>
public sealed record EdContext(IEDiscoveryRecords Records, IDirectoryIndex Index, ChangePipeline? Pipeline, Availability CanEdit, Func<IReadOnlyList<SiteSummary>> Sites,
    Action<string, object> ShowDialog, Action CloseDialog, Action<EdSearch> OpenSearch, Func<EdDirectory?> Directory)
{
    public bool Editable => Pipeline is not null && CanEdit.IsAvailable;

    public string EditReason => Pipeline is null ? "This tenant can't be edited" : CanEdit.Reason ?? "";

    public IEnumerable<string> SearchNames => Directory()?.Searches.Select(s => s.Name) ?? [];

    public static string Day(DateTimeOffset? when) => when is { } w ? w.ToLocalTime().ToString("d MMM yyyy h:mm tt", CultureInfo.CurrentCulture) : "Unknown";

    public const string PurviewCases = "https://purview.microsoft.com/ediscovery/cases";

    public void NewSearch(string? caseName) => ShowDialog(caseName is null ? "New Search" : $"New Search in {caseName}",
        new EdItemEditorViewModel(Pipeline!, false, caseName, SearchNames, Index, Sites, CloseDialog));
}

/// <summary>
/// A case's page (8f-1): Overview (details, description), Searches, Holds and Members, read live
/// when it opens. Close, reopen and delete are in the header; every change goes to Pending changes.
/// </summary>
public sealed partial class EdCasePageViewModel : CardPageViewModel
{
    readonly EdContext _ctx;
    readonly AppPickForm _memberForm;
    Task<EdCaseDetail>? _detail;
    EdCaseDetail? _last;

    public EdCasePageViewModel(EdCase eCase, EdContext ctx) : base(ctx.Pipeline)
    {
        Case = eCase;
        _ctx = ctx;
        _memberForm = new AppPickForm(new PersonPicker("Add someone to the case: type a name", () =>
        {
            var have = (_last?.Members ?? []).Select(m => m.Address).Concat(Mine.OfType<SetCaseMember>().Where(m => m.Add).Select(m => m.Member.Address))
                .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            return GroupAccess.People(ctx.Index).Where(o => o.Address is null || !have.Contains(o.Address));
        }, o => Pipeline?.Stage(new SetCaseMember(Target, o.Target("user"), true))));
        Sections =
        [
            new AppSection("overview", "Overview", "Home", OverviewAsync, "Reading the case…"),
            new AppSection("searches", "Searches", "DocumentSearch", SearchesAsync, "Reading its searches…"),
            new AppSection("holds", "Holds", "LockClosed", HoldsAsync, "Reading its holds…"),
            new AppSection("members", "Members", "People", MembersAsync, "Reading who works on it…"),
        ];
        SelectedSection = Sections[0];
    }

    public EdCase Case { get; private set; }

    public override string Title => Case.Name;

    public override string Subtitle => Case.IsClosed
        ? $"Closed {EdContext.Day(Case.Closed)}{(Case.ClosedBy is { } by ? $" by {by}" : "")}"
        : $"eDiscovery case · created {EdContext.Day(Case.Created)}";

    public override string Icon => "Briefcase";

    ChangeTarget Target => EdPlaces.CaseTarget(Case.Name);

    bool CanChange => _ctx.Editable && !Case.IsClosed;

    public override IReadOnlyList<HealthChip> Badges
    {
        get
        {
            var (text, kind) = EdLists.CaseBadge(Case);
            var chips = new List<HealthChip> { new(text, kind, Case.IsClosed ? "Its holds no longer keep anything" : "Open for work") };
            if (_last is { } d && d.Holds.Count(h => h.Enabled) is > 0 and var on)
                chips.Add(new HealthChip($"{on} {(on == 1 ? "Hold" : "Holds")} On", BadgeKind.Info, "Content in their places can't be deleted for good"));
            return chips;
        }
    }

    /// <summary>Every change for this case: to it, its members, and its searches and holds.</summary>
    IEnumerable<EdChange> Mine
    {
        get
        {
            if (Pipeline is null)
                return [];
            var searches = (_last?.Searches.Select(s => s.Name) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var holds = (_last?.Holds.Select(h => h.Name) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Pipeline.Pending.OfType<EdChange>().Where(c => c switch
            {
                CreateSearch s => s.CaseName == Case.Name,
                CreateHold h => h.CaseName == Case.Name,
                _ => c.Item.Kind switch
                {
                    EdChange.CaseKind => c.Item.Name == Case.Name,
                    EdChange.SearchKind => searches.Contains(c.Item.Name),
                    _ => holds.Contains(c.Item.Name),
                },
            });
        }
    }

    protected override IEnumerable<ChangeOp> Pending => Mine.Where(c => c.Item.Kind == EdChange.CaseKind && c is not SetCaseMember);

    public override IReadOnlyList<PageButton> Buttons
    {
        get
        {
            var buttons = new List<PageButton> { new("Open in Purview", "Globe", "Open eDiscovery in the Microsoft Purview portal", () => Platform.OpenUrl(EdContext.PurviewCases)) };
            if (!_ctx.Editable)
                return buttons;
            var waiting = Mine.FirstOrDefault(c => c is SetCaseClosed or DeleteCase);
            if (waiting is not null)
                buttons.Add(new PageButton("Undo", "ArrowUndo", "Take the change back out of Pending Changes", () => Pipeline!.Unstage(waiting)));
            else
            {
                var holds = _last?.Holds.Count(h => h.Enabled) ?? 0;
                buttons.Add(Case.IsClosed
                    ? new PageButton("Reopen Case", "FolderOpen", "Make it active again (added to Pending Changes)", () => Pipeline!.Stage(new SetCaseClosed(Target, false, 0)))
                    : new PageButton("Close Case", "LockClosed", holds > 0 ? $"Close it: its {holds} holds stop keeping content (added to Pending Changes)" : "Close it (added to Pending Changes)",
                        () => Pipeline!.Stage(new SetCaseClosed(Target, true, holds))));
                buttons.Add(new PageButton("Delete Case", "Delete", "Delete it with its searches (holds must be deleted first; you type its name to confirm)",
                    () => Pipeline!.Stage(new DeleteCase(Target, Describe())), isDanger: true));
            }
            return buttons;
        }
    }

    string Describe() => _last is { } d
        ? $"{(Case.IsClosed ? "Closed" : "Active")}; {d.Searches.Count} searches, {d.Holds.Count} holds"
        : Case.IsClosed ? "Closed" : "Active";

    /// <summary>Shows the case as just read; false when it's gone.</summary>
    public bool Update(EdDirectory directory)
    {
        if ((directory.Cases.FirstOrDefault(c => c.Id == Case.Id) ?? directory.Cases.FirstOrDefault(c => c.Name == Case.Name)) is not { } now)
            return false;
        Case = now;
        _detail = null;
        Refresh();
        return true;
    }

    /// <summary>The searches, holds and members, read once for every tab; a failed read is tried again next time.</summary>
    async Task<EdCaseDetail> DetailAsync(CancellationToken cancellationToken)
    {
        var read = _detail ??= _ctx.Records.GetCaseDetailAsync(Case, cancellationToken);
        try
        {
            var detail = await read;
            if (!ReferenceEquals(_last, detail))
            {
                _last = detail;
                foreach (var name in new[] { nameof(Badges), nameof(Buttons), nameof(PendingText), nameof(HasPending) })
                    OnPropertyChanged(name);
            }
            return detail;
        }
        catch
        {
            if (ReferenceEquals(_detail, read))
                _detail = null;
            throw;
        }
    }

    async Task<IReadOnlyList<AppCard>> OverviewAsync(CancellationToken cancellationToken)
    {
        var detail = await DetailAsync(cancellationToken);
        var described = Mine.OfType<SetCaseDescription>().FirstOrDefault();
        IReadOnlyList<SummaryLine> facts =
        [
            new("Status", Case.IsClosed ? "Closed: its holds no longer keep anything" : "Active"),
            new("Created", EdContext.Day(Case.Created)),
            new("Last Changed", Case.ModifiedBy is { } by ? $"{EdContext.Day(Case.Modified)} by {by}" : EdContext.Day(Case.Modified)),
            .. Case.IsClosed ? [new SummaryLine("Closed", Case.ClosedBy is { } closer ? $"{EdContext.Day(Case.Closed)} by {closer}" : EdContext.Day(Case.Closed))] : Array.Empty<SummaryLine>(),
            new("Searches", $"{detail.Searches.Count:N0}"),
            new("Holds", detail.Holds.Count == 0 ? "None" : $"{detail.Holds.Count:N0} ({detail.Holds.Count(h => h.Enabled):N0} on)"),
            new("Members", $"{detail.Members.Count:N0}"),
        ];
        return
        [
            new AppCard("Details", Facts: facts),
            new AppCard("Description", described is null ? null : "The new description is in Pending Changes.",
                Form: new AppTextForm("DESCRIPTION", described?.New ?? Case.Description, "What the case is about", CanChange, text =>
                {
                    if (described is not null)
                        Pipeline!.Unstage(described);
                    if (text != (Case.Description ?? ""))
                        Pipeline!.Stage(new SetCaseDescription(Target, Case.Description, text.Length == 0 ? null : text));
                })),
        ];
    }

    async Task<IReadOnlyList<AppCard>> SearchesAsync(CancellationToken cancellationToken)
    {
        var detail = await DetailAsync(cancellationToken);
        var pending = Mine.ToArray();
        var rows = detail.Searches.OrderByDescending(s => s.Created).Select(s =>
        {
            var (text, kind) = EdLists.SearchBadge(s.Status);
            var row = new AppRow
            {
                Title = s.Name,
                Detail = string.Join(" · ", new[] { s.Description, s.LastRun is null ? "Never run" : $"Last run {EdContext.Day(s.LastRun)}" }.OfType<string>()),
                Badges = [new HealthChip(text, kind, "When it last ran, and how that went")],
                OpenAction = () => _ctx.OpenSearch(s),
                OpenTip = "Open the search: what it found, and where",
            };
            return Removable(row, _ctx.Editable, pending.OfType<DeleteSearch>().FirstOrDefault(d => d.Search.Name == s.Name),
                () => new DeleteSearch(EdPlaces.SearchTarget(s.Name), row.Detail ?? ""), "Delete", "Delete the search (what it found is untouched; added to Pending Changes)", "Deleting");
        }).ToList();
        rows.AddRange(pending.OfType<CreateSearch>().Select(c => Adding(c.Search.Name, c.After, c)));
        return
        [
            new AppCard("Searches", "Each search counts what matches a query in the places you choose. Open one to see what it found.", Rows: rows,
                EmptyText: "No searches yet.",
                Form: CanChange ? new AppButtonForm("New Search", "Look for content in mailboxes, sites and OneDrives.", "Make a search in this case (added to Pending Changes)",
                    () => _ctx.NewSearch(Case.Name)) : null),
        ];
    }

    async Task<IReadOnlyList<AppCard>> HoldsAsync(CancellationToken cancellationToken)
    {
        var detail = await DetailAsync(cancellationToken);
        var pending = Mine.ToArray();
        var rows = detail.Holds.OrderBy(h => h.Name, StringComparer.CurrentCulture).Select(h =>
        {
            var badges = new List<HealthChip>
            {
                h.Enabled ? new HealthChip("On", BadgeKind.Success, "Content in its places can't be deleted for good") : new HealthChip("Off", BadgeKind.Neutral, "It keeps nothing now"),
            };
            if (h.Enabled && h.Distribution is { } dist && dist != "Success")
                badges.Add(new HealthChip(dist == "Pending" ? "Being Put in Place" : "Not Everywhere", dist == "Pending" ? BadgeKind.Info : BadgeKind.Warning,
                    EDiscovery.DistributionText(dist)));
            var row = new AppRow
            {
                Title = h.Name,
                Detail = $"{EDiscovery.PlacesText(h.Mailboxes, h.Sites, h.PublicFolders)} · keeps {(h.Query.Length == 0 ? "everything" : "what matches its query")}",
                Code = h.Query.Length == 0 ? null : h.Query,
                Badges = badges,
            };
            var waiting = pending.FirstOrDefault(p => p.Item.Kind == EdChange.HoldKind && p.Item.Name == h.Name);
            // Purview keeps a deleted hold listed while it lifts it (seen live: up to about 20 minutes).
            if (h.IsDeleting)
                return new AppRow
                {
                    Title = h.Name,
                    Detail = $"{row.Detail} · Purview is lifting it; it disappears when done, and the case can be deleted then",
                    Badges = [new HealthChip("Being Deleted", BadgeKind.Neutral, "Deleted; Purview is still lifting it")],
                };
            if (!_ctx.Editable)
                return row;
            if (waiting is not null)
                return row.With(waiting switch { DeleteHold => "Deleting", SetHoldEnabled e => e.New ? "Turning On" : "Turning Off", _ => "Changing" },
                    "Undo", "Take the change back out of Pending Changes", () => Pipeline!.Unstage(waiting));
            return new AppRow
            {
                Title = row.Title,
                Detail = row.Detail,
                Code = row.Code,
                Badges = row.Badges,
                EditText = CanChange ? "Change" : null,
                EditTip = "Change where it applies and what it keeps (added to Pending Changes)",
                EditAct = () => _ctx.ShowDialog($"Change Hold {h.Name}",
                    new EdItemEditorViewModel(Pipeline!, true, Case.Name, [], _ctx.Index, _ctx.Sites, _ctx.CloseDialog, hold: h)),
                ActionText = "Delete",
                ActionTip = "Delete the hold: what it kept can be deleted again (you type its name to confirm)",
                Act = () => Pipeline!.Stage(new DeleteHold(EdPlaces.HoldTarget(h.Name), row.Detail)),
                AltText = Case.IsClosed ? null : h.Enabled ? "Turn Off" : "Turn On",
                AltTip = h.Enabled ? "Stop keeping content (added to Pending Changes)" : "Keep content again (added to Pending Changes)",
                AltAct = () => Pipeline!.Stage(new SetHoldEnabled(EdPlaces.HoldTarget(h.Name), h.Enabled, !h.Enabled)),
            };
        }).ToList();
        rows.AddRange(pending.OfType<CreateHold>().Select(c => Adding(c.Hold.Name, c.After, c)));
        return
        [
            new AppCard("Holds", "A hold keeps content from being deleted for good, whatever people or retention settings do, until the hold is turned off or deleted.",
                Rows: rows, EmptyText: "No holds.",
                Form: CanChange ? new AppButtonForm("New Hold", "Keep mail, sites or OneDrives from being deleted while the case is open.", "Make a hold in this case (added to Pending Changes)",
                    () => _ctx.ShowDialog($"New Hold in {Case.Name}",
                        new EdItemEditorViewModel(Pipeline!, true, Case.Name, detail.Holds.Select(h => h.Name), _ctx.Index, _ctx.Sites, _ctx.CloseDialog))) : null),
        ];
    }

    async Task<IReadOnlyList<AppCard>> MembersAsync(CancellationToken cancellationToken)
    {
        var detail = await DetailAsync(cancellationToken);
        var pending = Mine.OfType<SetCaseMember>().ToArray();
        var rows = detail.Members.OrderBy(m => m.Name, StringComparer.CurrentCulture).Select(m =>
        {
            var person = new ChangeTarget("user", m.Address ?? m.Name, m.Name, m.Address);
            return Removable(new AppRow { Title = m.Name, Detail = m.Address }, _ctx.Editable,
                pending.FirstOrDefault(p => !p.Add && string.Equals(p.Member.Address, m.Address, StringComparison.OrdinalIgnoreCase)),
                () => new SetCaseMember(Target, person, false), tip: "Stop them working on this case (added to Pending Changes)");
        }).ToList();
        rows.AddRange(pending.Where(p => p.Add).Select(p => Adding(p.Member.Name, p.Member.Address, p)));
        return
        [
            new AppCard("Members", "Members see the case and can work on its searches and holds. They also need an eDiscovery role in Purview.", Rows: rows,
                EmptyText: "Nobody is a member.", Form: _ctx.Editable ? _memberForm : null),
        ];
    }
}

/// <summary>
/// A search's page (8f-1): what it looks for and where, and what it found the last time it ran
/// (per place). Run Search counts again, with a wheel until Purview finishes.
/// </summary>
public sealed partial class EdSearchPageViewModel : CardPageViewModel
{
    /// <summary>How often a running search is checked on (settable so checks needn't wait).</summary>
    public static TimeSpan CheckEvery { get; set; } = TimeSpan.FromSeconds(10);

    static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(30);

    readonly EdContext _ctx;
    readonly CancellationTokenSource _closing = new();
    Task<EdSearchDetail>? _detail;

    public EdSearchPageViewModel(EdSearch search, EdContext ctx) : base(ctx.Pipeline)
    {
        Search = search;
        _ctx = ctx;
        Sections =
        [
            new AppSection("overview", "Overview", "Home", OverviewAsync, "Reading the search…"),
            new AppSection("found", "What It Found", "DocumentSearch", FoundAsync, "Reading what it found…"),
            new AppSection("actions", "Actions", "History", ActionsAsync, "Reading what's been done with its results…"),
        ];
        SelectedSection = Sections[0];
        if (EDiscovery.IsRunning(search.Status))
            _ = FollowRunAsync();
    }

    public EdSearch Search { get; private set; }

    public EdSearchDetail? Detail { get; private set; }

    public override string Title => Search.Name;

    public override string Subtitle => Search.CaseName is { } c ? $"Content search in case {c}" : "Content search (not in a case)";

    public override string Icon => "DocumentSearch";

    ChangeTarget Target => EdPlaces.SearchTarget(Search.Name);

    public override IReadOnlyList<HealthChip> Badges
    {
        get
        {
            var (text, kind) = EdLists.SearchBadge(Search.Status);
            var chips = new List<HealthChip> { new(text, kind, "When it last ran, and how that went") };
            if (Detail is { HasRun: true } d)
                chips.Add(new HealthChip($"{d.Items:N0} Items", BadgeKind.Info, $"{EDiscovery.Size(d.Bytes)} in all"));
            if (Detail?.Errors is not null)
                chips.Add(new HealthChip("Some Places Failed", BadgeKind.Warning, "Purview couldn't search everywhere; see Overview"));
            return chips;
        }
    }

    protected override IEnumerable<ChangeOp> Pending =>
        Pipeline?.Pending.OfType<EdChange>().Where(c => c.Item.Kind == EdChange.SearchKind && c.Item.Name == Search.Name) ?? [];

    public override IReadOnlyList<PageButton> Buttons
    {
        get
        {
            var buttons = new List<PageButton>();
            if (!IsBusy)
                buttons.Add(new PageButton("Run Search", "Play", "Count what it finds now (changes nothing)", () => _ = RunAsync(), isAccent: true));
            buttons.Add(new PageButton("Export", "ArrowDownload", "Save what it found to this computer, or export it in Purview",
                () => _ctx.ShowDialog($"Export {Search.Name}", new EdExportViewModel(_ctx.Records as IEdExports, Pipeline, Search, _ctx.CloseDialog))));
            buttons.Add(new PageButton("Open in Purview", "Globe", "Open eDiscovery in the Microsoft Purview portal", () => Platform.OpenUrl(EdContext.PurviewCases)));
            if (!_ctx.Editable)
                return buttons;
            if (Pending.FirstOrDefault() is { } waiting)
            {
                buttons.Add(new PageButton("Undo", "ArrowUndo", "Take the change back out of Pending Changes", () => Pipeline!.Unstage(waiting)));
                return buttons;
            }
            if (Detail is { HasRun: true, Mailboxes.Count: > 0 } found)
                buttons.Add(new PageButton("Search and Delete", "MailDismiss", "Delete what it found from the mailboxes (up to 10 per mailbox; you type the search's name to confirm)",
                    () => _ctx.ShowDialog($"Search and Delete: {Search.Name}", new EdPurgeViewModel(Pipeline!, found, _ctx.Index, _ctx.CloseDialog)), isDanger: true));
            if (Detail is { } detail)
                buttons.Add(new PageButton("Change Search", "Edit", "Change what it looks for and where (added to Pending Changes; it runs again once applied)",
                    () => _ctx.ShowDialog($"Change Search {Search.Name}",
                        new EdItemEditorViewModel(Pipeline!, false, Search.CaseName, [], _ctx.Index, _ctx.Sites, _ctx.CloseDialog, search: detail))));
            buttons.Add(new PageButton("Delete Search", "Delete", "Delete the search; what it found is untouched (added to Pending Changes)",
                () => Pipeline!.Stage(new DeleteSearch(Target, Detail is { } d ? $"Found {d.Items:N0} items" : EDiscovery.SearchStatus(Search.Status))), isDanger: true));
            return buttons;
        }
    }

    /// <summary>Shows the search as just listed; false when it's gone.</summary>
    public bool Update(EdDirectory directory)
    {
        if (directory.Searches.FirstOrDefault(s => s.Name == Search.Name && s.CaseName == Search.CaseName) is not { } now)
            return false;
        Search = now;
        _detail = null;
        Refresh();
        if (EDiscovery.IsRunning(now.Status) && !IsBusy)
            _ = FollowRunAsync();
        return true;
    }

    async Task<EdSearchDetail> DetailAsync(CancellationToken cancellationToken)
    {
        var read = _detail ??= _ctx.Records.GetEdSearchAsync(Search.Name, cancellationToken);
        try
        {
            var detail = await read;
            if (!ReferenceEquals(Detail, detail))
            {
                Detail = detail;
                Search = detail.Search with { CaseName = Search.CaseName };
                foreach (var name in new[] { nameof(Badges), nameof(Buttons), nameof(Title) })
                    OnPropertyChanged(name);
            }
            return detail;
        }
        catch
        {
            if (ReferenceEquals(_detail, read))
                _detail = null;
            throw;
        }
    }

    async Task RunAsync()
    {
        BusyText = "Starting the search…";
        Notice = null;
        OnPropertyChanged(nameof(Buttons));
        try
        {
            await _ctx.Records.StartEdSearchAsync(Search.Name, _closing.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            BusyText = null;
            Notice = $"Couldn't start the search: {Friendly.Message(e)}";
            OnPropertyChanged(nameof(Buttons));
            return;
        }
        await FollowRunAsync();
    }

    /// <summary>Reads the search every few seconds until Purview has finished counting (or the page closes).</summary>
    async Task FollowRunAsync()
    {
        BusyText = "Purview is counting what the search finds… This can take a few minutes.";
        OnPropertyChanged(nameof(Buttons));
        var started = DateTimeOffset.UtcNow;
        try
        {
            var finished = false;
            while (DateTimeOffset.UtcNow - started < GiveUpAfter)
            {
                await Task.Delay(CheckEvery, _closing.Token);
                _detail = null;
                var detail = await DetailAsync(_closing.Token);
                finished = !EDiscovery.IsRunning(detail.Search.Status) && detail.Search.Status != "NotStarted";
                if (finished)
                    break;
            }
            // Big searches can outlast the wait: say so rather than look finished.
            if (!finished)
                Notice = "Purview is still counting. This page stopped checking after 30 minutes; choose Run Search later, or open the search again to see where it got to.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            Notice = $"Couldn't check on the search: {Friendly.Message(e)}";
        }
        BusyText = null;
        Refresh();
    }

    async Task<IReadOnlyList<AppCard>> OverviewAsync(CancellationToken cancellationToken)
    {
        var d = await DetailAsync(cancellationToken);
        IReadOnlyList<SummaryLine> facts =
        [
            new("Status", EDiscovery.SearchStatus(Search.Status)),
            new("Case", Search.CaseName ?? "Not in a case"),
            .. Search.Description is { } desc ? [new SummaryLine("Description", desc)] : Array.Empty<SummaryLine>(),
            new("Looks In", EDiscovery.PlacesText(d.Mailboxes, d.Sites, d.PublicFolders)),
            new("Created", Search.CreatedBy is { } by ? $"{EdContext.Day(Search.Created)} by {by}" : EdContext.Day(Search.Created)),
            new("Last Run", Search.LastRun is null ? "Never" : Search.RunBy is { } runner ? $"{EdContext.Day(Search.LastRun)} by {runner}" : EdContext.Day(Search.LastRun)),
        ];
        var cards = new List<AppCard>
        {
            new("Details", Facts: facts),
            new("Query", d.Query.Length == 0 ? "No query: it finds everything in its places." : "Keyword query language (KQL), as Purview runs it.",
                Rows: d.Query.Length == 0 ? null : [new AppRow { Title = "Looks For", Code = d.Query }]),
        };
        if (d.HasRun)
            cards.Add(new AppCard("Found", Facts:
            [
                new("Items", $"{d.Items:N0} ({EDiscovery.Size(d.Bytes)})"),
                new("Not Searchable", d.UnindexedItems == 0 ? "None" : $"{d.UnindexedItems:N0} items ({EDiscovery.Size(d.UnindexedBytes)}) Purview couldn't read, such as some attachments or encrypted files"),
            ]));
        if (d.Errors is { } errors)
            cards.Add(new AppCard("Problems", "Purview couldn't search everywhere:", Rows: [new AppRow { Title = "What Purview Said", Code = errors }]));
        cards.Add(new AppCard("Places", Rows:
        [
            .. Places("Mailboxes", d.Mailboxes, EdPlaces.Mailboxes(d.Mailboxes, _ctx.Index)),
            .. Places("Sites and OneDrives", d.Sites, EdPlaces.Sites(d.Sites, _ctx.Sites())),
            .. d.PublicFolders ? [new AppRow { Title = "Public Folders", Detail = "All of them" }] : Array.Empty<AppRow>(),
        ], EmptyText: "It doesn't look anywhere."));
        return cards;
    }

    static IEnumerable<AppRow> Places(string title, IReadOnlyList<string> raw, IReadOnlyList<ChangeTarget> named) =>
        raw.Count == 0 ? []
        : EDiscovery.IsAll(raw) ? [new AppRow { Title = title, Detail = "All of them" }]
        : [new AppRow { Title = title, Chips = [.. named.Select(n => n.Name == EdChange.Place(n) ? n.Name : $"{n.Name} ({EdChange.Place(n)})")] }];

    async Task<IReadOnlyList<AppCard>> FoundAsync(CancellationToken cancellationToken)
    {
        var d = await DetailAsync(cancellationToken);
        if (!d.HasRun)
            return [new AppCard("What It Found", EmptyText: EDiscovery.IsRunning(Search.Status) ? "It's running now; counts appear when it finishes." : "It hasn't run yet. Use Run Search.")];
        return
        [
            new AppCard("What It Found, by Place", $"{d.Items:N0} items in all ({EDiscovery.Size(d.Bytes)}).",
                Rows: [.. d.Hits.OrderByDescending(h => h.Items).Select(h => new AppRow
                {
                    Title = h.Location,
                    Detail = h.Size is { } size ? $"{h.Items:N0} items · {size}" : $"{h.Items:N0} items",
                    Badges = h.Items == 0 ? [new HealthChip("Nothing Found", BadgeKind.Neutral, "No matches here")] : [],
                })],
                EmptyText: "Purview gave no count per place."),
        ];
    }

    async Task<IReadOnlyList<AppCard>> ActionsAsync(CancellationToken cancellationToken)
    {
        var actions = await _ctx.Records.GetSearchActionsAsync(Search.CaseName, Search.Name, cancellationToken);
        return
        [
            new AppCard("Actions", "Deletions (and, before Purview retired them, previews and exports) run on what this search found.",
                Rows: [.. actions.Select(a => new AppRow
                {
                    Title = a.Action switch { "Purge" => a.PurgeType == "HardDelete" ? "Hard Delete" : "Soft Delete", var other => other },
                    Detail = string.Join(" · ", new[]
                    {
                        $"Started {EdContext.Day(a.Created)}{(a.RunBy is { } by ? $" by {by}" : "")}",
                        a.ItemCount is { } n ? $"{n:N0} items" : null,
                        a.Ended is { } end && !EDiscovery.IsRunning(a.Status) ? $"finished {EdContext.Day(end)}" : null,
                    }.OfType<string>()),
                    Code = a.Errors,
                    Badges = [a.Status switch
                    {
                        "Completed" => new HealthChip("Done", BadgeKind.Success, "Finished"),
                        "Starting" or "InProgress" => new HealthChip("Running", BadgeKind.Info, "Purview is working on it"),
                        "PartiallySucceeded" => new HealthChip("Partly Done", BadgeKind.Warning, "Some mailboxes failed; see below"),
                        "Failed" => new HealthChip("Failed", BadgeKind.Danger, "See below"),
                        var other => new HealthChip(other, BadgeKind.Neutral, "Purview's status"),
                    }],
                })],
                EmptyText: "Nothing has been done with its results."),
        ];
    }

    // Cancelled, not disposed: a check still in flight reads the token again, and a disposed
    // source would throw instead of stopping it.
    public override void Dispose()
    {
        _closing.Cancel();
        base.Dispose();
    }
}
