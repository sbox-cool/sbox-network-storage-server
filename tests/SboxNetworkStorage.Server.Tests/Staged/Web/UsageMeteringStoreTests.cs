using System.Text.Json;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>
/// Task 1.4 (fix-usage-and-query-telemetry): usage-counter store semantics on
/// <see cref="InMemoryNetworkStorageStore"/> plus schema-migrator coverage for the V5
/// usage tables. The fake mirrors the real ScyllaDB counter tables' additive
/// merge behavior, so these tests pin the contract the tracker and read paths
/// rely on.
/// </summary>
public sealed class UsageMeteringStoreTests
{
    private static UsageDelta Delta(
        long requests = 0, long reads = 0, long writes = 0, long endpointCalls = 0,
        long bytesIn = 0, long bytesOut = 0, long errors = 0,
        long durationMsSum = 0, long durationSamples = 0, long storageDeltaBytes = 0,
        long computeUnits = 0)
        => new(requests, reads, writes, endpointCalls, bytesIn, bytesOut, errors,
            durationMsSum, durationSamples, storageDeltaBytes, computeUnits);

    private static long Long(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;

    [Fact]
    public async Task Increments_Accumulate_Additively_Across_Flushes()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.IncrementProjectUsageAsync("proj1", "2026-07", "2026-07-06", "save-all",
            Delta(requests: 2, endpointCalls: 2, bytesIn: 100, bytesOut: 300, durationMsSum: 40, durationSamples: 2, computeUnits: 41), CancellationToken.None);
        await store.IncrementProjectUsageAsync("proj1", "2026-07", "2026-07-06", "save-all",
            Delta(requests: 1, endpointCalls: 1, bytesIn: 50, bytesOut: 150, errors: 1, durationMsSum: 25, durationSamples: 1, computeUnits: 25), CancellationToken.None);

        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", "2026-07", CancellationToken.None);
        Assert.NotNull(monthly);
        Assert.Equal(3, Long(monthly!.Value, "requests"));
        Assert.Equal(3, Long(monthly.Value, "endpoint_calls"));
        Assert.Equal(150, Long(monthly.Value, "bytes_in"));
        Assert.Equal(450, Long(monthly.Value, "bytes_out"));
        Assert.Equal(1, Long(monthly.Value, "errors"));
        Assert.Equal(65, Long(monthly.Value, "duration_ms_sum"));
        Assert.Equal(3, Long(monthly.Value, "duration_samples"));
        Assert.Equal(66, Long(monthly.Value, "compute_units"));

        var endpoints = await store.ReadProjectUsageEndpointsAsync("proj1", "2026-07", 50, CancellationToken.None);
        var row = Assert.Single(endpoints);
        Assert.Equal("save-all", row.GetProperty("endpoint_slug").GetString());
        Assert.Equal(3, Long(row, "calls"));
        Assert.Equal(1, Long(row, "errors"));
        Assert.Equal(66, Long(row, "compute_units"));
    }

    [Fact]
    public async Task Daily_Series_Is_Per_Day_And_Ordered()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.IncrementProjectUsageAsync("proj1", "2026-07", "2026-07-02", null,
            Delta(requests: 5, bytesOut: 10), CancellationToken.None);
        await store.IncrementProjectUsageAsync("proj1", "2026-07", "2026-07-01", null,
            Delta(requests: 3, bytesOut: 20), CancellationToken.None);

        var daily = await store.ReadProjectUsageDailyAsync("proj1", "2026-07", CancellationToken.None);
        Assert.Equal(2, daily.Count);
        Assert.Equal("2026-07-01", daily[0].GetProperty("day").GetString());
        Assert.Equal(3, Long(daily[0], "requests"));
        Assert.Equal("2026-07-02", daily[1].GetProperty("day").GetString());
        Assert.Equal(5, Long(daily[1], "requests"));
    }

    [Fact]
    public async Task No_Endpoint_Row_Without_Slug_And_No_Cross_Project_Leak()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.IncrementProjectUsageAsync("proj1", "2026-07", "2026-07-06", null,
            Delta(requests: 1, reads: 1), CancellationToken.None);
        await store.IncrementProjectUsageAsync("proj2", "2026-07", "2026-07-06", "load-player",
            Delta(requests: 1, endpointCalls: 1), CancellationToken.None);

        Assert.Empty(await store.ReadProjectUsageEndpointsAsync("proj1", "2026-07", 50, CancellationToken.None));
        var monthly1 = await store.ReadProjectUsageMonthlyAsync("proj1", "2026-07", CancellationToken.None);
        Assert.Equal(1, Long(monthly1!.Value, "requests"));
        Assert.Equal(1, Long(monthly1.Value, "reads"));
        Assert.Equal(0, Long(monthly1.Value, "endpoint_calls"));

        // proj2's endpoint row does not bleed into proj1's month.
        var endpoints2 = await store.ReadProjectUsageEndpointsAsync("proj2", "2026-07", 50, CancellationToken.None);
        Assert.Single(endpoints2);
    }

    [Fact]
    public async Task Missing_Month_Reads_Return_Null_And_Empty()
    {
        var store = new InMemoryNetworkStorageStore();
        Assert.Null(await store.ReadProjectUsageMonthlyAsync("proj1", "2026-06", CancellationToken.None));
        Assert.Empty(await store.ReadProjectUsageDailyAsync("proj1", "2026-06", CancellationToken.None));
        Assert.Empty(await store.ReadProjectUsageEndpointsAsync("proj1", "2026-06", 50, CancellationToken.None));
    }

    [Fact]
    public async Task Negative_Storage_Deltas_Accumulate_Below_Zero_In_Store()
    {
        // The STORE keeps the raw (possibly negative) accumulated delta; the
        // READER clamps to >= 0 (design D4). Pin the raw behavior here.
        var store = new InMemoryNetworkStorageStore();
        await store.IncrementProjectUsageAsync("proj1", "2026-07", "2026-07-06", null,
            Delta(requests: 1, writes: 1, storageDeltaBytes: 100), CancellationToken.None);
        await store.IncrementProjectUsageAsync("proj1", "2026-07", "2026-07-06", null,
            Delta(requests: 1, writes: 1, storageDeltaBytes: -250), CancellationToken.None);

        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", "2026-07", CancellationToken.None);
        Assert.Equal(-150, Long(monthly!.Value, "storage_delta_bytes"));
    }

}
