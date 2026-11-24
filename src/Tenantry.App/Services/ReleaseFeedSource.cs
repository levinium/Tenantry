using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Tenantry.App.Services;

/// <summary>
/// Where updates come from: the list of releases is read from the repository's <c>updates</c>
/// branch, and each package is downloaded from the GitHub release it belongs to (tag v{version}).
/// Reading the list from the branch keeps update checks out of the releases' download counts and
/// away from GitHub's API limit (60 calls an hour per address); only real updates count. The
/// Packages workflow copies each release's list to the branch.
/// </summary>
sealed class ReleaseFeedSource(string repository, string feedUrl) : IUpdateSource
{
    readonly SimpleWebSource _feed = new(feedUrl);
    readonly HttpClientFileDownloader _downloader = new();

    public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null) =>
        _feed.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);

    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken = default) =>
        _downloader.DownloadFile(PackageUrl(repository, releaseEntry), localFile, progress, null, _feed.Timeout, cancelToken);

    /// <summary>https://github.com/{owner}/{repo}/releases/download/v{version}/{file}</summary>
    public static string PackageUrl(string repository, VelopackAsset asset) =>
        $"{repository.TrimEnd('/')}/releases/download/v{asset.Version}/{Uri.EscapeDataString(asset.FileName)}";
}
