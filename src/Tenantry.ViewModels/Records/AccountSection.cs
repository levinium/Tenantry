using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>A titled group of fields on the Account tab ("Name", "Work", "Contact").</summary>
public sealed record FieldGroup(string Title, string Icon, IReadOnlyList<EditableField> Fields);

/// <summary>
/// The Account tab: who the person is, how to reach them, where they work, how they sign in.
/// Read live when first shown (the list's local copy holds only list fields); every value is
/// editable in place through the Pending changes tray.
/// </summary>
public sealed partial class AccountSection : RecordSection, IDisposable
{
    UserSummary _user;
    readonly IUserRecords? _records;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly IDirectoryIndex _index;
    bool _loaded;
    (string? Id, string? Name) _manager;

    public AccountSection(UserSummary user, IUserRecords? records, ChangePipeline? pipeline, Availability canEdit, IDirectoryIndex? index = null)
    {
        _user = user;
        _records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _index = index ?? EmptyIndex.Instance;
        ManagerPicker = new PersonPicker("Choose a manager: type a name", ManagerOptions, o =>
            _pipeline?.Stage(new SetManager(Target, _manager.Id, _manager.Name, o.Id, o.Name)));
        Summary = user.Department is { Length: > 0 } d ? $"{user.JobTitle ?? "No title"} · {d}" : user.JobTitle ?? "Name, contact and work details";
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public override string Key => "account";
    public override string Title => "Account";
    public override string Icon => "Person";
    public override string FreshKeys => "name,upn,mail,department,title";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? LoadError { get; private set; }

    public bool HasError => LoadError is not null;

    public bool IsReady => !IsLoading && Groups.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial IReadOnlyList<FieldGroup> Groups { get; private set; } = [];

    // ---- Manager ---------------------------------------------------------------------------

    ChangeTarget Target => new("user", _user.Id, _user.DisplayName, _user.UserPrincipalName);

    /// <summary>The manager, like the profile, comes from on-premises AD for a synced person.</summary>
    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable && !_user.SyncedFromOnPremises;

    public string EditReason => _pipeline is null ? "This tenant can't be edited"
        : _user.SyncedFromOnPremises ? UserSummary.SyncedReason
        : _canEdit.Reason ?? "";

    public PersonPicker ManagerPicker { get; }

    SetManager? PendingManager => _pipeline?.PendingFor(_user.Id, "manager") as SetManager;

    /// <summary>The manager as it will be once pending changes apply.</summary>
    public string ManagerText => PendingManager is { } p ? p.After : _manager.Name ?? "No manager set";

    public bool HasPendingManager => PendingManager is not null;

    public string PendingManagerText => PendingManager is { } p ? $"Pending: was {p.Before}" : "";

    public bool CanClearManager => CanEdit && !HasPendingManager && _manager.Id is not null;

    public string ClearManagerTip => CanEdit ? "Remove their manager (added to Pending changes)" : EditReason;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void ClearManager()
    {
        if (CanClearManager)
            _pipeline!.Stage(new SetManager(Target, _manager.Id, _manager.Name, null, null));
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void UndoManager()
    {
        if (PendingManager is { } p)
            _pipeline!.Unstage(p);
    }

    /// <summary>Anyone who signs in, except the person themselves and their current manager.</summary>
    IEnumerable<PickOption> ManagerOptions() =>
        GroupAccess.People(_index).Where(o => o.Id != _user.Id && o.Id != _manager.Id && o.Id != PendingManager?.NewId);

    void RefreshManager()
    {
        OnPropertyChanged(nameof(ManagerText));
        OnPropertyChanged(nameof(HasPendingManager));
        OnPropertyChanged(nameof(PendingManagerText));
        OnPropertyChanged(nameof(CanClearManager));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(EditReason));
        OnPropertyChanged(nameof(ClearManagerTip));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuest))]
    public partial string? GuestStatus { get; private set; }

    public bool IsGuest => GuestStatus is not null;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded || IsLoading)
            return;
        if (_records is null)
        {
            Build(new UserDetails(_user.Id, new Dictionary<string, string?>
            {
                ["displayName"] = _user.DisplayName,
                ["userPrincipalName"] = _user.UserPrincipalName,
                ["jobTitle"] = _user.JobTitle,
                ["department"] = _user.Department,
            }, null, null, null, null), []);
            _loaded = true;
            return;
        }
        IsLoading = true;
        LoadError = null;
        try
        {
            var details = await _records.GetDetailsAsync(_user.Id, cancellationToken);
            var domains = await _records.GetDomainsAsync(cancellationToken);
            Build(details, domains);
            _loaded = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read the account details: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    void Build(UserDetails d, IReadOnlyList<string> domains)
    {
        var target = new ChangeTarget("user", _user.Id, _user.DisplayName, _user.UserPrincipalName);
        // A synced person's profile and sign-in name come from on-premises AD (Graph refuses changes);
        // only cloud settings such as usage location are changed here.
        var fromAd = _user.SyncedFromOnPremises ? Availability.No(UserSummary.SyncedReason) : _canEdit;
        EditableField Field(string label, string property, bool required = false, IReadOnlyList<FieldChoice>? choices = null,
            Func<string, string?>? validate = null, string? hint = null, bool cloud = false) =>
            new(label, property, d[property], target, _pipeline, cloud ? _canEdit : fromAd, required, choices: choices, validate: validate, hint: hint);

        var countries = Countries.All.Select(c => new FieldChoice(c.Code, $"{c.Name} ({c.Code})")).ToArray();
        Groups =
        [
            new("Name", "Person",
            [
                Field("First Name", "givenName"),
                Field("Last Name", "surname"),
                Field("Display Name", "displayName", required: true, hint: "How the name appears in Outlook, Teams and the address list."),
            ]),
            new("Work", "Briefcase",
            [
                Field("Job Title", "jobTitle"),
                Field("Department", "department"),
                Field("Company", "companyName"),
                Field("Employee ID", "employeeId"),
                Field("Office", "officeLocation"),
            ]),
            new("Contact", "Call",
            [
                Field("Work Phone", "businessPhones"),
                Field("Mobile Phone", "mobilePhone"),
            ]),
            new("Address", "Location",
            [
                Field("Street", "streetAddress"),
                Field("City", "city"),
                Field("State or Province", "state"),
                Field("Postal Code", "postalCode"),
                Field("Country or Region", "country"),
            ]),
            new("Sign-In and Licensing", "Key",
            [
                Field("Sign-In Name", "userPrincipalName", required: true, validate: upn => ValidateSignInName(upn, domains) ?? InUse(upn),
                    hint: "Changing it changes how they sign in everywhere; their old name stops working."),
                Field("Usage Location", "usageLocation", choices: countries,
                    hint: "The country they use Microsoft 365 in. Required before licenses can be assigned.", cloud: true),
            ]),
        ];
        _manager = (d.ManagerId, d.ManagerName);
        RefreshManager();
        GuestStatus = d.GuestInvitationState switch
        {
            "PendingAcceptance" => $"Invitation not accepted yet{(d.GuestInvitationChanged is { } at ? $" (sent {Display.Relative(at, DateTimeOffset.Now)})" : "")}",
            "Accepted" => "Invitation accepted",
            null => null,
            var other => other,
        };
    }

    public override void RecordChanged(UserSummary user)
    {
        _user = user;
        RefreshManager();
        if (!_loaded)
            return;
        _loaded = false;
        _ = LoadAsync();
    }

    /// <summary>A sign-in name must be name@domain, on one of the tenant's own domains.</summary>
    public static string? ValidateSignInName(string upn, IReadOnlyList<string> domains)
    {
        var at = upn.IndexOf('@');
        if (at <= 0 || at != upn.LastIndexOf('@') || at == upn.Length - 1 || upn.Contains(' '))
            return "A sign-in name looks like name@domain, with no spaces.";
        var domain = upn[(at + 1)..];
        if (domains.Count > 0 && !domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            return $"\"{domain}\" isn't one of this tenant's domains ({string.Join(", ", domains)}).";
        return null;
    }

    /// <summary>Someone else's sign-in name or address (theirs, a mailbox's or a group's), or null when it's free.</summary>
    string? InUse(string upn)
    {
        bool Same(string? address) => string.Equals(address, upn, StringComparison.OrdinalIgnoreCase);
        return _index.AllUsers.Any(u => u.Id != _user.Id && (Same(u.UserPrincipalName) || Same(u.Mail)))
               || _index.AllGroups.Any(g => g.Id != _user.Id && Same(g.Mail))
            ? $"{upn} is already in use: someone else's sign-in name, or the address of a mailbox or group."
            : null;
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        foreach (var field in Groups.SelectMany(g => g.Fields))
            field.Refresh();
        RefreshManager();
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
