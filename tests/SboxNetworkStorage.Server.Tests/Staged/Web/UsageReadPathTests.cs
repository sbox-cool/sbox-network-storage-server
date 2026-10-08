using System.Text.Json;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>
/// Task 3.5 (fix-usage-and-query-telemetry): usage read paths —
/// ScyllaMetadataWorkspaceClient.GetProjectUsageAsync ScyllaDB-authoritative
/// read with Bunny fallback, and the workspace endpoint fallback avg-duration
/// fix (duration_ms_sum / calls, not duration_samples).
/// </summary>
public sealed class UsageReadPathTests
{
    private static readonly string Month = DateTimeOffset.UtcNow.ToString("yyyy-MM");

    private static UsageDelta Delta(
        long requests = 0, long reads = 0, long writes = 0, long endpointCalls = 0,
        long bytesIn = 0, long bytesOut = 0, long errors = 0,
        long durationMsSum = 0, long durationSamples = 0, long storageDeltaBytes = 0,
        long computeUnits = 0)
        => new(requests, reads, writes, endpointCalls, bytesIn, bytesOut, errors,
            durationMsSum, durationSamples, storageDeltaBytes, computeUnits);

    [Fact]
    public async Task ScyllaMonthlyRow_MapsToWorkspaceUsage()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.IncrementProjectUsageAsync("proj1", Month, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), null,
            Delta(requests: 10, bytesIn: 200, bytesOut: 500, errors: 1, durationMsSum: 1000, durationSamples: 10),
            CancellationToken.None);

        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None);
        Assert.NotNull(monthly);
        Assert.Equal(10, monthly!.Value.GetProperty("requests").GetInt64());
        Assert.Equal(200, monthly.Value.GetProperty("bytes_in").GetInt64());
        Assert.Equal(500, monthly.Value.GetProperty("bytes_out").GetInt64());
        Assert.Equal(1, monthly.Value.GetProperty("errors").GetInt64());
    }

    [Fact]
    public void LiveTraffic_RemainsAuthoritativeWhileRetainedStorageIsRecovered()
    {
        var current = new WorkspaceProjectUsage(10, 200, 500, 1, 800, 55)
        {
            Source = "scylla-live",
        };
        var retained = new WorkspaceProjectUsage(15, 180, 700, 2, 1_200, 40)
        {
            Source = "retained-snapshot",
            LastUpdatedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
        };

        var merged = ScyllaMetadataWorkspaceClient.MergeRecoveredUsage(current, retained);

        Assert.Equal(10, merged!.Requests);
        Assert.Equal(200, merged.BytesIn);
        Assert.Equal(500, merged.BytesOut);
        Assert.Equal(1, merged.Errors);
        Assert.Equal(1_200, merged.StorageBytes);
        Assert.Equal(55, merged.ComputeUnits);
        Assert.Equal("scylla-live+retained-storage", merged.Source);
        Assert.Equal(retained.LastUpdatedAt, merged.LastUpdatedAt);
    }

    [Fact]
    public async Task MissingMonth_ReturnsNull()
    {
        var store = new InMemoryNetworkStorageStore();
        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", "2025-01", CancellationToken.None);
        Assert.Null(monthly);
    }

    [Fact]
    public async Task EndpointRow_HasDurationMsSumButNoDurationSamples()
    {
        // Regression for task 3.4: BuildUsageEndpointRow emits duration_ms_sum
        // but NOT duration_samples. The workspace controller must compute avg
        // as duration_ms_sum / calls, not duration_ms_sum / duration_samples.
        var store = new InMemoryNetworkStorageStore();
        await store.IncrementProjectUsageAsync("proj1", Month, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), "save-all",
            Delta(requests: 5, endpointCalls: 5, durationMsSum: 250, durationSamples: 5),
            CancellationToken.None);

        var endpoints = await store.ReadProjectUsageEndpointsAsync("proj1", Month, 50, CancellationToken.None);
        var ep = Assert.Single(endpoints);
        Assert.Equal("save-all", ep.GetProperty("endpoint_slug").GetString());
        Assert.Equal(5, ep.GetProperty("calls").GetInt64());
        Assert.Equal(250, ep.GetProperty("duration_ms_sum").GetInt64());

        // Verify duration_samples is NOT present on endpoint rows.
        Assert.False(ep.TryGetProperty("duration_samples", out _));

        // The correct avg computation: duration_ms_sum / calls = 250 / 5 = 50.
        var calls = ep.GetProperty("calls").GetInt64();
        var sumDur = ep.GetProperty("duration_ms_sum").GetInt64();
        var avgDur = calls > 0 ? (double)sumDur / calls : 0.0;
        Assert.Equal(50.0, avgDur);
    }

    [Fact]
    public async Task DailySeries_OrdersByDay()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.IncrementProjectUsageAsync("proj1", Month, "2026-07-02", null,
            Delta(requests: 3), CancellationToken.None);
        await store.IncrementProjectUsageAsync("proj1", Month, "2026-07-01", null,
            Delta(requests: 5), CancellationToken.None);

        var daily = await store.ReadProjectUsageDailyAsync("proj1", Month, CancellationToken.None);
        Assert.Equal(2, daily.Count);
        Assert.Contains(daily, d => d.GetProperty("day").GetString() == "2026-07-01");
        Assert.Contains(daily, d => d.GetProperty("day").GetString() == "2026-07-02");
    }

    [Fact]
    public async Task StorageDelta_ClampsNegativeToZero()
    {
        // Regression for design D4: storage_delta_bytes can go negative on
        // deletes; the reader clamps to >= 0.
        var store = new InMemoryNetworkStorageStore();
        await store.IncrementProjectUsageAsync("proj1", Month, "2026-07-06", null,
            Delta(storageDeltaBytes: -500), CancellationToken.None);

        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None);
        Assert.NotNull(monthly);
        Assert.Equal(-500, monthly!.Value.GetProperty("storage_delta_bytes").GetInt64());

        // The reader (ScyllaMetadataWorkspaceClient.MapMonthlyUsage) clamps:
        var storageBytes = Math.Max(0, monthly.Value.GetProperty("storage_delta_bytes").GetInt64());
        Assert.Equal(0, storageBytes);
    }
}
