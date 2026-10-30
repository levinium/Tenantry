using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.Records;

/// <summary>A change Tenantry made (or tried to make) to them.</summary>
public sealed class TenantryChangeRow(ChangeLogEntry entry)
{
    public string When => entry.At.ToLocalTime().ToString("d MMM yyyy h:mm tt");
    public string What => entry.Change.Label;
    public string Change => entry.Change is SecretViewed ? entry.Change.After : $"{entry.Change.Before} → {entry.Change.After}";
    public string Who => entry.Account;
    public string? Detail => entry.Detail;
    public bool HasDetail => !string.IsNullOrEmpty(entry.Detail);

    public string OutcomeText => entry.Outcome switch
    {
        ChangeOutcome.Applied => "Done",
        ChangeOutcome.AppliedNotYetVisible => "Done, Showing Soon",
        ChangeOutcome.Refused => "Stopped by Test Mode",
        _ => "Failed",
    };

    public BadgeKind OutcomeKind => entry.Outcome switch
    {
        ChangeOutcome.Applied or ChangeOutcome.AppliedNotYetVisible => BadgeKind.Success,
        ChangeOutcome.Refused => BadgeKind.Neutral,
        _ => BadgeKind.Danger,
    };
}

/// <summary>An entry of Microsoft's audit log.</summary>
public sealed class AuditRow(AuditEvent audit, bool byThem)
{
    public string When => audit.At.ToLocalTime().ToString("d MMM yyyy h:mm tt");
    public string Activity => audit.Activity;

    /// <summary>"By Pat Lee" for changes made to them; "To Finance team" for changes they made.</summary>
    public string Party => byThem
        ? audit.About is { Length: > 0 } about ? $"To {about}" : ""
        : audit.By is { Length: > 0 } by ? $"By {by}" : "By Microsoft 365";

    public string Category => audit.Category;
    public bool Failed => !audit.Succeeded;
    public string? Reason => audit.Reason;
    public bool HasReason => Failed && !string.IsNullOrEmpty(audit.Reason);
}

/// <summary>When one app last saw them active.</summary>
public sealed class AppActivityRow(string app, DateOnly? last, DateOnly today)
{
    public string App => app;

    public string LastText => last is { } day
        ? (today.DayNumber - day.DayNumber) switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            var n and < 30 => $"{n} days ago",
            _ => day.ToString("d MMM yyyy"),
        }
        : "No activity in the last 30 days";

    public bool IsActive => last is not null;
}

/// <summary>
/// The Activity tab (4d-1): what Tenantry changed for them (from its own change log), Microsoft's
/// directory audit log both ways (changes made to them, and changes they made), and the last day
/// each Microsoft 365 app saw them.
/// </summary>
public sealed partial class ActivitySection : RecordSection, IDisposable
{
    /// <summary>The most Tenantry changes listed; the log keeps every one.</summary>
    const int ChangeLimit = 200;

    UserSummary _user;
    readonly IActivityRecords? _records;
    readonly ChangePipeline? _pipeline;
    readonly Func<DateOnly> _today;
    bool _loaded;

    public ActivitySection(UserSummary user, IActivityRecords? records, ChangePipeline? pipeline, Func<DateOnly>? today = null)
    {
        _user = user;
        _records = records;
        _pipeline = pipeline;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        Summary = "Changes and sign-in activity";
        if (_pipeline is not null)
            _pipeline.Applied += OnApplied;
    }

