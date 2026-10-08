using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;
using SboxNetworkStorage.Server.Middleware;
using Xunit;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>
/// Task 2.4 (fix-usage-and-query-telemetry): NetworkStorageUsageTracker
/// accumulation/flush/failure semantics and NetworkStorageUsageMiddleware
/// classification (401 skip, suppression, annotation, fallback, byte counting).
/// </summary>
public sealed class UsageTrackerAndMiddlewareTests
{
    private static (NetworkStorageUsageTracker Tracker, InMemoryNetworkStorageStore Store) BuildTracker(InMemoryNetworkStorageStore? store = null)
    {
        store ??= new InMemoryNetworkStorageStore();
        var services = new ServiceCollection();
        services.AddSingleton<INetworkStorageStore>(store);
        var provider = services.BuildServiceProvider();
        var tracker = new NetworkStorageUsageTracker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<NetworkStorageUsageTracker>.Instance);
        return (tracker, store);
    }

    private static long Long(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;

    private static string Month => DateTimeOffset.UtcNow.ToString("yyyy-MM");

    // ── Tracker ──

    [Fact]
    public async Task Track_Then_Flush_Lands_Monthly_Daily_And_Endpoint_Rows()
    {
        var (tracker, store) = BuildTracker();
        tracker.Track("proj1", UsageKind.EndpointCall, "POST", bytesIn: 120, bytesOut: 300, durationMs: 42.4, endpointSlug: "save-all");
        tracker.Track("proj1", UsageKind.EndpointCall, "GET", bytesIn: 0, bytesOut: 80, durationMs: 7.6, endpointSlug: "load-player");
        tracker.Track("proj1", UsageKind.Read, "GET", bytesIn: 0, bytesOut: 50, durationMs: 3);

        await tracker.FlushAsync(force: true, CancellationToken.None);
        Assert.Equal(0, tracker.PendingBucketCount);

        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None);
        Assert.NotNull(monthly);
        Assert.Equal(3, Long(monthly!.Value, "requests"));
        Assert.Equal(2, Long(monthly.Value, "endpoint_calls"));
        // save-all POST => write; load-player GET => read; plain Read => read.
        Assert.Equal(2, Long(monthly.Value, "reads"));
        Assert.Equal(1, Long(monthly.Value, "writes"));
        Assert.Equal(120, Long(monthly.Value, "bytes_in"));
        Assert.Equal(430, Long(monthly.Value, "bytes_out"));
        Assert.Equal(42 + 8 + 3, Long(monthly.Value, "duration_ms_sum"));
        Assert.Equal(3, Long(monthly.Value, "duration_samples"));
        Assert.Equal(43 + 8 + 3, Long(monthly.Value, "compute_units"));

        var endpoints = await store.ReadProjectUsageEndpointsAsync("proj1", Month, 50, CancellationToken.None);
        Assert.Equal(2, endpoints.Count);
        Assert.Contains(endpoints, e => e.GetProperty("endpoint_slug").GetString() == "save-all" && Long(e, "calls") == 1);
        Assert.Contains(endpoints, e => e.GetProperty("endpoint_slug").GetString() == "load-player" && Long(e, "calls") == 1);

        var daily = await store.ReadProjectUsageDailyAsync("proj1", Month, CancellationToken.None);
        var day = Assert.Single(daily);
        Assert.Equal(3, Long(day, "requests"));
        Assert.Equal(43 + 8 + 3, Long(day, "compute_units"));
    }

    [Fact]
    public async Task Failed_Flush_Requeues_Deltas_And_Backs_Off()
    {
        var store = new ThrowOnceStore();
        var (tracker, _) = BuildTracker(store);
        tracker.Track("proj1", UsageKind.Write, "POST", 10, 20, 5);

        // First flush fails; deltas are re-queued.
        await tracker.FlushAsync(force: true, CancellationToken.None);
        Assert.Equal(1, tracker.PendingBucketCount);
        Assert.Null(await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None));

        // Non-forced flush inside the backoff window is a no-op.
        await tracker.FlushAsync(force: false, CancellationToken.None);
        Assert.Equal(1, tracker.PendingBucketCount);

        // Forced flush bypasses backoff; the store now succeeds — nothing lost.
        await tracker.FlushAsync(force: true, CancellationToken.None);
        Assert.Equal(0, tracker.PendingBucketCount);
        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None);
        Assert.Equal(1, Long(monthly!.Value, "requests"));
        Assert.Equal(10, Long(monthly.Value, "bytes_in"));
        var snapshot = tracker.GetSnapshot();
        Assert.Equal(1, snapshot.TotalTrackedRequests);
        Assert.Equal(10, snapshot.TotalTrackedBytesIn);
        Assert.NotNull(snapshot.LastSuccessfulFlushAt);
        Assert.NotNull(snapshot.LastFailedFlushAt);
        Assert.Equal(0, snapshot.ConsecutiveFlushFailures);
    }


    [Fact]
    public async Task Track_During_And_After_Flush_Is_Never_Lost()
    {
        var (tracker, store) = BuildTracker();
        tracker.Track("proj1", UsageKind.Read, "GET", 0, 1, 1);
        await tracker.FlushAsync(force: true, CancellationToken.None);
        tracker.Track("proj1", UsageKind.Read, "GET", 0, 1, 1);
        await tracker.FlushAsync(force: true, CancellationToken.None);

        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None);
        Assert.Equal(2, Long(monthly!.Value, "requests"));
    }

    // ── Middleware ──

    private static DefaultHttpContext BuildContext(string path, string method = "GET", string? routeProjectId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        if (routeProjectId is not null) context.Request.RouteValues["projectId"] = routeProjectId;
        return context;
    }

    private static async Task<InMemoryNetworkStorageStore> RunMiddlewareAsync(DefaultHttpContext context, RequestDelegate handler)
    {
        var (tracker, store) = BuildTracker();
        var middleware = new NetworkStorageUsageMiddleware(
            handler,
            tracker,
            NullLogger<NetworkStorageUsageMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        await tracker.FlushAsync(force: true, CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task Annotated_DataPlane_Request_Is_Metered_With_Response_Bytes()
    {
        var context = BuildContext("/v3/endpoints/proj1/save-all", "POST");
        var payload = Encoding.UTF8.GetBytes("{\"ok\":true}");
        var store = await RunMiddlewareAsync(context, async ctx =>
        {
            NetworkStorageUsageContext.Set(ctx, "proj1", UsageKind.EndpointCall, "save-all");
            ctx.Response.StatusCode = 200;
            await ctx.Response.Body.WriteAsync(payload);
        });

        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None);
        Assert.NotNull(monthly);
        Assert.Equal(1, Long(monthly!.Value, "requests"));
        Assert.Equal(1, Long(monthly.Value, "endpoint_calls"));
        Assert.Equal(payload.Length, Long(monthly.Value, "bytes_out"));
        Assert.Equal(0, Long(monthly.Value, "errors"));

        var endpoints = await store.ReadProjectUsageEndpointsAsync("proj1", Month, 10, CancellationToken.None);
        Assert.Equal("save-all", Assert.Single(endpoints).GetProperty("endpoint_slug").GetString());
    }

    [Fact]
    public async Task Chunked_Request_Body_Bytes_Are_Metered_From_Actual_Reads()
    {
        var context = BuildContext("/v3/storage/proj1/players/765", "POST", routeProjectId: "proj1");
        var payload = Encoding.UTF8.GetBytes("{\"level\":7}");
        context.Request.Body = new MemoryStream(payload);
        context.Request.ContentLength = null;

        var store = await RunMiddlewareAsync(context, async ctx =>
        {
            var buffer = new byte[64];
            Assert.Equal(payload.Length, await ctx.Request.Body.ReadAsync(buffer));
            ctx.Response.StatusCode = 200;
        });

        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None);
        Assert.Equal(payload.Length, Long(monthly!.Value, "bytes_in"));
        Assert.True(Long(monthly.Value, "compute_units") >= 1);
    }

    [Fact]
    public async Task Unauthorized_401_Is_Not_Metered()
    {
        var context = BuildContext("/v3/queries/proj1/query_x", routeProjectId: "proj1");
        var store = await RunMiddlewareAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 401;
            return Task.CompletedTask;
        });
        Assert.Null(await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None));
    }

    [Fact]
    public async Task Suppressed_Request_Is_Not_Metered_Even_On_200()
    {
        var context = BuildContext("/v3/sessions/proj1/create", "POST", routeProjectId: "proj1");
        var store = await RunMiddlewareAsync(context, ctx =>
        {
            NetworkStorageUsageContext.Suppress(ctx); // auth-session failure path (HTTP 200 body error)
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        Assert.Null(await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None));
    }

    [Fact]
    public async Task Unannotated_Request_Falls_Back_To_Route_ProjectId_And_Method_Kind()
    {
        var context = BuildContext("/api/storage/proj1/players/765", "GET", routeProjectId: "proj1");
        var store = await RunMiddlewareAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None);
        Assert.Equal(1, Long(monthly!.Value, "requests"));
        Assert.Equal(1, Long(monthly.Value, "reads"));
        Assert.Equal(0, Long(monthly.Value, "writes"));
    }

    [Theory]
    [InlineData("/pages/proj1/status")]
    [InlineData("/api/pages/proj1/status")]
    public async Task Published_Project_Page_Transfer_Is_Metered(string path)
    {
        var context = BuildContext(path, routeProjectId: "proj1");
        var payload = Encoding.UTF8.GetBytes("{\"players\":12}");
        var store = await RunMiddlewareAsync(context, async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.Body.WriteAsync(payload);
        });

        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None);
        Assert.Equal(1, Long(monthly!.Value, "requests"));
        Assert.Equal(payload.Length, Long(monthly.Value, "bytes_out"));
    }

    [Fact]
    public async Task Error_Statuses_Count_As_Errors()
    {
        var context = BuildContext("/v3/storage/proj1/players/765", "POST", routeProjectId: "proj1");
        var store = await RunMiddlewareAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 500;
            return Task.CompletedTask;
        });
        var monthly = await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None);
        Assert.Equal(1, Long(monthly!.Value, "errors"));
    }

    [Fact]
    public async Task Non_DataPlane_Paths_Are_Ignored()
    {
        var context = BuildContext("/api/storage-browse/proj1/records", routeProjectId: "proj1");
        var store = await RunMiddlewareAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        Assert.Null(await store.ReadProjectUsageMonthlyAsync("proj1", Month, CancellationToken.None));
    }

    /// <summary>Fails the first increment, succeeds afterwards.</summary>
    private sealed class ThrowOnceStore : InMemoryNetworkStorageStore
    {
        private int _calls;
        public override Task IncrementProjectUsageAsync(string projectId, string month, string day, string? endpointSlug, UsageDelta delta, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("ScyllaDB unavailable");
            return base.IncrementProjectUsageAsync(projectId, month, day, endpointSlug, delta, ct);
        }
    }
}
