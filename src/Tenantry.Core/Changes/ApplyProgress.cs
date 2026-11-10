namespace Tenantry.Core.Changes;

/// <summary>
/// How far a long change has got while it applies ("Moving rule 2 of 5: …"), for Pending Changes to
/// show. The pipeline listens around each change it sends; an executor step reports with
/// <see cref="Report"/>. It flows with the async call, so nothing has to pass it along, and a report
/// with no one listening does nothing.
/// </summary>
public static class ApplyProgress
{
    static readonly AsyncLocal<Action<string>?> Listener = new();

    /// <summary>Tells whoever is listening for the change being applied how far it has got.</summary>
    public static void Report(string text) => Listener.Value?.Invoke(text);

    /// <summary>Listens for reports from the work started inside the returned scope.</summary>
    public static IDisposable Listen(Action<string> listener)
    {
        var previous = Listener.Value;
        Listener.Value = listener;
        return new Scope(() => Listener.Value = previous);
    }

    sealed class Scope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}
