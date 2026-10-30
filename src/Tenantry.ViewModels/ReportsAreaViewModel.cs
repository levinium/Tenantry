using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.MailSecurity;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>One health check: a built-in view of a list, why it matters, and whether any match is a concern.</summary>
/// <param name="Concern">Matches need looking at (admins without MFA); otherwise the count is for information.</param>
public sealed record ReportCheck(string Group, DataListViewModel List, string ViewId, string Why, bool Concern = true);

/// <summary>
/// The Reports area (9): health checks over the local copy, each a built-in list view with its live
/// count and Open List, and the Change Log of everything Tenantry has changed in this tenant.
/// Nothing here reads Microsoft 365 beyond what the lists already hold.
/// </summary>
public sealed partial class ReportsAreaViewModel : TabViewModel
{
    readonly IReadOnlyList<ReportCheck> _checks;
    readonly Action<DataListViewModel, SavedViewItem> _open;
    readonly HashSet<SavedViewItem> _watched = [];
    readonly Tenantry.Core.PowerShell.IChangeScripts? _scripts;
    readonly Action<ChangeOp>? _showPowerShell;
    readonly Action<string>? _openInConsole;
    bool _listsLoaded;
    bool _changeLogRead;
    bool _changeLogBehind;

    public static IReadOnlyList<MailSecurityPage> Pages { get; } =
    [
        new("reports", "Reports", "DataBarVertical"),
        new("signins", "Sign-In History", "PersonKey"),
        new("audits", "Directory Audits", "DocumentBulletList"),
        new("exchangeadmin", "Exchange Admin", "Mail"),
        new("changelog", "Change Log", "History"),
    ];

    /// <param name="open">Shows a list with one of its views applied.</param>
    /// <param name="archive">Sign-in and audit history kept on this computer (10), or null.</param>
    /// <param name="readNew">Adds new events to it now, or null.</param>
    /// <param name="scripts">The PowerShell for a logged change (Show PowerShell), or null.</param>
    /// <param name="showPowerShell">Shows that PowerShell in a popup.</param>
    /// <param name="openInConsole">Puts a logged console command back in the console, or null without one.</param>
    /// <param name="logNotes">What the last archive run couldn't read, for the history pages.</param>
    public ReportsAreaViewModel(IReadOnlyList<ReportCheck> checks, ChangePipeline? pipeline, Action<DataListViewModel, SavedViewItem> open, Action<ChangeTarget>? openTarget,
        IListStateStore? listState, Func<DateTimeOffset> now, Tenantry.Core.Cache.ILogArchive? archive = null, Func<Task>? readNew = null, Action<string>? openUserById = null,
        Tenantry.Core.PowerShell.IChangeScripts? scripts = null, Action<ChangeOp>? showPowerShell = null, Action<string>? openInConsole = null,
        Func<IReadOnlyList<string>>? logNotes = null)
    {
        _scripts = scripts;
        _showPowerShell = showPowerShell;
        _openInConsole = openInConsole;
        SignIns = LogHistoryViewModel.Create(archive, Tenantry.Core.Directory.LogKinds.SignIns,
            "Every interactive sign-in, kept on this computer for a year (Microsoft keeps 30 days). Non-interactive sign-ins can be added in Settings.",
            LogLists.SignIns(e => LogLists.SignInPreview(e, openUserById)), listState, now, readNew, logNotes);
        Audits = LogHistoryViewModel.Create(archive, Tenantry.Core.Directory.LogKinds.Audits,
            "Changes made in Entra (users, groups, roles, apps), kept on this computer for a year.", LogLists.Audits(LogLists.AuditPreview), listState, now, readNew, logNotes);
        ExchangeAdmin = LogHistoryViewModel.Create(archive, Tenantry.Core.Directory.LogKinds.ExchangeAdmin,
            "Exchange commands run by admins (and by Tenantry), from the unified audit log, kept on this computer for a year.",
            LogLists.ExchangeAdmin(LogLists.ExchangePreview), listState, now, readNew, logNotes);
        _checks = checks;
        _open = open;
        Reports = new AppSection("reports", "Reports", "DataBarVertical", BuildAsync, "Counting from the local copy…");
        ChangeLog = DataListViewModel.Create(ChangeLogList.Definition(e => Preview(e, openTarget)),
            // A year of bulk edits is a lot to read: off the window's thread.
            ct => Task.Run(() => (IReadOnlyList<ChangeLogEntry>)(pipeline?.Log.All() ?? []), ct), _ => { }, listState, now);
        ChangeLog.HasRecordPages = false;
        ChangeLog.EmptyText = pipeline is null ? "This tenant can't be changed here, so nothing has been logged." : "No changes match.";
        if (pipeline is not null)
        {
            pipeline.Applied += (_, _) => FollowLog();
            pipeline.Recorded += (_, _) => FollowLog();
        }
        SelectedPage = Pages[0];
    }

