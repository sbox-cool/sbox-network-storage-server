using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// The stats heartbeat is a ~2s-interval best-effort presence ping. A slow or
/// unavailable store must never hold the worker or emit a 500 (the source of the
/// <c>POST /v3/storage/*/stats/heartbeat</c> 15s workspace-write TaskCanceledException).
/// </summary>
public sealed class NativeStatsHeartbeatHandlerTests
{
    private const string ApiKey = "sk-hb";
    private const string ProjectId = "demo-project";
    private const string SteamId = "76561198000000000";

    private static NativeStatsHeartbeatHandler Handler(INetworkStorageDataPlane dataPlane, IPlayerAnalyticsService? analytics = null) =>
        Handler(dataPlane, analytics, new HeartbeatFailureThrottle(Bounded(), TimeProvider.System));

    private static NativeStatsHeartbeatHandler Handler(
        INetworkStorageDataPlane dataPlane, IPlayerAnalyticsService? analytics, HeartbeatFailureThrottle throttle) =>
        new(new FakeResolver(ApiKey, ProjectId), dataPlane, analytics ?? new CapturingAnalyticsService(),
            throttle, new HeartbeatAnalyticsGate(Bounded()));

    private static MemoryCache Bounded() => new(new MemoryCacheOptions { SizeLimit = 100 });

    [Fact]
    public async Task RotatingSteamIds_WithAWrongKey_ShareOneThrottleBucket()
    {
        var handler = Handler(new FakeDataPlane(), null, new HeartbeatFailureThrottle(Bounded(), TimeProvider.System));
        for (var i = 0; i < 30; i++)
        {
            var failed = await handler.RunAsync(ProjectId, "wrong-key", $"7656119800000{i:D4}", null, CancellationToken.None, clientIp: "203.0.113.9");
            Assert.Equal(401, failed.StatusCode);
        }

        var blocked = await handler.RunAsync(ProjectId, "wrong-key", "76561198999999999", null, CancellationToken.None, clientIp: "203.0.113.9");
        Assert.Equal(429, blocked.StatusCode);

        // Another address, and a correct key from the blocked address's project, are judged separately.
        var otherIp = await handler.RunAsync(ProjectId, ApiKey, SteamId, null, CancellationToken.None, clientIp: "203.0.113.10");
        Assert.Equal(200, otherIp.StatusCode);
    }

