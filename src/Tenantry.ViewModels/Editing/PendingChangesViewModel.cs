using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;

namespace Tenantry.ViewModels.Editing;

/// <summary>One staged change in the tray, and (after a failed apply) why it didn't go through.</summary>
public sealed partial class PendingRow(ChangeOp change, Action<ChangeOp> remove, Action<ChangeOp>? showPowerShell = null) : ObservableObject
{
    public bool CanShowPowerShell => showPowerShell is not null;

    [RelayCommand]
    void ShowPowerShell() => showPowerShell?.Invoke(Change);

    public ChangeOp Change { get; } = change;

    public string TargetName => Change.Target.Name;

    public string Label => Change.Label;

    public string LabelText => Change.Label + ":";

    public string Before => Change.Before;

    public string After => Change.After;

    public bool IsCaution => Change.IsCaution;

    /// <summary>Something to know before applying it (a change Microsoft takes in two steps).</summary>
    public string? Notice => Change.Notice;

    public bool HasNotice => Change.Notice is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    /// <summary>While it applies, how far it has got ("Moving rule 2 of 5: …").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    public partial string? Progress { get; set; }

    public bool HasProgress => Progress is not null;

    /// <summary>While Apply runs: Waiting, Applying, Applied or Didn't Go Through; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasState), nameof(IsWorking), nameof(IsDone), nameof(IsFailed), nameof(IsWaiting))]
    public partial ApplyState? State { get; set; }

    public bool HasState => State is not null;
    public bool IsWaiting => State == ApplyState.Waiting;
    public bool IsWorking => State == ApplyState.Applying;
    public bool IsDone => State == ApplyState.Applied;
    public bool IsFailed => State == ApplyState.Failed;

    public string StateText => State switch
    {
        ApplyState.Waiting => "Waiting",
        ApplyState.Applying => "Applying…",
        ApplyState.Applied => "Applied",
        ApplyState.Failed => "Didn't Go Through",
        _ => "",
    };

    partial void OnStateChanged(ApplyState? value) => OnPropertyChanged(nameof(StateText));

    [RelayCommand]
    void Remove() => remove(Change);
}

/// <summary>Where one change is while the tray applies them.</summary>
public enum ApplyState
{
    Waiting,
    Applying,
    Applied,
    Failed,
}

/// <summary>
/// A change that can't be undone (wiping a phone), waiting for the admin to type its phrase
/// before Apply goes ahead.
/// </summary>
public sealed partial class TypedConfirmRow(ChangeOp change, Action changed) : ObservableObject
{
    public ChangeOp Change { get; } = change;

    public string Phrase => Change.ConfirmPhrase ?? "";

    public string Prompt => $"{Change.Label} ({Change.Target.Name}): {Change.After}. {Change.TypedReason ?? "This can't be undone."} Type {Phrase} to confirm.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Matches))]
    public partial string Typed { get; set; } = "";

    public bool Matches => string.Equals(Typed.Trim(), Phrase.Trim(), StringComparison.CurrentCultureIgnoreCase);

    partial void OnTypedChanged(string value) => changed();
}

/// <summary>
/// Something to show the admin once after applying (a temporary password): with a Copy button,
/// kept only until the tray is closed, never stored or logged.
/// </summary>
/// <param name="person">Who it's for, and their sign-in name (for Copy all and the CSV).</param>
public sealed partial class RevealRow(string label, string secret, Action copied, ChangeTarget? person = null) : ObservableObject
{
    public string Label { get; } = label;

    public string Secret { get; } = secret;

    public ChangeTarget? Person { get; } = person;

    [ObservableProperty]
    public partial bool Copied { get; private set; }

    [RelayCommand]
    void Copy()
    {
        Platform.CopyText(Secret);
        Copied = true;
        copied();
    }
}

/// <summary>
/// The Pending changes tray at the bottom of the window, and the tenant's Test mode banner.
/// Changes are reviewed as before → after and applied together; ones that take something away
/// (blocking sign-in) need a second, in-place confirmation. Test mode can only be switched off by
/// typing the tenant's name.
/// </summary>
public sealed partial class PendingChangesViewModel : ObservableObject
{
    readonly ChangePipeline _pipeline;
    readonly string _tenantName;
    readonly Func<Task> _afterApply;
    readonly Dictionary<ChangeOp, string> _problems = [];
    readonly Action<IReadOnlyList<ChangeOp>>? _showPowerShell;

