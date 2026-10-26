using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>Someone who can open the whole OneDrive: its owner, or someone given access.</summary>
public sealed class OneDriveAccessRow(string name, string? detail, bool isOwner, string? pendingText, bool canRemove, string removeText, Action remove)
{
    public string Name => name;
    public string? Detail => detail;
    public bool HasDetail => !string.IsNullOrEmpty(detail);
    public bool IsOwner => isOwner;
    public string? PendingText => pendingText;
    public bool IsPending => pendingText is not null;
    public bool CanRemove => canRemove;
    public string RemoveText => removeText;
    public IRelayCommand RemoveCommand { get; } = new RelayCommand(remove);
}

/// <summary>
/// The OneDrive tab (4d-1): storage used against the quota, a link to open it, and who can open
/// all of it. Giving someone access (a manager taking over a leaver's files) and taking it away go
/// through Pending changes. A OneDrive is made the first time its owner opens it; for one that
/// isn't there yet, the tab offers the PowerShell command that sets it up.
/// </summary>
public sealed partial class OneDriveSection : RecordSection, IDisposable
{
    UserSummary _user;
    readonly IOneDriveRecords? _records;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly IDirectoryIndex _index;
    IReadOnlyList<SiteAdmin>? _admins;
    bool _loaded;

    public OneDriveSection(UserSummary user, IOneDriveRecords? records, ChangePipeline? pipeline, Availability canEdit, IDirectoryIndex index)
    {
        _user = user;
        _records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _index = index;
        Summary = "Storage, and who can open it";
        AccessPicker = new PersonPicker("Give someone access: type a name", AccessOptions, GiveAccess);
        if (_pipeline is not null)
        {
            _pipeline.Changed += OnPendingChanged;
            _pipeline.Applied += OnApplied;
        }
    }

    public override string Key => "onedrive";
    public override string Title => "OneDrive";
    public override string Icon => "Cloud";

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    ChangeTarget Target => new("user", _user.Id, _user.DisplayName, _user.UserPrincipalName);

    // ---- Loading ----------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? LoadError { get; private set; }

