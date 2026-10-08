using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;
using SboxNetworkStorage.Application.Errors;

using SboxNetworkStorage.Server.Tests.Support;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Locks in the fix for the 2026-06-20 production incident where the per-player
/// analytics dashboard showed nothing for active players even though their saves
/// were persisting. Two independent ingest crashes (both swallowed best-effort,
/// so they silently dropped events) were responsible:
///
///   1. <b>Undefined payload</b> — every <c>save-all</c> / endpoint-call / tracked
///      -field-delta event was recorded with <c>payload: null</c>, which became
///      <c>default(JsonElement)</c> (ValueKind=Undefined). Serializing that threw
///      <see cref="System.InvalidOperationException"/> in
///      <see cref="PlayerAnalyticsIngester"/> <i>before</i> the event insert, so
///      the event and its ledger deltas were never stored.
///   2. <b>Corrupt JSON column</b> — a blank/whitespace <c>player_sessions</c>
///      metrics column made <see cref="SboxNetworkStorage.Storage.Relational.RowJson.ParseJsonColumn"/>
///      throw <see cref="JsonException"/> during the session read inside ingest,
///      aborting every session-category event (every heartbeat).
///
/// Plus the visibility fix: endpoint executions are recorded as <c>endpoint.call</c>
/// (shown on the timeline) instead of <c>session.heartbeat</c> (hidden as noise),
/// and the player display-name is captured onto the profile.
/// </summary>
public sealed class PlayerAnalyticsIngestRobustnessTests
{
    private const string ProjectId = "proj_robust";
    private const string SteamId = "76561198363609085";

