using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>
/// A user's record, as a page (tabs across the top) or in the preview pane (a summary with one
/// Manage row per tab, opening that tab in a popup). Edits from any tab go to the Pending
/// changes tray and show here as pending until applied.
/// </summary>
public sealed partial class UserDetailViewModel : TabViewModel, IDisposable
{
    readonly DateTimeOffset _now;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly IUserRecords? _records;
    readonly Action<DialogViewModel>? _openDialog;
    readonly Action? _closeDialog;
    UserRow _row;

    /// <param name="openDialog">Shows a popup (the preview's Manage rows, Reset password); none in tests.</param>
    public UserDetailViewModel(UserSummary user, DateTimeOffset? now = null, ChangePipeline? pipeline = null, Availability? canEdit = null,
        IUserRecords? records = null, Action<DialogViewModel>? openDialog = null, Action? closeDialog = null)
    {
        _now = now ?? DateTimeOffset.Now;
        _pipeline = pipeline;
        _canEdit = canEdit ?? Availability.No("Editing isn't available here");
        _records = records;
        _openDialog = openDialog;
        _closeDialog = closeDialog;
        User = user;
        _row = new UserRow(user, _now);
        Sections = BuildSections();
        SelectedSection = Sections[0];
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public UserSummary User { get; private set; }

    public override string Title => User.DisplayName;

    public string Initials => _row.Initials;

    public string SecondaryText => _row.SecondaryText;

    public string StatusText => User.AccountEnabled ? "Sign-in allowed" : "Sign-in blocked";
    public BadgeKind StatusKind => _row.StatusKind;

    public string KindText => _row.KindText;
    public BadgeKind KindKind => _row.KindKind;

    public string Email => User.Mail ?? "No mailbox address";
    public string LastSignInText => _row.LastSignInText;
    public string LastSignInExact => _row.LastSignInExact;
    public string CreatedText => User.CreatedAt.ToLocalTime().ToString("MMMM d, yyyy");

    public IReadOnlyList<string> Licenses => User.Licenses;
    public bool HasLicenses => User.Licenses.Count > 0;

    public string MailboxText => User.Mailbox is { } m ? $"{m.Type} mailbox" : "No mailbox";

    public string ForwardingText => User.Mailbox?.Forwarding switch
    {
        ForwardingKind.External => $"Forwarding outside the organization, to {User.Mailbox.ForwardingTo}",
        ForwardingKind.Internal => $"Forwarding to {User.Mailbox.ForwardingTo}",
        _ => "Not forwarding",
    };

    // ---- At a glance ----------------------------------------------------------------------

    /// <summary>What deserves attention, worst first; empty when all looks normal.</summary>
    public IReadOnlyList<HealthChip> Health
    {
        get
        {
            var chips = new List<HealthChip>();
            var isPerson = UserFields.TypeOf(User) is UserFields.Member or UserFields.Guest;
            if (!User.AccountEnabled && isPerson)
                chips.Add(new("Sign-in blocked", BadgeKind.Danger, "This account can't sign in."));
            if (User.Mailbox?.Forwarding == ForwardingKind.External)
                chips.Add(new("Forwarding outside", BadgeKind.Danger, $"Mail is forwarded to {User.Mailbox.ForwardingTo}, outside the organization."));
            if (isPerson && User.AccountEnabled)
            {
                if (User.LastSignIn is null)
                    chips.Add(new("Never signed in", BadgeKind.Warning, "No sign-in recorded for this account."));
                else if (_now - User.LastSignIn > TimeSpan.FromDays(90))
                    chips.Add(new("Inactive 90+ days", BadgeKind.Warning, $"Last signed in {LastSignInText}."));
            }
            if (User.Kind == UserKind.Member && isPerson && User.Licenses.Count == 0)
                chips.Add(new("Unlicensed", BadgeKind.Warning, "A member account with no licenses: no mailbox, Office apps or OneDrive."));
            if (!isPerson && User.Licenses.Count > 0)
                chips.Add(new("Licensed mailbox account", BadgeKind.Info, "Shared, room and equipment mailboxes usually need no license (up to 50 GB)."));
            if (User.Mailbox?.LitigationHold == true)
                chips.Add(new("Litigation hold", BadgeKind.Info, "Mailbox content is preserved, even if deleted."));
            return chips;
        }
    }

    public bool HasHealth => Health.Count > 0;

    // ---- Tabs -----------------------------------------------------------------------------

    /// <summary>Overview first, then one tab per area. The preview shows all but Overview as Manage rows.</summary>
    public IReadOnlyList<RecordSection> Sections { get; private set; }

    public AccountSection Account => Sections.OfType<AccountSection>().Single();

    public IReadOnlyList<RecordSection> ManageRows => Sections.Skip(1).ToArray();

    [ObservableProperty]
    public partial RecordSection? SelectedSection { get; set; }

    partial void OnSelectedSectionChanged(RecordSection? value) => _ = value?.LoadAsync();

    IReadOnlyList<RecordSection> BuildSections() =>
    [
        new OverviewSection(this),
        new AccountSection(User, _records, _pipeline, _canEdit),
        new PlannedSection("licenses", "Licenses & apps", "Apps", "the next update (4a)",
            ["Add and remove licenses, with how many are left", "Turn individual apps on or off inside a license",
             "Licenses inherited from groups, and where they come from", "License assignment errors", "Enterprise apps assigned to them"]),
        new PlannedSection("groups", "Groups & access", "People", "the next update (4a)",
            ["Groups and teams they belong to or own: add and remove", "Shared mailboxes they can use, and with what rights",
             "Who can use their mailbox (Full access, Send as, Send on behalf)"]),
        new PlannedSection("mail", "Mail", "Mail", "update 4b",
            ["Email addresses: add aliases, choose the primary", "Forwarding on or off", "Automatic replies: off, on, or between dates",
             "Address list visibility", "Limits: recipients, message size, who can send to them", "Email apps: POP, IMAP, ActiveSync, SMTP and more",
             "Storage used against the quota", "Archive and auto-expanding archive", "Litigation hold and retention", "Recover deleted items",
             "Inbox rules, calendar and folder permissions, mobile devices", "Convert to a shared mailbox"]),
        new PlannedSection("onedrive", "OneDrive", "Cloud", "update 4d",
            ["Storage used against the quota", "Open their OneDrive", "Give someone (a manager) access"]),
        new PlannedSection("security", "Security & sign-in", "ShieldLock", "update 4c",
            ["Authentication methods and MFA", "Sign-in logs", "Risk status: confirm compromised or dismiss",
             "Roles: active and eligible (PIM)", "Apps they've granted access to their data", "Conditional Access policies that apply"]),
        new PlannedSection("devices", "Devices", "Laptop", "update 4c",
            ["Devices they've registered or that are managed", "BitLocker recovery keys and local admin passwords"]),
        new PlannedSection("activity", "Activity", "History", "update 4d",
            ["Every change made to them in Tenantry", "Microsoft 365 audit log for this person", "Last activity in mail, OneDrive and Teams"]),
    ];

    /// <summary>Opens a tab's editor in a popup (the preview pane's Manage buttons).</summary>
    [RelayCommand]
    void Manage(RecordSection section)
    {
        _ = section.LoadAsync();
        _openDialog?.Invoke(new DialogViewModel(section.Title, User.DisplayName, section));
    }

    // ---- Quick actions --------------------------------------------------------------------

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    ChangeTarget Target => new("user", User.Id, User.DisplayName, User.UserPrincipalName);

    SetSignInAllowed? PendingSignIn => _pipeline?.PendingFor(User.Id, "signin") as SetSignInAllowed;

    public bool HasPendingSignIn => PendingSignIn is not null;

    public string PendingSignInText => PendingSignIn is { } p ? $"Pending: sign-in will be {(p.New ? "allowed" : "blocked")}" : "";

    /// <summary>"Block sign-in" / "Allow sign-in", or "Undo" when a change is already staged.</summary>
    public string SignInActionText => HasPendingSignIn ? "Undo sign-in change" : User.AccountEnabled ? "Block sign-in" : "Allow sign-in";

    public string SignInActionTip => !CanEdit ? EditReason
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

    public string ResetPasswordTip => CanEdit ? "Give them a new temporary password (shown to you once)" : EditReason;

    [RelayCommand]
    void ResetPassword()
    {
        if (CanEdit)
            _openDialog?.Invoke(new DialogViewModel("Reset password", User.DisplayName,
                new ResetPasswordViewModel(Target, _pipeline!, () => _closeDialog?.Invoke())));
    }

    public bool HasPendingSignOut => _pipeline?.PendingFor(User.Id, "sessions") is not null;

    public string SignOutText => HasPendingSignOut ? "Undo sign out everywhere" : "Sign out everywhere";

    [RelayCommand]
    void SignOutEverywhere()
    {
        if (!CanEdit)
            return;
        if (_pipeline!.PendingFor(User.Id, "sessions") is { } pending)
            _pipeline.Unstage(pending);
        else
            _pipeline.Stage(new RevokeSessions(Target));
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingSignIn));
        OnPropertyChanged(nameof(PendingSignInText));
        OnPropertyChanged(nameof(SignInActionText));
        OnPropertyChanged(nameof(SignInActionTip));
        OnPropertyChanged(nameof(HasPendingSignOut));
        OnPropertyChanged(nameof(SignOutText));
    }

    /// <summary>Shows the user as they are now (after changes are applied and read back).</summary>
    public void Update(UserSummary user)
    {
        User = user;
        _row = new UserRow(user, _now);
        // The tabs stay (rebuilding them would jump back to Overview); each re-reads if it needs to.
        foreach (var section in Sections)
            section.RecordChanged(user);
        foreach (var name in new[]
                 {
                     nameof(User), nameof(Title), nameof(Initials), nameof(SecondaryText), nameof(StatusText), nameof(StatusKind),
                     nameof(KindText), nameof(KindKind), nameof(Email), nameof(LastSignInText), nameof(LastSignInExact), nameof(CreatedText),
                     nameof(Licenses), nameof(HasLicenses), nameof(MailboxText), nameof(ForwardingText), nameof(Health), nameof(HasHealth),
                     nameof(SignInActionText), nameof(SignInActionTip), nameof(PendingSignInText), nameof(HasPendingSignIn),
                 })
            OnPropertyChanged(name);
    }

    void DisposeSections()
    {
        foreach (var section in Sections.OfType<IDisposable>())
            section.Dispose();
    }

    public void Dispose()
    {
        DisposeSections();
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
