using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;
using SboxNetworkStorage.Application.Errors;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Reproduces and locks in the fix for the "Online + 0 events + no session
/// activity" bug (2026-06-20). Root cause: the heartbeat handler emitted no
/// sessionId, so:
///   1. <see cref="PlayerAnalyticsIngester.IngestAsync"/> skipped
///      <see cref="PlayerAnalyticsIngester.UpdateSessionAsync"/> (gated on
///      non-empty sessionId) → session rows went stale even while heartbeats
///      kept arriving.
///   2. <see cref="PlayerAnalyticsIngester.UpdateProfileAsync"/> only
///      incremented sessionCount on session.join → heartbeat-only players
///      (dedicated servers, reconnects) showed session_count=0 forever.
/// </summary>
public sealed class PlayerAnalyticsHeartbeatSessionTests
{
    private const string ProjectId = "proj_hb";
    private const string SteamId = "76561198000000000";

    [Fact]
    public async Task Heartbeat_WithSessionId_IncrementsSessionCount_OnFirstHeartbeat()
    {
        // Before fix: heartbeats carried no sessionId → session_count stayed 0.
        // After fix: handler sends "hb:{steamId}", and the first heartbeat for
        // a profile with no current_session_id counts as a session start.
        var store = new InMemoryNetworkStorageStore();
        var analytics = new PlayerAnalyticsIngester(store, TimeProvider.System, new AnalyticsIngestionFailureTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalyticsIngestionFailureTracker>.Instance), NullLogger<PlayerAnalyticsIngester>.Instance);

        await analytics.RecordEndpointEventAsync(
            ProjectId, SteamId,
            endpointSlug: "stats-heartbeat",
            eventType: "session.heartbeat",
            payload: new Dictionary<string, object>
            {
                ["steamId"] = SteamId,
                ["sessionId"] = "hb:76561198000000000",
                ["payload"] = new Dictionary<string, object> { ["sessionSeconds"] = 2 },
            },
            trackedFieldDeltas: null,
            cancellationToken: CancellationToken.None);

