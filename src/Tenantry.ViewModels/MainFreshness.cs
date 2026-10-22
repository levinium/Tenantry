using System.ComponentModel;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>
/// Opening a record checks it against Microsoft 365 behind the page: the saved copy shows at
/// once, this re-reads the one object (Graph, and Exchange for a mailbox) into the local copy, and
/// anything that changed updates in place. A preview is checked once it has stayed on screen for a
/// moment, so arrowing down a list costs nothing; an object checked a minute ago isn't re-read
/// unless asked (the refresh button).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>A record checked this recently isn't read again just for being reopened.</summary>
    public static TimeSpan RecheckAfter { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a preview must stay before it's checked.</summary>
    public static TimeSpan PreviewSettle { get; set; } = TimeSpan.FromMilliseconds(500);

    readonly Dictionary<string, DateTimeOffset> _lastChecked = new(StringComparer.Ordinal);
    readonly HashSet<string> _checkingRecords = new(StringComparer.Ordinal);

    IRecordRefresher? Refresher => _tenant.Sync as IRecordRefresher ?? _tenant.Directory as IRecordRefresher;

    /// <summary>Previews are checked when they settle; called once from the constructor.</summary>
    void CheckPreviewsWhenTheySettle()
    {
        foreach (var list in new[] { Users, Groups })
            list.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DataListViewModel.PreviewModel))
                    _ = CheckPreviewAsync(list);
            };
    }

    async Task CheckPreviewAsync(DataListViewModel list)
    {
        if (list.PreviewModel is not { } shown || Refresher is null)
            return;
        await Task.Delay(PreviewSettle);
        if (ReferenceEquals(list.PreviewModel, shown))
            await CheckAsync(shown, force: false);
    }

    /// <summary>Wires a page's refresh button; every user and group page is made through here.</summary>
    T Fresh<T>(T page) where T : class
    {
        if (Refresher is not null && FreshnessOf(page) is { } freshness)
            freshness.CheckNow = () => CheckAsync(page, force: true);
        return page;
    }

    static RecordFreshness? FreshnessOf(object page) => page switch
    {
        UserDetailViewModel u => u.Freshness,
        GroupDetailViewModel g => g.Freshness,
        _ => null,
    };

    /// <summary>Checks a user or group page against Microsoft 365 (see the class summary).</summary>
    public Task CheckAsync(object page, bool force) => page switch
    {
        UserDetailViewModel user => CheckAsync(user.User.Id, user.Freshness, force,
            ct => Refresher!.RefreshUserAsync(user.User.Id, ct),
            async ct => await _tenant.Directory.GetUserAsync(user.User.Id, ct) is { } fresh ? RecordChanges.Between(user.User, fresh) : null),
        GroupDetailViewModel group => CheckAsync(group.Group.Id, group.Freshness, force,
            ct => Refresher!.RefreshGroupAsync(group.Group.Id, ct),
            async ct => await _tenant.Directory.GetGroupAsync(group.Group.Id, ct) is { } fresh ? RecordChanges.Between(group.Group, fresh) : null),
        _ => Task.CompletedTask,
    };

    /// <param name="compare">What changed, comparing the page's copy with the refreshed local copy; null when it's gone.</param>
    async Task CheckAsync(string id, RecordFreshness freshness, bool force, Func<CancellationToken, Task> refresh,
        Func<CancellationToken, Task<IReadOnlyList<FieldChange>?>> compare)
    {
        if (Refresher is null)
            return;
        if (!force && _lastChecked.TryGetValue(id, out var at) && _now() - at < RecheckAfter)
        {
            if (freshness.State == FreshnessState.None)
                freshness.UpToDate(at);
            return;
        }
        if (!_checkingRecords.Add(id))
            return;
        freshness.Start();
        try
        {
            await refresh(_lifetime.Token);
            var changes = await compare(_lifetime.Token);
            _lastChecked[id] = _now();
            if (changes is null)
            {
                freshness.Failed("it no longer exists in Microsoft 365.");
                await FollowLocalCopyAsync();
            }
            else if (changes.Count == 0)
                freshness.UpToDate(_now());
            else
            {
                // Lists and every open copy of this record re-read the local copy, then the page names what moved.
                await FollowLocalCopyAsync();
                freshness.Updated(changes, _now());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            freshness.Failed(Friendly.Message(e));
        }
        finally
        {
            _checkingRecords.Remove(id);
        }
    }
}
