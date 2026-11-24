using System.Reflection;
using Avalonia.Threading;
using Tenantry.ViewModels;
using Velopack;
using Velopack.Sources;

namespace Tenantry.App.Services;

/// <summary>
/// Looks for a newer release when the app opens, every few hours, and whenever Settings'
/// Check for Updates is clicked; downloads it in the background and offers a restart
/// (<see cref="AppUpdate"/>). Only installed copies update: a development build or an unpacked
/// folder does nothing.
/// </summary>
public static class UpdateChecker
{
    const string Repository = "https://github.com/levinium/Tenantry";

    /// <summary>The release lists, copied here by the Packages workflow (see <see cref="ReleaseFeedSource"/>).</summary>
    const string Feed = "https://raw.githubusercontent.com/levinium/Tenantry/updates/";

    static readonly TimeSpan Every = TimeSpan.FromHours(4);

    static UpdateManager? _manager;

    /// <summary>One check at a time: the timer and the button share it.</summary>
    static Task? _running;

    public static void Start()
    {
        try
        {
            var manager = new UpdateManager(new ReleaseFeedSource(Repository, Feed));
            if (!manager.IsInstalled)
                return;
            _manager = manager;
        }
        catch (Exception)
        {
            return;
        }
        AppUpdate.Current.Enable(CheckNowAsync);
        _ = LoopAsync();
    }

    /// <summary>The version this build was given (a release's tag), without the commit suffix.</summary>
    public static string RunningVersion()
    {
        var version = typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    static async Task LoopAsync()
    {
        while (!AppUpdate.Current.IsReady)
        {
            await CheckNowAsync();
            await Task.Delay(Every);
        }
    }

    /// <summary>Checks now, or waits for the check already running.</summary>
    static Task CheckNowAsync()
    {
        if (_running is { IsCompleted: false })
            return _running;
        return _running = CheckAsync();
    }

    static async Task CheckAsync()
    {
        if (_manager is not { } manager || AppUpdate.Current.IsReady)
            return;
        Dispatcher.UIThread.Post(AppUpdate.Current.Checking);
        try
        {
            if (await manager.CheckForUpdatesAsync() is not { } update)
            {
                Dispatcher.UIThread.Post(() => AppUpdate.Current.UpToDate(DateTimeOffset.Now));
                return;
            }
            var version = update.TargetFullRelease.Version.ToString();
            Dispatcher.UIThread.Post(() => AppUpdate.Current.Downloading(version));
            await manager.DownloadUpdatesAsync(update);
            Dispatcher.UIThread.Post(() => AppUpdate.Current.Ready(version, () => manager.ApplyUpdatesAndRestart(update.TargetFullRelease)));
        }
        catch (Exception)
        {
            // Offline, rate-limited or GitHub unreachable: say so, and the timer tries again later.
            Dispatcher.UIThread.Post(() => AppUpdate.Current.Failed(DateTimeOffset.Now));
        }
    }
}
