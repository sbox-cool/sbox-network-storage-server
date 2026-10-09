using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Endpoints;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Analytics;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Metadata;

namespace SboxNetworkStorage.Server.Tests;

public sealed class DataPlanePerformanceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string SteamId = "76561198363609085";
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    [Fact]
    public async Task One_thousand_record_reads_produce_no_queued_or_persisted_analytics()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertRecordAsync("p", "c", SteamId, Json("{\"hp\":100}"), false, 1, Ct);
        var queue = new AnalyticsEventQueue();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IStorageApiKeyResolver, ReadKeyResolver>()
            .AddSingleton<INetworkStorageDataPlane>(new StoreNetworkStorageDataPlane(store))
            .AddSingleton<IPlayerAnalyticsService>(new QueuedPlayerAnalyticsService(queue, TimeProvider.System,
                NullLogger<QueuedPlayerAnalyticsService>.Instance))
            .BuildServiceProvider();
        for (var i = 0; i < 1000; i++)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Method = "GET";
            context.Request.Headers["x-api-key"] = "test";
            context.Request.RouteValues["projectId"] = "p";
            context.Request.RouteValues["collectionId"] = "c";
            context.Request.RouteValues["key"] = SteamId;
            using var response = new MemoryStream();
            context.Response.Body = response;
            await StorageApiEndpoints.GetRecordAsync(context);
            Assert.Equal(200, context.Response.StatusCode);
        }
        using var writer = Writer(queue, store, TimeProvider.System);
        await writer.FlushAsync(Ct);
        Assert.Equal(0, queue.Buffered);
        Assert.Empty(store.PlayerAnalyticsEvents);
    }

    [Fact]
    public async Task Memory_transactions_hide_uncommitted_writes_and_preserve_unrelated_live_writes_on_rollback()
    {
        var store = new InMemoryNetworkStorageStore();
        await using (var transaction = await store.BeginTransactionAsync(Ct))
        {
            await transaction.Store.UpsertRecordAsync("p", "c", "private", Json("{}"), false, 1, Ct);
            Assert.NotNull(await transaction.Store.ReadRecordAsync("p", "c", "private", Ct));
            Assert.Null(await store.ReadRecordAsync("p", "c", "private", Ct));
            await store.UpsertRecordAsync("p", "c", "other", Json("{}"), false, 1, Ct);
        }
        Assert.Null(await store.ReadRecordAsync("p", "c", "private", Ct));
        Assert.NotNull(await store.ReadRecordAsync("p", "c", "other", Ct));
    }

    [Fact]
    public async Task Memory_transaction_conflict_rolls_back_all_changes_without_overwriting_the_concurrent_writer()
    {
        var store = new InMemoryNetworkStorageStore();
        await using var transaction = await store.BeginTransactionAsync(Ct);
        await transaction.Store.UpsertRecordAsync("p", "c", "a", Json("{}"), false, 1, Ct);
        await transaction.Store.UpsertGlobalRecordAsync("p", "g", "r", Json("{\"v\":1}"), 1, Ct);
        await store.UpsertGlobalRecordAsync("p", "g", "r", Json("{\"v\":2}"), 1, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(Ct));
        Assert.Null(await store.ReadRecordAsync("p", "c", "a", Ct));
        Assert.Equal(2, (await store.ReadGlobalRecordAsync("p", "g", "r", Ct))!.Value.GetProperty("payload_json").GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task Queued_analytics_owns_its_payload_after_the_request_document_is_disposed()
    {
        var queue = new AnalyticsEventQueue();
        var service = new QueuedPlayerAnalyticsService(queue, TimeProvider.System, NullLogger<QueuedPlayerAnalyticsService>.Instance);
        using (var payload = JsonDocument.Parse("{\"playerName\":\"Alice\"}"))
            await service.RecordEventAsync(new PlayerEventRequest("p", "c", SteamId, "record.write", payload.RootElement), Ct);
        var store = new InMemoryNetworkStorageStore();
        using var writer = Writer(queue, store, TimeProvider.System);
        await writer.FlushAsync(Ct);
        Assert.Single(store.PlayerAnalyticsEvents);
        Assert.Equal("Alice", (await store.ReadPlayerProfileAsync("p", SteamId, Ct))!.Value.GetProperty("player_name").GetString());
    }

    [Fact]
    public async Task Metadata_reads_share_one_snapshot_and_endpoint_parse_until_a_management_mutation()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var source = new CountingMetadataStore();
        var store = new MetadataCachingNetworkStore(source, new ProjectMetadataCache(memory));
        await store.UpsertCollectionAsync("p", "c", "Players", "public", Json("{}"), 1, Ct);
        await store.UpsertEndpointAsync("p", "ep", "save", "POST", true, Json("{\"marker\":1}"), null, 1, Ct);
        await store.UpsertGameValuesAsync("p", Json("{\"gold\":5}"), null, 1, Ct);
        var first = await store.ReadEndpointDefinitionAsync("p", "save", Ct);
        for (var i = 0; i < 100; i++)
        {
            Assert.NotNull(await store.ReadCollectionAsync("p", "c", Ct));
            Assert.Same(first, await store.ReadEndpointDefinitionAsync("p", "save", Ct));
            Assert.NotNull(await store.ReadGameValuesAsync("p", Ct));
        }
        Assert.Equal((1, 1, 1), source.Reads);

        await store.UpsertEndpointAsync("p", "ep", "save", "POST", true, Json("{\"marker\":2}"), null, 2, Ct);
        var updated = await store.ReadEndpointDefinitionAsync("p", "save", Ct);
        Assert.NotSame(first, updated);
        Assert.Equal(2d, updated!["marker"]);
        Assert.Equal((2, 2, 2), source.Reads);

        // A project setting is also a management mutation, even though it is not one of the cached tables.
        await store.UpsertProjectAsync("p", Json("{\"name\":\"Changed\"}"), 2, Ct);
        await store.ReadCollectionAsync("p", "c", Ct);
        Assert.Equal((3, 3, 3), source.Reads);
    }

    [Fact]
    public async Task Import_invalidates_a_previously_empty_snapshot()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new MetadataCachingNetworkStore(new InMemoryNetworkStorageStore(), new ProjectMetadataCache(memory));
        Assert.Empty(await store.ListCollectionsAsync("p", Ct));
        Assert.True(await store.TryImportProjectAsync("p", async (target, ct) =>
        {
            await target.UpsertProjectAsync("p", Json("{}"), 1, ct);
            await target.UpsertCollectionAsync("p", "imported", "Imported", "public", Json("{}"), 1, ct);
        }, Ct));
        Assert.Equal("imported", Assert.Single(await store.ListCollectionsAsync("p", Ct)).GetProperty("collection_id").GetString());
    }

    [Fact]
    public async Task Transaction_metadata_is_not_cached_or_invalidated_until_commit()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new MetadataCachingNetworkStore(new InMemoryNetworkStorageStore(), new ProjectMetadataCache(memory));
        var first = await store.ListCollectionsAsync("p", Ct);
        await using (var tx = await store.BeginTransactionAsync(Ct))
        {
            await tx.Store.UpsertCollectionAsync("p", "c", "New", "public", Json("{}"), 1, Ct);
            Assert.Single(await tx.Store.ListCollectionsAsync("p", Ct));
            Assert.Same(first, await store.ListCollectionsAsync("p", Ct));
            await tx.CommitAsync(Ct);
        }
        Assert.Single(await store.ListCollectionsAsync("p", Ct));
    }

    [Fact]
    public void Analytics_buffer_drops_oldest_and_counts_every_excess_event()
    {
        var queue = new AnalyticsEventQueue();
        for (var i = 0; i < 15_000; i++) queue.Enqueue(Event(i));
        Assert.Equal(10_000, queue.Buffered);
        Assert.Equal(5_000L, queue.DroppedCount);
        Assert.True(queue.Reader.TryRead(out var first));
        Assert.Equal(5_000, first!.TimestampMs);
    }

    [Fact]
    public async Task Analytics_flush_uses_transactions_of_500_500_and_200_events()
    {
        var queue = new AnalyticsEventQueue();
        var source = new CountingAnalyticsStore();
        using var writer = Writer(queue, source, TimeProvider.System);
        for (var i = 0; i < 1200; i++) queue.Enqueue(Event(i));
        await writer.FlushAsync(Ct);
        Assert.Equal(new[] { 500, 500, 200 }, source.Batches);
        Assert.Equal(1200, source.Inner.PlayerAnalyticsEvents.Count);
        Assert.Equal(0, queue.Buffered);
    }

    [Fact]
    public async Task Analytics_background_writer_flushes_a_partial_batch_without_more_requests()
    {
        var queue = new AnalyticsEventQueue();
        var source = new CountingAnalyticsStore();
        using var writer = Writer(queue, source, TimeProvider.System);
        queue.Enqueue(Event(1));
        await writer.StartAsync(Ct);
        try
        {
            await source.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 1 }, source.Batches);
        }
        finally { await writer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Analytics_retention_defaults_to_90_days_and_runs_daily()
    {
        var time = new ManualTime();
        var source = new InMemoryNetworkStorageStore(time);
        var cutoff = time.Now.AddDays(-90).ToUnixTimeMilliseconds();
        await source.InsertPlayerAnalyticsEventV2Async("p", SteamId, cutoff - 1, "old", "endpoint.call", "endpoint", "old", "save", "c", Json("{}"), Ct);
        await source.InsertPlayerAnalyticsEventV2Async("p", SteamId, cutoff, "keep", "endpoint.call", "endpoint", "keep", "save", "c", Json("{}"), Ct);
        using var writer = Writer(new AnalyticsEventQueue(), source, time);
        await writer.PurgeIfDueAsync(Ct);
        Assert.Equal(2, source.PlayerAnalyticsEvents.Count);
        time.Now += TimeSpan.FromMinutes(1);
        await writer.PurgeIfDueAsync(Ct);
        Assert.Single(source.PlayerAnalyticsEvents);
        time.Now += TimeSpan.FromHours(23);
        await writer.PurgeIfDueAsync(Ct);
        Assert.Single(source.PlayerAnalyticsEvents);
        time.Now += TimeSpan.FromHours(1);
        await writer.PurgeIfDueAsync(Ct);
        Assert.Empty(source.PlayerAnalyticsEvents);
    }

    [Fact]
    public void Matches_catastrophic_pattern_becomes_an_expression_error_with_a_50ms_timeout()
    {
        var watch = Stopwatch.StartNew();
        var error = Assert.Throws<ExpressionException>(() => EndpointExpression.EvaluateCondition(new Dictionary<string, object?>
        {
            ["field"] = "input.value", ["op"] = "matches", ["value"] = "(a+)+$",
        }, new Dictionary<string, object?>
        {
            ["input"] = new Dictionary<string, object?> { ["value"] = new string('a', 20_000) + "!" },
        }));
        Assert.Contains("50 ms", error.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Dynamic_patterns_use_a_bounded_LRU_and_invalid_patterns_are_expression_errors()
    {
        var cache = new ExpressionRegexCache(2, TimeSpan.FromMilliseconds(50));
        cache.Match("a", "a");
        cache.Match("b", "b");
        cache.Match("a", "a");
        cache.Match("c", "c");
        Assert.Equal(2, cache.Count);
        // Inspect cache membership without adding a production-only diagnostics API.
        var index = (IDictionary)typeof(ExpressionRegexCache).GetField("_index", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        Assert.True(index.Contains("a"));
        Assert.False(index.Contains("b"));
        Assert.Throws<ExpressionException>(() => cache.Match("[", "a"));
        Assert.True((bool)EndpointExpression.ResolveTemplate("{{matches(input.value, '^a+$')}}",
            new Dictionary<string, object?> { ["input"] = new Dictionary<string, object?> { ["value"] = "aaa" } })!);
    }

    [Fact]
    public async Task Append_rate_limiter_removes_previous_day_buckets_even_when_limits_are_disabled()
    {
        var time = new ManualTime();
        var limiter = new InMemoryAppendRateLimiter(time);
        await limiter.CheckAsync("p", "c", "player", 10, "a", Ct);
        await limiter.CheckAsync("p", "c", "player", 10, "b", Ct);
        Assert.Equal(2, limiter.BucketCount);
        time.Now += TimeSpan.FromDays(1);
        await limiter.CheckAsync("p", "c", "player", 0, "a", Ct);
        Assert.Equal(0, limiter.BucketCount);
        await limiter.CheckAsync("p", "c", "player", 10, "a", Ct);
        Assert.Equal(1, limiter.BucketCount);
    }

    private static AnalyticsEvent Event(long timestamp) => new("p", SteamId, "endpoint.call", "save", "network-storage-library",
        "save", "c", null, "Player", timestamp, Json("{}"), null);

    private static AnalyticsWriterService Writer(AnalyticsEventQueue queue, INetworkStorageStore store, TimeProvider time)
    {
        var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        return new AnalyticsWriterService(queue, services.GetRequiredService<IServiceScopeFactory>(), time, new AnalyticsIngestionFailureTracker(NullLogger<AnalyticsIngestionFailureTracker>.Instance),
            new AnalyticsWriterOptions(), NullLoggerFactory.Instance);
    }

    private sealed class ReadKeyResolver : IStorageApiKeyResolver
    {
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string projectId, CancellationToken ct)
            => Task.FromResult<StorageApiKeyAuthResult?>(new StorageApiKeyAuthResult(1, projectId, true, "secret"));
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingMetadataStore : EmptyNetworkStorageStore
    {
        private readonly InMemoryNetworkStorageStore _inner = new();
        private int _collections, _endpoints, _values;
        public (int, int, int) Reads => (_collections, _endpoints, _values);
        public override Task<IReadOnlyList<JsonElement>> ListCollectionsAsync(string p, CancellationToken ct) { _collections++; return _inner.ListCollectionsAsync(p, ct); }
        public override Task<IReadOnlyList<JsonElement>> ListEndpointsAsync(string p, CancellationToken ct) { _endpoints++; return _inner.ListEndpointsAsync(p, ct); }
        public override Task<JsonElement?> ReadGameValuesAsync(string p, CancellationToken ct) { _values++; return _inner.ReadGameValuesAsync(p, ct); }
        public override Task UpsertCollectionAsync(string p, string id, string name, string visibility, JsonElement definition, long version, CancellationToken ct)
            => _inner.UpsertCollectionAsync(p, id, name, visibility, definition, version, ct);
        public override Task UpsertEndpointAsync(string p, string id, string slug, string method, bool enabled, JsonElement definition, string? hash, long version, CancellationToken ct)
            => _inner.UpsertEndpointAsync(p, id, slug, method, enabled, definition, hash, version, ct);
        public override Task UpsertGameValuesAsync(string p, JsonElement values, string? hash, long version, CancellationToken ct)
            => _inner.UpsertGameValuesAsync(p, values, hash, version, ct);
        public override Task UpsertProjectAsync(string p, JsonElement payload, long version, CancellationToken ct)
            => _inner.UpsertProjectAsync(p, payload, version, ct);
    }

    private sealed class CountingAnalyticsStore : EmptyNetworkStorageStore
    {
        public InMemoryNetworkStorageStore Inner { get; } = new();
        public List<int> Batches { get; } = new();
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<IStoreTransaction> BeginTransactionAsync(CancellationToken ct)
        {
            var before = Inner.PlayerAnalyticsEvents.Count;
            return new CountingTransaction(await Inner.BeginTransactionAsync(ct), before, this);
        }
        private sealed class CountingTransaction(IStoreTransaction inner, int before, CountingAnalyticsStore owner) : IStoreTransaction
        {
            public INetworkStorageStore Store => inner.Store;
            public async Task CommitAsync(CancellationToken ct)
            {
                await inner.CommitAsync(ct);
                owner.Batches.Add(owner.Inner.PlayerAnalyticsEvents.Count - before);
                owner.Committed.TrySetResult();
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