        var profile = await store.ReadPlayerProfileAsync(ProjectId, SteamId, CancellationToken.None);
        Assert.NotNull(profile);
        Assert.Equal(1, profile.Value.GetProperty("session_count").GetInt64());
        Assert.Equal("hb:76561198000000000", profile.Value.GetProperty("current_session_id").GetString());
    }

    [Fact]
    public async Task Heartbeat_WithSameSessionId_DoesNotIncrementSessionCount_OnSubsequentHeartbeat()
    {
        // Second heartbeat on the same session must NOT double-count.
        var store = new InMemoryNetworkStorageStore();
        var analytics = new PlayerAnalyticsIngester(store, TimeProvider.System, new AnalyticsIngestionFailureTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalyticsIngestionFailureTracker>.Instance), NullLogger<PlayerAnalyticsIngester>.Instance);

        var payload = new Dictionary<string, object>
        {
            ["steamId"] = SteamId,
            ["sessionId"] = "hb:76561198000000000",
            ["payload"] = new Dictionary<string, object> { ["sessionSeconds"] = 4 },
        };

        await analytics.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.heartbeat", payload, null, CancellationToken.None);
        await analytics.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.heartbeat", payload, null, CancellationToken.None);

        var profile = await store.ReadPlayerProfileAsync(ProjectId, SteamId, CancellationToken.None);
        Assert.NotNull(profile);
        Assert.Equal(1, profile.Value.GetProperty("session_count").GetInt64());
    }

    [Fact]
    public async Task Heartbeat_WithSessionId_CreatesSessionRow()
    {
        // Before fix: UpdateSessionAsync was skipped because sessionId was null.
        // After fix: the handler sends a sessionId, so a session row is created.
        var store = new InMemoryNetworkStorageStore();
        var analytics = new PlayerAnalyticsIngester(store, TimeProvider.System, new AnalyticsIngestionFailureTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalyticsIngestionFailureTracker>.Instance), NullLogger<PlayerAnalyticsIngester>.Instance);

        await analytics.RecordEndpointEventAsync(
            ProjectId, SteamId,
            endpointSlug: "stats-heartbeat",
            eventType: "session.heartbeat",
            payload: new Dictionary<string, object>
            {
                ["steamId"] = SteamId,
                ["sessionId"] = "hb:76561198000000000",
                ["payload"] = new Dictionary<string, object> { ["sessionSeconds"] = 2 },
            },
            trackedFieldDeltas: null,
            cancellationToken: CancellationToken.None);

        var session = await store.ReadPlayerSessionAsync(ProjectId, SteamId, "hb:76561198000000000", CancellationToken.None);
        Assert.NotNull(session);
        // A heartbeat that creates a new session should set started_at (not null).
        Assert.True(session.Value.TryGetProperty("last_heartbeat_at_unix_ms", out var hb) && hb.ValueKind == JsonValueKind.Number);
    }

    [Fact]
    public async Task Heartbeat_AfterLeave_WithNewSessionId_IncrementsSessionCount()
    {
        // Simulate: join → leave → heartbeat with new session id.
        // The heartbeat after leave should count as a new session.
        var store = new InMemoryNetworkStorageStore();
        var analytics = new PlayerAnalyticsIngester(store, TimeProvider.System, new AnalyticsIngestionFailureTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalyticsIngestionFailureTracker>.Instance), NullLogger<PlayerAnalyticsIngester>.Instance);

        // 1. Join
        await analytics.RecordEndpointEventAsync(ProjectId, SteamId, "ep", "session.join",
            new Dictionary<string, object> { ["steamId"] = SteamId, ["sessionId"] = "s1" }, null, CancellationToken.None);

        // 2. Leave
        await analytics.RecordEndpointEventAsync(ProjectId, SteamId, "ep", "session.leave",
            new Dictionary<string, object> { ["steamId"] = SteamId, ["sessionId"] = "s1" }, null, CancellationToken.None);

        // 3. Heartbeat with a different session id (reconnect)
        await analytics.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.heartbeat",
            new Dictionary<string, object> { ["steamId"] = SteamId, ["sessionId"] = "hb:76561198000000000" }, null, CancellationToken.None);

        var profile = await store.ReadPlayerProfileAsync(ProjectId, SteamId, CancellationToken.None);
        Assert.NotNull(profile);
        // join (1) + heartbeat-after-leave (1) = 2
        Assert.Equal(2, profile.Value.GetProperty("session_count").GetInt64());
    }

    [Fact]
    public async Task Heartbeat_IncrementsTotalEventsCounter()
    {
        // The dashboard's "N events" badge reads eventCount, which is backed by
        // __totalEvents in managed_counters_json. Each event must increment it.
        var store = new InMemoryNetworkStorageStore();
        var analytics = new PlayerAnalyticsIngester(store, TimeProvider.System, new AnalyticsIngestionFailureTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalyticsIngestionFailureTracker>.Instance), NullLogger<PlayerAnalyticsIngester>.Instance);

        await analytics.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.heartbeat",
            new Dictionary<string, object> { ["steamId"] = SteamId, ["sessionId"] = "hb:x" }, null, CancellationToken.None);
        await analytics.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.heartbeat",
            new Dictionary<string, object> { ["steamId"] = SteamId, ["sessionId"] = "hb:x" }, null, CancellationToken.None);

        var profile = await store.ReadPlayerProfileAsync(ProjectId, SteamId, CancellationToken.None);
        Assert.NotNull(profile);
        var managedJson = profile.Value.GetProperty("managed_counters_json").GetRawText();
        using var doc = JsonDocument.Parse(managedJson);
        Assert.True(doc.RootElement.TryGetProperty("__totalEvents", out var te));
        Assert.Equal(2, te.GetInt64());
    }
}
