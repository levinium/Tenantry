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
    readonly IDirectoryIndex _index;
    readonly Action<UserSummary> _openUser;
    readonly Action<GroupSummary> _openGroup;
    UserRow _row;

    /// <param name="openDialog">Shows a popup (the preview's Manage rows, Reset password); none in tests.</param>
    /// <param name="index">The local copy, for Groups &amp; access and the people pickers.</param>
    public UserDetailViewModel(UserSummary user, DateTimeOffset? now = null, ChangePipeline? pipeline = null, Availability? canEdit = null,
        IUserRecords? records = null, Action<DialogViewModel>? openDialog = null, Action? closeDialog = null,
        IDirectoryIndex? index = null, Action<UserSummary>? openUser = null, Action<GroupSummary>? openGroup = null)
    {
        _now = now ?? DateTimeOffset.Now;
        _pipeline = pipeline;
        _canEdit = canEdit ?? Availability.No("Editing isn't available here");
        _records = records;
        _openDialog = openDialog;
        _closeDialog = closeDialog;
        _index = index ?? EmptyIndex.Instance;
        // Opening a record from a popup closes the popup first, so the page isn't hidden behind it.
        _openUser = u => { _closeDialog?.Invoke(); (openUser ?? (_ => { }))(u); };
        _openGroup = g => { _closeDialog?.Invoke(); (openGroup ?? (_ => { }))(g); };
        User = user;
        _row = new UserRow(user, _now);
        Sections = BuildSections();
        SelectedSection = Sections[0];
        MailTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MailSection.IsReady))
            {
                OnPropertyChanged(nameof(Health));
                OnPropertyChanged(nameof(HasHealth));
            }
        };
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
            // From Entra's MFA registration report (synced; absent without Entra ID P1).
            if (isPerson && User.AccountEnabled && User.Security is { MfaRegistered: false } security)
                chips.Add(security.IsAdmin
                    ? new("Admin without MFA", BadgeKind.Danger, "Holds an admin role and has no multi-factor sign-in registered.")
                    : new("No MFA registered", BadgeKind.Warning, "Signs in with a password alone. A Temporary Access Pass (Security & sign-in) lets them set MFA up."));            if (User.Kind == UserKind.Member && isPerson && User.Licenses.Count == 0)
                chips.Add(new("Unlicensed", BadgeKind.Warning, "A member account with no licenses: no mailbox, Office apps or OneDrive."));
            if (!isPerson && User.Licenses.Count > 0)
                chips.Add(new("Licensed mailbox account", BadgeKind.Info, "Shared, room and equipment mailboxes usually need no license (up to 50 GB)."));
            if (User.Mailbox?.LitigationHold == true)
                chips.Add(new("Litigation hold", BadgeKind.Info, "Mailbox content is preserved, even if deleted."));
            // From the mailbox itself, once the Mail tab has read it.
            if (MailTab.IsReady && MailTab.IsNearlyFull)
                chips.Add(new("Mailbox nearly full", BadgeKind.Warning, $"{MailTab.UsedText}. Mail stops arriving when it's full."));
            if (MailTab.IsReady && MailTab.IsAutoReplying)
                chips.Add(new("Automatic reply on", BadgeKind.Info, MailTab.AutoReplyText));
            return chips;
        }
    }

    public bool HasHealth => Health.Count > 0;

    // ---- Tabs -----------------------------------------------------------------------------

    /// <summary>Overview first, then one tab per area. The preview shows all but Overview as Manage rows.</summary>
    public IReadOnlyList<RecordSection> Sections { get; private set; }

    public AccountSection Account => Sections.OfType<AccountSection>().Single();

    public LicensesSection LicensesTab => Sections.OfType<LicensesSection>().Single();

    public GroupsAccessSection GroupsTab => Sections.OfType<GroupsAccessSection>().Single();

    public MailSection MailTab => Sections.OfType<MailSection>().Single();

    /// <summary>A record page reads the mailbox straight away, so its health chips (nearly full, auto-reply) show on Overview.</summary>
    public void LoadMailInBackground() => _ = MailTab.LoadAsync();

    public IReadOnlyList<RecordSection> ManageRows => Sections.Skip(1).ToArray();

    [ObservableProperty]
    public partial RecordSection? SelectedSection { get; set; }

    partial void OnSelectedSectionChanged(RecordSection? value) => _ = value?.LoadAsync();

    IReadOnlyList<RecordSection> BuildSections() =>
    [
        new OverviewSection(this),
        new AccountSection(User, _records, _pipeline, _canEdit, _index),
        new LicensesSection(User, _records, _pipeline, _canEdit),
        new GroupsAccessSection(User, _index, _pipeline, _canEdit, _records, _openGroup, _openUser),
        new MailSection(User, _records, _pipeline, _canEdit, _index, OpenConvertMailbox),
        new PlannedSection("onedrive", "OneDrive", "Cloud", "update 4d",
            ["Storage used against the quota", "Open their OneDrive", "Give someone (a manager) access"]),
        new SecuritySection(User, _records as ISecurityRecords, _pipeline, _canEdit),
        new DevicesSection(User, _records as IDeviceRecords, _pipeline, _canEdit),
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
        : User.AccountEnabled ? "Stop this account signing in (added to Pending changes)"
        : "Let this account sign in again (added to Pending changes)";

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

    public bool HasMailbox => User.Mailbox is not null;

    public string ConvertMailboxText => User.Mailbox?.Type == "Shared" ? "Convert to a user mailbox…" : "Convert to a shared mailbox…";

    /// <summary>Opens the Convert mailbox popup (the Actions menu and the Mail tab).</summary>
    [RelayCommand]
    void OpenConvertMailbox()
    {
        if (CanEdit && User.Mailbox is not null)
            _openDialog?.Invoke(new DialogViewModel("Convert mailbox", User.DisplayName,
                new ConvertMailboxViewModel(User, _records, _pipeline!, () => _closeDialog?.Invoke())));
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

    /// <summary>Whether this page matches Microsoft 365: checked when the page opens (see MainViewModel).</summary>
    public RecordFreshness Freshness { get; } = new();

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
