using Tenantry.ViewModels;

namespace Tenantry.Tests;

public sealed class SharedReadTests
{
    [Fact]
    public async Task One_read_serves_every_tab_and_one_tab_giving_up_doesnt_end_it_for_the_others()
    {
        var reads = 0;
        var answer = new TaskCompletionSource<string>();
        var shared = new SharedRead<string>();
        Task<string> Read(CancellationToken _)
        {
            reads++;
            return answer.Task;
        }

        using var first = new CancellationTokenSource();
        var a = shared.GetAsync(Read, first.Token);
        var b = shared.GetAsync(Read, CancellationToken.None);
        first.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);

        answer.SetResult("detail");
        Assert.Equal("detail", await b);
        Assert.Equal(1, reads);
        Assert.True(shared.TryGetDone(out var done));
        Assert.Equal("detail", done);
    }

    [Fact]
    public async Task A_failed_read_is_tried_again_and_Reset_reads_afresh()
    {
        var reads = 0;
        var shared = new SharedRead<int>();
        Task<int> Read(CancellationToken _) => ++reads == 1 ? Task.FromException<int>(new InvalidOperationException("no")) : Task.FromResult(reads);

        await Assert.ThrowsAsync<InvalidOperationException>(() => shared.GetAsync(Read, CancellationToken.None));
        Assert.Equal(2, await shared.GetAsync(Read, CancellationToken.None));
        Assert.Equal(2, await shared.GetAsync(Read, CancellationToken.None));
        shared.Reset();
        Assert.Equal(3, await shared.GetAsync(Read, CancellationToken.None));
    }
}
