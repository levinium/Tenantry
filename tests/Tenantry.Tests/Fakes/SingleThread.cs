using System.Collections.Concurrent;

namespace Tenantry.Tests.Fakes;

/// <summary>
/// Runs a test body on one thread with its own synchronization context, as the app's UI thread
/// is: continuations and posted work (a background read finishing) queue up instead of running
/// on the thread pool at the same time as the test, which only happens in tests.
/// </summary>
static class SingleThread
{
    public static void Run(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        var context = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = body();
            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.RunUntilComplete();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    sealed class QueueContext : SynchronizationContext
    {
        readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void Complete() => _queue.CompleteAdding();

        public void RunUntilComplete()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                callback(state);
        }
    }
}
