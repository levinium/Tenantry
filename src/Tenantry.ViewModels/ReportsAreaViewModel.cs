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
    bool _listsLoaded;
    bool _changeLogRead;

    public static IReadOnlyList<MailSecurityPage> Pages { get; } =
    [
        new("reports", "Reports", "DataBarVertical"),
        new("changelog", "Change Log", "History"),
    ];

    /// <param name="open">Shows a list with one of its views applied.</param>
    public ReportsAreaViewModel(IReadOnlyList<ReportCheck> checks, ChangePipeline? pipeline, Action<DataListViewModel, SavedViewItem> open, Action<ChangeTarget>? openTarget,
        IListStateStore? listState, Func<DateTimeOffset> now)
    {
        _checks = checks;
        _open = open;
        Reports = new AppSection("reports", "Reports", "DataBarVertical", BuildAsync, "Counting from the local copy…");
        ChangeLog = DataListViewModel.Create(ChangeLogList.Definition(e => Preview(e, openTarget)),
            _ => Task.FromResult<IReadOnlyList<ChangeLogEntry>>(pipeline?.Log.All() ?? []), _ => { }, listState, now);
        ChangeLog.HasRecordPages = false;
        ChangeLog.EmptyText = pipeline is null ? "This tenant can't be changed here, so nothing has been logged." : "No changes match.";
        if (pipeline is not null)
            pipeline.Applied += (_, _) =>
            {
                if (_changeLogRead)
                    _ = ChangeLog.ReloadAsync(busyText: "Reading the change log…");
            };
        SelectedPage = Pages[0];
    }

    public override string Title => "Reports";

    public override bool CanClose => false;

    public AppSection Reports { get; }

    public DataListViewModel ChangeLog { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReports), nameof(ShowChangeLog))]
    public partial MailSecurityPage SelectedPage { get; set; }

    public bool ShowReports => SelectedPage.Key == "reports";
    public bool ShowChangeLog => SelectedPage.Key == "changelog";

    partial void OnSelectedPageChanged(MailSecurityPage value) => Shown();

    /// <summary>Reads what the shown page needs the first time.</summary>
    public void Shown()
    {
        if (ShowReports)
            _ = Reports.LoadAsync();
        if (ShowChangeLog && !_changeLogRead)
        {
            _changeLogRead = true;
            _ = ChangeLog.LoadAsync();
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

    static AppPreviewViewModel Preview(ChangeLogEntry e, Action<ChangeTarget>? openTarget)
    {
        var (text, kind) = ChangeLogList.OutcomeBadge(e.Outcome);
        var target = e.Change.Target;
        var canOpen = openTarget is not null && target.Kind is "user" or "group";
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
            target.Kind == "group" ? "Open Group" : "Open Person", canOpen ? () => openTarget!(target) : null);
    }
}
