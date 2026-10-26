using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Tenantry.ViewModels;

/// <summary>
/// A newer Tenantry, downloaded in the background and ready to install: the sidebar offers to
/// restart into it. Set by the app's update check; stays empty when the app wasn't installed by
/// its installer (a development build), so nothing shows.
/// </summary>
public sealed partial class AppUpdate : ObservableObject
{
    public static AppUpdate Current { get; } = new();

    Action? _restart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(ReadyText))]
    public partial string? ReadyVersion { get; private set; }

    public bool IsReady => ReadyVersion is not null;

    public string ReadyText => $"Update to {ReadyVersion}";

    /// <summary>Called when a new version is downloaded; <paramref name="restart"/> installs it and reopens Tenantry.</summary>
    public void Ready(string version, Action restart)
    {
        _restart = restart;
        ReadyVersion = version;
    }

    [RelayCommand]
    void Restart() => _restart?.Invoke();
}
