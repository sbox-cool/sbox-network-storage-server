namespace SboxNetworkStorage.Server.Tests;

public sealed class QueryResultCacheTests
{
    private readonly ManualTime _time = new();
    private int _runs;

    private Task<QueryResult?> Run()
    {
        var run = Interlocked.Increment(ref _runs);
        return Task.FromResult<QueryResult?>(new QueryResult { Type = "count", Count = run });
    }

    private Task<QueryResult?> Get(QueryResultCache cache, int ttlSeconds = 300, string projectId = "p")
        => cache.GetOrRunAsync(projectId, "q", ttlSeconds, Run, CancellationToken.None);

    [Fact]
    public async Task ResultIsReusedForTheTtlAndRunsAgainAfterIt()
    {
        var cache = new QueryResultCache(_time);
        var first = await Get(cache, ttlSeconds: 60);
        Assert.False(first!.FromCache);

        _time.Advance(TimeSpan.FromSeconds(59));
        var reused = await Get(cache, ttlSeconds: 60);
        Assert.True(reused!.FromCache);
        Assert.Equal(1, reused.Count);
        Assert.NotNull(reused.ExpiresAt);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, (await Get(cache, ttlSeconds: 60))!.Count);
    }

    [Fact]
    public async Task ZeroTtlStillReusesTheResultForTheMinimumLifetime()
    {
        var cache = new QueryResultCache(_time);
        await Get(cache, ttlSeconds: 0);

        _time.Advance(QueryResultCache.MinimumLifetime - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, (await Get(cache, ttlSeconds: 0))!.Count);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, (await Get(cache, ttlSeconds: 0))!.Count);
    }

    [Fact]
    public async Task RecordWriteEndsReuseOnlyAfterTheMinimumLifetime()
    {
        var cache = new QueryResultCache(_time);
        await Get(cache);

        cache.DataChanged("p");
        Assert.Equal(1, (await Get(cache))!.Count);

        _time.Advance(QueryResultCache.MinimumLifetime);
        Assert.Equal(2, (await Get(cache))!.Count);

        // The new result is reused again until the next write.
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(2, (await Get(cache))!.Count);

        // Writes to another project do not touch it.
        cache.DataChanged("other");
        Assert.Equal(2, (await Get(cache))!.Count);
    }

    [Fact]
    public async Task DefinitionChangeEndsReuseImmediately()
    {
        var cache = new QueryResultCache(_time);
        await Get(cache);

        cache.DefinitionsChanged("p");

        Assert.Equal(2, (await Get(cache))!.Count);
    }

    [Fact]
    public async Task ConcurrentMissesShareOneRunAndOneCallerGivingUpDoesNotFailTheOthers()
    {
        var cache = new QueryResultCache(_time);
        var release = new TaskCompletionSource<QueryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        Task<QueryResult?> Slow()
        {
            Interlocked.Increment(ref runs);
            return release.Task;
        }

        using var gaveUp = new CancellationTokenSource();
        var quitter = cache.GetOrRunAsync("p", "q", 300, Slow, gaveUp.Token);
        var waiters = Enumerable.Range(0, 20).Select(_ => cache.GetOrRunAsync("p", "q", 300, Slow, CancellationToken.None)).ToArray();
        await gaveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quitter);

        release.SetResult(new QueryResult { Type = "count", Count = 7 });
        var results = await Task.WhenAll(waiters);

        Assert.Equal(1, runs);
        Assert.All(results, result => Assert.Equal(7, result!.Count));
        Assert.True((await cache.GetOrRunAsync("p", "q", 300, Slow, CancellationToken.None))!.FromCache);
    }

    [Fact]
    public async Task ErrorResultsAreNotReused()
    {
        var cache = new QueryResultCache(_time);
        var calls = 0;
        Task<QueryResult?> Failing()
        {
            calls++;
            return Task.FromResult<QueryResult?>(new QueryResult { Type = "error", Message = "Query has no sources." });
        }

        await cache.GetOrRunAsync("p", "q", 300, Failing, CancellationToken.None);
        var second = await cache.GetOrRunAsync("p", "q", 300, Failing, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.False(second!.FromCache);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
