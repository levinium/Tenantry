using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;

namespace Tenantry.ViewModels.Records;

public enum SecurityPage
{
    Methods,
    SignIns,
    Access,
    ConditionalAccess,
}

public sealed record SecurityPageItem(SecurityPage Page, string Title, string Icon);

/// <summary>
/// One sign-in method: Change (a phone's number, the email) and Remove; while a change to it waits
/// in Pending Changes, only Undo. A method being added shows as a row too, with Undo.
/// </summary>
public sealed partial class MethodRow(string kind, string? detail, string addedText, string? pendingText, Action? change, Action? remove, Action? undo,
    string changeText = "Change", string? key = null, bool isDefault = false) : ObservableObject
{
    /// <summary>Their default way to confirm a sign-in (removing it moves the default to another method).</summary>
    public bool IsDefault => isDefault;

    /// <summary>The method's id: a rebuilt list keeps a form open on the same method.</summary>
    public string? Key => key;

    public string Kind => kind;

    /// <summary>"Change" (a number or address) or "Activate" (a hardware token not yet in use).</summary>
    public string ChangeText => changeText;
    public string? Detail => detail;
    public bool HasDetail => !string.IsNullOrEmpty(detail);
    public string AddedText => addedText;

    /// <summary>"Adding", "Removing" or "Changing to …" while it waits in Pending Changes.</summary>
    public string? PendingText => pendingText;
    public bool HasPending => pendingText is not null;

    public bool CanChange => change is not null && !HasPending && !HasForm;

    /// <summary>Changing this method (its number, its activation code): the form opens under this row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForm), nameof(CanChange))]
    public partial object? Form { get; set; }

    public bool HasForm => Form is not null;
    public bool CanRemove => remove is not null && !HasPending;
    public bool CanUndo => undo is not null && HasPending;

    [RelayCommand]
    void Change() => change?.Invoke();

    [RelayCommand]
    void Remove() => remove?.Invoke();

    [RelayCommand]
    void Undo() => undo?.Invoke();
}

/// <summary>
/// The small form under the sign-in methods for adding or changing a phone or the password-reset
/// email: one box, plus which phone when it's a second one. Save puts it in Pending Changes.
/// </summary>
public sealed partial class ContactMethodForm : ObservableObject
{
    readonly Func<ContactMethodForm, bool> _save;
    readonly Action _close;

    public ContactMethodForm(string title, string saveText, bool isPhone, string value, IReadOnlyList<(string Type, string Name)> phoneTypes,
        Func<ContactMethodForm, bool> save, Action close)
    {
        Title = title;
        SaveText = saveText;
        IsPhone = isPhone;
        _save = save;
        _close = close;
        PhoneTypes = phoneTypes.Select(t => new PhoneTypeChoice(t.Type, t.Name, this)).ToArray();
        PhoneType = phoneTypes.FirstOrDefault().Type;
        Value = value;
    }

    public string Title { get; }
    public string SaveText { get; }
    public bool IsPhone { get; }
    public string Placeholder => IsPhone ? "+1 2065551234" : "name@example.com";

    /// <summary>Which phone, only when adding one beside another (mobile otherwise).</summary>
    public IReadOnlyList<PhoneTypeChoice> PhoneTypes { get; }
    public bool HasPhoneTypes => PhoneTypes.Count > 1;

    [ObservableProperty]
    public partial string? PhoneType { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(HasHint))]
    public partial string Value { get; set; } = "";

    /// <summary>How to finish what's typed, while it isn't whole yet (a hint, not an error).</summary>
    public string? Hint => Value.Trim().Length == 0 || IsValid ? null
        : IsPhone ? "Type the country code, a space, then the number: +1 2065551234." : "Type a full email address, like name@example.com.";
    public bool HasHint => Hint is not null;

    bool IsValid => IsPhone ? SecuritySection.NormalizePhone(Value) is not null : SecuritySection.IsEmail(Value.Trim());

    [RelayCommand(CanExecute = nameof(IsValid))]
    void Save()
    {
        if (IsValid && _save(this))
            _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}

/// <summary>
/// Adding a hardware token: what's printed on it or in the vendor's file (serial, maker, model, the
/// secret key) and how its code works. Save puts it in Pending Changes; after applying, it's activated.
/// </summary>
public sealed partial class HardwareTokenForm(Func<HardwareTokenForm, bool> save, Action close) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(HasHint))]
    public partial string Serial { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(HasHint))]
    public partial string Manufacturer { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(HasHint))]
    public partial string Model { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(HasHint))]
    public partial string SecretKey { get; set; } = "";

    /// <summary>The code changes every 30 seconds (most tokens) or every 60.</summary>
    [ObservableProperty]
    public partial bool SixtySeconds { get; set; }

    /// <summary>SHA-256 tokens are rarer; the vendor's file says which.</summary>
    [ObservableProperty]
    public partial bool Sha256 { get; set; }

    /// <summary>The secret key as Microsoft takes it: Base32 (letters and the digits 2-7), no spaces.</summary>
    public string Key => SecretKey.Replace(" ", "", StringComparison.Ordinal).Trim();

    static bool IsBase32(string key) => key.Length is >= 16 and <= 128 && key.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '2' and <= '7');

    bool IsValid => Serial.Trim().Length > 0 && Manufacturer.Trim().Length > 0 && Model.Trim().Length > 0 && IsBase32(Key);

    public string? Hint => SecretKey.Trim().Length > 0 && !IsBase32(Key)
        ? "The secret key is Base32: letters and the digits 2 to 7, 16 to 128 of them (as in the vendor's file)."
        : IsValid || (Serial + Manufacturer + Model + SecretKey).Trim().Length == 0 ? null : "Fill in all four, as printed on the token or in the vendor's file.";
    public bool HasHint => Hint is not null;

    [RelayCommand(CanExecute = nameof(IsValid))]
    void Save()
    {
        if (IsValid && save(this))
            close();
    }

    [RelayCommand]
    void Cancel() => close();
}

