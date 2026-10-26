using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.Workflows;

static class Throttled
{
    /// <summary>
    /// Reads something for each person, a few at a time. Continuations stay on the caller's
    /// context (the UI thread), so <paramref name="progress"/> can update bound properties.
    /// </summary>
    public static async Task<Dictionary<string, T>> ReadAllAsync<T>(IReadOnlyList<UserSummary> people, int atOnce,
        Func<UserSummary, CancellationToken, Task<T>> read, Action<int> progress, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(atOnce);
        var results = new Dictionary<string, T>(StringComparer.Ordinal);
        var done = 0;
        await Task.WhenAll(people.Select(async p =>
        {
            await gate.WaitAsync(ct);
            try
            {
                results[p.Id] = await read(p, ct);
            }
            finally
            {
                gate.Release();
            }
            progress(++done);
        }));
        return results;
    }
}
