using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels;

/// <summary>
/// A user's record, as a page or in the preview pane. With a change pipeline and the right role,
/// its name, job title, department and sign-in can be edited; edits go to the Pending changes
/// tray and show here as pending until applied.
/// </summary>
public sealed partial class UserDetailViewModel : TabViewModel, IDisposable
{
    readonly DateTimeOffset _now;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    UserRow _row;

    public UserDetailViewModel(UserSummary user, DateTimeOffset? now = null, ChangePipeline? pipeline = null, Availability? canEdit = null)
    {
        _now = now ?? DateTimeOffset.Now;
        _pipeline = pipeline;
        _canEdit = canEdit ?? Availability.No("Editing isn't available here");
        User = user;
        _row = new UserRow(user, _now);
        Fields = BuildFields();
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public UserSummary User { get; private set; }

    public override string Title => User.DisplayName;

    public string Initials => _row.Initials;

    public string StatusText => User.AccountEnabled ? "Sign-in allowed" : "Sign-in blocked";
    public BadgeKind StatusKind => _row.StatusKind;

    public string KindText => _row.KindText;
    public BadgeKind KindKind => _row.KindKind;

    public string Email => User.Mail ?? "No mailbox address";
    public string Department => User.Department ?? "Not set";
    public string JobTitle => User.JobTitle ?? "Not set";

    public string LastSignInText => _row.LastSignInText;
    public string LastSignInExact => _row.LastSignInExact;

    public string CreatedText => User.CreatedAt.ToLocalTime().ToString("MMMM d, yyyy");

    public IReadOnlyList<string> Licenses => User.Licenses;
    public bool HasLicenses => User.Licenses.Count > 0;

    // ---- Editing ------------------------------------------------------------------------

    ChangeTarget Target => new("user", User.Id, User.DisplayName, User.UserPrincipalName);

    /// <summary>Display name, job title and department, each editable in place.</summary>
    public IReadOnlyList<EditableField> Fields { get; private set; }

    public EditableField NameField => Fields[0];

    /// <summary>The organisation fields (job title, department), for the Organization section.</summary>
    public IReadOnlyList<EditableField> OrganizationFields => Fields.Skip(1).ToArray();

    IReadOnlyList<EditableField> BuildFields() =>
    [
        new("Display name", "displayName", User.DisplayName, Target, _pipeline, _canEdit, required: true),
        new("Job title", "jobTitle", User.JobTitle, Target, _pipeline, _canEdit),
        new("Department", "department", User.Department, Target, _pipeline, _canEdit),
    ];

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    SetSignInAllowed? PendingSignIn => _pipeline?.PendingFor(User.Id, "signin") as SetSignInAllowed;

    public bool HasPendingSignIn => PendingSignIn is not null;

    public string PendingSignInText => PendingSignIn is { } p ? $"Pending: sign-in will be {(p.New ? "allowed" : "blocked")}" : "";

    /// <summary>"Block sign-in" / "Allow sign-in", or "Undo" when a change is already staged.</summary>
    public string SignInActionText => HasPendingSignIn ? "Undo sign-in change" : User.AccountEnabled ? "Block sign-in" : "Allow sign-in";

    public string SignInActionTip => !CanEdit ? (_pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "Not available")
        : HasPendingSignIn ? "Take the sign-in change out of Pending changes"
        : User.AccountEnabled ? "Stop this account signing in (staged for review in Pending changes)"
        : "Let this account sign in again (staged for review in Pending changes)";

    [RelayCommand]
    void ToggleSignIn()
    {
        if (!CanEdit)
            return;
        if (PendingSignIn is { } pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new SetSignInAllowed(Target, User.AccountEnabled, !User.AccountEnabled));
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        foreach (var f in Fields)
            f.Refresh();
        OnPropertyChanged(nameof(HasPendingSignIn));
        OnPropertyChanged(nameof(PendingSignInText));
        OnPropertyChanged(nameof(SignInActionText));
        OnPropertyChanged(nameof(SignInActionTip));
    }

    /// <summary>Shows the user as they are now (after changes are applied and read back).</summary>
    public void Update(UserSummary user)
    {
        User = user;
        _row = new UserRow(user, _now);
        Fields = BuildFields();
        // Everything on the page derives from the user.
        OnPropertyChanged(string.Empty);
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