    [Fact]
    public async Task SuccessfulWrite_ReturnsPersistedTrue()
    {
        var result = await Handler(new FakeDataPlane()).RunAsync(ProjectId, ApiKey, SteamId, null, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.True(json.GetProperty("persisted").GetBoolean());
    }

    [Fact]
    public async Task StoreWriteFailure_DegradesToSoftOkNot500()
    {
        var result = await Handler(new FakeDataPlane(throwOnWrite: true)).RunAsync(ProjectId, ApiKey, SteamId, null, CancellationToken.None);

        // Best-effort: a store failure must NOT become a 500; it returns a fast soft-ok.
        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.False(json.GetProperty("persisted").GetBoolean());
    }

    [Fact]
    public async Task MissingApiKey_Returns401()
    {
        var result = await Handler(new FakeDataPlane()).RunAsync(ProjectId, null, SteamId, null, CancellationToken.None);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task MissingSteamId_Returns400()
    {
        var result = await Handler(new FakeDataPlane()).RunAsync(ProjectId, ApiKey, null, null, CancellationToken.None);
        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_RecordKey_PassesStoreValidation_AndPersists()
    {
        // Regression (2026-06-21): the handler keyed the player-stats record as
        // "{steamId}.json" (a workspace-era filename). After the store cutover the
        // data plane rejects any record key containing '.', so every heartbeat
        // read+write threw ArgumentException and the handler returned
        // persisted:false — silently freezing player-stats. The no-op FakeDataPlane
        // hid this; ValidatingDataPlane mirrors INetworkStorageStore.ValidateRecordKey.
        var plane = new ValidatingDataPlane();
        var result = await Handler(plane).RunAsync(ProjectId, ApiKey, SteamId, null, CancellationToken.None);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(
            json.GetProperty("persisted").GetBoolean(),
            "heartbeat must persist; a record key the data plane rejects regresses to persisted:false");
        Assert.Equal(SteamId, plane.LastWrittenKey);
        Assert.DoesNotContain('.', plane.LastWrittenKey!);
    }

    [Fact]
    public async Task Heartbeat_EmitsAnalyticsEvent_WithStableSessionId()
    {
        // Bug: heartbeats carried no sessionId, so UpdateSessionAsync was skipped
        // (IngestAsync gates on non-empty sessionId) and session rows went stale.
        // Fix: the handler synthesizes "hb:{steamId}" so heartbeats reattach.
        var analytics = new CapturingAnalyticsService();
        await Handler(new FakeDataPlane(), analytics).RunAsync(ProjectId, ApiKey, SteamId, null, CancellationToken.None);

        Assert.Single(analytics.EndpointEvents);
        var evt = analytics.EndpointEvents[0];
        Assert.Equal("session.heartbeat", evt.EventType);
        Assert.Equal("stats-heartbeat", evt.EndpointSlug);
        Assert.NotNull(evt.Payload);
        Assert.True(evt.Payload!.TryGetValue("sessionId", out var sid));
        Assert.Equal($"hb:{SteamId}", sid.ToString());
    }

    [Fact]
    public async Task Heartbeat_UsesClientSessionId_AndSessionSeconds_FromBody()
    {
        // The client reports its real session id and cumulative session seconds in
        // the heartbeat body. These must flow through (previously hardcoded to
        // "hb:{steamId}" and 2s), so heartbeats group with the rest of the play
        // session and playtime is accurate.
        var analytics = new CapturingAnalyticsService();
        var body = JsonSerializer.SerializeToElement(new { steamId = SteamId, sessionId = "sess-abc", sessionSeconds = 152909.5, @event = "heartbeat" });
        await Handler(new FakeDataPlane(), analytics).RunAsync(ProjectId, ApiKey, SteamId, body, CancellationToken.None);

        var evt = Assert.Single(analytics.EndpointEvents);
        Assert.Equal("session.heartbeat", evt.EventType);
        Assert.True(evt.Payload!.TryGetValue("sessionId", out var sid));
        Assert.Equal("sess-abc", sid.ToString());
        // The handler emits a FLAT inner payload (sessionSeconds at the top level),
        // not a re-enveloped { payload: { sessionSeconds } }. Re-enveloping caused
        // double-nesting under payload.payload where UpdateProfileAsync and the
        // session-journey duration builder couldn't read it (durationSeconds=0 bug).
        Assert.True(evt.Payload!.TryGetValue("sessionSeconds", out var ss));
        Assert.Equal(152909L, Convert.ToInt64(ss));
    }
    [Fact]
    public async Task Heartbeat_AnalyticsWrite_SurvivesRequestCancellation()
    {
        // fire-and-forget analytics write. The 200 response flushed and the
        // request completed immediately, RequestAborted fired, and the in-flight
        // The store write was cancelled before it committed. Over an hour of 30s
        // heartbeats, only the ones that completed fast enough landed; the rest
        // silently vanished — which is why the dashboard showed "last event 1h
        // ago" despite the client sending heartbeats continuously. The fix uses
        // CancellationToken.None so the write outlives the request, matching the
        // endpoint-execution fire-and-forget pattern.
        var analytics = new SlowAnalyticsService();
        var handler = Handler(new FakeDataPlane(), analytics);

        // Simulate the request: handler returns 200, then the request's
        // CancellationToken is cancelled (as ASP.NET Core does when the response
        // completes and the request is torn down).
        using var requestCts = new CancellationTokenSource();
        var result = await handler.RunAsync(ProjectId, ApiKey, SteamId, null, requestCts.Token);
        Assert.Equal(200, result.StatusCode);
        // Cancel the request token the moment the handler returns, exactly as
        // the framework does when the response is flushed.
        requestCts.Cancel();

        // The analytics write must still complete. The assertion is completion,
        // not latency: allow for slow, contended CI runners.
        await analytics.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(analytics.WasCalled, "analytics RecordEndpointEventAsync must be called");
        Assert.True(analytics.Token.IsCancellationRequested == false,
            "the analytics write must receive a non-cancellable token, not the request's aborted token");
        Assert.Equal("session.heartbeat", analytics.EventType);
    }

    private sealed class SlowAnalyticsService : IPlayerAnalyticsService
    {
        public TaskCompletionSource WasCalledTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public string EventType { get; private set; } = "";
        public bool WasCalled;
        private int _callCount;

        public Task RecordEventAsync(PlayerEventRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task RecordEndpointEventAsync(
            string projectId, string steamId, string endpointSlug,
            string eventType, IReadOnlyDictionary<string, object>? payload,
            IReadOnlyList<TrackedFieldDelta>? trackedFieldDeltas,
            CancellationToken cancellationToken)
        {
            // First call is the join? No — handler emits heartbeat by default.
            // Simulate a store round-trip that takes longer than the
            // request lifetime (the realistic cause of the cancellation).
            Token = cancellationToken;
            EventType = eventType;
            WasCalled = true;
            WasCalledTcs.TrySetResult();
            // Simulate async store I/O. The request will cancel during this delay.
            await Task.Delay(150, cancellationToken);
            Completed.TrySetResult();
        }
    }

    [Fact]
    public async Task Heartbeat_JoinEvent_RecordsSessionJoin()
    {
        var analytics = new CapturingAnalyticsService();
        var body = JsonSerializer.SerializeToElement(new { steamId = SteamId, sessionId = "sess-join", @event = "join" });
        await Handler(new FakeDataPlane(), analytics).RunAsync(ProjectId, ApiKey, SteamId, body, CancellationToken.None);

        var evt = Assert.Single(analytics.EndpointEvents);
        Assert.Equal("session.join", evt.EventType);
        Assert.True(evt.Payload!.TryGetValue("sessionId", out var sid));
        Assert.Equal("sess-join", sid.ToString());
    }

    [Fact]
    public async Task Heartbeat_ForwardsFps_FromBody()
    {
        // Regression (2026-06-21): the handler hardcoded the analytics payload to
        // {steamId, sessionId, sessionSeconds} and dropped any client-reported FPS,
        // so FPS never reached player_analytics_events or the dashboard. A scalar
        // fps:60 must be forwarded (normalized to { average }); an object passes through.
        var analytics = new CapturingAnalyticsService();
        var body = JsonSerializer.SerializeToElement(new { steamId = SteamId, sessionId = "s", @event = "heartbeat", fps = 60 });
        await Handler(new FakeDataPlane(), analytics).RunAsync(ProjectId, ApiKey, SteamId, body, CancellationToken.None);

        var evt = Assert.Single(analytics.EndpointEvents);
        Assert.NotNull(evt.Payload);
        Assert.True(evt.Payload!.TryGetValue("fps", out var fps), "heartbeat must forward client FPS to analytics");
        var fpsDict = Assert.IsType<Dictionary<string, object>>(fps);
        Assert.Equal(60d, Convert.ToDouble(fpsDict["average"], System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class FakeResolver(string key, string project) : IStorageApiKeyResolver
    {
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string projectId, CancellationToken cancellationToken)
            => Task.FromResult(apiKey == key && projectId == project
                ? new StorageApiKeyAuthResult(42, projectId, true, "secret")
                : null);
    }

    private sealed class FakeDataPlane(bool throwOnWrite = false) : INetworkStorageDataPlane
    {
        public Task<RecordReadResult> ReadRecordAsync(long ownerUserId, string projectId, string collectionId, string recordKey, CancellationToken ct)
            => Task.FromResult(RecordReadResult.NotFound);

        public Task WriteRecordAsync(long ownerUserId, string projectId, string collectionId, string recordKey, JsonElement value, CancellationToken ct)
            => throwOnWrite ? Task.FromException(new InvalidOperationException("store unavailable")) : Task.CompletedTask;

        public Task DeleteRecordAsync(long ownerUserId, string projectId, string collectionId, string recordKey, CancellationToken ct)
            => Task.CompletedTask;
    }

    // Mirrors INetworkStorageStore.ValidateRecordKey: the production data plane
    // throws on any record key outside ^[a-zA-Z0-9_:-]{1,256}$. The no-op
    // FakeDataPlane accepts every key and so hides record-key regressions; this
    // one surfaces them the way real store does.
    private sealed class ValidatingDataPlane : INetworkStorageDataPlane
    {
        private static readonly System.Text.RegularExpressions.Regex RecordKeyPattern =
            new("^[a-zA-Z0-9_:-]{1,256}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        public string? LastWrittenKey { get; private set; }

        private static void Validate(string recordKey)
        {
            if (string.IsNullOrEmpty(recordKey) || !RecordKeyPattern.IsMatch(recordKey))
                throw new ArgumentException($"Invalid record key '{recordKey}'.");
        }

        public Task<RecordReadResult> ReadRecordAsync(long ownerUserId, string projectId, string collectionId, string recordKey, CancellationToken ct)
        {
            Validate(recordKey);
            return Task.FromResult(RecordReadResult.NotFound);
        }

        public Task WriteRecordAsync(long ownerUserId, string projectId, string collectionId, string recordKey, JsonElement value, CancellationToken ct)
        {
            Validate(recordKey);
            LastWrittenKey = recordKey;
            return Task.CompletedTask;
        }

        public Task DeleteRecordAsync(long ownerUserId, string projectId, string collectionId, string recordKey, CancellationToken ct)
        {
            Validate(recordKey);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingAnalyticsService : IPlayerAnalyticsService
    {
        public List<EndpointEventRecord> EndpointEvents { get; } = new();

        public Task RecordEventAsync(PlayerEventRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordEndpointEventAsync(
            string projectId, string steamId, string endpointSlug,
            string eventType, IReadOnlyDictionary<string, object>? payload,
            IReadOnlyList<TrackedFieldDelta>? trackedFieldDeltas,
            CancellationToken cancellationToken)
        {
            EndpointEvents.Add(new EndpointEventRecord(projectId, steamId, endpointSlug, eventType, payload));
            return Task.CompletedTask;
        }
    }

    public sealed record EndpointEventRecord(
        string ProjectId, string SteamId, string EndpointSlug,
        string EventType, IReadOnlyDictionary<string, object>? Payload);
}

internal static class HeartbeatHandlerTestExtensions
{
    /// <summary>Authenticates from the key, then runs the heartbeat, as the endpoint does.</summary>
    public static async Task<NativeHeartbeatResult> RunAsync(
        this NativeStatsHeartbeatHandler handler, string projectId, string? apiKey, string? steamId,
        JsonElement? body, CancellationToken ct, string clientIp = "198.51.100.1")
    {
        var authentication = await handler.AuthenticateAsync(projectId, apiKey, clientIp, ct);
        return authentication.Auth is { } auth
            ? await handler.ExecuteAsync(projectId, auth, steamId, body, ct)
            : authentication.Rejection!;
    }
}