/// <summary>
/// Activating a hardware token with the code it shows right now. The code lasts a minute, so this
/// is sent at once (guarded and logged) rather than waiting in Pending Changes.
/// </summary>
public sealed partial class TokenActivationForm(string serial, Func<string, Task<string?>> activate, Action close) : ObservableObject
{
    public string Title => $"Activate Hardware Token {serial}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActivateCommand))]
    public partial string Code { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActivateCommand))]
    public partial bool IsWorking { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    bool CanActivate => !IsWorking && Code.Trim().Length == 6 && Code.Trim().All(char.IsDigit);

    [RelayCommand(CanExecute = nameof(CanActivate))]
    async Task Activate()
    {
        IsWorking = true;
        Problem = null;
        try
        {
            Problem = await activate(Code.Trim());
        }
        finally
        {
            IsWorking = false;
        }
        if (Problem is null)
            close();
    }

    [RelayCommand]
    void Cancel() => close();
}

/// <summary>One of the phones a second number can be (a radio button).</summary>
public sealed partial class PhoneTypeChoice(string type, string name, ContactMethodForm form) : ObservableObject
{
    public string Name => name;

    public bool IsChosen
    {
        get => form.PhoneType == type;
        set
        {
            if (value)
                form.PhoneType = type;
        }
    }
}

/// <summary>One sign-in from the log.</summary>
public sealed class SignInRow(SignInEvent e)
{
    public string When => e.When.ToLocalTime().ToString("d MMM h:mm tt");
    public string App => e.App;
    public string Where => string.Join(" · ", new[] { e.Location, e.IpAddress, e.Device }.Where(s => !string.IsNullOrEmpty(s)));
    public string? Device => e.Device;
    public bool Succeeded => e.Succeeded;
    public string Status => e.Succeeded ? "Signed In" : "Failed";
    public BadgeKind StatusKind => e.Succeeded ? BadgeKind.Success : BadgeKind.Danger;
    public string Detail => string.Join(" · ", new[] { e.Problem, e.ConditionalAccess, e.Detail }.Where(s => !string.IsNullOrEmpty(s)).Distinct());
    public string Client => e.ClientKind + (e.MultiFactor switch { true => " · multi-factor", false => " · password only", _ => "" });
    public bool IsLegacy => e.ClientKind.StartsWith("Legacy", StringComparison.Ordinal);
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>A role, an app consent or anything else that can be taken away (pressing again undoes).</summary>
public sealed partial class RevocableRow(string name, string detail, bool revoking, bool canEdit, string actionTip, Action toggle) : ObservableObject
{
    public string Name => name;
    public string Detail => detail;
    public bool IsRevoking => revoking;
    public bool CanEdit => canEdit;
    public string Tip => revoking ? "Keep it (take the change out of Pending Changes)" : actionTip;

    [RelayCommand]
    void Toggle() => toggle();
}

/// <summary>A Conditional Access policy as it bears on this person.</summary>
public sealed class PolicyRow(PolicyMatch policy)
{
    public string Name => policy.Name;
    public string State => policy.State;
    public BadgeKind StateKind => policy.State switch { "On" => BadgeKind.Success, "Report-only" => BadgeKind.Info, _ => BadgeKind.Neutral };
    public string Fit => policy.Fit switch { PolicyFit.Applies => "Applies", PolicyFit.Excluded => "Excluded", _ => "Doesn't Apply" };
    public BadgeKind FitKind => policy.Fit == PolicyFit.Applies ? BadgeKind.Info : BadgeKind.Neutral;
    public string Why => policy.Why;
    public string Controls => $"{policy.Controls} · {policy.Apps}";
    public bool Targets => policy.Fit != PolicyFit.NotTargeted;
}

/// <summary>
/// The Security &amp; sign-in tab (4c-1): how they sign in (methods, MFA, Temporary Access Pass,
/// risk), the sign-in log, the roles and app access they hold, and the Conditional Access
/// policies that cover them. Pages read their data the first time they're shown, and again after
/// changes to this person apply. Features the tenant isn't licensed for say so instead.
/// </summary>
public sealed partial class SecuritySection : RecordSection, IDisposable
{
    UserSummary _user;
    readonly ISecurityRecords? _records;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    bool _shown;

    readonly Action<DialogViewModel>? _openDialog;
    readonly Action? _closeDialog;

    public SecuritySection(UserSummary user, ISecurityRecords? records, ChangePipeline? pipeline, Availability canEdit,
        Action<DialogViewModel>? openDialog = null, Action? closeDialog = null)
    {
        _user = user;
        _records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _openDialog = openDialog;
        _closeDialog = closeDialog;
        Summary = SummaryFor(user);
        if (_pipeline is not null)
        {
            _pipeline.Changed += OnPendingChanged;
            _pipeline.Applied += OnApplied;
        }
    }

    public override string Key => "security";
    public override string Title => "Security & Sign-In";
    public override string Icon => "ShieldLock";
    public override string FreshKeys => "mfa,admin,status";

    static string SummaryFor(UserSummary user) => user.Security switch
    {
        null => "Sign-in methods, sign-ins, roles and policies",
        { MfaRegistered: false, IsAdmin: true } => "Admin without MFA",
        { MfaRegistered: false } => "No MFA registered",
        { IsAdmin: true } s => $"MFA registered · admin · {s.Methods.Count} method{(s.Methods.Count == 1 ? "" : "s")}",
        var s => $"MFA registered · {s.Methods.Count} method{(s.Methods.Count == 1 ? "" : "s")}",
    };

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    ChangeTarget Target => new("user", _user.Id, _user.DisplayName, _user.UserPrincipalName);

    ChangeOp? PendingFor(string key) => _pipeline?.PendingFor(_user.Id, key);

    // ---- Pages ------------------------------------------------------------------------------

