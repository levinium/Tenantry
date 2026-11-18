using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Tenantry.ViewModels;

/// <summary>
/// Tenantry's own updates: which version this is, what the last check found, and a newer
/// version downloaded in the background and ready to install (the sidebar offers to restart into
/// it). Set by the app's update check. A copy not installed by its installer (a development build,
/// an unpacked folder) never updates, and Settings says so.
/// </summary>
public sealed partial class AppUpdate : ObservableObject
{
    public static AppUpdate Current { get; } = new();

    /// <summary>Where releases are published, for a copy that can't update itself.</summary>
    public const string ReleasesPage = "https://github.com/levinium/Tenantry/releases";

    Action? _restart;
    Func<Task>? _check;

    /// <summary>The version running now, like "0.2.1".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionText))]
    public partial string Version { get; set; } = "";

    public string VersionText => Version.Length == 0 ? "Tenantry" : $"Tenantry {Version}";

    /// <summary>Installed by Tenantry's installer, so it can update itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CannotUpdate))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial bool CanUpdate { get; private set; }

    public bool CannotUpdate => !CanUpdate;

    public string CannotUpdateText =>
        "This copy wasn't installed with Tenantry's installer, so it can't update itself. Get the latest version from the releases page.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; private set; }

    public bool HasStatus => Status is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial bool IsChecking { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(ReadyText))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial string? ReadyVersion { get; private set; }

    public bool IsReady => ReadyVersion is not null;

    public string ReadyText => $"Update to {ReadyVersion}";

    /// <summary>The update check is running: <paramref name="check"/> looks now, whenever asked.</summary>
    public void Enable(Func<Task> check)
    {
        _check = check;
        CanUpdate = true;
    }

    /// <summary>Called as a check starts, finds nothing, downloads or fails.</summary>
    public void Checking() => (IsChecking, Status) = (true, "Checking for a newer version…");

    public void Downloading(string version) => Status = $"Downloading Tenantry {version}…";

    public void UpToDate(DateTimeOffset when) =>
        (IsChecking, Status) = (false, $"You have the latest version (checked {when.ToLocalTime():h:mm tt}).");

    public void Failed(DateTimeOffset when) =>
        (IsChecking, Status) = (false, $"Couldn't reach GitHub to check ({when.ToLocalTime():h:mm tt}). Tenantry tries again on its own later.");

    /// <summary>Called when a new version is downloaded; <paramref name="restart"/> installs it and reopens Tenantry.</summary>
    public void Ready(string version, Action restart)
    {
        _restart = restart;
        ReadyVersion = version;
        IsChecking = false;
        Status = $"Tenantry {version} is downloaded. Restart to install it.";
    }

    bool CanCheck => CanUpdate && !IsChecking && !IsReady;

    /// <summary>Settings' Check for Updates: looks now rather than waiting for the next timed check.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    Task Check() => _check?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    void Restart() => _restart?.Invoke();

    [RelayCommand]
    void OpenReleases() => Platform.OpenUrl(ReleasesPage);
}
