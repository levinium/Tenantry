namespace Tenantry.Core.Changes;

/// <summary>What applying one change came to.</summary>
public sealed record ApplyResult(ChangeOutcome Outcome, string? Detail = null)
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
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public IReadOnlyList<ChangeOp> Pending => _pending;

    public bool IsTestMode => testMode.IsTestMode;

    public ChangeLog Log => log;

    /// <summary>Raised whenever the staged changes change.</summary>
    public event EventHandler? Changed;

    /// <summary>The staged change for a slot, if any (for showing "pending" values in place).</summary>
    public ChangeOp? PendingFor(string targetId, string key) =>
        _pending.FirstOrDefault(p => p.Target.Id == targetId && p.Key == key);

    public void Stage(ChangeOp change)
    {
        var index = _pending.FindIndex(p => p.Target.Id == change.Target.Id && p.Key == change.Key);
        if (index >= 0)
        {
            change = change.Since(_pending[index]);
            _pending.RemoveAt(index);
            if (!change.IsNoChange)
                _pending.Insert(index, change);
        }
        else if (!change.IsNoChange)
            _pending.Add(change);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Unstage(ChangeOp change)
    {
        if (_pending.Remove(change))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _pending.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies every staged change; returns each change with its result, in order.</summary>
    public async Task<IReadOnlyList<(ChangeOp Change, ApplyResult Result)>> ApplyAsync(
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var batch = _pending.ToArray();
        var results = new List<(ChangeOp, ApplyResult)>(batch.Length);
        for (var i = 0; i < batch.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = batch[i];
            ApplyResult result;
            if (WriteGuard.Refusal(change, testMode.IsTestMode) is { } refusal)
                result = new ApplyResult(ChangeOutcome.Refused, refusal);
            else
            {
                try
                {
                    result = await executor.ApplyAsync(change, cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    result = new ApplyResult(ChangeOutcome.Failed, e.Message);
                }
            }
            log.Add(new ChangeLogEntry(Guid.NewGuid().ToString("N"), _now(), account, change, result.Outcome, result.Detail));
            if (result.Succeeded)
                _pending.Remove(change);
            results.Add((change, result));
            progress?.Report((i + 1, batch.Length));
        }
        Changed?.Invoke(this, EventArgs.Empty);
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
    public override string Label => "Test mode";
    public override string Before => Old ? "On" : "Off";
    public override string After => New ? "On" : "Off";
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => this;
}