    private static PlayerAnalyticsIngester NewIngester(InMemoryNetworkStorageStore store)
        => new(store, TimeProvider.System, new AnalyticsIngestionFailureTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalyticsIngestionFailureTracker>.Instance), NullLogger<PlayerAnalyticsIngester>.Instance);

    [Fact]
    public async Task EndpointEvent_WithNullPayload_IsStored_NotSilentlyDropped()
    {
        // Bug 1: before the fix, a null payload → Undefined JsonElement → the
        // ingester threw while serializing the event payload (before the insert),
        // so save-all/endpoint events never reached ScyllaDB.
        var store = new InMemoryNetworkStorageStore();
        var analytics = NewIngester(store);

        await analytics.RecordEndpointEventAsync(
            ProjectId, SteamId,
            endpointSlug: "save-all",
            eventType: "endpoint.call",
            payload: null,
            trackedFieldDeltas: null,
            cancellationToken: CancellationToken.None);

        Assert.Single(store.PlayerAnalyticsEvents);
    }

    [Fact]
    public async Task EndpointEvent_WithNullPayload_AndTrackedDeltas_WritesLedgerEntry()
    {
        // Bug 1, executor shape: ApplyPostFlushProjectionsAsync records the stat
        // progression with payload:null + tracked-field deltas. The crash dropped
        // the ledger entries that feed the dashboard progression chart.
        var store = new InMemoryNetworkStorageStore();
        var analytics = NewIngester(store);

        var deltas = new List<TrackedFieldDelta>
        {
            new(Field: "totalLevel", CollectionId: "players", Before: 15, After: 16, Delta: 1, Source: "save-all"),
        };

        await analytics.RecordEndpointEventAsync(
            ProjectId, SteamId,
            endpointSlug: "save-all",
            eventType: "endpoint.call",
            payload: null,
            trackedFieldDeltas: deltas,
            cancellationToken: CancellationToken.None);

        Assert.Single(store.PlayerAnalyticsEvents);
        var ledger = await store.ListLedgerEntriesAsync(ProjectId, "players", SteamId, CancellationToken.None);
        Assert.Single(ledger);
    }

    [Fact]
    public async Task EndpointCall_IsRecordedAsEndpointCall_AndCapturesPlayerName()
    {
        // Visibility + username capture: a save-all execution records an
        // `endpoint.call` event (not a hidden heartbeat) and persists the
        // player's display name onto the profile.
        var store = new InMemoryNetworkStorageStore();
        var analytics = NewIngester(store);

        await analytics.RecordEndpointEventAsync(
            ProjectId, SteamId,
            endpointSlug: "save-all",
            eventType: "endpoint.call",
            payload: new Dictionary<string, object>
            {
                ["label"] = "save-all",
                ["playerName"] = "seabug",
                ["status"] = 200,
                ["method"] = "POST",
            },
            trackedFieldDeltas: null,
            cancellationToken: CancellationToken.None);

        var events = await store.ListPlayerEventsAsync(ProjectId, SteamId, 0, long.MaxValue, 50, CancellationToken.None);
        var ev = Assert.Single(events);
        Assert.Equal("endpoint.call", ev.GetProperty("event_type").GetString());
        Assert.Equal("endpoint", ev.GetProperty("category").GetString());

        var profile = await store.ReadPlayerProfileAsync(ProjectId, SteamId, CancellationToken.None);
        Assert.NotNull(profile);
        Assert.Equal("seabug", profile.Value.GetProperty("player_name").GetString());
        Assert.Equal("endpoint.call", profile.Value.GetProperty("last_event_type").GetString());
        Assert.True(profile.Value.GetProperty("is_online").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("not json at all")]
    [InlineData("{ broken")]
    public void ParseJsonColumn_ReturnsNull_ForBlankOrCorruptValues(string? value)
    {
        // Bug 2: a blank/whitespace/corrupt JSON column must degrade to null, never
        // throw and abort the enclosing analytics read/ingest.
        Assert.Null(SboxNetworkStorage.Storage.Relational.RowJson.ParseJsonColumn(value));
    }

    [Fact]
    public void ParseJsonColumn_ParsesValidJson()
    {
        var result = SboxNetworkStorage.Storage.Relational.RowJson.ParseJsonColumn("{\"a\":1}");
        Assert.NotNull(result);
        Assert.Equal(1, result.Value.GetProperty("a").GetInt32());
    }

    [Fact]
    public async Task CountPlayerEventsAsync_CountsOnlyThatPlayer()
    {
        var store = new InMemoryNetworkStorageStore();
        var analytics = NewIngester(store);
        for (var i = 0; i < 3; i++)
            await analytics.RecordEndpointEventAsync(ProjectId, SteamId, "save-all", "endpoint.call", null, null, CancellationToken.None);
        await analytics.RecordEndpointEventAsync(ProjectId, "999999", "save-all", "endpoint.call", null, null, CancellationToken.None);

        Assert.Equal(3, await store.CountPlayerEventsAsync(ProjectId, SteamId, CancellationToken.None));
        Assert.Equal(1, await store.CountPlayerEventsAsync(ProjectId, "999999", CancellationToken.None));
    }

    [Fact]
    public async Task DetailEventCount_ReflectsActualEvents_NotDriftedCounter()
    {
        // The dashboard "N events" badge must equal the real number of stored
        // events, never the drift-prone managed_counters_json.__totalEvents (which
        // historically read 2 for a player with thousands of events).
        var store = new InMemoryNetworkStorageStore();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            var ts = now.AddMinutes(-i).ToUnixTimeMilliseconds();
            await store.SeedPlayerEventAsync(ProjectId, SteamId, ts, $"e{i}", "session.heartbeat", "session", "", "", "", new
            {
                steamId = SteamId, type = "session.heartbeat",
                ts = now.AddMinutes(-i).ToString("o"), category = "session", sessionId = "s1",
            });
        }
        // Deliberately drifted counter: says 2 despite 5 real events.
        await store.SeedPlayerProfileAsync(ProjectId, SteamId, "seabug", now.ToUnixTimeMilliseconds(), isOnline: true,
            lastEventType: "session.heartbeat", managedCountersJson: "{\"__totalEvents\":2}");

        var reader = new ScyllaPlayerAnalyticsReader(store);
        var query = new PlayerTimelineQuery(30, null, "session", false, new LedgerInsightQuery());
        var result = (Dictionary<string, object?>)(await reader.GetPlayerAnalyticsAsync(
            1, ProjectId, SteamId, null, query, CancellationToken.None))!;

        Assert.Equal(5L, Convert.ToInt64(result["eventCount"]));
    }

    [Fact]
    public async Task SessionlessEndpointEvent_BackfillsSessionId_FromCurrentSession()
    {
        // save-all/load-player endpoint calls carry no sessionId in their body, so
        // they used to land under "no-session" ("Activity outside sessions").
        // After a heartbeat establishes the active session, a sessionless
        // endpoint.call must inherit that session id and group with it.
        var store = new InMemoryNetworkStorageStore();
        var analytics = NewIngester(store);

        await analytics.RecordEndpointEventAsync(ProjectId, SteamId, "stats-heartbeat", "session.heartbeat",
            new Dictionary<string, object>
            {
                ["sessionId"] = "sess-live",
                ["type"] = "session.heartbeat",
                ["category"] = "session",
                ["payload"] = new Dictionary<string, object> { ["sessionSeconds"] = 5 },
            },
            null, CancellationToken.None);

        await analytics.RecordEndpointEventAsync(ProjectId, SteamId, "save-all", "endpoint.call",
            new Dictionary<string, object> { ["label"] = "save-all" }, null, CancellationToken.None);

        var events = await store.ListPlayerEventsAsync(ProjectId, SteamId, 0, long.MaxValue, 50, CancellationToken.None);
        var save = events.First(e => e.GetProperty("event_type").GetString() == "endpoint.call");
        var payload = save.GetProperty("payload_json");
        Assert.Equal("sess-live", payload.GetProperty("sessionId").GetString());
    }

    [Fact]
    public void SessionJourney_ReadsScyllaSessionRowShape_AndMarksLive()
    {
        // The live session row from ScyllaDB is snake_case + unix-ms; the journey
        // builder (a Bun port that read camelCase/ISO) must understand it, else
        // every session collapses to "no-session" and the active session never
        // surfaces on the dashboard.
        var now = DateTimeOffset.UtcNow;
        var startedMs = now.AddMinutes(-10).ToUnixTimeMilliseconds();
        var hbMs = now.AddMinutes(-1).ToUnixTimeMilliseconds();
        var session = JsonSerializer.SerializeToElement(new
        {
            session_id = "sess-live",
            started_at_unix_ms = startedMs,
            last_heartbeat_at_unix_ms = hbMs,
            ended_at_unix_ms = (long?)null,
        });
        // PlayerAnalyticsEvent wraps the stored payload_json (inner object whose
        // top-level fields are sessionId/type/ts/...), not the DB row envelope.
        var hb = PlayerAnalyticsEvent.From(JsonSerializer.SerializeToElement(new
        {
            steamId = SteamId, type = "session.heartbeat", ts = now.AddMinutes(-1).ToString("o"),
            label = "", source = "network-storage-library", category = "session",
            endpointSlug = "stats-heartbeat", collectionId = (string?)null,
            sessionId = "sess-live", payload = new { sessionSeconds = 600 },
        }))!;

        var journey = PlayerTimelineEngine.BuildSessionJourney(new[] { session }, new[] { hb });

        var entry = Assert.Single(journey);
        Assert.NotNull(entry["startedAt"]);          // unix-ms converted to ISO
        Assert.Null(entry["endedAt"]);               // live (no end)
        Assert.Equal(1, Convert.ToInt32(entry["eventCount"])); // heartbeat grouped under the real session
    }
}