    public static IReadOnlyList<SecurityPageItem> Pages { get; } =
    [
        new(SecurityPage.Methods, "Sign-In Methods", "Key"),
        new(SecurityPage.SignIns, "Sign-In Log", "History"),
        new(SecurityPage.Access, "Roles & App Access", "PersonKey"),
        new(SecurityPage.ConditionalAccess, "Conditional Access", "ShieldTask"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMethods), nameof(ShowSignIns), nameof(ShowAccess), nameof(ShowConditionalAccess), nameof(SelectedPageItem))]
    public partial SecurityPage SelectedPage { get; set; }

    public SecurityPageItem SelectedPageItem
    {
        get => Pages.First(p => p.Page == SelectedPage);
        set => SelectedPage = value.Page;
    }

    public bool ShowMethods => SelectedPage == SecurityPage.Methods;
    public bool ShowSignIns => SelectedPage == SecurityPage.SignIns;
    public bool ShowAccess => SelectedPage == SecurityPage.Access;
    public bool ShowConditionalAccess => SelectedPage == SecurityPage.ConditionalAccess;

    partial void OnSelectedPageChanged(SecurityPage value)
    {
        if (_shown)
            _ = LoadPageAsync(value);
    }

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _shown = true;
        return LoadPageAsync(SelectedPage);
    }

    Task LoadPageAsync(SecurityPage page) => page switch
    {
        SecurityPage.Methods => Task.WhenAll(LoadMethodsAsync(), LoadRiskAsync()),
        SecurityPage.SignIns => LoadSignInsAsync(),
        SecurityPage.Access => Task.WhenAll(LoadRolesAsync(), LoadConsentsAsync()),
        _ => LoadPoliciesAsync(),
    };

    public override void RecordChanged(UserSummary user)
    {
        _user = user;
        Summary = SummaryFor(user);
        OnPropertyChanged(nameof(MfaText));
        OnPropertyChanged(nameof(MfaKind));
        OnPropertyChanged(nameof(HasMfaWarning));
    }

    void OnApplied(object? sender, IReadOnlyList<ChangeOp> applied)
    {
        if (!applied.Any(c => c.Target.Id == _user.Id))
            return;
        _methods = null;
        _risk = null;
        _roles = null;
        _consents = null;
        if (_shown)
            _ = LoadPageAsync(SelectedPage);
        // Microsoft's list of sign-in methods lags a new or removed one by up to half a minute
        // (seen live): read it once more when that's had time to catch up.
        if (_shown && applied.Any(c => c.Target.Id == _user.Id && c is AddPhoneMethod or UpdatePhoneMethod or SetEmailMethod or CreateTemporaryAccessPass or RemoveAuthMethod))
            _ = ReadMethodsAgainAsync();
    }

    /// <summary>How long after a sign-in-method change the list is read again.</summary>
    public static TimeSpan MethodsSettle { get; set; } = TimeSpan.FromSeconds(20);

    async Task ReadMethodsAgainAsync()
    {
        await Task.Delay(MethodsSettle);
        _methods = null;
        await LoadMethodsAsync();
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        RebuildMethods();
        RebuildAccess();
        foreach (var name in new[] { nameof(TapPendingText), nameof(HasTapPending), nameof(RiskPendingText), nameof(HasRiskPending) })
            OnPropertyChanged(name);
    }

    /// <summary>Reads once (unless <paramref name="again"/>), reporting progress and errors on its card.</summary>
    async Task<T?> ReadAsync<T>(T? current, Func<ISecurityRecords, Task<T>> read, Action<bool> loading, Action<string?> error, bool again = false)
    {
        if ((current is not null && !again) || _records is not { } records)
            return current;
        loading(true);
        error(null);
        try
        {
            return await read(records);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error(Friendly.Message(e));
            return current;
        }
        finally
        {
            loading(false);
        }
    }

    // ---- MFA registration (from the sync) ---------------------------------------------------

    public string MfaText => _user.Security switch
    {
        null => "MFA registration isn't read for this tenant (Entra's registration report needs Microsoft Entra ID P1).",
        { MfaRegistered: false, IsAdmin: true } => "No MFA registered, and they hold an admin role. An admin account protected by a password alone is the riskiest kind.",
        { MfaRegistered: false } => "No MFA registered: they sign in with a password alone. A Temporary Access Pass lets them set it up.",
        var s => $"MFA registered: {string.Join(", ", s.Methods.Select(UserSecurity.MethodName).Distinct())}"
                 + (s.DefaultMethod is { } d ? $". Default: {UserSecurity.MethodName(d)}." : "."),
    };

    public BadgeKind MfaKind => _user.Security switch
    {
        { MfaRegistered: false, IsAdmin: true } => BadgeKind.Danger,
        { MfaRegistered: false } => BadgeKind.Warning,
        _ => BadgeKind.Neutral,
    };

    public bool HasMfaWarning => _user.Security is { MfaRegistered: false };

    // ---- Sign-in methods --------------------------------------------------------------------

    IReadOnlyList<AuthMethod>? _methods;

    public ObservableCollection<MethodRow> Methods { get; } = [];

    [ObservableProperty]
    public partial bool MethodsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMethodsError))]
    public partial string? MethodsError { get; private set; }

    public bool HasMethodsError => MethodsError is not null;

    public bool CanRemoveAll => CanEdit && (_methods?.Any(m => m.Removable && PendingFor("authmethod:" + m.Id) is null) ?? false);

    async Task LoadMethodsAsync()
    {
        _methods = await ReadAsync(_methods, r => r.GetAuthMethodsAsync(_user.Id), v => MethodsLoading = v, v => MethodsError = v);
        RebuildMethods();
    }

    void RebuildMethods()
    {
        // A form open under a row stays open on the same method (the list is rebuilt when anything is staged).
        var openForms = Methods.Where(r => r is { Form: not null, Key: not null }).ToDictionary(r => r.Key!, r => r.Form);
        Methods.Clear();
        foreach (var method in _methods ?? [])
        {
            var m = method;
            // What waits for this method: its removal, or a new number or address.
            var pending = PendingFor("authmethod:" + m.Id)
                ?? (m.PhoneType is { } type ? PendingFor("phone:" + type) as UpdatePhoneMethod : null)
                ?? (m.Segment == "emailMethods" ? PendingFor("emailmethod") : null);
            var pendingText = pending switch
            {
                RemoveAuthMethod => "Removing",
                UpdatePhoneMethod p => $"Changing to {p.NewNumber}",
                SetEmailMethod e => $"Changing to {e.NewAddress}",
                _ => null,
            };
            var inactiveToken = m.Segment == "hardwareOathMethods" && m.Status is { } s && s != "activated";
            // The row's own form opens under it, so the change reads as part of that row.
            Action? change = !CanEdit ? null
                : inactiveToken ? () => OpenRowForm(m.Id, close => ActivateToken(m, close))
                : m.Value is null ? null
                : m.Segment == "phoneMethods" ? () => OpenRowForm(m.Id, close => ChangePhone(m, close))
                : m.Segment == "emailMethods" ? () => OpenRowForm(m.Id, close => ChangeEmail(m, close))
                : null;
            var row = new MethodRow(m.Kind, m.Detail, m.Added is { } at ? $"Added {at.ToLocalTime():d MMM yyyy}" : "", pendingText,
                change,
                CanEdit && m.Removable ? () => _pipeline!.Stage(new RemoveAuthMethod(Target, m.Id, m.Segment, m.Kind)) : null,
                pending is null ? null : () => _pipeline!.Unstage(pending),
                inactiveToken ? "Activate" : "Change", m.Id, m.IsDefault);
            if (pending is null && openForms.GetValueOrDefault(m.Id) is { } open)
                row.Form = open;
            Methods.Add(row);
        }
        // Methods being added are rows too, so they read as part of the list.
        foreach (var adding in _pipeline?.Pending.Where(p => p.Target.Id == _user.Id && p is AddPhoneMethod or SetEmailMethod { MethodId: null } or Core.Changes.AddHardwareToken).ToArray() ?? [])
        {
            var captured = adding;
            var (kind, detail) = adding switch
            {
                AddPhoneMethod p => ("Phone", $"{p.Number} ({PhoneName(p.PhoneType).ToLowerInvariant()})"),
                Core.Changes.AddHardwareToken t => ("Hardware Token", $"{t.Manufacturer} {t.Model} · serial {t.Serial}"),
                _ => ("Email (Password Reset)", ((SetEmailMethod)adding).NewAddress),
            };
            Methods.Add(new MethodRow(kind, detail, "", "Adding", null, null, () => _pipeline!.Unstage(captured)));
        }
        OnPropertyChanged(nameof(CanRemoveAll));
        RefreshContactButtons();
        RefreshLostPhone();
    }

    /// <summary>
    /// Every method except the password: they register again at their next sign-in (the usual
    /// answer to a lost phone or a suspected compromise).
    /// </summary>
    [RelayCommand]
    void RemoveAll()
    {
        if (!CanRemoveAll)
            return;
        _pipeline!.StageAll(AuthMethod.RemovalOrder(_methods!.Where(m => m.Removable && PendingFor("authmethod:" + m.Id) is null))
            .Select(m => new RemoveAuthMethod(Target, m.Id, m.Segment, m.Kind)));
    }

    // ---- Reset sign-in -----------------------------------------------------------------------
    // Entra's two buttons for when someone must prove who they are again (a lost or new phone, a
    // suspected compromise, someone leaving), side by side with a Temporary Access Pass.

    IEnumerable<AuthMethod> Removable => _methods?.Where(m => m.Removable) ?? [];

    /// <summary>Every MFA method is pending removal: re-registration is staged.</summary>
    public bool IsReregisterPending => Removable.Any() && Removable.All(m => PendingFor("authmethod:" + m.Id) is not null);

    public bool CanReregister => CanEdit && Removable.Any();

    public string ReregisterText => IsReregisterPending ? "Undo Re-Register" : "Require Re-Register";

    public string ReregisterTip => !Removable.Any() && _methods is not null
        ? "They have no MFA methods to remove: they'll be asked to register MFA when a policy needs it."
        : IsReregisterPending ? "Keep their methods (take the removals out of Pending Changes)"
        : "Removes their MFA methods (Authenticator, phones, passkeys…) but not the password; they set MFA up again at their next sign-in (added to Pending Changes)";

    /// <summary>Entra's "Require re-register multifactor authentication": removes every method but the password, or undoes that.</summary>
    [RelayCommand]
    void ToggleReregister()
    {
        if (!CanEdit)
            return;
        if (IsReregisterPending)
        {
            foreach (var pending in Removable.Select(m => PendingFor("authmethod:" + m.Id)).OfType<ChangeOp>().ToArray())
                _pipeline!.Unstage(pending);
        }
        else
            RemoveAll();
    }

    public bool IsRevokePending => PendingFor("sessions") is not null;

    public string RevokeText => IsRevokePending ? "Undo Revoke Sessions" : "Revoke Sessions";

    /// <summary>Entra's "Revoke sessions" (the record's "Sign out everywhere"): every app and browser asks them to sign in again.</summary>
    [RelayCommand]
    void ToggleRevokeSessions()
    {
        if (!CanEdit)
            return;
        if (PendingFor("sessions") is { } pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new RevokeSessions(Target));
    }

    void RefreshLostPhone()
    {
        foreach (var name in new[] { nameof(IsReregisterPending), nameof(CanReregister), nameof(ReregisterText), nameof(ReregisterTip), nameof(IsRevokePending), nameof(RevokeText) })
            OnPropertyChanged(name);
    }

    // ---- Phones and the password-reset email ---------------------------------------------------

    /// <summary>Microsoft allows one phone of each kind; a second is the alternate mobile or office phone.</summary>
    public static IReadOnlyList<(string Type, string Name)> PhoneTypes { get; } = [("mobile", "Mobile"), ("alternateMobile", "Alternate Mobile"), ("office", "Office")];

    static string PhoneName(string type) => PhoneTypes.FirstOrDefault(p => p.Type == type).Name ?? "Mobile";

    /// <summary>The phone kinds they have, counting ones being added.</summary>
    IReadOnlyList<string> PhonesHeld => (_methods ?? []).Where(m => m.Segment == "phoneMethods").Select(m => m.PhoneType ?? "mobile")
        .Concat(_pipeline?.Pending.OfType<AddPhoneMethod>().Where(p => p.User.Id == _user.Id).Select(p => p.PhoneType) ?? [])
        .Distinct().ToArray();

    bool HasEmailMethod => (_methods ?? []).Any(m => m.Segment == "emailMethods")
        || _pipeline?.Pending.OfType<SetEmailMethod>().Any(e => e.User.Id == _user.Id) == true;

    bool MethodsReady => _methods is not null && CanEdit && Form is null;

    public bool CanAddPhone => MethodsReady && PhonesHeld.Count < PhoneTypes.Count;

    /// <summary>"Add Phone" for the first one; after that, "Add Another Phone".</summary>
    public string AddPhoneText => PhonesHeld.Count == 0 ? "Add Phone" : "Add Another Phone";

    public bool CanAddEmail => MethodsReady && !HasEmailMethod;

    /// <summary>The one form open under the list (a phone or email, a hardware token, an activation), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForm), nameof(CanAddPhone), nameof(CanAddEmail), nameof(CanAddMethod))]
    public partial object? Form { get; private set; }

    public bool HasForm => Form is not null;

    /// <summary>Hardware tokens and the Authenticator guide: whenever methods can be added.</summary>
    public bool CanAddMethod => MethodsReady;

    void RefreshContactButtons()
    {
        foreach (var name in new[] { nameof(CanAddPhone), nameof(AddPhoneText), nameof(CanAddEmail), nameof(CanAddMethod) })
            OnPropertyChanged(name);
    }

    // ---- Hardware tokens (Microsoft's preview API) ----------------------------------------------

    [RelayCommand]
    void AddHardwareToken()
    {
        if (!CanAddMethod)
            return;
        Form = new HardwareTokenForm(form =>
        {
            _pipeline!.Stage(new Core.Changes.AddHardwareToken(Target, form.Serial.Trim(), form.Manufacturer.Trim(), form.Model.Trim(),
                form.SixtySeconds ? 60 : 30, form.Sha256 ? "hmacsha256" : "hmacsha1", form.Key));
            return true;
        }, CloseForm);
    }

    /// <param name="token">Its <see cref="AuthMethod.Value"/> is the serial number.</param>
    object ActivateToken(AuthMethod token, Action close) =>
        new TokenActivationForm(token.Value ?? "", async code =>
        {
            var result = await _pipeline!.ApplyNowAsync(new ActivateHardwareToken(Target, token.Id, token.Value ?? "", code));
            return result.Succeeded ? null : result.Detail ?? "It wasn't activated.";
        }, close);

    // ---- Microsoft Authenticator (set up by them, guided) ----------------------------------------

    [RelayCommand]
    void SetUpAuthenticator()
    {
        if (!CanAddMethod || _openDialog is null)
            return;
        var guide = new AuthenticatorSetupViewModel(_user.DisplayName, _user.UserPrincipalName,
            (minutes, once) => _pipeline!.ApplyNowAsync(new CreateTemporaryAccessPass(Target, minutes, once)), () => _closeDialog?.Invoke());
        // Its pass is made at once, so the usual "nothing is sent until you apply" footnote would be wrong.
        _openDialog(new DialogViewModel("Set Up Microsoft Authenticator", _user.DisplayName, guide, Footnote: null));
    }

    void CloseForm() => Form = null;

    [RelayCommand]
    void AddPhone()
    {
        var free = PhoneTypes.Where(t => !PhonesHeld.Contains(t.Type)).ToArray();
        if (!CanAddPhone || free.Length == 0)
            return;
        var first = PhonesHeld.Count == 0;
        Form = new ContactMethodForm(first ? "Add a Phone" : "Add Another Phone", "Add Phone", isPhone: true, "",
            first ? [free[0]] : free,
            form =>
            {
                _pipeline!.Stage(new AddPhoneMethod(Target, form.PhoneType ?? "mobile", NormalizePhone(form.Value)!));
                return true;
            }, CloseForm);
    }

    /// <summary>The form most recently opened (an add form at the top, or one under a row): the view puts the cursor in it.</summary>
    [ObservableProperty]
    public partial object? OpenedForm { get; private set; }

    partial void OnFormChanged(object? value)
    {
        if (value is null)
            return;
        // One form at a time: an add form closes any row's.
        foreach (var row in Methods)
            row.Form = null;
        OpenedForm = value;
    }

    /// <summary>Opens a form under one method's row, closing any other form first.</summary>
    /// <param name="key">The method's id: closing finds its row even after the list is rebuilt.</param>
    void OpenRowForm(string key, Func<Action, object> make)
    {
        Form = null;
        foreach (var other in Methods)
            other.Form = null;
        if (Methods.FirstOrDefault(r => r.Key == key) is not { } row)
            return;
        row.Form = make(() =>
        {
            if (Methods.FirstOrDefault(r => r.Key == key) is { } current)
                current.Form = null;
        });
        OpenedForm = row.Form;
    }

    object ChangePhone(AuthMethod phone, Action close)
    {
        var type = phone.PhoneType ?? "mobile";
        return new ContactMethodForm($"Change {PhoneName(type)} Phone", "Save", isPhone: true, phone.Value ?? "", [(type, PhoneName(type))],
            form =>
            {
                var number = NormalizePhone(form.Value)!;
                if (PhoneNumbers.Same(number, phone.Value))
                    return true;
                _pipeline!.Stage(new UpdatePhoneMethod(Target, phone.Id, type, phone.Value ?? "", number));
                return true;
            }, close);
    }

    [RelayCommand]
    void AddEmail()
    {
        if (!CanAddEmail)
            return;
        Form = new ContactMethodForm("Add a Password Reset Email", "Add Email", isPhone: false, "", [],
            form =>
            {
                _pipeline!.Stage(new SetEmailMethod(Target, null, null, form.Value.Trim()));
                return true;
            }, CloseForm);
    }

    object ChangeEmail(AuthMethod email, Action close) =>
        new ContactMethodForm("Change Password Reset Email", "Save", isPhone: false, email.Value ?? "", [],
            form =>
            {
                if (!string.Equals(form.Value.Trim(), email.Value, StringComparison.OrdinalIgnoreCase))
                    _pipeline!.Stage(new SetEmailMethod(Target, email.Id, email.Value, form.Value.Trim()));
                return true;
            }, close);

    /// <summary>Graph wants "+{country code} {number}": a space after the country code, digits after.</summary>
    public static string? NormalizePhone(string typed)
    {
        var parts = typed.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].StartsWith('+') || parts[0].Length is < 2 or > 4 || !parts[0][1..].All(char.IsDigit))
            return null;
        var number = string.Concat(parts.Skip(1)).Replace("-", "").Replace("(", "").Replace(")", "");
        var extension = number.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        var main = extension >= 0 ? number[..extension] : number;
        if (main.Length is < 4 or > 15 || !main.All(char.IsDigit) || (extension >= 0 && !number[(extension + 1)..].All(char.IsDigit)))
            return null;
        return $"{parts[0]} {number}";
    }

    /// <summary>One @, something before it, a dot after it, no spaces.</summary>
    public static bool IsEmail(string text)
    {
        var at = text.IndexOf('@');
        return at > 0 && at == text.LastIndexOf('@') && text.IndexOf('.', at) > at + 1 && !text.EndsWith('.') && !text.Contains(' ');
    }

    // ---- Temporary Access Pass --------------------------------------------------------------

    public static IReadOnlyList<string> TapLifetimes { get; } = ["1 Hour", "8 Hours", "1 Day"];

    [ObservableProperty]
    public partial string TapLifetime { get; set; } = "1 Hour";

    [ObservableProperty]
    public partial bool TapOneTime { get; set; } = true;

    public string? TapPendingText => PendingFor("tap") is { } p ? $"Pending: {p.After}" : null;

    public bool HasTapPending => TapPendingText is not null;

    /// <summary>An action, so a button: the pass is shown once, in Pending changes, after applying.</summary>
    [RelayCommand]
    void CreateTap()
    {
        if (!CanEdit)
            return;
        var minutes = TapLifetime switch { "8 Hours" => 480, "1 Day" => 1440, _ => 60 };
        _pipeline!.Stage(new CreateTemporaryAccessPass(Target, minutes, TapOneTime));
    }

    // ---- Risk -------------------------------------------------------------------------------

    Available<UserRisk>? _risk;

    [ObservableProperty]
    public partial bool RiskLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRiskError))]
    public partial string? RiskError { get; private set; }

    public bool HasRiskError => RiskError is not null;

    async Task LoadRiskAsync()
    {
        _risk = await ReadAsync(_risk, r => r.GetRiskAsync(_user.Id), v => RiskLoading = v, v => RiskError = v);
        foreach (var name in new[] { nameof(RiskText), nameof(RiskKind), nameof(IsRiskAvailable), nameof(IsAtRisk) })
            OnPropertyChanged(name);
    }

    public bool IsRiskAvailable => _risk is { Value: not null };

    public bool IsAtRisk => _risk?.Value?.IsAtRisk == true;

    public string RiskText => _risk switch
    {
        null => "",
        { Unavailable: { } why } => why,
        { Value: { IsAtRisk: true } r } => $"At risk: {r.Level} risk{(r.Detail is { } d ? $" ({d})" : "")}{(r.Updated is { } at ? $", since {at.ToLocalTime():d MMM yyyy}" : "")}.",
        { Value: { State: "confirmedCompromised" } } => "Confirmed compromised.",
        { Value: { State: "dismissed" or "remediated" } r } => $"No risk now ({r.State}).",
        _ => "No risk detected.",
    };

    public BadgeKind RiskKind => IsAtRisk ? BadgeKind.Danger : BadgeKind.Neutral;

    public string? RiskPendingText => PendingFor("risk") is { } p ? $"Pending: {p.After.ToLowerInvariant()}" : null;

    public bool HasRiskPending => RiskPendingText is not null;

    [RelayCommand]
    void ConfirmCompromised()
    {
        if (CanEdit && IsRiskAvailable)
            _pipeline!.Stage(new SetUserRisk(Target, true));
    }

    [RelayCommand]
    void DismissRisk()
    {
        if (CanEdit && IsAtRisk)
            _pipeline!.Stage(new SetUserRisk(Target, false));
    }

    // ---- Sign-in log ------------------------------------------------------------------------
    // Days, type and result are asked of the sign-in log itself; app, place, client, MFA and the
    // search box narrow what came back (the latest 500), instantly.

    public static IReadOnlyList<int> SignInDayOptions { get; } = [1, 7, 30];

    public static IReadOnlyList<string> SignInKindOptions { get; } = ["Interactive", "Non-Interactive"];

    public static IReadOnlyList<string> SignInResultOptions { get; } = ["All Results", "Succeeded", "Failed"];

    [ObservableProperty]
    public partial int SignInDays { get; set; } = 7;

    [ObservableProperty]
    public partial string SignInKind { get; set; } = "Interactive";

    [ObservableProperty]
    public partial string SignInResult { get; set; } = "All Results";

    partial void OnSignInDaysChanged(int value) => _ = LoadSignInsAsync(again: true);
    partial void OnSignInKindChanged(string value) => _ = LoadSignInsAsync(again: true);
    partial void OnSignInResultChanged(string value) => _ = LoadSignInsAsync(again: true);

    SignInQuery Query => new(SignInDays,
        SignInKind == "Non-Interactive" ? Tenantry.Core.Directory.SignInKind.NonInteractive : Tenantry.Core.Directory.SignInKind.Interactive,
        SignInResult switch { "Succeeded" => Tenantry.Core.Directory.SignInResult.Succeeded, "Failed" => Tenantry.Core.Directory.SignInResult.Failed, _ => Tenantry.Core.Directory.SignInResult.All });

    Available<IReadOnlyList<SignInEvent>>? _signIns;

    public ObservableCollection<SignInRow> SignIns { get; } = [];

    [ObservableProperty]
    public partial bool SignInsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInsError))]
    public partial string? SignInsError { get; private set; }

    public bool HasSignInsError => SignInsError is not null;

    public string? SignInsUnavailable => _signIns?.Unavailable;

    public bool HasSignInsUnavailable => SignInsUnavailable is not null;

    // The facets: each lists what the loaded sign-ins hold, with counts, most common first.

    [ObservableProperty]
    public partial IReadOnlyList<FacetOption> AppOptions { get; private set; } = [FacetOption.Any("Any App")];

    [ObservableProperty]
    public partial IReadOnlyList<FacetOption> LocationOptions { get; private set; } = [FacetOption.Any("Anywhere")];

    [ObservableProperty]
    public partial IReadOnlyList<FacetOption> ClientOptions { get; private set; } = [FacetOption.Any("Any Client")];

    [ObservableProperty]
    public partial IReadOnlyList<FacetOption> AuthOptions { get; private set; } = [FacetOption.Any("Any Authentication")];

    [ObservableProperty]
    public partial FacetOption? SelectedApp { get; set; }

    [ObservableProperty]
    public partial FacetOption? SelectedLocation { get; set; }

    [ObservableProperty]
    public partial FacetOption? SelectedClient { get; set; }

    [ObservableProperty]
    public partial FacetOption? SelectedAuth { get; set; }

    /// <summary>Words to find in the app, place, IP address, device or failure reason.</summary>
    [ObservableProperty]
    public partial string SignInSearch { get; set; } = "";

    partial void OnSelectedAppChanged(FacetOption? value) => FilterSignIns();
    partial void OnSelectedLocationChanged(FacetOption? value) => FilterSignIns();
    partial void OnSelectedClientChanged(FacetOption? value) => FilterSignIns();
    partial void OnSelectedAuthChanged(FacetOption? value) => FilterSignIns();
    partial void OnSignInSearchChanged(string value) => FilterSignIns();

    static string AuthOf(SignInEvent e) => e.MultiFactor switch { true => "Multi-Factor", false => "Single-Factor (Password Only)", _ => "Not Recorded" };

    public bool HasSignInFilters => SelectedApp?.Value is not null || SelectedLocation?.Value is not null || SelectedClient?.Value is not null
                                    || SelectedAuth?.Value is not null || SignInSearch.Trim().Length > 0;

    [RelayCommand]
    void ClearSignInFilters()
    {
        _settingFacets = true;
        SelectedApp = AppOptions[0];
        SelectedLocation = LocationOptions[0];
        SelectedClient = ClientOptions[0];
        SelectedAuth = AuthOptions[0];
        SignInSearch = "";
        _settingFacets = false;
        FilterSignIns();
    }

    bool _settingFacets;

    [ObservableProperty]
    public partial string SignInsSummary { get; private set; } = "";

    async Task LoadSignInsAsync(bool again = false)
    {
        if (!_shown)
            return;
        var query = Query;
        _signIns = await ReadAsync(_signIns, r => r.GetSignInsAsync(_user.Id, query), v => SignInsLoading = v, v => SignInsError = v, again);
        BuildFacets(_signIns?.Value ?? []);
        OnPropertyChanged(nameof(SignInsUnavailable));
        OnPropertyChanged(nameof(HasSignInsUnavailable));
        FilterSignIns();
    }

    /// <summary>Options from what was loaded; a choice that's still there stays chosen.</summary>
    void BuildFacets(IReadOnlyList<SignInEvent> events)
    {
        static IReadOnlyList<FacetOption> Options(string any, IEnumerable<string?> values) =>
        [
            FacetOption.Any(any),
            .. values.Select(v => string.IsNullOrEmpty(v) ? "Unknown" : v)
                .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCulture)
                .Select(g => new FacetOption(g.Key, $"{g.Key} ({g.Count():N0})")),
        ];
        static FacetOption Keep(IReadOnlyList<FacetOption> options, FacetOption? chosen) =>
            options.FirstOrDefault(o => o.Value is not null && string.Equals(o.Value, chosen?.Value, StringComparison.OrdinalIgnoreCase)) ?? options[0];

        _settingFacets = true;
        var (app, location, client, auth) = (SelectedApp, SelectedLocation, SelectedClient, SelectedAuth);
        AppOptions = Options("Any App", events.Select(e => e.App));
        LocationOptions = Options("Anywhere", events.Select(e => e.Location));
        ClientOptions = Options("Any Client", events.Select(e => e.ClientKind));
        AuthOptions = Options("Any Authentication", events.Select(AuthOf));
        SelectedApp = Keep(AppOptions, app);
        SelectedLocation = Keep(LocationOptions, location);
        SelectedClient = Keep(ClientOptions, client);
        SelectedAuth = Keep(AuthOptions, auth);
        _settingFacets = false;
    }

    void FilterSignIns()
    {
        if (_settingFacets)
            return;
        var all = _signIns?.Value ?? [];
        var words = SignInSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Is(FacetOption? chosen, string? value) => chosen?.Value is null || string.Equals(chosen.Value, string.IsNullOrEmpty(value) ? "Unknown" : value, StringComparison.OrdinalIgnoreCase);
        var shown = all.Where(e => Is(SelectedApp, e.App) && Is(SelectedLocation, e.Location) && Is(SelectedClient, e.ClientKind) && Is(SelectedAuth, AuthOf(e))
                                   && words.All(w => string.Join(' ', e.App, e.Location, e.IpAddress, e.Device, e.Problem, e.ClientApp).Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .ToArray();
        SignIns.Clear();
        foreach (var e in shown)
            SignIns.Add(new SignInRow(e));
        OnPropertyChanged(nameof(HasSignInFilters));
        var kind = SignInKind == "Non-Interactive" ? "non-interactive " : "";
        var result = SignInResult switch { "Failed" => "failed ", "Succeeded" => "successful ", _ => "" };
        var period = SignInDays == 1 ? "day" : $"{SignInDays} days";
        SignInsSummary = _signIns?.Value is null ? ""
            : all.Count == 0 ? $"No {result}{kind}sign-ins in the last {period}."
            : (shown.Length == all.Count ? $"{all.Count:N0}" : $"Showing {shown.Length:N0} of {all.Count:N0}")
              + $" {result}{kind}sign-in{(all.Count == 1 ? "" : "s")} in the last {period}"
              + (SignInResult == "All Results" ? $", {shown.Count(e => !e.Succeeded):N0} failed" : "")
              + (all.Count >= ISecurityRecords.SignInLimit ? $" (the latest {ISecurityRecords.SignInLimit:N0}; narrow the days or type to see older ones)" : "")
              + ".";
    }

    // ---- Roles and app access ---------------------------------------------------------------

    (IReadOnlyList<RoleHeld> Active, Available<IReadOnlyList<RoleHeld>> Eligible)? _roles;
    IReadOnlyList<AppConsent>? _consents;

    public ObservableCollection<RevocableRow> Roles { get; } = [];

    public ObservableCollection<RevocableRow> Consents { get; } = [];

    public IReadOnlyList<string> EligibleRoles => _roles?.Eligible.Value?.Select(r => $"{r.RoleName} ({r.Scope.ToLowerInvariant()})").ToArray() ?? [];

    public string? EligibleText => _roles is not { } roles ? null
        : roles.Eligible.Unavailable ?? (roles.Eligible.Value!.Count == 0 ? "No roles they can activate." : null);

    public bool HasEligibleText => EligibleText is not null;

    public bool HasNoRoles => _roles is { Active.Count: 0 };

    public bool HasNoConsents => _consents is { Count: 0 };

    [ObservableProperty]
    public partial bool AccessLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccessError))]
    public partial string? AccessError { get; private set; }

    public bool HasAccessError => AccessError is not null;

    async Task LoadRolesAsync()
    {
        var read = await ReadAsync<(IReadOnlyList<RoleHeld>, Available<IReadOnlyList<RoleHeld>>)?>(_roles, async r => await r.GetRolesAsync(_user.Id),
            v => AccessLoading = v, v => AccessError = v);
        _roles = read;
        RebuildAccess();
    }

    async Task LoadConsentsAsync()
    {
        _consents = await ReadAsync(_consents, r => r.GetAppConsentsAsync(_user.Id), _ => { }, v => AccessError ??= v);
        RebuildAccess();
    }

    void RebuildAccess()
    {
        Roles.Clear();
        foreach (var role in _roles?.Active ?? [])
        {
            var captured = role;
            Roles.Add(new RevocableRow(role.RoleName, role.Scope, PendingFor("role:" + role.AssignmentId) is not null, CanEdit,
                "Take this role away (added to Pending Changes)", () => Toggle("role:" + captured.AssignmentId,
                    () => new RemoveRoleAssignment(Target, captured.AssignmentId, captured.RoleName))));
        }
        Consents.Clear();
        foreach (var consent in _consents ?? [])
        {
            var captured = consent;
            Consents.Add(new RevocableRow(consent.AppName, string.Join(", ", consent.Scopes), PendingFor("consent:" + consent.GrantId) is not null, CanEdit,
                "Take back this app's access to their data (added to Pending Changes)", () => Toggle("consent:" + captured.GrantId,
                    () => new RevokeAppConsent(Target, captured.GrantId, captured.AppName))));
        }
        foreach (var name in new[] { nameof(EligibleRoles), nameof(EligibleText), nameof(HasEligibleText), nameof(HasNoRoles), nameof(HasNoConsents) })
            OnPropertyChanged(name);
    }

    void Toggle(string key, Func<ChangeOp> change)
    {
        if (!CanEdit)
            return;
        if (PendingFor(key) is { } pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(change());
    }

    // ---- Conditional Access -----------------------------------------------------------------

    IReadOnlyList<PolicyMatch>? _policies;

    public ObservableCollection<PolicyRow> Policies { get; } = [];

    [ObservableProperty]
    public partial bool PoliciesLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPoliciesError))]
    public partial string? PoliciesError { get; private set; }

    public bool HasPoliciesError => PoliciesError is not null;

    /// <summary>Policies that don't target them, shown too (off by default: they're most of the list).</summary>
    [ObservableProperty]
    public partial bool ShowAllPolicies { get; set; }

    partial void OnShowAllPoliciesChanged(bool value) => RebuildPolicies();

    public string PoliciesSummary => _policies is not { } all ? ""
        : $"{all.Count(p => p.Fit == PolicyFit.Applies)} of {all.Count} policies apply to them ({all.Count(p => p.Fit == PolicyFit.Applies && p.State == "On")} switched on); "
          + (all.Count(p => p.Fit == PolicyFit.Excluded) is var left && left == 1 ? "1 leaves them out." : $"{left} leave them out.");

    async Task LoadPoliciesAsync()
    {
        _policies = await ReadAsync(_policies, r => r.GetConditionalAccessAsync(_user.Id, _user.Kind == UserKind.Guest), v => PoliciesLoading = v, v => PoliciesError = v);
        RebuildPolicies();
    }

    void RebuildPolicies()
    {
        Policies.Clear();
        foreach (var policy in _policies ?? [])
            if (ShowAllPolicies || policy.Fit != PolicyFit.NotTargeted)
                Policies.Add(new PolicyRow(policy));
        OnPropertyChanged(nameof(PoliciesSummary));
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

/// <summary>One choice in a sign-in filter: a value with how many sign-ins have it, or "any".</summary>
public sealed record FacetOption(string? Value, string Label)
{
    public static FacetOption Any(string label) => new(null, label);

    public override string ToString() => Label;
}
