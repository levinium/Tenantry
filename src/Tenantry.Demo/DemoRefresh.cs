using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo "checks" a record the way a real tenant would, taking a moment, but its generated
/// people never change behind it, so a check always ends "Up to date".
/// </summary>
public sealed partial class DemoTenantDirectory : IRecordRefresher
{
    /// <summary>How long a demo check takes (zero for screenshots and tests).</summary>
    public TimeSpan CheckTakes { get; set; } = TimeSpan.FromMilliseconds(600);

    public Task RefreshUserAsync(string userId, CancellationToken cancellationToken = default) => Task.Delay(CheckTakes, cancellationToken);

    public Task RefreshGroupAsync(string groupId, CancellationToken cancellationToken = default) => Task.Delay(CheckTakes, cancellationToken);
}
