using Avalonia.Threading;
using Tenantry.ViewModels;
using Velopack;
using Velopack.Sources;

namespace Tenantry.App.Services;

/// <summary>
/// Looks for a newer release on GitHub when the app opens and every few hours, downloads it in the
/// background, and offers a restart (<see cref="AppUpdate"/>). Only installed copies update: a
/// development build or an unpacked folder does nothing. Releases are pre-releases while the builds
/// are unsigned, so pre-releases are included.
/// </summary>
public static class UpdateChecker
{
    const string Repository = "https://github.com/levinium/Tenantry";

    static readonly TimeSpan Every = TimeSpan.FromHours(4);

    public static void Start()
    {
        UpdateManager manager;
        try
        {
            manager = new UpdateManager(new GithubSource(Repository, null, true));
            if (!manager.IsInstalled)
                return;
        }
        catch (Exception)
        {
            return;
        }
        _ = LoopAsync(manager);
    }

    static async Task LoopAsync(UpdateManager manager)
    {
        while (true)
        {
            try
            {
                if (await manager.CheckForUpdatesAsync() is { } update)
                {
                    await manager.DownloadUpdatesAsync(update);
                    var version = update.TargetFullRelease.Version.ToString();
                    Dispatcher.UIThread.Post(() => AppUpdate.Current.Ready(version, () => manager.ApplyUpdatesAndRestart(update.TargetFullRelease)));
                    return;
                }
            }
            catch (Exception)
            {
                // Offline, rate-limited or GitHub unreachable: try again later; updates never get in the way.
            }
            await Task.Delay(Every);
        }
    }
}