    /// <summary>
    /// Something new was logged: the Change Log re-reads now if it's on screen, otherwise the next
    /// time it's shown (rather than reading the whole log after every change and console command).
    /// </summary>
    void FollowLog()
    {
        if (!_changeLogRead)
            return;
        if (ShowChangeLog)
            _ = ChangeLog.ReloadAsync(busyText: "Reading the change log…");
        else
            _changeLogBehind = true;
    }

    public override string Title => "Reports";

    public override bool CanClose => false;

    public AppSection Reports { get; }

    public DataListViewModel ChangeLog { get; }

    public LogHistoryViewModel SignIns { get; }
    public LogHistoryViewModel Audits { get; }
    public LogHistoryViewModel ExchangeAdmin { get; }

    /// <summary>The sync added events to the history.</summary>
    public void Archived()
    {
        SignIns.Archived();
        Audits.Archived();
        ExchangeAdmin.Archived();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReports), nameof(ShowChangeLog), nameof(ShowSignIns), nameof(ShowAudits), nameof(ShowExchangeAdmin))]
    public partial MailSecurityPage SelectedPage { get; set; }

    public bool ShowReports => SelectedPage.Key == "reports";
    public bool ShowChangeLog => SelectedPage.Key == "changelog";
    public bool ShowSignIns => SelectedPage.Key == "signins";
    public bool ShowAudits => SelectedPage.Key == "audits";
    public bool ShowExchangeAdmin => SelectedPage.Key == "exchangeadmin";

    partial void OnSelectedPageChanged(MailSecurityPage value) => Shown();

    /// <summary>Reads what the shown page needs the first time.</summary>
    public void Shown()
    {
        if (ShowReports)
            _ = Reports.LoadAsync();
        if (ShowSignIns)
            SignIns.Shown();
        if (ShowAudits)
            Audits.Shown();
        if (ShowExchangeAdmin)
            ExchangeAdmin.Shown();
        if (ShowChangeLog && !_changeLogRead)
        {
            _changeLogRead = true;
            _ = ChangeLog.LoadAsync();
        }
        else if (ShowChangeLog && _changeLogBehind)
        {
            _changeLogBehind = false;
            _ = ChangeLog.ReloadAsync(busyText: "Reading the change log…");
        }
    }

    [RelayCommand]
    async Task Refresh()
    {
        if (ShowChangeLog)
        {
            await ChangeLog.ReloadAsync(busyText: "Reading the change log…");
            return;
        }
        _listsLoaded = false;
        Reports.Rebuild();
    }

    async Task<IReadOnlyList<AppCard>> BuildAsync(CancellationToken cancellationToken)
    {
        // Lists not shown yet haven't read the local copy; reading it is quick.
        if (!_listsLoaded)
        {
            _listsLoaded = true;
            await Task.WhenAll(_checks.Select(c => c.List).Distinct().Where(l => l.Total == 0 && !l.IsLoading).Select(l => l.LoadAsync(cancellationToken)));
        }
        return [.. _checks.GroupBy(c => c.Group).Select(g => new AppCard(g.Key, Rows: [.. g.Select(Row).OfType<AppRow>()]))];
    }

    AppRow? Row(ReportCheck check)
    {
        if (check.List.Views.FirstOrDefault(v => v.View.Id == check.ViewId) is not { } view)
            return null;
        // Counts change as the lists sync; the card follows.
        if (_watched.Add(view))
            view.PropertyChanged += OnCountChanged;
        var count = view.Count ?? 0;
        var badge = count == 0
            ? new HealthChip("None", BadgeKind.Success, "Nothing matches")
            : new HealthChip(count.ToString("N0", CultureInfo.CurrentCulture), check.Concern ? BadgeKind.Warning : BadgeKind.Info,
                check.Concern ? "Worth a look" : "For information");
        return new AppRow
        {
            Title = view.Name,
            Detail = $"{check.List.Title} · {check.Why}",
            Badges = [badge],
            EditText = "Open List",
            EditTip = $"Show {check.List.Title} with the {view.Name} view",
            EditAct = () => _open(check.List, view),
        };
    }

    void OnCountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SavedViewItem.Count))
            Reports.Rebuild();
    }

    AppPreviewViewModel Preview(ChangeLogEntry e, Action<ChangeTarget>? openTarget)
    {
        var (text, kind) = ChangeLogList.OutcomeBadge(e.Outcome);
        var target = e.Change.Target;
        var canOpen = openTarget is not null && target.Kind is "user" or "group";
        var buttons = new List<PageButton>();
        if (e.Change is PowerShellCommand command && _openInConsole is { } reopen)
            buttons.Add(new PageButton("Open in Console", "WindowConsole", "Put this command in the PowerShell console, to change and run again", () => reopen(command.Script)));
        else if (_showPowerShell is { } show && _scripts?.For(e.Change) is not null)
            buttons.Add(new PageButton("Show PowerShell", "WindowConsole", "The PowerShell that does the same change, to copy or run in the console", () => show(e.Change)));
        if (e.Change is PowerShellCommand ran)
            return new AppPreviewViewModel(e.Change.Label, $"{e.At.ToLocalTime().ToString("d MMM yyyy h:mm tt", CultureInfo.CurrentCulture)} by {e.Account}",
                [new HealthChip(e.Outcome == ChangeOutcome.Applied ? "Ran Without Errors" : "Had Errors", e.Outcome == ChangeOutcome.Applied ? BadgeKind.Success : BadgeKind.Danger,
                    "Whether PowerShell reported errors (its output isn't kept)")],
                e.Detail is { Length: > 0 } said ? [new SummaryLine("Detail", said)] : [],
                "", null, buttons) { Code = ran.Script };
        return new AppPreviewViewModel(e.Change.Label, $"{e.At.ToLocalTime().ToString("d MMM yyyy h:mm tt", CultureInfo.CurrentCulture)} by {e.Account}",
            [
                new HealthChip(text, kind, "What happened when it was applied"),
                .. e.Change.IsCaution ? [new HealthChip("Caution Change", BadgeKind.Neutral, "Asked for a second confirmation")] : Array.Empty<HealthChip>(),
            ],
            [
                new("Changed", target.Address is { Length: > 0 } a && a != target.Name ? $"{target.Name} ({a})" : target.Name),
                new("Kind", ChangeLogList.KindText(target.Kind)),
                new("Before", e.Change.Before),
                new("After", e.Change.After),
                .. e.Detail is { Length: > 0 } d ? [new SummaryLine("Detail", d)] : Array.Empty<SummaryLine>(),
            ],
            target.Kind == "group" ? "Open Group" : "Open Person", canOpen ? () => openTarget!(target) : null, buttons);
    }
}
