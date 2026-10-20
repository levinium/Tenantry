using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Permissions;

namespace Tenantry.ViewModels;

public enum SetupStep
{
    Choose,
    Automatic,
    Manual,
    Done,
}

/// <summary>
/// Adds a tenant: either Tenantry creates its own app registration (automatic) or the admin
/// creates it with a generated script or the portal and pastes the IDs (manual).
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    readonly ITenantConnector _connector;
    readonly Action _finished;
    readonly Action _cancelled;

    public SetupWizardViewModel(ITenantConnector connector, Action finished, Action cancelled)
    {
        _connector = connector;
        _finished = finished;
        _cancelled = cancelled;
        Step = SetupStep.Choose;
        TenantIdInput = "";
        ClientIdInput = "";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoose), nameof(IsAutomatic), nameof(IsManual), nameof(IsDone))]
    public partial SetupStep Step { get; private set; }

    public bool IsChoose => Step == SetupStep.Choose;
    public bool IsAutomatic => Step == SetupStep.Automatic;
    public bool IsManual => Step == SetupStep.Manual;
    public bool IsDone => Step == SetupStep.Done;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunAutomaticCommand), nameof(VerifyManualCommand), nameof(BackCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public ObservableCollection<string> Progress { get; } = [];

    public ObservableCollection<string> Unresolved { get; } = [];

    public bool HasUnresolved => Unresolved.Count > 0;

    [ObservableProperty]
    public partial string? DoneMessage { get; private set; }

    /// <summary>What the automatic setup will change, shown before anything runs.</summary>
    public IReadOnlyList<string> AutomaticChanges { get; } =
    [
        "Creates an app registration named \"Tenantry\" in your tenant (or updates it if one exists).",
        "Grants it tenant-wide consent to delegated permissions. Delegated means Tenantry can never do more than the signed-in admin's own roles allow.",
        "Requires assignment, so only admins you assign can sign in to it, and assigns you.",
        "Needs the Cloud Application Administrator role or higher. Grants made this way take effect immediately and skip any admin-consent workflow.",
    ];

    public string ManualScript { get; } = ManualSetupScript.Generate();

    public string PortalUrl => ManualSetupScript.PortalAppRegistrations;

    public IReadOnlyList<string> PermissionSummary { get; } = TenantryPermissions.All
        .Select(a => $"{a.ResourceName}: {string.Join(", ", a.DelegatedScopes)}")
        .ToArray();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyManualCommand))]
    public partial string TenantIdInput { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyManualCommand))]
    public partial string ClientIdInput { get; set; }

    bool NotBusy => !IsBusy;

    [RelayCommand]
    void ChooseAutomatic()
    {
        Error = null;
        Step = SetupStep.Automatic;
    }

    [RelayCommand]
    void ChooseManual()
    {
        Error = null;
        Step = SetupStep.Manual;
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    void Back()
    {
        Error = null;
        Step = SetupStep.Choose;
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    void Cancel() => _cancelled();

    [RelayCommand]
    void Finish() => _finished();

    CancellationTokenSource? _run;

    [RelayCommand]
    void CancelRun() => _run?.Cancel();

    [RelayCommand(CanExecute = nameof(NotBusy))]
    async Task RunAutomaticAsync()
    {
        IsBusy = true;
        Error = null;
        Progress.Clear();
        Unresolved.Clear();
        Progress.Add("Signing in… finish in your browser.");
        _run = new CancellationTokenSource();
        try
        {
            var outcome = await _connector.SetUpAutomaticallyAsync(new InlineProgress(Progress.Add), _run.Token);
            foreach (var u in outcome.Unresolved)
                Unresolved.Add(u);
            OnPropertyChanged(nameof(HasUnresolved));
            DoneMessage = $"{outcome.Profile.DisplayName} is ready. The app registration was {(outcome.Reused ? "updated" : "created")}.";
            Step = SetupStep.Done;
        }
        catch (Exception e)
        {
            Error = Friendly.Message(e);
        }
        finally
        {
            _run.Dispose();
            _run = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanVerifyManual))]
    async Task VerifyManualAsync()
    {
        IsBusy = true;
        Error = null;
        _run = new CancellationTokenSource();
        try
        {
            var profile = await _connector.AddManualAsync(TenantIdInput.Trim(), ClientIdInput.Trim(), _run.Token);
            DoneMessage = $"{profile.DisplayName} is ready. Sign-in with your app registration works.";
            Step = SetupStep.Done;
        }
        catch (Exception e)
        {
            Error = Friendly.Message(e);
        }
        finally
        {
            _run.Dispose();
            _run = null;
            IsBusy = false;
        }
    }

    bool CanVerifyManual() =>
        !IsBusy && Guid.TryParse(TenantIdInput.Trim(), out _) && Guid.TryParse(ClientIdInput.Trim(), out _);

    /// <summary>
    /// Reports progress on the caller's thread immediately. Progress&lt;T&gt; would post to a
    /// captured context, which reorders lines in tests and adds nothing in the app, where
    /// awaits already resume on the UI thread.
    /// </summary>
    sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