    /// <param name="afterApply">Runs after changes apply (reload the lists, refresh open records).</param>
    /// <param name="showPowerShell">Shows the PowerShell for one change or all of them; null hides the buttons.</param>
    /// <param name="staleReason">Why a staged change's starting point has since changed (someone else changed it), or null.</param>
    public PendingChangesViewModel(ChangePipeline pipeline, string tenantName, bool showTestMode, Func<Task> afterApply,
        Action<IReadOnlyList<ChangeOp>>? showPowerShell = null, Func<ChangeOp, string?>? staleReason = null)
    {
        _staleReason = staleReason;
        _pipeline = pipeline;
        _tenantName = tenantName;
        _afterApply = afterApply;
        _showPowerShell = showPowerShell;
        ShowTestModeBanner = showTestMode;
        TurnOffText = "";
        _pipeline.Changed += (_, _) =>
        {
            // A new edit starts a new batch: "applied" from the last one would read as if it were pending.
            if (!IsApplying && ResultIsSuccess)
                Result = null;
            // A different set of changes is checked against Microsoft 365 again.
            if (!IsApplying)
                _staleShown = false;
            Rebuild();
        };
        // Reports can come from a background thread; the rows belong to the UI's.
        var ui = SynchronizationContext.Current;
        _pipeline.Progress += (_, report) =>
        {
            void Show()
            {
                foreach (var row in Rows.Where(r => ReferenceEquals(r.Change, report.Change)))
                    row.Progress = report.Text;
            }
            if (ui is null || SynchronizationContext.Current == ui)
                Show();
            else
                ui.Post(_ => Show(), null);
        };
        void OnUi(Action act)
        {
            if (ui is null || SynchronizationContext.Current == ui)
                act();
            else
                ui.Post(_ => act(), null);
        }
        _pipeline.Starting += (_, change) => OnUi(() =>
        {
            CurrentRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Change, change));
            if (CurrentRow is { } row)
                row.State = ApplyState.Applying;
            ApplyCurrent = change.Target.Name;
            UpdateApplyStatus();
        });
        _pipeline.Finished += (_, done) => OnUi(() =>
        {
            foreach (var row in Rows.Where(r => ReferenceEquals(r.Change, done.Change)))
            {
                row.State = done.Result.Succeeded ? ApplyState.Applied : ApplyState.Failed;
                row.Progress = null;
            }
            ApplyDone++;
            if (!done.Result.Succeeded)
                ApplyFailed++;
            UpdateApplyStatus();
        });
        Rebuild();
    }

    // ---- Checking before applying -------------------------------------------------------------------

    readonly Func<ChangeOp, string?>? _staleReason;

    /// <summary>Changes someone else made since staging were shown; the next Apply goes ahead.</summary>
    bool _staleShown;

    /// <summary>Bringing the local copy up to date before Apply.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(DiscardAllCommand))]
    public partial bool IsCheckingFirst { get; private set; }

    /// <summary>Checking first or applying: the progress line shows.</summary>
    public bool IsWorking => IsCheckingFirst || IsApplying;

    partial void OnIsApplyingChanged(bool value) => OnPropertyChanged(nameof(IsWorking));

    /// <summary>
    /// Brings the copy up to date, then marks staged changes whose starting point someone else has
    /// since changed. True when there were some (shown on their rows; Apply again to go ahead).
    /// </summary>
    async Task<bool> ShowChangedSinceAsync()
    {
        IsCheckingFirst = true;
        ApplyStatus = "Checking Microsoft 365 for changes first…";
        string? note;
        try
        {
            note = await Freshness.EnsureAsync(Freshness.ForAction);
        }
        finally
        {
            IsCheckingFirst = false;
        }
        var changed = 0;
        foreach (var row in Rows)
        {
            if (_staleReason?.Invoke(row.Change) is { } reason)
            {
                row.Problem = reason;
                changed++;
            }
        }
        if (changed == 0)
        {
            if (note is not null)
                Result = note;
            return false;
        }
        _staleShown = true;
        Result = $"{Plural(changed, "change")} {(changed == 1 ? "was" : "were")} changed by someone else after you staged {(changed == 1 ? "it" : "them")} (see below). " +
                 "Apply again to go ahead anyway, or Undo the ones you don't want.";
        return true;
    }

    // ---- Progress while applying --------------------------------------------------------------------

    readonly System.Diagnostics.Stopwatch _applyClock = new();

    /// <summary>The row being applied now, so the list can keep it in view.</summary>
    [ObservableProperty]
    public partial PendingRow? CurrentRow { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyPercent))]
    public partial int ApplyDone { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyPercent))]
    public partial int ApplyTotal { get; private set; }

    [ObservableProperty]
    public partial int ApplyFailed { get; private set; }

    /// <summary>Whose change is being sent now.</summary>
    [ObservableProperty]
    public partial string? ApplyCurrent { get; private set; }

    public double ApplyPercent => ApplyTotal == 0 ? 0 : 100.0 * ApplyDone / ApplyTotal;

    /// <summary>"Applying 23 of 177 · 1 didn't go through · about 3 minutes left".</summary>
    [ObservableProperty]
    public partial string ApplyStatus { get; private set; } = "";

    void UpdateApplyStatus()
    {
        var parts = new List<string> { $"Applying {Math.Min(ApplyDone + 1, ApplyTotal):N0} of {ApplyTotal:N0}" + (ApplyCurrent is { } who ? $": {who}" : "") };
        if (ApplyFailed > 0)
            parts.Add($"{ApplyFailed:N0} didn't go through");
        // An estimate once a few are done: the average so far, times what's left.
        if (ApplyDone >= 3 && ApplyDone < ApplyTotal)
            parts.Add(TimeLeft(TimeSpan.FromTicks(_applyClock.Elapsed.Ticks / ApplyDone * (ApplyTotal - ApplyDone))));
        ApplyStatus = string.Join(" · ", parts);
    }

    public static string TimeLeft(TimeSpan left) => left.TotalSeconds switch
    {
        < 60 => "less than a minute left",
        < 90 => "about a minute left",
        _ => $"about {Math.Round(left.TotalMinutes):N0} minutes left",
    };

    public ObservableCollection<PendingRow> Rows { get; } = [];

    /// <summary>Temporary passwords from the last apply, shown once until dismissed.</summary>
    public ObservableCollection<RevealRow> Reveals { get; } = [];

    public bool HasReveals => Reveals.Count > 0;

    /// <summary>Two or more passwords (a bulk reset): copy all of them at once.</summary>
    public bool HasManyReveals => Reveals.Count > 1;

    // Passwords from an earlier apply stay until closed, so the count can be more than the last apply made: say so.
    public string RevealsHeader =>
        $"{Reveals.Count:N0} new {RevealNoun(Reveals.Count == 1)}"
        + (_earlierReveals > 0 && _earlierReveals < Reveals.Count ? $" ({_earlierReveals:N0} from an earlier apply)" : "")
        + ". Shown once and never stored: copy or save now, then pass on securely.";

    /// <summary>How many of <see cref="Reveals"/> were already there before the last apply.</summary>
    int _earlierReveals;

    /// <summary>What the secrets are, in words: one kind by its name, a mix as "passwords and codes".</summary>
    string RevealNoun(bool one)
    {
        var kinds = Reveals.Select(r => r.Label.StartsWith("Client Secret", StringComparison.Ordinal) ? "client secret"
            : r.Label.StartsWith("Temporary Access Pass", StringComparison.Ordinal) ? "Temporary Access Pass"
            : "password").Distinct().ToArray();
        return kinds.Length != 1 ? "passwords and codes" : one ? kinds[0] : kinds[0] + (kinds[0].EndsWith('s') ? "es" : "s");
    }

    [ObservableProperty]
    public partial bool AllCopied { get; private set; }

    /// <summary>Every password copied (together or one by one) or saved: closing loses nothing.</summary>
    bool RevealsKept => AllCopied || _revealsSaved || Reveals.All(r => r.Copied);

    bool _revealsSaved;

    /// <summary>One line per person: name, sign-in name and password, separated by tabs (pastes into a spreadsheet).</summary>
    [RelayCommand]
    void CopyAllReveals()
    {
        Platform.CopyText(string.Join(Environment.NewLine, Reveals.Select(r => string.Join('\t', r.Person?.Name ?? r.Label, r.Person?.Address ?? "", r.Secret))));
        AllCopied = true;
        IsConfirmingClose = false;
    }

    [RelayCommand]
    async Task SaveReveals()
    {
        var csv = new System.Text.StringBuilder("Name,Sign-in name,Password\r\n");
        foreach (var r in Reveals)
            csv.Append(Csv(r.Person?.Name ?? r.Label)).Append(',').Append(Csv(r.Person?.Address ?? "")).Append(',').Append(Csv(r.Secret)).Append("\r\n");
        try
        {
            if (await Platform.SaveCsv($"new-passwords-{DateTime.Now:yyyy-MM-dd-HHmm}.csv", csv.ToString()))
            {
                _revealsSaved = true;
                IsConfirmingClose = false;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The passwords stay on screen, so nothing is lost: copy them instead, or save elsewhere.
            _resultSucceeded = false;
            Result = Records.ImportUsersViewModel.FileError("save the file", e);
        }
    }

    // Always quoted, never altered: a password must arrive exactly as set.
    static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    // ---- Closing the tray ---------------------------------------------------------------------

    /// <summary>Close shows once nothing is waiting: after an apply, to clear the result and any passwords.</summary>
    public bool CanClose => !HasChanges && (HasResult || HasReveals);

    /// <summary>Close was clicked with passwords not yet copied or saved: the next click closes anyway.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseText), nameof(CloseTip))]
    public partial bool IsConfirmingClose { get; private set; }

    public string CloseText => IsConfirmingClose ? "Close Without Copying" : "Close";

    public string CloseTip => IsConfirmingClose
        ? "The passwords aren't copied or saved, and can't be shown again. Click again to close anyway."
        : "Close this bar; the Change Log keeps what was applied";

    [RelayCommand]
    void Close()
    {
        if (HasReveals && !RevealsKept && !IsConfirmingClose)
        {
            IsConfirmingClose = true;
            return;
        }
        CloseNow();
    }

    void CloseNow()
    {
        _lingering?.Cancel();
        IsConfirmingClose = false;
        Reveals.Clear();
        AllCopied = false;
        _revealsSaved = false;
        Result = null;
        RevealsChanged();
    }

    /// <summary>How long a clean result stays before the bar closes by itself (nothing to copy, nothing failed).</summary>
    public static TimeSpan DefaultSuccessLingers { get; set; } = TimeSpan.FromSeconds(8);

    public TimeSpan SuccessLingers { get; set; } = DefaultSuccessLingers;

    CancellationTokenSource? _lingering;

    async void CloseAfterSuccess()
    {
        _lingering?.Cancel();
        var lingering = _lingering = new CancellationTokenSource();
        try
        {
            await Task.Delay(SuccessLingers, lingering.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ResultIsSuccess && !HasChanges && !HasReveals)
            CloseNow();
    }

    void RevealsChanged()
    {
        OnPropertyChanged(nameof(HasReveals));
        OnPropertyChanged(nameof(HasManyReveals));
        OnPropertyChanged(nameof(RevealsHeader));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(CanClose));
    }

    public int Count => Rows.Count;

    public bool HasChanges => Count > 0;

    public bool HasResult => Result is not null;

    /// <summary>The tray shows while there are changes, a result to read, or a password to pass on.</summary>
    public bool IsVisible => HasChanges || HasResult || HasReveals;

    void RevealCopied()
    {
        if (RevealsKept)
            IsConfirmingClose = false;
    }

    // With only new passwords to pass on (nothing waiting), the tray is about those.
    public string HeaderText =>
        Count > 0 ? $"Pending Changes ({Count})"
        : HasReveals ? "New " + Title(RevealNoun(Reveals.Count == 1))
        : HasResult ? "Changes Applied"
        : "Pending Changes";

    // A change with a phrase to type is confirmed like a caution change, even if it isn't one.
    int CautionCount => Rows.Count(r => r.IsCaution || r.Change.ConfirmPhrase is not null);

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [RelayCommand]
    void ToggleOpen() => IsOpen = !IsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyText), nameof(ApplyIsDanger), nameof(HasTypedConfirms))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsConfirming { get; private set; }

    /// <summary>While confirming: the changes that also need their phrase typed.</summary>
    public ObservableCollection<TypedConfirmRow> TypedConfirms { get; } = [];

    public bool HasTypedConfirms => IsConfirming && TypedConfirms.Count > 0;

    partial void OnIsConfirmingChanged(bool value)
    {
        TypedConfirms.Clear();
        if (value)
            foreach (var row in Rows.Where(r => r.Change.ConfirmPhrase is not null))
                TypedConfirms.Add(new TypedConfirmRow(row.Change, ApplyCommand.NotifyCanExecuteChanged));
        OnPropertyChanged(nameof(HasTypedConfirms));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(DiscardAllCommand))]
    public partial bool IsApplying { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(IsVisible), nameof(HeaderText), nameof(ResultIsSuccess), nameof(CanClose))]
    public partial string? Result { get; private set; }

    /// <summary>The last apply went through completely: shown as done, cleared by the next edit.</summary>
    public bool ResultIsSuccess => Result is not null && _resultSucceeded;

    bool _resultSucceeded;

    public string ApplyText =>
        IsConfirming ? $"Confirm {CautionCount} {(CautionCount == 1 ? "Change That Takes" : "Changes That Take")} Something Away"
        : Count == 1 ? "Apply 1 Change"
        : $"Apply {Count} Changes";

    public bool ApplyIsDanger => IsConfirming;

    bool CanApply => HasChanges && !IsApplying && !IsCheckingFirst && (!IsConfirming || TypedConfirms.All(t => t.Matches));

    /// <summary>Applies everything; with changes that take something away, the first click asks to confirm.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    async Task Apply()
    {
        // First: is everything still as it was when staged? (Skipped once that has been shown.)
        if (!_staleShown && await ShowChangedSinceAsync())
            return;
        if (CautionCount > 0 && !IsConfirming)
        {
            IsConfirming = true;
            return;
        }
        IsConfirming = false;
        Result = null;
        _resultSucceeded = false;
        foreach (var row in Rows)
        {
            row.State = ApplyState.Waiting;
            row.Problem = null;
        }
        ApplyTotal = Rows.Count;
        ApplyDone = 0;
        ApplyFailed = 0;
        ApplyCurrent = null;
        _applyClock.Restart();
        UpdateApplyStatus();
        IsApplying = true;
        try
        {
            var results = await _pipeline.ApplyAsync();
            _earlierReveals = Reveals.Count;
            foreach (var (change, result) in results.Where(r => r.Result.Reveal is not null))
                Reveals.Add(new RevealRow(change switch
                    {
                        CreateTemporaryAccessPass => $"Temporary Access Pass for {change.Target.Name}",
                        AddAppSecret s => $"Client Secret \"{s.Description}\" for {change.Target.Name}",
                        _ => $"New Password for {change.Target.Name}",
                    }, result.Reveal!, RevealCopied, change.Target));
            if (results.Any(r => r.Result.Reveal is not null))
            {
                AllCopied = false;
                _revealsSaved = false;
            }
            RevealsChanged();
            _problems.Clear();
            foreach (var (change, result) in results.Where(r => !r.Result.Succeeded))
                _problems[result.Remaining ?? change] = result.Detail ?? result.Outcome.ToString();
            var applied = results.Count(r => r.Result.Succeeded);
            var lagging = results.Count(r => r.Result.Outcome == ChangeOutcome.AppliedNotYetVisible);
            var failed = results.Count - applied;
            _resultSucceeded = failed == 0;
            Result = (applied, failed) switch
            {
                (_, 0) => $"{Plural(applied, "change")} applied." + (lagging > 0 ? $" {lagging} not visible yet; Microsoft 365 usually shows it within a minute." : ""),
                (0, _) => $"{Plural(failed, "change")} didn't go through; the reasons are below.",
                _ => $"{Plural(applied, "change")} applied; {failed} didn't go through (reasons below).",
            };
            Rebuild();
            if (applied > 0)
            {
                // The changes went through; only showing them failed, so say that and carry on.
                try
                {
                    await _afterApply();
                }
                catch (Exception e)
                {
                    _resultSucceeded = false;
                    Result += $" The lists couldn't be refreshed ({Friendly.Message(e)}); Refresh shows the changes.";
                }
            }
            // Nothing to read twice: a clean result goes away by itself.
            if (ResultIsSuccess && lagging == 0 && !HasReveals && !HasChanges)
                CloseAfterSuccess();
        }
        finally
        {
            IsApplying = false;
            _applyClock.Stop();
            CurrentRow = null;
            // Rows still here (didn't go through, or not yet applied) go back to plain pending rows.
            foreach (var row in Rows)
                row.State = null;
        }
    }

    bool CanDiscard => HasChanges && !IsApplying && !IsCheckingFirst;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    void DiscardAll()
    {
        _problems.Clear();
        Result = null;
        _pipeline.Clear();
    }

    public bool CanShowPowerShell => _showPowerShell is not null;

    /// <summary>Every pending change as one script, in the order they'd be applied.</summary>
    [RelayCommand]
    void ShowAllPowerShell() => _showPowerShell?.Invoke(_pipeline.Pending.ToArray());

    [RelayCommand]
    void CancelConfirm() => IsConfirming = false;

    void Rebuild()
    {
        Rows.Clear();
        foreach (var change in _pipeline.Pending)
            Rows.Add(new PendingRow(change, c => _pipeline.Unstage(c), _showPowerShell is null ? null : c => _showPowerShell([c]))
            {
                Problem = _problems.GetValueOrDefault(change),
            });
        if (Rows.Count == 0)
            IsOpen = false;
        else if (!IsApplying && Result is null)
            IsOpen = true;
        IsConfirming = false;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(CanClose));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(IsTestMode));
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(TestModeStateText));
        ApplyCommand.NotifyCanExecuteChanged();
        DiscardAllCommand.NotifyCanExecuteChanged();
    }

    static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    // Headings are in Title Case; "and" stays lower case.
    static string Title(string words) => string.Join(' ', words.Split(' ').Select(w => w == "and" ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    // ---- Test mode ------------------------------------------------------------------------

    public bool ShowTestModeBanner { get; }

    public bool IsTestMode => _pipeline.IsTestMode;

    public bool ShowBanner => ShowTestModeBanner && IsTestMode;

    public string TestModeStateText => IsTestMode
        ? $"On: only objects starting {WriteGuard.Marker} can be changed. Turn it off from the banner above the list."
        : "Off: real users and groups can be changed in this tenant.";

    public string TestModeText => $"Test mode: only objects whose sign-in name or address starts with {WriteGuard.Marker} can be changed in {_tenantName}.";

    public string TurnOffPrompt => $"To allow changes to real users and groups in this tenant, type {_tenantName} and confirm.";

    [ObservableProperty]
    public partial bool IsTurningOff { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TurnOffTestModeCommand))]
    public partial string TurnOffText { get; set; }

    bool NameMatches => string.Equals(TurnOffText.Trim(), _tenantName, StringComparison.CurrentCultureIgnoreCase);

    [RelayCommand]
    void StartTurnOff()
    {
        TurnOffText = "";
        IsTurningOff = true;
    }

    [RelayCommand]
    void CancelTurnOff() => IsTurningOff = false;

    [RelayCommand(CanExecute = nameof(NameMatches))]
    void TurnOffTestMode()
    {
        _pipeline.SetTestMode(false, _tenantName);
        IsTurningOff = false;
        Rebuild();
    }

    [RelayCommand]
    void TurnOnTestMode()
    {
        _pipeline.SetTestMode(true, _tenantName);
        Rebuild();
    }
}
