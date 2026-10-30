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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    [RelayCommand]
    void Remove() => remove(Change);
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
/// kept only until dismissed, never stored or logged.
/// </summary>
/// <param name="person">Who it's for, and their sign-in name (for Copy all and the CSV).</param>
public sealed partial class RevealRow(string label, string secret, Action<RevealRow> dismiss, ChangeTarget? person = null) : ObservableObject
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
    }

    [RelayCommand]
    void Dismiss() => dismiss(this);
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
    public PendingChangesViewModel(ChangePipeline pipeline, string tenantName, bool showTestMode, Func<Task> afterApply,
        Action<IReadOnlyList<ChangeOp>>? showPowerShell = null)
    {
        _pipeline = pipeline;
        _tenantName = tenantName;
        _afterApply = afterApply;
        _showPowerShell = showPowerShell;
        ShowTestModeBanner = showTestMode;
        TurnOffText = "";
        _pipeline.Changed += (_, _) => Rebuild();
        Rebuild();
    }

    public ObservableCollection<PendingRow> Rows { get; } = [];

    /// <summary>Temporary passwords from the last apply, shown once until dismissed.</summary>
    public ObservableCollection<RevealRow> Reveals { get; } = [];

    public bool HasReveals => Reveals.Count > 0;

    /// <summary>Two or more passwords (a bulk reset): copy or save them all at once.</summary>
    public bool HasManyReveals => Reveals.Count > 1;

    public string RevealsHeader => $"{Reveals.Count} new {(Reveals.All(r => r.Label.StartsWith("Client Secret", StringComparison.Ordinal)) ? "client secrets" : "passwords and codes")}. "
        + "Shown once and never stored: copy or save them now, then pass each on securely.";

    [ObservableProperty]
    public partial bool AllCopied { get; private set; }

    /// <summary>One line per person: name, sign-in name and password, separated by tabs (pastes into a spreadsheet).</summary>
    [RelayCommand]
    void CopyAllReveals()
    {
        Platform.CopyText(string.Join(Environment.NewLine, Reveals.Select(r => string.Join('\t', r.Person?.Name ?? r.Label, r.Person?.Address ?? "", r.Secret))));
        AllCopied = true;
    }

    [RelayCommand]
    async Task SaveReveals()
    {
        var csv = new System.Text.StringBuilder("Name,Sign-in name,Password\r\n");
        foreach (var r in Reveals)
            csv.Append(Csv(r.Person?.Name ?? r.Label)).Append(',').Append(Csv(r.Person?.Address ?? "")).Append(',').Append(Csv(r.Secret)).Append("\r\n");
        try
        {
            await Platform.SaveCsv($"new-passwords-{DateTime.Now:yyyy-MM-dd-HHmm}.csv", csv.ToString());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The passwords stay on screen, so nothing is lost: copy them instead, or save elsewhere.
            Result = Records.ImportUsersViewModel.FileError("save the file", e);
        }
    }

    // Always quoted, never altered: a password must arrive exactly as set.
    static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    [RelayCommand]
    void DismissAllReveals()
    {
        Reveals.Clear();
        AllCopied = false;
        RevealsChanged();
    }

    void RevealsChanged()
    {
        OnPropertyChanged(nameof(HasReveals));
        OnPropertyChanged(nameof(HasManyReveals));
        OnPropertyChanged(nameof(RevealsHeader));
        OnPropertyChanged(nameof(IsVisible));
    }

    public int Count => Rows.Count;

    public bool HasChanges => Count > 0;

    public bool HasResult => Result is not null;

    /// <summary>The tray shows while there are changes, a result to read, or a password to pass on.</summary>
    public bool IsVisible => HasChanges || HasResult || HasReveals;

    void DismissReveal(RevealRow row)
    {
        Reveals.Remove(row);
        RevealsChanged();
    }

    public string HeaderText => Count == 0 ? "Pending Changes" : $"Pending Changes ({Count})";

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
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(IsVisible))]
    public partial string? Result { get; private set; }

    public string ApplyText =>
        IsConfirming ? $"Confirm: {CautionCount} {(CautionCount == 1 ? "Change Takes" : "Changes Take")} Something Away"
        : Count == 1 ? "Apply 1 Change"
        : $"Apply {Count} Changes";

    public bool ApplyIsDanger => IsConfirming;

    bool CanApply => HasChanges && !IsApplying && (!IsConfirming || TypedConfirms.All(t => t.Matches));

    /// <summary>Applies everything; with changes that take something away, the first click asks to confirm.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    async Task Apply()
    {
        if (CautionCount > 0 && !IsConfirming)
        {
            IsConfirming = true;
            return;
        }
        IsConfirming = false;
        IsApplying = true;
        Result = null;
        try
        {
            var results = await _pipeline.ApplyAsync();
            foreach (var (change, result) in results.Where(r => r.Result.Reveal is not null))
                Reveals.Add(new RevealRow(change switch
                    {
                        CreateTemporaryAccessPass => $"Temporary Access Pass for {change.Target.Name}",
                        AddAppSecret s => $"Client Secret \"{s.Description}\" for {change.Target.Name}",
                        _ => $"New Password for {change.Target.Name}",
                    }, result.Reveal!, DismissReveal, change.Target));
            AllCopied = false;
            RevealsChanged();
            _problems.Clear();
            foreach (var (change, result) in results.Where(r => !r.Result.Succeeded))
                _problems[change] = result.Detail ?? result.Outcome.ToString();
            var applied = results.Count(r => r.Result.Succeeded);
            var lagging = results.Count(r => r.Result.Outcome == ChangeOutcome.AppliedNotYetVisible);
            var failed = results.Count - applied;
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
                    Result += $" The lists couldn't be refreshed ({Friendly.Message(e)}); Refresh shows the changes.";
                }
            }
        }
        finally
        {
            IsApplying = false;
        }
    }

    bool CanDiscard => HasChanges && !IsApplying;

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

    [RelayCommand]
    void DismissResult() => Result = null;

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
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(IsTestMode));
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(TestModeStateText));
        ApplyCommand.NotifyCanExecuteChanged();
        DiscardAllCommand.NotifyCanExecuteChanged();
    }

    static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

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
