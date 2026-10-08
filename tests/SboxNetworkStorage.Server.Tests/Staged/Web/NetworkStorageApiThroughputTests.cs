using SboxNetworkStorage.Server.Tests.Hosting;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;
using Xunit.Abstractions;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Throughput and latency benchmarks for the Network Storage record CRUD API.
/// Measures the end-to-end HTTP pipeline (routing, auth, data plane,
/// serialization) using an in-memory <see cref="InMemoryNetworkStorageStore"/> so
/// results isolate ASP.NET Core + ScyllaNetworkStorageDataPlane overhead
/// from actual ScyllaDB latency.
///
/// Each test is an [SkippableFact] that runs a controlled workload and emits timing
/// output via <see cref="ITestOutputHelper"/>. No BenchmarkDotNet dependency
/// required — the results appear in the test runner output.
/// </summary>
// Perf/throughput benchmarks: they assert HTTP CRUD correctness (already
// covered functionally by NetworkStorageApiRoundTripTests) but their real
// purpose is timing measurement under 50-way concurrency. On shared CI runners
// that contention is flaky, so they are excluded from the CI gate via
// `--filter Category!=Benchmark` and run locally for measurement.
[Trait("Category", "Benchmark")]
public abstract class NetworkStorageApiThroughputTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string ApiKey = "sk-bench-key";
    private const string ProjectId = "bench-project";
    private const string Collection = "players";
    private const long OwnerUserId = 99;

    private readonly SelfHostFactory _factory;
    private readonly ITestOutputHelper _output;

    protected NetworkStorageApiThroughputTests(TFactory factory, ITestOutputHelper output)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
        _output = output;
    }

    // ── Setup ────────────────────────────────────────────────────────────

    private HttpClient CreateClient(InMemoryNetworkStorageStore store) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<INetworkStorageStore>();
                services.AddScoped<INetworkStorageStore>(_ => store);
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new BenchRecordKeyResolver());
                services.RemoveAll<IPlayerAnalyticsService>();
                services.AddSingleton<IPlayerAnalyticsService, NoopPlayerAnalyticsService>();
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string Url(string key) => $"/api/storage/{ProjectId}/{Collection}/{key}?apiKey={ApiKey}";

    // ── Benchmarks ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Bench_SequentialPost_100Records()
    {
        const int count = 100;
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        // Warm up the pipeline.
        await client.PostAsJsonAsync(Url("warmup"), new { w = true });

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            using var resp = await client.PostAsJsonAsync(Url($"seq-{i}"), new { score = i, tag = $"player-{i}" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        sw.Stop();

        var opsPerSec = count / sw.Elapsed.TotalSeconds;
        var avgMs = sw.Elapsed.TotalMilliseconds / count;
        _output.WriteLine($"Sequential POST x{count}: {sw.ElapsedMilliseconds}ms total, {avgMs:F2}ms avg, {opsPerSec:F0} ops/sec");
    }

    [SkippableFact]
    public async Task Bench_SequentialGet_100Records()
    {
        const int count = 100;
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        // Seed data.
        for (int i = 0; i < count; i++)
            await client.PostAsJsonAsync(Url($"get-{i}"), new { hp = i * 10 });

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            using var resp = await client.GetAsync(Url($"get-{i}"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        sw.Stop();

        var opsPerSec = count / sw.Elapsed.TotalSeconds;
        var avgMs = sw.Elapsed.TotalMilliseconds / count;
        _output.WriteLine($"Sequential GET x{count}: {sw.ElapsedMilliseconds}ms total, {avgMs:F2}ms avg, {opsPerSec:F0} ops/sec");
    }

    [SkippableFact]
    public async Task Bench_PostGetRoundTrip_50Records()
    {
        const int count = 50;
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        await client.PostAsJsonAsync(Url("warmup"), new { w = true });

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            using var post = await client.PostAsJsonAsync(Url($"rt-{i}"), new { hp = i, mana = i * 2, name = $"P{i}" });
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);

            using var get = await client.GetAsync(Url($"rt-{i}"));
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var body = await get.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(i, body.GetProperty("hp").GetInt32());
        }
        sw.Stop();

        var roundTrips = count;
        var opsPerSec = roundTrips / sw.Elapsed.TotalSeconds;
        var avgMs = sw.Elapsed.TotalMilliseconds / roundTrips;
        _output.WriteLine($"POST+GET round-trip x{count}: {sw.ElapsedMilliseconds}ms total, {avgMs:F2}ms avg, {opsPerSec:F0} round-trips/sec");
    }

    [SkippableFact]
    public async Task Bench_ConcurrentPost_50Parallel()
    {
        const int count = 50;
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        await client.PostAsJsonAsync(Url("warmup"), new { w = true });

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            using var resp = await client.PostAsJsonAsync(Url($"par-{i}"), new { score = i });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        });
        await Task.WhenAll(tasks);
        sw.Stop();

        var opsPerSec = count / sw.Elapsed.TotalSeconds;
        var avgMs = sw.Elapsed.TotalMilliseconds / count;
        _output.WriteLine($"Concurrent POST x{count}: {sw.ElapsedMilliseconds}ms total, {avgMs:F2}ms avg, {opsPerSec:F0} ops/sec");
    }

    [SkippableFact]
    public async Task Bench_ConcurrentGet_50Parallel()
    {
        const int count = 50;
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        for (int i = 0; i < count; i++)
            await client.PostAsJsonAsync(Url($"cget-{i}"), new { val = i });

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            using var resp = await client.GetAsync(Url($"cget-{i}"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        });
        await Task.WhenAll(tasks);
        sw.Stop();

        var opsPerSec = count / sw.Elapsed.TotalSeconds;
        var avgMs = sw.Elapsed.TotalMilliseconds / count;
        _output.WriteLine($"Concurrent GET x{count}: {sw.ElapsedMilliseconds}ms total, {avgMs:F2}ms avg, {opsPerSec:F0} ops/sec");
    }

    [SkippableFact]
    public async Task Bench_LargePayload_PostGet()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        // ~16 KB payload: realistic game save blob.
        var bigArray = Enumerable.Range(0, 400)
            .Select(i => new { id = i, data = $"item-{i}-" + new string('x', 30), qty = i % 99 })
            .ToArray();
        var payload = new { items = bigArray, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), version = 3 };

        await client.PostAsJsonAsync(Url("warmup"), new { w = true });

        // POST
        var sw = Stopwatch.StartNew();
        using (var post = await client.PostAsJsonAsync(Url("bigsave"), payload))
        {
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        }
        var postMs = sw.Elapsed.TotalMilliseconds;

        // GET
        sw.Restart();
        using (var get = await client.GetAsync(Url("bigsave")))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var body = await get.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(400, body.GetProperty("items").GetArrayLength());
        }
        var getMs = sw.Elapsed.TotalMilliseconds;

        _output.WriteLine($"Large payload (~16KB): POST {postMs:F2}ms, GET {getMs:F2}ms");
    }

    [SkippableFact]
    public async Task Bench_DeletePathLatency()
    {
        const int count = 50;
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        // Seed.
        for (int i = 0; i < count; i++)
            await client.PostAsJsonAsync(Url($"del-{i}"), new { v = i });

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            using var resp = await client.DeleteAsync(Url($"del-{i}"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        sw.Stop();

        var opsPerSec = count / sw.Elapsed.TotalSeconds;
        var avgMs = sw.Elapsed.TotalMilliseconds / count;
        _output.WriteLine($"Sequential DELETE x{count}: {sw.ElapsedMilliseconds}ms total, {avgMs:F2}ms avg, {opsPerSec:F0} ops/sec");

        // Verify all deleted.
        for (int i = 0; i < count; i++)
        {
            using var resp = await client.GetAsync(Url($"del-{i}"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Bench_MixedReadWrite_Workload()
    {
        // Simulates a realistic game workload: 60% reads, 30% writes, 10% deletes.
        const int totalOps = 200;
        const int readPct = 60;
        const int writePct = 30;
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        // Seed 20 records.
        for (int i = 0; i < 20; i++)
            await client.PostAsJsonAsync(Url($"mix-{i}"), new { hp = 100 });

        var rng = new Random(42);
        var reads = 0; var writes = 0; var deletes = 0;
        var errors = 0;

        var sw = Stopwatch.StartNew();
        for (int op = 0; op < totalOps; op++)
        {
            var roll = rng.Next(100);
            var key = $"mix-{rng.Next(30)}"; // some keys won't exist yet

            if (roll < readPct)
            {
                using var resp = await client.GetAsync(Url(key));
                if (resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound) reads++;
                else errors++;
            }
            else if (roll < readPct + writePct)
            {
                using var resp = await client.PostAsJsonAsync(Url(key), new { hp = rng.Next(1, 200), ts = op });
                if (resp.StatusCode == HttpStatusCode.OK) writes++;
                else errors++;
            }
            else
            {
                using var resp = await client.DeleteAsync(Url(key));
                if (resp.StatusCode == HttpStatusCode.OK) deletes++;
                else errors++;
            }
        }
        sw.Stop();

        var opsPerSec = totalOps / sw.Elapsed.TotalSeconds;
        _output.WriteLine($"Mixed workload x{totalOps}: {sw.ElapsedMilliseconds}ms total, {opsPerSec:F0} ops/sec");
        _output.WriteLine($"  reads={reads} writes={writes} deletes={deletes} errors={errors}");
        Assert.Equal(0, errors);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class BenchRecordKeyResolver : IStorageApiKeyResolver
    {
        private static readonly StorageApiKeyAuthResult CachedResult = new(OwnerUserId, ProjectId, true, "secret");
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string project, CancellationToken ct)
            => Task.FromResult(string.Equals(apiKey, ApiKey, StringComparison.Ordinal) ? CachedResult : null);
    }

    private sealed class NoopPlayerAnalyticsService : IPlayerAnalyticsService
    {
        public Task RecordEventAsync(PlayerEventRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordEndpointEventAsync(string projectId, string steamId, string endpointSlug, string eventType, IReadOnlyDictionary<string, object>? payload, IReadOnlyList<TrackedFieldDelta>? trackedFieldDeltas, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class NetworkStorageApiThroughputTests_Sqlite(SqliteHostFactory factory, ITestOutputHelper output) : NetworkStorageApiThroughputTests<SqliteHostFactory>(factory, output);

public sealed class NetworkStorageApiThroughputTests_Postgres(PostgresHostFactory factory, ITestOutputHelper output) : NetworkStorageApiThroughputTests<PostgresHostFactory>(factory, output);
