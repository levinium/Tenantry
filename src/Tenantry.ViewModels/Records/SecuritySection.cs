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

/// <summary>One sign-in method, with remove (pressing again undoes).</summary>
public sealed partial class MethodRow(AuthMethod method, bool removing, bool canEdit, Action toggle) : ObservableObject
{
    public string Kind => method.Kind;
    public string? Detail => method.Detail;
    public bool HasDetail => !string.IsNullOrEmpty(method.Detail);
    public string AddedText => method.Added is { } at ? $"Added {at.ToLocalTime():d MMM yyyy}" : "";
    public bool CanRemove => canEdit && method.Removable;
    public bool IsRemoving => removing;
    public string RemoveTip => removing ? "Keep this method (take the removal out of Pending changes)" : "Remove this method (added to Pending changes)";

    [RelayCommand]
    void Remove() => toggle();
}

/// <summary>One sign-in from the log.</summary>
public sealed class SignInRow(SignInEvent e)
{
    public string When => e.When.ToLocalTime().ToString("d MMM, h:mm tt");
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
    public string Tip => revoking ? "Keep it (take the change out of Pending changes)" : actionTip;

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

    public SecuritySection(UserSummary user, ISecurityRecords? records, ChangePipeline? pipeline, Availability canEdit)
    {
        _user = user;
        _records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
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
        if (_shown && applied.Any(c => c.Target.Id == _user.Id && c is AddPhoneMethod or CreateTemporaryAccessPass or RemoveAuthMethod))
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
        foreach (var name in new[] { nameof(TapPendingText), nameof(HasTapPending), nameof(RiskPendingText), nameof(HasRiskPending), nameof(PhonePendingText), nameof(HasPhonePending) })
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
        Methods.Clear();
        foreach (var method in _methods ?? [])
        {
            var captured = method;
            Methods.Add(new MethodRow(method, PendingFor("authmethod:" + method.Id) is not null, CanEdit, () => ToggleRemove(captured)));
        }
        OnPropertyChanged(nameof(CanRemoveAll));
        RefreshLostPhone();
    }

    void ToggleRemove(AuthMethod method)
    {
        if (!CanEdit || !method.Removable)
            return;
        if (PendingFor("authmethod:" + method.Id) is { } pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new RemoveAuthMethod(Target, method.Id, method.Segment, method.Kind));
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
        _pipeline!.StageAll(_methods!.Where(m => m.Removable && PendingFor("authmethod:" + m.Id) is null)
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
        : IsReregisterPending ? "Keep their methods (take the removals out of Pending changes)"
        : "Removes their MFA methods (Authenticator, phones, passkeys…) but not the password; they set MFA up again at their next sign-in (added to Pending changes)";

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

    // ---- Add a phone ------------------------------------------------------------------------

    public static IReadOnlyList<(string Type, string Name)> PhoneTypes { get; } = [("mobile", "Mobile"), ("alternateMobile", "Alternate Mobile"), ("office", "Office")];

    public IReadOnlyList<string> PhoneTypeNames { get; } = PhoneTypes.Select(p => p.Name).ToArray();

    [ObservableProperty]
    public partial string NewPhoneType { get; set; } = "Mobile";

    [ObservableProperty]
    public partial string NewPhoneNumber { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoneError))]
    public partial string? PhoneError { get; private set; }

    public bool HasPhoneError => PhoneError is not null;

    string PhoneTypeKey => PhoneTypes.First(p => p.Name == NewPhoneType).Type;

    public string? PhonePendingText => _pipeline?.Pending.OfType<AddPhoneMethod>().Where(p => p.User.Id == _user.Id)
        .Select(p => $"Pending: {p.Label.ToLowerInvariant()} {p.Number}").FirstOrDefault();

    public bool HasPhonePending => PhonePendingText is not null;

    partial void OnNewPhoneNumberChanged(string value) => StagePhone();
    partial void OnNewPhoneTypeChanged(string value) => StagePhone();

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

    void StagePhone()
    {
        if (!CanEdit)
            return;
        // One phone of each type: a new number replaces a pending one.
        foreach (var pending in _pipeline!.Pending.OfType<AddPhoneMethod>().Where(p => p.User.Id == _user.Id).ToArray())
            _pipeline.Unstage(pending);
        if (NewPhoneNumber.Trim().Length == 0)
        {
            PhoneError = null;
            return;
        }
        if (NormalizePhone(NewPhoneNumber) is not { } number)
        {
            PhoneError = "Type the country code, a space, then the number: +1 2065551234.";
            return;
        }
        if (_methods?.Any(m => m.Segment == "phoneMethods" && (m.Detail ?? "").Contains($"({NewPhoneType.ToLowerInvariant()})", StringComparison.OrdinalIgnoreCase)) == true)
        {
            PhoneError = $"They already have a{(NewPhoneType == "Office" ? "n" : "")} {NewPhoneType.ToLowerInvariant()} phone. Remove it first to change it.";
            return;
        }
        PhoneError = null;
        _pipeline.Stage(new AddPhoneMethod(Target, PhoneTypeKey, number));
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
                "Take this role away (added to Pending changes)", () => Toggle("role:" + captured.AssignmentId,
                    () => new RemoveRoleAssignment(Target, captured.AssignmentId, captured.RoleName))));
        }
        Consents.Clear();
        foreach (var consent in _consents ?? [])
        {
            var captured = consent;
            Consents.Add(new RevocableRow(consent.AppName, string.Join(", ", consent.Scopes), PendingFor("consent:" + consent.GrantId) is not null, CanEdit,
                "Take back this app's access to their data (added to Pending changes)", () => Toggle("consent:" + captured.GrantId,
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
          + $"{all.Count(p => p.Fit == PolicyFit.Excluded)} exclude them.";

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
