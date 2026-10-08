using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>
/// Task 4.10 (fix-usage-and-query-telemetry): ScyllaQueryRunRecorder
/// throttle/force/cache-hit semantics, durationMs on performance, and
/// InMemoryNetworkStorageStore query-run read paths.
/// </summary>
public sealed class QueryRunTelemetryTests
{
    private static (ScyllaQueryRunRecorder Recorder, InMemoryNetworkStorageStore Store) BuildRecorder()
    {
        var store = new InMemoryNetworkStorageStore();
        var services = new ServiceCollection();
        services.AddSingleton<INetworkStorageStore>(store);
        var provider = services.BuildServiceProvider();
        var recorder = new ScyllaQueryRunRecorder(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ScyllaQueryRunRecorder>.Instance);
        return (recorder, store);
    }

    private static async Task FlushAsync()
    {
        // The recorder fire-and-forgets on the ThreadPool. Give it time to land.
        await Task.Delay(150);
    }

    [Fact]
    public async Task Record_PersistsLastRunAndLogRow()
    {
        var (recorder, store) = BuildRecorder();
        recorder.Record("proj1", "q1", "2026-07-06T12:00:00Z", 42, 100, 5, fromCache: false, force: true);
        await FlushAsync();

        var lastRun = await store.ReadQueryLastRunAsync("proj1", "q1", CancellationToken.None);
        Assert.NotNull(lastRun);
        Assert.Equal(42, lastRun!.Value.GetProperty("duration_ms").GetInt64());
        Assert.Equal(100, lastRun.Value.GetProperty("keys_scanned").GetInt32());
        Assert.Equal(5, lastRun.Value.GetProperty("records_returned").GetInt32());
        Assert.False(lastRun.Value.GetProperty("from_cache").GetBoolean());

        var logs = await store.ListQueryLogsAsync("proj1", "q1", 50, CancellationToken.None);
        var log = Assert.Single(logs);
        Assert.Equal("run", log.GetProperty("log_type").GetString());
        Assert.Equal(42, log.GetProperty("duration_ms").GetInt64());
    }

    [Fact]
    public async Task Throttle_SuppressesSecondRecordWithinWindow()
    {
        var (recorder, store) = BuildRecorder();
        recorder.Record("proj1", "q1", "2026-07-06T12:00:00Z", 10, 50, 1, false, force: true);
        await FlushAsync();

        // Non-forced record within 60s — should be suppressed.
        recorder.Record("proj1", "q1", "2026-07-06T12:00:30Z", 20, 60, 2, false, force: false);
        await FlushAsync();

        var logs = await store.ListQueryLogsAsync("proj1", "q1", 50, CancellationToken.None);
        Assert.Single(logs); // only the forced one
    }

    [Fact]
    public async Task Force_BypassesThrottle()
    {
        var (recorder, store) = BuildRecorder();
        recorder.Record("proj1", "q1", "2026-07-06T12:00:00Z", 10, 50, 1, false, force: true);
        await FlushAsync();

        // Forced record within 60s — should persist.
        recorder.Record("proj1", "q1", "2026-07-06T12:00:10Z", 20, 60, 2, false, force: true);
        await FlushAsync();

        var logs = await store.ListQueryLogsAsync("proj1", "q1", 50, CancellationToken.None);
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public async Task ListQueryLastRuns_ReturnsAllForProject()
    {
        var (recorder, store) = BuildRecorder();
        recorder.Record("proj1", "q1", "2026-07-06T12:00:00Z", 10, 50, 1, false, force: true);
        recorder.Record("proj1", "q2", "2026-07-06T12:01:00Z", 20, 60, 2, false, force: true);
        recorder.Record("proj2", "q3", "2026-07-06T12:02:00Z", 30, 70, 3, false, force: true);
        await FlushAsync();

        var proj1Runs = await store.ListQueryLastRunsAsync("proj1", CancellationToken.None);
        Assert.Equal(2, proj1Runs.Count);
        Assert.Contains(proj1Runs, r => r.GetProperty("query_id").GetString() == "q1");
        Assert.Contains(proj1Runs, r => r.GetProperty("query_id").GetString() == "q2");

        var proj2Runs = await store.ListQueryLastRunsAsync("proj2", CancellationToken.None);
        Assert.Single(proj2Runs);
    }

    [Fact]
    public void QueryPerformance_DurationMs_IsSet()
    {
        var perf = new QueryPerformance
        {
            At = "2026-07-06T12:00:00Z",
            DurationMs = 1234,
            KeysScanned = 100
        };
        Assert.Equal(1234, perf.DurationMs);
    }

}
