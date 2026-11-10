using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's group lifecycle: Microsoft 365 groups expire a year after they were made or last
/// renewed, the first department security group gives its members a license, and distribution lists
/// with only people in them can be upgraded.
/// </summary>
public sealed partial class DemoUserRecords : IGroupLifecycleRecords
{
    readonly Dictionary<string, DateTimeOffset> _renewed = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<string>> _groupLicenses = new(StringComparer.Ordinal);

    public async Task<GroupLifecycle> GetGroupLifecycleAsync(GroupSummary group, CancellationToken cancellationToken = default)
    {
        var catalog = await GetLicenseCatalogAsync(cancellationToken);
        if (!_groupLicenses.ContainsKey(group.Id))
        {
            var groups = await directory.GetGroupsAsync(cancellationToken);
            var licensed = groups.FirstOrDefault(g => g.Kind == GroupKind.Security && !g.IsDynamic && g.DisplayName.StartsWith("SG ", StringComparison.Ordinal));
            _groupLicenses[group.Id] = group.Id == licensed?.Id && catalog.Count > 0 ? [catalog[0].SkuId] : [];
        }
        var unified = group.Kind == GroupKind.Microsoft365;
        var renewed = _renewed.TryGetValue(group.Id, out var r) ? r : group.CreatedAt ?? DateTimeOffset.UtcNow.AddDays(-100);
        // An expiry that has passed in the demo is moved on a year at a time, as Microsoft auto-renews active groups.
        var expires = renewed.AddDays(365);
        while (expires < DateTimeOffset.UtcNow)
            expires = expires.AddDays(365);
        var names = catalog.ToDictionary(s => s.SkuId, s => s.Name, StringComparer.OrdinalIgnoreCase);
        var licenses = _groupLicenses[group.Id].Select(id => new GroupLicense(id, names.GetValueOrDefault(id, id), [])).ToArray();
        var canUpgrade = group.Kind == GroupKind.Distribution && group.Members.Count > 0 && group.Members.All(m => m.Type == "user") && group.OwnerList.Count > 0;
        return new GroupLifecycle(unified ? expires : null, unified ? renewed : null, unified ? 365 : null, null, licenses, catalog,
            licenses.Length > 0 ? "ProcessingComplete" : null, licenses.Length > 0 ? 0 : null,
            canUpgrade, group.Kind != GroupKind.Distribution || canUpgrade ? null
                : "Microsoft won't upgrade this list. Usual reasons: it has no owner or no members, it's inside another list or has lists inside it, a member isn't a mailbox, or another list's sender rules name it.");
    }

    /// <summary>Applies a lifecycle change; null when it isn't one.</summary>
    public async Task<ApplyResult?> ApplyLifecycleAsync(ChangeOp change, CancellationToken cancellationToken)
    {
        switch (change)
        {
            case RenewGroup r:
                _renewed[r.Group.Id] = DateTimeOffset.UtcNow;
                return new ApplyResult(ChangeOutcome.Applied);
            case SetGroupLicense l:
                var list = _groupLicenses.TryGetValue(l.Group.Id, out var existing) ? existing : _groupLicenses[l.Group.Id] = [];
                list.Remove(l.SkuId);
                if (l.New)
                    list.Add(l.SkuId);
                return new ApplyResult(ChangeOutcome.Applied);
            case UpgradeList u:
                directory.UpdateGroup(u.Group.Id, g => g with { Kind = GroupKind.Microsoft365, Visibility = "Private" });
                await Task.CompletedTask;
                return new ApplyResult(ChangeOutcome.Applied, "Upgraded (demo).");
            default:
                return null;
        }
    }
}