    public bool HasError => LoadError is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnavailable))]
    public partial string? Unavailable { get; private set; }

    public bool HasUnavailable => Unavailable is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrive), nameof(IsNotSetUp), nameof(UsedText), nameof(UsedPercent), nameof(HasQuota), nameof(IsNearlyFull),
        nameof(DeletedText), nameof(HasDeleted), nameof(LastChangedText), nameof(Url))]
    public partial OneDriveInfo? Drive { get; private set; }

    public bool HasDrive => Drive is not null;

    /// <summary>Read, and there's no OneDrive yet.</summary>
    public bool IsNotSetUp => _loaded && Drive is null && Unavailable is null && LoadError is null;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded || IsLoading || _records is null)
            return;
        IsLoading = true;
        LoadError = null;
        try
        {
            var read = await _records.GetOneDriveAsync(_user.Id, cancellationToken);
            Unavailable = read.Unavailable;
            Drive = read.Value;
            _loaded = true;
            Summary = Drive is { } d ? (d.Used is { } used ? $"{MailDetails.Size(used)} used" : "Set up") + (d.IsNearlyFull ? " · nearly full" : "")
                : Unavailable is null ? "Not set up yet" : "Can't be read";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read their OneDrive: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsNotSetUp));
        }
        if (Drive is not null)
            await LoadAdminsAsync(cancellationToken);
    }

    public override void RecordChanged(UserSummary user) => _user = user;

    void Reload()
    {
        _loaded = false;
        _admins = null;
        _ = LoadAsync();
    }

    // ---- Storage ----------------------------------------------------------------------------

    public string? Url => Drive?.Url;

    public bool HasQuota => Drive is { Used: not null, Total: > 0 };

    public string UsedText => Drive is { Used: { } used } d
        ? d.Total is { } total and > 0 ? $"{MailDetails.Size(used)} of {MailDetails.Size(total)} used" : $"{MailDetails.Size(used)} used"
        : "Size not reported";

    public double UsedPercent => Drive is { Used: { } used, Total: { } total and > 0 } ? Math.Min(100, 100.0 * used / total) : 0;

    public bool IsNearlyFull => Drive?.IsNearlyFull == true;

    public bool HasDeleted => Drive is { Deleted: > 0 };

    public string DeletedText => Drive?.Deleted is { } deleted ? $"{MailDetails.Size(deleted)} in the recycle bin (counts toward the quota)" : "";

    public string LastChangedText => Drive?.LastChanged is { } at ? $"Last changed {at.ToLocalTime():d MMM yyyy}" : "";

    [RelayCommand]
    void Open()
    {
        if (Url is { Length: > 0 } url)
            Platform.OpenUrl(url);
    }

    [ObservableProperty]
    public partial bool Copied { get; private set; }

    [RelayCommand]
    void CopyLink()
    {
        if (Url is not { Length: > 0 } url)
            return;
        Platform.CopyText(url);
        Copied = true;
    }

    // ---- Not set up yet ---------------------------------------------------------------------

    public bool HasLicense => _user.Licenses.Count > 0;

    // SharePoint refuses "set up a OneDrive" from any app but Microsoft's own tools (see
    // SharePointAdmin), so the tab hands over the PowerShell command instead.
    public string NotSetUpText => HasLicense
        ? "Microsoft 365 creates a OneDrive the first time they open it. To set it up sooner (for example so a manager can have their files), run this in SharePoint Online PowerShell; SharePoint only accepts this from Microsoft's own tools. It's ready within a few minutes."
        : "They have no license, and a OneDrive needs one that includes it.";

    public string SetUpCommandText => $"Request-SPOPersonalSite -UserEmails \"{_user.UserPrincipalName}\"";

    [ObservableProperty]
    public partial bool CommandCopied { get; private set; }

    [RelayCommand]
    void CopySetUpCommand()
    {
        Platform.CopyText(SetUpCommandText);
        CommandCopied = true;
    }

    [RelayCommand]
    void CheckAgain() => Reload();

    // ---- Who can open it --------------------------------------------------------------------

    public ObservableCollection<OneDriveAccessRow> Access { get; } = [];

    [ObservableProperty]
    public partial bool AccessLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccessError))]
    public partial string? AccessError { get; private set; }

    public bool HasAccessError => AccessError is not null;

    public PersonPicker AccessPicker { get; }

    public bool CanGrant => CanEdit && Drive is not null && _admins is not null;

    async Task LoadAdminsAsync(CancellationToken cancellationToken)
    {
        if (_records is null || Drive is not { } drive)
            return;
        AccessLoading = true;
        AccessError = null;
        try
        {
            _admins = await _records.GetOneDriveAdminsAsync(drive, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AccessError = $"Couldn't read who has access: {Friendly.Message(e)}";
        }
        finally
        {
            AccessLoading = false;
            RebuildAccess();
        }
    }

    bool IsOwner(SiteAdmin admin) =>
        string.Equals(admin.UserPrincipalName, _user.UserPrincipalName, StringComparison.OrdinalIgnoreCase)
        || admin.LoginName.EndsWith("|" + _user.UserPrincipalName, StringComparison.OrdinalIgnoreCase);

    IEnumerable<SetOneDriveAccess> PendingAccess() =>
        _pipeline?.Pending.OfType<SetOneDriveAccess>().Where(p => p.User.Id == _user.Id) ?? [];

    void RebuildAccess()
    {
        Access.Clear();
        OnPropertyChanged(nameof(CanGrant));
        if (_admins is null)
            return;
        var pending = PendingAccess().ToDictionary(p => p.Login, StringComparer.OrdinalIgnoreCase);
        // The owner first, then everyone else by name.
        foreach (var admin in _admins.OrderByDescending(IsOwner).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var owner = IsOwner(admin);
            var change = pending.GetValueOrDefault(admin.LoginName);
            var captured = admin;
            Access.Add(new OneDriveAccessRow(admin.Name, owner ? "Owner" : admin.Email ?? admin.UserPrincipalName, owner,
                change is null ? null : "Removing", CanEdit && !owner, change is null ? "Remove" : "Undo", () => ToggleRemove(captured)));
        }
        foreach (var add in pending.Values.Where(p => p.New && !_admins.Any(a => string.Equals(a.LoginName, p.Login, StringComparison.OrdinalIgnoreCase))))
        {
            var captured = add;
            Access.Add(new OneDriveAccessRow(add.Person.Name, add.Person.Address, false, "Adding", CanEdit, "Undo", () => _pipeline!.Unstage(captured)));
        }
    }

    void ToggleRemove(SiteAdmin admin)
    {
        if (!CanEdit || Drive is not { } drive)
            return;
        if (_pipeline!.PendingFor(_user.Id, "onedrive:" + admin.LoginName.ToLowerInvariant()) is { } pending)
        {
            _pipeline.Unstage(pending);
            return;
        }
        var person = admin.UserPrincipalName is { Length: > 0 } upn && _index.AllUsers.FirstOrDefault(u =>
                string.Equals(u.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase)) is { } known
            ? new ChangeTarget("user", known.Id, known.DisplayName, known.UserPrincipalName)
            : new ChangeTarget("user", admin.LoginName, admin.Name, admin.UserPrincipalName ?? admin.Email);
        _pipeline.Stage(new SetOneDriveAccess(Target, drive.Url, drive.SiteId, person, admin.LoginName, true, false));
    }

    IEnumerable<PickOption> AccessOptions()
    {
        var listed = (_admins ?? []).Select(a => a.UserPrincipalName).OfType<string>()
            .Concat(PendingAccess().Select(p => p.Person.Address).OfType<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GroupAccess.People(_index).Where(o => o.Id != _user.Id && !listed.Contains(o.Address ?? ""));
    }

    void GiveAccess(PickOption option)
    {
        if (!CanGrant || Drive is not { } drive || option.Address is not { Length: > 0 } upn)
            return;
        _pipeline!.Stage(new SetOneDriveAccess(Target, drive.Url, drive.SiteId, option.Target("user"), SetOneDriveAccess.LoginFor(upn), false, true));
    }

    // ---- Pipeline ---------------------------------------------------------------------------

    void OnPendingChanged(object? sender, EventArgs e) => RebuildAccess();

    void OnApplied(object? sender, IReadOnlyList<ChangeOp> applied)
    {
        if (applied.Any(c => c.Target.Id == _user.Id && c is SetOneDriveAccess))
            Reload();
    }

    public void Dispose()
    {
        if (_pipeline is not null)
        {
            _pipeline.Changed -= OnPendingChanged;
            _pipeline.Applied -= OnApplied;
        }
    }
}
