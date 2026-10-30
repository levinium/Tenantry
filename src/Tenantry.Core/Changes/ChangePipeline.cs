namespace Tenantry.Core.Changes;

/// <summary>What applying one change came to.</summary>
/// <param name="Reveal">Something the admin must see once and that is never stored or logged
/// (a temporary password).</param>
public sealed record ApplyResult(ChangeOutcome Outcome, string? Detail = null, string? Reveal = null)
{
    public bool Succeeded => Outcome is ChangeOutcome.Applied or ChangeOutcome.AppliedNotYetVisible;
}

/// <summary>
/// Carries a change out against Microsoft 365, reads the object back to confirm it, and updates
/// the local copy. Implemented over Graph (and Exchange) for real tenants and in memory for the demo.
/// </summary>
public interface IChangeExecutor
{
    Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default);
}

/// <summary>A tenant's Test mode switch, remembered per tenant.</summary>
public interface ITestModeStore
{
    bool IsTestMode { get; }
    void Set(bool testMode);
}

public sealed class MemoryTestModeStore(bool testMode) : ITestModeStore
{
    public bool IsTestMode { get; private set; } = testMode;

    public void Set(bool testMode) => IsTestMode = testMode;
}

/// <summary>
/// The staged changes for one tenant and how they get applied: guard, execute, read back, log.
/// <list type="bullet">
/// <item>Staging the same slot again replaces the earlier change but keeps its original "before",
/// so the review always shows where things started; staging it back to that original drops it.</item>
/// <item>Applying runs changes one at a time in the order staged. Each is checked by the write
/// guard first (a refusal is logged and nothing is sent), then executed, then logged. Changes
/// that succeed leave the tray; failures stay, with the reason, to retry or discard.</item>
/// </list>
/// </summary>
public sealed class ChangePipeline(IChangeExecutor executor, ChangeLog log, ITestModeStore testMode, string account, Func<DateTimeOffset>? now = null)
{
    readonly List<ChangeOp> _pending = [];
    // Slot → staged change, so screens can ask "is anything pending here?" per row without
    // scanning the tray (bulk edits stage hundreds at once).
    readonly Dictionary<(string TargetId, string Key), ChangeOp> _bySlot = [];
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public IReadOnlyList<ChangeOp> Pending => _pending;

    public bool IsTestMode => testMode.IsTestMode;

    public ChangeLog Log => log;

    /// <summary>Raised whenever the staged changes change (once per call, however many changes it staged).</summary>
    public event EventHandler? Changed;

    /// <summary>After an apply: the changes that went through, so open pages can re-read what they show.</summary>
    public event EventHandler<IReadOnlyList<ChangeOp>>? Applied;

    /// <summary>The staged change for a slot, if any (for showing "pending" values in place).</summary>
    public ChangeOp? PendingFor(string targetId, string key) => _bySlot.GetValueOrDefault((targetId, key));