    public override string Key => "activity";
    public override string Title => "Activity";
    public override string Icon => "History";

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
            return;
        _loaded = true;
        LoadChanges();
        var apps = LoadAppsAsync(cancellationToken);
        await LoadAuditAsync(cancellationToken);
        await apps;
    }

    public override void RecordChanged(UserSummary user) => _user = user;

    // ---- Changes in Tenantry ----------------------------------------------------------------

    public ObservableCollection<TenantryChangeRow> Changes { get; } = [];

    public bool HasNoChanges => Changes.Count == 0;

    public bool HasChangeLog => _pipeline is not null;

    void LoadChanges()
    {
        Changes.Clear();
        if (_pipeline is null)
            return;
        // Anything aimed at them, or adding or removing them somewhere (a group, a mailbox).
        foreach (var entry in _pipeline.Log.All().Where(e => e.Change.Guarded.Any(t => t.Id == _user.Id)).Take(ChangeLimit))
            Changes.Add(new TenantryChangeRow(entry));
        OnPropertyChanged(nameof(HasNoChanges));
        Summary = Changes.Count switch
        {
            0 => "No changes made in Tenantry",
            1 => "1 change made in Tenantry",
            var n => $"{n} changes made in Tenantry",
        };
    }

    void OnApplied(object? sender, IReadOnlyList<ChangeOp> applied)
    {
        if (_loaded && applied.Any(c => c.Guarded.Any(t => t.Id == _user.Id)))
            LoadChanges();
    }

    // ---- Microsoft audit log ----------------------------------------------------------------

    public ObservableCollection<AuditRow> Audit { get; } = [];

    /// <summary>False: changes made to them; true: changes they made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuditEmptyText), nameof(AuditAboutThem))]
    public partial bool AuditByThem { get; set; }

    /// <summary>The other choice of the pair, for the second option button.</summary>
    public bool AuditAboutThem
    {
        get => !AuditByThem;
        set => AuditByThem = !value;
    }

    partial void OnAuditByThemChanged(bool value) => _ = LoadAuditAsync(CancellationToken.None);

    [ObservableProperty]
    public partial bool AuditLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuditError))]
    public partial string? AuditError { get; private set; }

    public bool HasAuditError => AuditError is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuditUnavailable))]
    public partial string? AuditUnavailable { get; private set; }

    public bool HasAuditUnavailable => AuditUnavailable is not null;

    [ObservableProperty]
    public partial bool HasNoAudit { get; private set; }

    public string AuditEmptyText => AuditByThem
        ? "They made no directory changes in the period Microsoft keeps (30 days with Entra ID P1, 7 without)."
        : "No directory changes to them in the period Microsoft keeps (30 days with Entra ID P1, 7 without).";

    int _auditGeneration;

    async Task LoadAuditAsync(CancellationToken cancellationToken)
    {
        if (_records is null)
            return;
        // Switching between the two views quickly: only the latest read fills the list.
        var generation = ++_auditGeneration;
        var byThem = AuditByThem;
        AuditLoading = true;
        AuditError = null;
        try
        {
            var read = await _records.GetAuditAsync(_user.Id, byThem, cancellationToken);
            if (generation != _auditGeneration)
                return;
            AuditUnavailable = read.Unavailable;
            Audit.Clear();
            foreach (var audit in read.Value ?? [])
                Audit.Add(new AuditRow(audit, byThem));
            HasNoAudit = Audit.Count == 0 && read.Unavailable is null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (generation == _auditGeneration)
                AuditError = $"Couldn't read the audit log: {Friendly.Message(e)}";
        }
        finally
        {
            if (generation == _auditGeneration)
                AuditLoading = false;
        }
    }

    // ---- Last activity per app --------------------------------------------------------------

    public ObservableCollection<AppActivityRow> Apps { get; } = [];

    [ObservableProperty]
    public partial bool AppsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppsError))]
    public partial string? AppsError { get; private set; }

    public bool HasAppsError => AppsError is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppsUnavailable))]
    public partial string? AppsUnavailable { get; private set; }

    public bool HasAppsUnavailable => AppsUnavailable is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppsNote))]
    public partial string? AppsNote { get; private set; }

    public bool HasAppsNote => AppsNote is not null;

    async Task LoadAppsAsync(CancellationToken cancellationToken)
    {
        if (_records is null || string.IsNullOrEmpty(_user.UserPrincipalName))
            return;
        AppsLoading = true;
        AppsError = null;
        try
        {
            var read = await _records.GetAppActivityAsync(_user.UserPrincipalName, cancellationToken);
            AppsUnavailable = read.Unavailable;
            Apps.Clear();
            var today = _today();
            foreach (var (app, last) in read.Value?.Apps ?? [])
                Apps.Add(new AppActivityRow(app, last, today));
            AppsNote = read.Value is not { } activity ? null
                : activity.Apps.Count == 0 ? "Not in Microsoft's usage report yet (new accounts appear after a day or two)."
                : activity.ReportDate is { } day ? $"From Microsoft's usage report, up to {day:d MMM yyyy}." : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppsError = $"Couldn't read app activity: {Friendly.Message(e)}";
        }
        finally
        {
            AppsLoading = false;
        }
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Applied -= OnApplied;
    }
}
