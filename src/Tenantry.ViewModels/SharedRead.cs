namespace Tenantry.ViewModels;

/// <summary>
/// One read shared by every tab of a page that asks for it, until <see cref="Reset"/>. A read that
/// fails is forgotten, so the next ask tries again. The read itself isn't tied to whoever asked
/// first: a tab that stops waiting (closed, or the token cancelled) doesn't cancel it for the others.
/// </summary>
public sealed class SharedRead<T>
{
    Task<T>? _task;

    public async Task<T> GetAsync(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        var task = _task ??= read(CancellationToken.None);
        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch when (task.IsFaulted || task.IsCanceled)
        {
            if (ReferenceEquals(_task, task))
                _task = null;
            throw;
        }
    }

    /// <summary>The answer when the read has finished well; false while it runs, failed, or hasn't started.</summary>
    public bool TryGetDone(out T value)
    {
        var done = _task is { IsCompletedSuccessfully: true } task;
        value = done ? _task!.Result : default!;
        return done;
    }

    /// <summary>Forgets the read, so the next ask reads afresh (the object changed, or it's being followed).</summary>
    public void Reset() => _task = null;
}
