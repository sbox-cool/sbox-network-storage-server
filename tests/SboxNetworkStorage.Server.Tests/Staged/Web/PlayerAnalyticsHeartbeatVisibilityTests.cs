using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;
using SboxNetworkStorage.Application.Errors;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Locks in the heartbeat-visibility fix (2026-06-21). Heartbeats were stored
/// every ~30s but were invisible on the player detail page:
///   1. The default timeline hides routine heartbeats as "noise" — correct, but
///      the only way to see them was the "Show N hidden noisy events" toggle.
///   2. The session-journey card never surfaced heartbeat presence, and its
///      <c>durationSeconds</c> read <c>sessionSeconds</c> from the wrong nesting
///      level (<see cref="NativeStatsHeartbeatHandler"/> re-enveloped the inner
///      payload), so heartbeat-derived duration was always 0.
/// These tests assert heartbeats are stored, hidden-by-default-but-revealable,
/// counted on the session card, and that duration resolves for both the fixed
/// flat payload shape and the legacy double-nested shape.
/// </summary>
public sealed class PlayerAnalyticsHeartbeatVisibilityTests
{
    private const string ProjectId = "proj_hbvis";
    private const string SteamId = "76561198363609085";
    private const long Owner = 1;

    private sealed class FixedClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (InMemoryNetworkStorageStore Store, PlayerAnalyticsIngester Ingester, FixedClock Clock) NewHarness()
    {
        var store = new InMemoryNetworkStorageStore();
        // Anchor inside the read window: the reader ceilings at real UtcNow.
        var clock = new FixedClock(DateTimeOffset.UtcNow.AddMinutes(-20));
        return (store, new PlayerAnalyticsIngester(store, clock, new AnalyticsIngestionFailureTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalyticsIngestionFailureTracker>.Instance), NullLogger<PlayerAnalyticsIngester>.Instance), clock);
    }

    private static async Task<Dictionary<string, object?>> ReadAsync(InMemoryNetworkStorageStore store, bool noise)
    {
        var reader = new StorePlayerAnalyticsReader(store);
        var query = new PlayerTimelineQuery(30, null, "session", noise, new LedgerInsightQuery());
        return (Dictionary<string, object?>)(await reader.GetPlayerAnalyticsAsync(Owner, ProjectId, SteamId, null, query, default))!;
    }

    private static int CountHeartbeatRows(Dictionary<string, object?> result)
    {
        var timeline = (List<Dictionary<string, object?>>)result["timeline"]!;
        return timeline.Count(ev => TypeOf(ev) == "session.heartbeat");
    }

    private static string TypeOf(Dictionary<string, object?> ev)
    {
        if (!ev.TryGetValue("type", out var t) || t is null) return string.Empty;
        return t switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } j => j.GetString() ?? string.Empty,
            _ => t.ToString() ?? string.Empty,
        };
    }

    private static List<Dictionary<string, object?>> Journey(Dictionary<string, object?> result)
        => (List<Dictionary<string, object?>>)result["sessionJourney"]!;

    /// <summary>The fixed handler emits a flat inner payload: { sessionSeconds }.</summary>
    private async Task EmitFlatHeartbeatAsync(PlayerAnalyticsIngester ingester, string sessionId, long sessionSeconds)
        => await ingester.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.heartbeat",
            new Dictionary<string, object> { ["steamId"] = SteamId, ["sessionId"] = sessionId, ["sessionSeconds"] = sessionSeconds },
            null, default);

    [Fact]
    public async Task Heartbeats_AreStored_HiddenByDefault_RevealedWithNoise()
    {
        var (store, ingester, clock) = NewHarness();
        await ingester.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.join",
            new Dictionary<string, object> { ["steamId"] = SteamId, ["sessionId"] = "s1" }, null, default);
        for (var i = 1; i <= 6; i++)
        {
            clock.Now = clock.Now.AddSeconds(30);
            await EmitFlatHeartbeatAsync(ingester, "s1", i * 30);
        }

        // Stored: all 7 events (1 join + 6 heartbeats) are persisted.
        Assert.Equal(7, await store.CountPlayerEventsAsync(ProjectId, SteamId, default));

        // Default view: heartbeats classified as noise and hidden, but counted.
        var hidden = await ReadAsync(store, noise: false);
        Assert.Equal(6, Convert.ToInt32(hidden["hiddenTimelineCount"]));
        Assert.Equal(0, CountHeartbeatRows(hidden));

        // Noise toggle on: every heartbeat is now visible on the timeline.
        var shown = await ReadAsync(store, noise: true);
        Assert.Equal(6, CountHeartbeatRows(shown));
    }

    [Fact]
    public async Task SessionCard_SurfacesHeartbeatCountAndDuration()
    {
        var (store, ingester, clock) = NewHarness();
        await ingester.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.join",
            new Dictionary<string, object> { ["steamId"] = SteamId, ["sessionId"] = "s1" }, null, default);
        for (var i = 1; i <= 8; i++)
        {
            clock.Now = clock.Now.AddSeconds(30);
            await EmitFlatHeartbeatAsync(ingester, "s1", i * 30);
        }

        var result = await ReadAsync(store, noise: false);
        var session = Journey(result).Single();

        Assert.Equal(8, Convert.ToInt32(session["heartbeatCount"]));
        // Duration derives from the latest heartbeat's cumulative seconds (8 * 30).
        Assert.Equal(240, Convert.ToInt32(session["durationSeconds"]));
        Assert.Equal(240, Convert.ToInt32(session["lastHeartbeatSeconds"]));
    }

    [Fact]
    public async Task LegacyNestedPayload_StillResolvesDuration()
    {
        // Pre-fix data double-nested sessionSeconds under payload.payload. The
        // engine must still resolve it so existing players show a real duration.
        var (store, ingester, clock) = NewHarness();
        for (var i = 1; i <= 4; i++)
        {
            clock.Now = clock.Now.AddSeconds(30);
            await ingester.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.heartbeat",
                new Dictionary<string, object>
                {
                    ["steamId"] = SteamId,
                    ["sessionId"] = "s1",
                    ["payload"] = new Dictionary<string, object> { ["sessionSeconds"] = i * 30 },
                }, null, default);
        }

        var result = await ReadAsync(store, noise: false);
        var session = Journey(result).Single();

        Assert.Equal(4, Convert.ToInt32(session["heartbeatCount"]));
        Assert.Equal(120, Convert.ToInt32(session["durationSeconds"]));
    }

    [Fact]
    public async Task SessionCard_SurfacesFps_WhenHeartbeatCarriesFps()
    {
        // Once the handler forwards FPS, the session card must surface it: a
        // heartbeat whose payload carries fps:{average,min,max} drives avgFps/peakFps.
        var (store, ingester, clock) = NewHarness();
        for (var i = 1; i <= 5; i++)
        {
            clock.Now = clock.Now.AddSeconds(30);
            await ingester.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.heartbeat",
                new Dictionary<string, object>
                {
                    ["steamId"] = SteamId,
                    ["sessionId"] = "s1",
                    ["sessionSeconds"] = i * 30,
                    ["fps"] = new Dictionary<string, object> { ["average"] = 60, ["min"] = 41, ["max"] = 72 },
                }, null, default);
        }

        var session = Journey(await ReadAsync(store, noise: false)).Single();
        Assert.Equal(60, Convert.ToInt32(session["avgFps"]));
        Assert.Equal(72, Convert.ToInt32(session["peakFps"]));
    }


    [Fact]
    public async Task LiveSessionSortsAboveDeadSessions_ThatStartedMoreRecently()
    {
        // Regression (2026-06-21): BuildSessionJourney sorted by startedAt desc.
        // A live session that started 30m ago with 300 heartbeats from seconds ago
        // was pushed below dead sessions that started 7m ago with 1 heartbeat,
        // because 7m > 30m in descending startedAt order. The fix sorts by
        // updatedAt (most recent activity) so the live session appears first.
        var (store, ingester, clock) = NewHarness();

        // Live session: started 30m ago, heartbeats every 30s, most recent = ~now.
        // All timestamps MUST be in the past relative to real UtcNow — the reader
        // uses DateTimeOffset.UtcNow for its query upper bound and will exclude
        // future-dated events.
        var now = DateTimeOffset.UtcNow;
        // Live session: 60 heartbeats from 30m ago to ~30s ago (every 30s).
        // The most recent heartbeat is ~30s ago — more recent than any dead session.
        for (var i = 0; i < 60; i++)
        {
            clock.Now = now.AddMinutes(-30).AddSeconds(i * 30);
            await ingester.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.heartbeat",
                new Dictionary<string, object>
                {
                    ["steamId"] = SteamId,
                    ["sessionId"] = "live-session",
                    ["sessionSeconds"] = 1800 + i * 30,
                    ["fps"] = new Dictionary<string, object> { ["average"] = 71 },
                }, null, default);
        }

        // Dead sessions: started 7m ago, 18m ago, 1h ago, 2h ago — each with 1 heartbeat.
        // These have a NEWER startedAt than the live session (7m < 30m) but older
        // updatedAt (no recent activity). Before the fix they sorted above the live
        // session; after the fix the live session sorts first by updatedAt.
        var deadTimes = new[] { -7, -18, -60, -120 };
        foreach (var offsetMin in deadTimes)
        {
            clock.Now = now.AddMinutes(offsetMin);
            await ingester.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.heartbeat",
                new Dictionary<string, object>
                {
                    ["steamId"] = SteamId,
                    ["sessionId"] = $"dead-{offsetMin}",
                    ["sessionSeconds"] = 100,
                    ["fps"] = new Dictionary<string, object> { ["average"] = 60 },
                }, null, default);
        }

        var result = await ReadAsync(store, noise: false);
        var sessions = Journey(result);

        // The live session must be first — it has the most recent activity
        Assert.NotEmpty(sessions);
        var first = sessions[0];
        Assert.Equal("live-session", first["sessionId"]?.ToString());
        Assert.Equal(60, Convert.ToInt32(first["heartbeatCount"]));
        Assert.True(Convert.ToInt32(first["durationSeconds"]) >= 1800,
            $"expected durationSeconds >= 1800, got {first["durationSeconds"]}");
    }
}
