using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.Records;

/// <summary>One app inside a license, with a switch. Turning it off or on stages the change.</summary>
public sealed partial class AppSwitch(LicensePlan plan, bool isOn, bool isPending, bool canEdit, Action<LicensePlan, bool> set) : ObservableObject
{
    public string Name => plan.Name;

    public bool CanEdit => canEdit;

    /// <summary>On or off as it will be once pending changes apply.</summary>
    public bool IsOn
    {
        get => isOn;
        set
        {
            if (value == isOn || !canEdit)
                return;
            isOn = value;
            set(plan, value);
        }
    }

    public bool IsPending => isPending;
}

/// <summary>A license on the user: where it comes from, its apps, and what can be done with it.</summary>
public sealed partial class LicenseRow : ObservableObject
{
    readonly Action _remove;

    public LicenseRow(string name, string source, bool isDirect, string? error, IReadOnlyList<AppSwitch> apps, bool pendingAdd, bool pendingRemove,
        bool canEdit, string? lockedReason, Action remove)
    {
        Name = name;
        Source = source;
        IsDirect = isDirect;
        Error = error;
        Apps = apps;
        IsPendingAdd = pendingAdd;
        IsPendingRemove = pendingRemove;
        CanEdit = canEdit;
        LockedReason = lockedReason;
        _remove = remove;
    }

    public string Name { get; }

    /// <summary>"Assigned directly" or "From group: All Staff".</summary>
    public string Source { get; }

    public bool IsDirect { get; }

    public string? Error { get; }

    public bool HasError => Error is not null;

    public IReadOnlyList<AppSwitch> Apps { get; }

    public bool HasApps => Apps.Count > 1;

    public string AppsSummary => $"{Apps.Count(a => a.IsOn)} of {Apps.Count} apps on";

    public bool IsPendingAdd { get; }

    public bool IsPendingRemove { get; }

    public string? PendingText => IsPendingAdd ? "Adding" : IsPendingRemove ? "Removing" : null;

    public bool HasPending => PendingText is not null;

    public bool CanEdit { get; }

    /// <summary>Why this license can't be changed here (it comes from a group), or null.</summary>
    public string? LockedReason { get; }

    public string RemoveTip => LockedReason ?? (IsPendingAdd || IsPendingRemove ? "Undo this pending change" : "Remove this license (added to Pending changes)");

    public bool CanRemove => CanEdit && LockedReason is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApps))]
    public partial bool IsExpanded { get; set; }

    /// <summary>A one-app license has nothing to switch separately: removing the license is the switch.</summary>
    public bool ShowApps => IsExpanded && HasApps;

    [RelayCommand]
    void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    void Remove() => _remove();
}

/// <summary>A license the user doesn't have, offered in the Add picker.</summary>
public sealed record LicenseOption(LicenseSku Sku)
{
    public bool IsAvailable => Sku.Available > 0;

    public string Label => IsAvailable ? $"{Sku.Name} · {Sku.Available:N0} of {Sku.Total:N0} left" : $"{Sku.Name} · none left";

    public override string ToString() => Label;
}

/// <summary>An enterprise app the user is assigned to.</summary>
public sealed partial class AppAssignmentRow(AppAssignment app, bool isPendingRemove, bool canEdit, Action toggle) : ObservableObject
{
    public string Name => app.AppName;

    public string Since => app.Since is { } s ? $"Assigned {s.ToLocalTime():MMM d, yyyy}" : "Assigned";

    public bool IsPendingRemove => isPendingRemove;

    public bool CanEdit => canEdit;

    public string RemoveTip => isPendingRemove ? "Undo: keep this app" : "Remove their access to this app (added to Pending changes)";

    [RelayCommand]
    void Remove() => toggle();
}

/// <summary>
/// The Licenses &amp; apps tab: which licenses the person has and where each comes from, the
/// apps inside each (switchable), adding a license (with how many are left), and the enterprise
/// apps they're assigned to. Read live when first shown; every change is staged.
/// </summary>
public sealed partial class LicensesSection : RecordSection, IDisposable
{
    UserSummary _user;
    readonly IUserRecords? _records;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    UserLicensing? _data;
    bool _loading;

