using System.Collections.Concurrent;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Unit tests for the in-process keyed write queue (issue #157). No database needed.</summary>
public sealed class EndpointWriteQueueTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public void RejectsNegativeLimitAndNonPositiveTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EndpointWriteQueue(maxQueued: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EndpointWriteQueue(timeout: TimeSpan.Zero));
    }

    [Fact]
    public async Task DisabledQueueTakesNoLease()
    {
        var queue = new EndpointWriteQueue(enabled: false);

        Assert.Null(await queue.AcquireAsync("p", [("players", "a")], default));
        Assert.False(queue.GetStats().Enabled);
        Assert.Equal(0, queue.GetStats().Counters.Entered);
    }

    [Fact]
    public async Task EmptyRecordSetTakesNoLease()
    {
        var queue = new EndpointWriteQueue(timeout: Generous);

        Assert.Null(await queue.AcquireAsync("p", [], default));
        Assert.Equal(0, queue.GetStats().Counters.Entered);
    }

    [Fact]
    public async Task WaitersOnOneRecordRunInArrivalOrderAndCount()
    {
        var queue = new EndpointWriteQueue(timeout: Generous);
        var order = new ConcurrentQueue<int>();
        using var holder = await queue.AcquireAsync("p", [("players", "a")], default);

        var calls = new List<Task>();
        for (var id = 1; id <= 3; id++)
        {
            var captured = id;
            calls.Add(RunOnRecordAsync(queue, "p", "a", () => order.Enqueue(captured)));
            await WaitForQueuedAsync(queue, id);
        }

        holder!.Dispose();
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { 1, 2, 3 }, order.ToArray());
        var stats = queue.GetStats();
        Assert.Equal(0, stats.ActiveKeys);
        Assert.Equal(4, stats.Counters.Entered);    // holder + three waiters
        Assert.Equal(3, stats.Counters.Enqueued);
        Assert.Equal(4, stats.Counters.Completed);  // one release per call
        Assert.Equal(3, stats.Counters.MaxObservedQueueDepth);
    }

    [Fact]
    public async Task DifferentRecordsCollectionsAndProjectsDoNotBlockEachOther()
    {
        var queue = new EndpointWriteQueue(timeout: Generous);
        using var held = await queue.AcquireAsync("p", [("players", "a")], default);

        using var otherKey = await queue.AcquireAsync("p", [("players", "b")], default).WaitAsync(TimeSpan.FromSeconds(5));
        using var otherCollection = await queue.AcquireAsync("p", [("guilds", "a")], default).WaitAsync(TimeSpan.FromSeconds(5));
        using var otherProject = await queue.AcquireAsync("q", [("players", "a")], default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(otherKey);
        Assert.NotNull(otherCollection);
        Assert.NotNull(otherProject);
        Assert.Equal(4, queue.GetStats().ActiveKeys);
        Assert.Equal(0, queue.GetStats().QueuedRequests);
    }

    [Fact]
    public async Task MultiRecordCallsListedInOppositeOrderDoNotDeadlock()
    {
        var queue = new EndpointWriteQueue(maxQueued: 1000, timeout: Generous); // 400 calls share two keys
        var calls = new List<Task>();
        for (var i = 0; i < 200; i++)
        {
            calls.Add(HoldBothAsync(queue, "x", "y"));
            calls.Add(HoldBothAsync(queue, "y", "x"));
        }

        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, queue.GetStats().ActiveKeys);
    }

    [Fact]
    public async Task FullRecordQueueRefusesAtOnceWithRetryHint()
    {
        var queue = new EndpointWriteQueue(maxQueued: 1, timeout: TimeSpan.FromMilliseconds(1500));
        using var holder = await queue.AcquireAsync("p", [("players", "a")], default);
        var queued = queue.AcquireAsync("p", [("players", "a")], default);
        await WaitForQueuedAsync(queue, 1);

        var refused = await Assert.ThrowsAsync<EndpointWriteQueueException>(() => queue.AcquireAsync("p", [("players", "a")], default));
        Assert.Equal("NETWORK_STORAGE_WRITE_QUEUE_FULL", refused.Code);
        Assert.Equal(2, refused.RetryAfterSeconds); // ceil(1.5 s)
        Assert.Equal(1, queue.GetStats().Counters.Rejected);
        Assert.Equal(1, queue.GetStats().QueuedRequests);

        holder!.Dispose();
        using var next = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(next);
    }

    [Fact]
    public async Task WaitPastTimeoutThrowsTimeoutCodeAndLeavesNoWaiter()
    {
        var queue = new EndpointWriteQueue(timeout: TimeSpan.FromMilliseconds(200));
        using var holder = await queue.AcquireAsync("p", [("players", "a")], default);

        var timedOut = await Assert.ThrowsAsync<EndpointWriteQueueException>(() => queue.AcquireAsync("p", [("players", "a")], default));
        Assert.Equal("NETWORK_STORAGE_WRITE_QUEUE_TIMEOUT", timedOut.Code);
        Assert.Equal(1, timedOut.RetryAfterSeconds);
        Assert.Equal(1, queue.GetStats().Counters.TimedOut);
        Assert.Equal(0, queue.GetStats().QueuedRequests);

        holder!.Dispose();
        using var next = await queue.AcquireAsync("p", [("players", "a")], default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(next);
    }

    [Fact]
    public async Task CancelledWaiterLeavesTheQueueAndNeverTakesTheRecord()
    {
        var queue = new EndpointWriteQueue(timeout: Generous);
        using var holder = await queue.AcquireAsync("p", [("players", "a")], default);
        using var cancel = new CancellationTokenSource();
        var waiting = queue.AcquireAsync("p", [("players", "a")], cancel.Token);
        await WaitForQueuedAsync(queue, 1);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, queue.GetStats().QueuedRequests);
        Assert.Equal(0, queue.GetStats().Counters.TimedOut);

        holder!.Dispose();
        Assert.Equal(0, queue.GetStats().ActiveKeys);
    }

    [Fact]
    public async Task DisposingALeaseTwiceReleasesOnce()
    {
        var queue = new EndpointWriteQueue(timeout: Generous);
        var lease = await queue.AcquireAsync("p", [("players", "a")], default);

        lease!.Dispose();
        lease.Dispose();

        Assert.Equal(0, queue.GetStats().ActiveKeys);
        Assert.Equal(1, queue.GetStats().Counters.Completed);
    }

    private static async Task RunOnRecordAsync(EndpointWriteQueue queue, string projectId, string key, Action work)
    {
        using var lease = await queue.AcquireAsync(projectId, [("players", key)], default);
        work();
    }

    private static async Task HoldBothAsync(EndpointWriteQueue queue, string first, string second)
    {
        using var lease = await queue.AcquireAsync("p", [("players", first), ("players", second)], default);
        await Task.Yield();
    }

    private static async Task WaitForQueuedAsync(EndpointWriteQueue queue, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (queue.GetStats().QueuedRequests < expected)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Expected {expected} queued request(s).");
            await Task.Delay(5);
        }
    }
}
