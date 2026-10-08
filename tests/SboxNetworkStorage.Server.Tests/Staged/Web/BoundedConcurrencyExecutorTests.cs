using SboxNetworkStorage.Infrastructure.NetworkStorage.Import;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Tests for the import bulk-write executor: full coverage, the concurrency
/// bound, fail-fast that surfaces the original error (not a masking
/// cancellation), and the empty no-op.
/// </summary>
public sealed class BoundedConcurrencyExecutorTests
{
    [Fact]
    public async Task RunAsync_ExecutesEveryItem()
    {
        var items = Enumerable.Range(0, 1000).ToList();
        var executed = 0;

        await BoundedConcurrencyExecutor.RunAsync(items, 64, (_, _) =>
        {
            Interlocked.Increment(ref executed);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(1000, executed);
    }

    [Fact]
    public async Task RunAsync_NeverExceedsMaxInFlight()
    {
        const int limit = 8;
        var items = Enumerable.Range(0, 200).ToList();
        var current = 0;
        var max = 0;
        var maxLock = new object();

        await BoundedConcurrencyExecutor.RunAsync(items, limit, async (_, _) =>
        {
            var now = Interlocked.Increment(ref current);
            lock (maxLock) max = Math.Max(max, now);
            await Task.Delay(5);
            Interlocked.Decrement(ref current);
        }, CancellationToken.None);

        Assert.True(max <= limit, $"observed {max} concurrent actions, limit was {limit}");
        Assert.True(max > 1, "expected real concurrency, not serial execution");
    }

    [Fact]
    public async Task RunAsync_FailFast_StopsDispatchingAndThrowsOriginalError()
    {
        var items = Enumerable.Range(0, 500).ToList();
        var started = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedConcurrencyExecutor.RunAsync(items, 4, async (item, _) =>
            {
                Interlocked.Increment(ref started);
                if (item == 0) throw new InvalidOperationException("boom");
                await Task.Delay(50);
            }, CancellationToken.None));

        Assert.Equal("boom", ex.Message); // original error, not a masking OperationCanceledException
        Assert.True(started < 500, $"fail-fast should stop dispatch; started {started}/500");
    }

    [Fact]
    public async Task RunAsync_EmptyList_DoesNotInvokeAction()
    {
        var invoked = false;
        await BoundedConcurrencyExecutor.RunAsync(new List<int>(), 16, (_, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.False(invoked);
    }
}