    public LicensesSection(UserSummary user, IUserRecords? records, ChangePipeline? pipeline, Availability canEdit)
    {
        _user = user;
        _records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
        Summary = SummaryFor(user.Licenses);
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public override string Key => "licenses";
    public override string Title => "Licenses & apps";
    public override string Icon => "Apps";

    static string SummaryFor(IReadOnlyList<string> licenses) => licenses.Count switch
    {
        0 => "No licenses",
        1 => licenses[0],
        _ => $"{licenses[0]} + {licenses.Count - 1} more",
    };

    bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? LoadError { get; private set; }

    public bool HasError => LoadError is not null;

    public bool IsReady => !IsLoading && _data is not null;

    public ObservableCollection<LicenseRow> Licenses { get; } = [];

    public bool HasLicenses => Licenses.Count > 0;

    public ObservableCollection<AppAssignmentRow> AppAssignments { get; } = [];

    public bool HasAppAssignments => AppAssignments.Count > 0;

    [ObservableProperty]
    public partial IReadOnlyList<LicenseOption> AddOptions { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddLicenseCommand))]
    public partial LicenseOption? SelectedToAdd { get; set; }

    /// <summary>Microsoft refuses licenses until a usage location is set.</summary>
    public bool MissingUsageLocation => _data is not null && string.IsNullOrEmpty(_data.UsageLocation);

    public bool CanAddLicenses => CanEdit && !MissingUsageLocation;

    ChangeTarget Target => new("user", _user.Id, _user.DisplayName, _user.UserPrincipalName);

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_data is not null || _loading || _records is null)
            return;
        _loading = true;
        IsLoading = true;
        LoadError = null;
        try
        {
            _data = await _records.GetLicensingAsync(_user.Id, cancellationToken);
            Rebuild();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read their licenses: {Friendly.Message(e)}";
        }
        finally
        {
            _loading = false;
            IsLoading = false;
        }
    }

    public override void RecordChanged(UserSummary user)
    {
        _user = user;
        Summary = SummaryFor(user.Licenses);
        if (_data is null)
            return;
        _data = null;
        _ = LoadAsync();
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        if (_data is not null)
            Rebuild();
    }

    /// <summary>Rows from what's assigned plus what's pending: added licenses show as "Adding", removed as "Removing".</summary>
    void Rebuild()
    {
        var data = _data!;
        var catalog = data.Catalog.ToDictionary(s => s.SkuId, StringComparer.OrdinalIgnoreCase);
        Licenses.Clear();

        foreach (var a in data.Assignments.OrderBy(a => a.Name, StringComparer.CurrentCulture))
        {
            var pending = _pipeline?.PendingFor(_user.Id, "license:" + a.SkuId) as SetLicenseAssigned;
            var removing = a.IsDirect && pending is { New: false };
            var plans = catalog.TryGetValue(a.SkuId, out var sku) ? sku.Plans : [];
            Licenses.Add(new LicenseRow(a.Name,
                a.IsDirect ? "Assigned directly" : $"From group: {a.AssignedByGroupName}",
                a.IsDirect,
                a.Error is { } err ? UserLicensing.Explain(err) : null,
                Switches(a, plans),
                pendingAdd: false,
                pendingRemove: removing,
                CanEdit,
                a.IsDirect ? null : $"Inherited from {a.AssignedByGroupName}: change it on the group, or remove them from the group.",
                () => ToggleRemove(a)));
        }

        // Licenses staged to be added: shown at once, marked "Adding".
        foreach (var add in _pipeline?.Pending.OfType<SetLicenseAssigned>().Where(p => p.User.Id == _user.Id && p.New) ?? [])
        {
            if (data.Assignments.Any(a => a.IsDirect && a.SkuId == add.SkuId))
                continue;
            Licenses.Add(new LicenseRow(add.SkuName, "Assigned directly", true, null, [], pendingAdd: true, pendingRemove: false, CanEdit, null,
                () => _pipeline!.Unstage(add)));
        }

        AddOptions = data.Catalog
            .Where(s => !Licenses.Any(l => l.IsDirect && l.Name == s.Name))
            .Select(s => new LicenseOption(s))
            .OrderByDescending(o => o.IsAvailable)
            .ThenBy(o => o.Sku.Name, StringComparer.CurrentCulture)
            .ToArray();
        SelectedToAdd = null;

        AppAssignments.Clear();
        foreach (var app in data.Apps.OrderBy(a => a.AppName, StringComparer.CurrentCulture))
        {
            var pending = _pipeline?.PendingFor(_user.Id, "app:" + app.Id);
            AppAssignments.Add(new AppAssignmentRow(app, pending is not null, CanEdit, () =>
            {
                if (pending is not null)
                    _pipeline!.Unstage(pending);
                else
                    _pipeline!.Stage(new RemoveAppAssignment(Target, app.Id, app.AppName));
            }));
        }

        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(HasLicenses));
        OnPropertyChanged(nameof(HasAppAssignments));
        OnPropertyChanged(nameof(MissingUsageLocation));
        OnPropertyChanged(nameof(CanAddLicenses));
    }

    IReadOnlyList<AppSwitch> Switches(LicenseAssignment assignment, IReadOnlyList<LicensePlan> plans)
    {
        var pending = _pipeline?.PendingFor(_user.Id, "apps:" + assignment.SkuId) as SetLicenseApps;
        var disabled = (pending?.NewDisabled ?? assignment.DisabledPlanIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = plans.ToDictionary(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);
        // Apps in a group-based license follow the group's settings.
        var canEdit = CanEdit && assignment.IsDirect;
        return plans.Select(plan => new AppSwitch(plan, !disabled.Contains(plan.Id),
            pending is not null && pending.OldDisabled.Contains(plan.Id) != pending.NewDisabled.Contains(plan.Id),
            canEdit,
            (p, on) =>
            {
                var next = (pending?.NewDisabled ?? assignment.DisabledPlanIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (on)
                    next.Remove(p.Id);
                else
                    next.Add(p.Id);
                _pipeline!.Stage(new SetLicenseApps(Target, assignment.SkuId, assignment.Name, assignment.DisabledPlanIds, next.ToArray(), names));
            })).ToArray();
    }

    void ToggleRemove(LicenseAssignment assignment)
    {
        if (!CanEdit || !assignment.IsDirect)
            return;
        if (_pipeline!.PendingFor(_user.Id, "license:" + assignment.SkuId) is { } pending)
            _pipeline.Unstage(pending);
        else
            _pipeline.Stage(new SetLicenseAssigned(Target, assignment.SkuId, assignment.Name, true, false));
    }

    bool CanAdd => CanAddLicenses && SelectedToAdd is { IsAvailable: true };

    [RelayCommand(CanExecute = nameof(CanAdd))]
    void AddLicense()
    {
        if (SelectedToAdd is not { } option)
            return;
        _pipeline!.Stage(new SetLicenseAssigned(Target, option.Sku.SkuId, option.Sku.Name, false, true));
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
