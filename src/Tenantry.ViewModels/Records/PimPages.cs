using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.Records;

/// <summary>A PIM page that reads live: a wheel while it reads, and a note when it can't (no Entra ID P2).</summary>
public abstract partial class PimPage : ObservableObject
{
    [ObservableProperty]
    public partial bool IsLoading { get; protected set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; protected set; }

    public bool HasNote => Note is not null;

    bool _read;

    /// <summary>Reads the first time it's shown.</summary>
    public void Shown()
    {
        if (!_read)
            _ = ReadAgainAsync();
    }

    [RelayCommand]
    public async Task ReadAgainAsync()
    {
        _read = true;
        IsLoading = true;
        try
        {
            await ReadAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note = $"Couldn't read this: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected abstract Task ReadAsync();
}

/// <summary>One of the admin's own roles: eligible (Activate, with a form) or switched on (Deactivate).</summary>
public sealed partial class MyRoleRow : ObservableObject
{
    readonly MyRolesViewModel _page;

    internal MyRoleRow(MyRolesViewModel page, RoleGrant grant)
    {
        _page = page;
        Grant = grant;
    }

    public RoleGrant Grant { get; }
    public string Name => Grant.PrincipalName;
    public bool IsActivated => Grant.Activated;
    public bool IsEligible => Grant.Kind == RoleGrantKind.Eligible;
    public string Detail => IsActivated ? $"Switched on {PimPageEnds(Grant.End)}" : IsEligible ? $"Eligible {PimPageEnds(Grant.End)}" : $"Active {PimPageEnds(Grant.End)}";

    static string PimPageEnds(DateTimeOffset? end) => end is { } e ? $"until {e.ToLocalTime().ToString("d MMM yyyy h:mm tt", CultureInfo.CurrentCulture)}" : "permanently";

    [ObservableProperty]
    public partial string? StateText { get; internal set; }

    public bool HasState => StateText is not null;

    partial void OnStateTextChanged(string? value) => OnPropertyChanged(nameof(HasState));

    // ---- Activating ------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<int> HourChoices { get; private set; } = [1, 2, 4, 8];

    [ObservableProperty]
    public partial int Hours { get; set; } = 1;

    [ObservableProperty]
    public partial string Justification { get; set; } = "";

    [ObservableProperty]
    public partial string Ticket { get; set; } = "";

    [ObservableProperty]
    public partial bool NeedsJustification { get; private set; }

    [ObservableProperty]
    public partial bool NeedsTicket { get; private set; }

    /// <summary>"Needs MFA and an approver's yes", from the role's settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNeeds))]
    public partial string? Needs { get; private set; }

    public bool HasNeeds => Needs is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>Opens the form, reading the role's settings for how long and what it needs.</summary>
    [RelayCommand]
    async Task Open()
    {
        IsOpen = !IsOpen;
        if (!IsOpen || _page.Records is not { } records)
            return;
        var settings = (await records.GetRoleSettingsAsync(Grant.RoleId)).Value;
        if (settings is null)
            return;
        var max = Math.Max(1, (int)settings.MaxActivation.TotalHours);
        HourChoices = [.. Enumerable.Range(1, max)];
        Hours = Math.Min(Hours, max);
        NeedsJustification = settings.ActivationJustification;
        NeedsTicket = settings.ActivationTicket;
        var needs = new[] { settings.ActivationMfa ? "MFA (you may be asked to sign in again)" : null, settings.ApprovalRequired ? "an approver's yes" : null }.OfType<string>().ToArray();
        Needs = needs.Length == 0 ? null : "Needs " + string.Join(" and ", needs) + ".";
    }

    [RelayCommand]
    void Activate()
    {
        Error = NeedsJustification && Justification.Trim().Length == 0 ? "Say why you need it."
            : NeedsTicket && Ticket.Trim().Length == 0 ? "Give the ticket number."
            : null;
        if (Error is not null)
            return;
        _page.Stage(new ActivateRole(_page.MeTarget, Grant.RoleId, Name, Grant.ScopeId, true, Hours,
            Justification.Trim() is { Length: > 0 } j ? j : null, Ticket.Trim() is { Length: > 0 } t ? t : null));
        IsOpen = false;
    }

    [RelayCommand]
    void Deactivate() => _page.Stage(new ActivateRole(_page.MeTarget, Grant.RoleId, Name, Grant.ScopeId, false));
}

/// <summary>
/// My Roles (PIM): the signed-in admin's eligible roles, to switch on for a while with a reason, and
/// the ones switched on, to switch off early. Each goes to Pending changes.
/// </summary>
public sealed class MyRolesViewModel : PimPage
{
    readonly ChangePipeline? _pipeline;
    readonly string? _me;
    readonly IDirectoryIndex _index;

    public MyRolesViewModel(IRoleRecords? records, ChangePipeline? pipeline, string? me, IDirectoryIndex index)
    {
        Records = records;
        _pipeline = pipeline;
        _me = me;
        _index = index;
        if (pipeline is not null)
        {
            pipeline.Changed += (_, _) => MarkPending();
            pipeline.Applied += (_, applied) =>
            {
                if (applied.Any(c => c is ActivateRole or ReviewRoleRequest))
                    _ = ReadAgainAsync();
            };
        }
    }

    internal IRoleRecords? Records { get; }

    public ObservableCollection<MyRoleRow> Eligible { get; } = [];
    public ObservableCollection<MyRoleRow> Active { get; } = [];

    public bool HasNoEligible => !IsLoading && Note is null && Eligible.Count == 0;
    public bool HasActive => Active.Count > 0;

    internal ChangeTarget MeTarget =>
        _index.AllUsers.FirstOrDefault(u => string.Equals(u.UserPrincipalName, _me, StringComparison.OrdinalIgnoreCase)) is { } user
            ? GroupAccess.Target(user)
            : new ChangeTarget("user", _me ?? "me", _me ?? "You", _me);

    internal void Stage(ChangeOp change) => _pipeline?.Stage(change);

    protected override async Task ReadAsync()
    {
        if (Records is null)
        {
            Note = "Privileged Identity Management isn't available for this tenant.";
            return;
        }
        var read = await Records.GetMyRolesAsync();
        Note = read.Unavailable;
        Eligible.Clear();
        Active.Clear();
        foreach (var g in read.Value ?? [])
            (g.Kind == RoleGrantKind.Eligible ? Eligible : Active).Add(new MyRoleRow(this, g));
        MarkPending();
        OnPropertyChanged(nameof(HasNoEligible));
        OnPropertyChanged(nameof(HasActive));
    }

    void MarkPending()
    {
        var pending = _pipeline?.Pending.OfType<ActivateRole>().ToArray() ?? [];
        foreach (var row in Eligible.Concat(Active))
            row.StateText = pending.FirstOrDefault(p => p.RoleId == row.Grant.RoleId) is { } p ? (p.On ? "Switching On" : "Switching Off") : null;
    }
}

/// <summary>A request waiting for the admin's answer, with Approve and Deny.</summary>
public sealed partial class RoleRequestRow(RoleRequest request, ApprovalsViewModel page) : ObservableObject
{
    public RoleRequest Request => request;
    public string Who => request.PrincipalName;
    public string What => $"{request.RoleName}{(request.Duration is { } d ? $" for {RolePolicyRules.Words(d)}" : "")}";
    public string Why => request.Justification is { Length: > 0 } j ? $"“{j}”" : "No reason given";
    public string When => request.Requested is { } at ? Display.Exact(at) : "";

    [ObservableProperty]
    public partial string Reason { get; set; } = "";

    [ObservableProperty]
    public partial string? StateText { get; internal set; }

    public bool HasState => StateText is not null;

    partial void OnStateTextChanged(string? value) => OnPropertyChanged(nameof(HasState));

    [RelayCommand]
    void Approve() => page.Review(this, true);

    [RelayCommand]
    void Deny() => page.Review(this, false);
}

/// <summary>Approvals (PIM): role activation requests waiting for the signed-in admin; Approve or Deny goes to Pending changes.</summary>
public sealed class ApprovalsViewModel : PimPage
{
    readonly IRoleRecords? _records;
    readonly ChangePipeline? _pipeline;

    public ApprovalsViewModel(IRoleRecords? records, ChangePipeline? pipeline)
    {
        _records = records;
        _pipeline = pipeline;
        if (pipeline is not null)
        {
            pipeline.Changed += (_, _) => MarkPending();
            pipeline.Applied += (_, applied) =>
            {
                if (applied.Any(c => c is ReviewRoleRequest))
                    _ = ReadAgainAsync();
            };
        }
    }

    public ObservableCollection<RoleRequestRow> Requests { get; } = [];

    public bool HasNone => !IsLoading && Note is null && Requests.Count == 0;

    protected override async Task ReadAsync()
    {
        if (_records is null)
        {
            Note = "Privileged Identity Management isn't available for this tenant.";
            return;
        }
        var read = await _records.GetApprovalsAsync();
        Note = read.Unavailable;
        Requests.Clear();
        foreach (var r in read.Value ?? [])
            Requests.Add(new RoleRequestRow(r, this));
        MarkPending();
        OnPropertyChanged(nameof(HasNone));
    }

    internal void Review(RoleRequestRow row, bool approve)
    {
        var r = row.Request;
        _pipeline?.Stage(new ReviewRoleRequest(new ChangeTarget("rolerequest", r.Id, $"{r.PrincipalName}: {r.RoleName}", r.PrincipalUpn), r.ApprovalId, approve,
            row.Reason.Trim() is { Length: > 0 } reason ? reason : approve ? "Approved in Tenantry" : "Denied in Tenantry"));
    }

    void MarkPending()
    {
        var pending = _pipeline?.Pending.OfType<ReviewRoleRequest>().ToArray() ?? [];
        foreach (var row in Requests)
            row.StateText = pending.FirstOrDefault(p => p.Request.Id == row.Request.Id) is { } p ? (p.Approve ? "Approving" : "Denying") : null;
    }
}