    public void Stage(ChangeOp change)
    {
        Put(change);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stages many changes (a bulk edit) and tells listeners once.</summary>
    public void StageAll(IEnumerable<ChangeOp> changes)
    {
        foreach (var change in changes)
            Put(change);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Put(ChangeOp change)
    {
        var slot = (change.Target.Id, change.Key);
        if (_bySlot.TryGetValue(slot, out var earlier))
        {
            var index = _pending.IndexOf(earlier);
            change = change.Since(earlier);
            _pending.RemoveAt(index);
            _bySlot.Remove(slot);
            if (!change.IsNoChange)
            {
                _pending.Insert(index, change);
                _bySlot[slot] = change;
            }
        }
        else if (!change.IsNoChange)
        {
            _pending.Add(change);
            _bySlot[slot] = change;
        }
    }

    public void Unstage(ChangeOp change)
    {
        if (!_pending.Remove(change))
            return;
        _bySlot.Remove((change.Target.Id, change.Key));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts slots back as they were (an edit cancelled after a pause had staged it): each slot's
    /// earlier change is restored, or its change removed when there wasn't one. Tells listeners once.
    /// </summary>
    public void Restore(IEnumerable<(string TargetId, string Key, ChangeOp? Earlier)> slots)
    {
        var changed = false;
        foreach (var (targetId, key, earlier) in slots)
        {
            var now = _bySlot.GetValueOrDefault((targetId, key));
            if (ReferenceEquals(now, earlier))
                continue;
            changed = true;
            if (now is not null)
            {
                _pending.Remove(now);
                _bySlot.Remove((targetId, key));
            }
            if (earlier is not null)
                Put(earlier);
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Logs something done outside Apply, such as revealing a secret, so the change log shows who
    /// saw what and when. Nothing is staged or sent.
    /// </summary>
    public void Record(ChangeOp happened, string? detail = null, ChangeOutcome outcome = ChangeOutcome.Applied)
    {
        log.Add(new ChangeLogEntry(Guid.NewGuid().ToString("N"), _now(), account, happened, outcome, detail));
        Recorded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Something was logged outside Apply (<see cref="Record"/>), so the change log can show it.</summary>
    public event EventHandler? Recorded;

    /// <summary>The tenant itself as a change target (for Test mode, console commands).</summary>
    public static ChangeTarget TenantTarget(string tenantName) => new("tenant", "tenant", tenantName, null);

    public void Clear()
    {
        _pending.Clear();
        _bySlot.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies every staged change; returns each change with its result, in order.</summary>
    public async Task<IReadOnlyList<(ChangeOp Change, ApplyResult Result)>> ApplyAsync(
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var batch = _pending.ToArray();
        var results = new List<(ChangeOp, ApplyResult)>(batch.Length);
        var succeeded = new HashSet<ChangeOp>(ReferenceEqualityComparer.Instance);
        // Slots whose change in this batch didn't go through, for changes that depend on them.
        var notApplied = new Dictionary<(string, string), ChangeOp>();
        for (var i = 0; i < batch.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = batch[i];
            ApplyResult result;
            if (change.DependsOn is { } needed && notApplied.TryGetValue((change.Target.Id, needed), out var blocker))
                result = new ApplyResult(ChangeOutcome.Failed, $"Not applied because \"{blocker.Label}\" didn't go through; it needs that first.");
            else if (WriteGuard.Refusal(change, testMode.IsTestMode) is { } refusal)
                result = new ApplyResult(ChangeOutcome.Refused, refusal);
            else
            {
                try
                {
                    result = await executor.ApplyAsync(change, cancellationToken);
                }
                // A request that timed out also surfaces as a cancellation; only the caller's own cancel stops the batch.
                catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    result = new ApplyResult(ChangeOutcome.Failed, e is OperationCanceledException ? "Microsoft didn't answer in time. Check the result, then try again." : e.Message);
                }
            }
            log.Add(new ChangeLogEntry(Guid.NewGuid().ToString("N"), _now(), account, change, result.Outcome, result.Detail));
            if (result.Succeeded)
                succeeded.Add(change);
            else
                notApplied[(change.Target.Id, change.Key)] = change;
            results.Add((change, result));
            progress?.Report((i + 1, batch.Length));
        }
        // Successes leave the tray in one pass (a bulk apply can be hundreds of changes).
        _pending.RemoveAll(succeeded.Contains);
        foreach (var change in succeeded)
        {
            var slot = (change.Target.Id, change.Key);
            if (ReferenceEquals(_bySlot.GetValueOrDefault(slot), change))
                _bySlot.Remove(slot);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (succeeded.Count > 0)
            Applied?.Invoke(this, batch.Where(succeeded.Contains).ToArray());
        return results;
    }

    /// <summary>
    /// Turns Test mode on or off for this tenant and logs who did it. Turning it off is the
    /// person's decision: the UI asks them to type the tenant's name first.
    /// </summary>
    public void SetTestMode(bool on, string tenantName)
    {
        if (testMode.IsTestMode == on)
            return;
        testMode.Set(on);
        var target = new ChangeTarget("tenant", "tenant", tenantName, null);
        log.Add(new ChangeLogEntry(Guid.NewGuid().ToString("N"), _now(), account, new SetTestModeChange(target, !on, on), ChangeOutcome.Applied, null));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A Test mode switch, recorded in the change log (never staged).</summary>
public sealed record SetTestModeChange(ChangeTarget Tenant, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => Tenant;
    public override string Key => "testmode";
    public override string Label => "Test Mode";
    public override string Before => Old ? "On" : "Off";
    public override string After => New ? "On" : "Off";
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => this;
}
