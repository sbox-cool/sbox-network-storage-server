using System.Globalization;
using System.Text.Json;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Tests.Support;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Unit coverage for the native log / transaction / ledger scan readers
/// (<see cref="ScyllaPlayerAnalyticsReader"/>). After the Bunny → ScyllaDB
/// migration the reader sources storage operations from the
/// <c>player_analytics_events</c> table (category "record") and tracked-field
/// deltas from <c>ledger_entries</c>. The scans are exercised directly against
/// <see cref="InMemoryNetworkStorageStore"/> so the aggregation logic is asserted without HTTP
/// and without any Bunny CDN dependency.
/// </summary>
public sealed class PlayerAnalyticsScanReaderTests
{
    private const long Owner = 42;
    private const string ProjectId = "proj_scan";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // ── Seed helpers ──
    // PORT-ADAPTED: rows are written through the store's public methods (production row shapes)
    // instead of poking the fake's dictionaries. The in-memory store completes synchronously.

    private static InMemoryNetworkStorageStore StoreWithCollection(string collectionId = "col_a", string name = "Players", object? schema = null)
    {
        var store = new InMemoryNetworkStorageStore();
        store.SeedCollectionAsync(ProjectId, collectionId, name, "per-steamid", schema ?? new { }).GetAwaiter().GetResult();
        return store;
    }

    private static void SeedProfile(InMemoryNetworkStorageStore store, string steamId, long lastSeenMs)
        => store.SeedPlayerProfileAsync(ProjectId, steamId, steamId, lastSeenMs).GetAwaiter().GetResult();

    /// <summary>Seed a player_analytics_events row (payload_json stored as an object, as the real store returns it).</summary>
    private static void SeedEvent(
        InMemoryNetworkStorageStore store, string steamId, long tsMs, string eventId, string eventType, string category, string collectionId)
    {
        var iso = DateTimeOffset.FromUnixTimeMilliseconds(tsMs).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        store.SeedPlayerEventAsync(ProjectId, steamId, tsMs, eventId, eventType, category, label: "", endpointSlug: "", collectionId,
            new { steamId, type = eventType, ts = iso, category, collectionId }).GetAwaiter().GetResult();
    }

    private static void SeedStorageEvent(InMemoryNetworkStorageStore store, string steamId, long tsMs, string eventId, string eventType, string collectionId)
        => SeedEvent(store, steamId, tsMs, eventId, eventType, "record", collectionId);

    /// <summary>Seed a ledger_entries row via the real write path (entry_json stored as a JSON string).</summary>
    private static Task SeedLedgerEntry(
        InMemoryNetworkStorageStore store, string collectionId, string steamId, DateTimeOffset ts, string field, double delta, string source)
    {
        var entry = JsonSerializer.SerializeToElement(new
        {
            field, collection_id = collectionId, delta, source,
            steam_id = steamId, ts = ts.ToString("o", CultureInfo.InvariantCulture),
        }, JsonOptions);
        return store.InsertLedgerEntryAsync(ProjectId, collectionId, steamId, ts.ToUnixTimeMilliseconds(), entry, default);
    }

    // ── Project operations log (LIVE: ProjectLogs.cshtml) ──

    [Fact]
    public async Task GetProjectLogsAsync_AggregatesSortsAndFilters()
    {
        var store = StoreWithCollection();
        var now = DateTimeOffset.UtcNow;
        var ms111 = now.AddMinutes(-20).ToUnixTimeMilliseconds();
        var ms222 = now.AddMinutes(-10).ToUnixTimeMilliseconds();
        SeedProfile(store, "111", ms111);
        SeedProfile(store, "222", ms222);
        SeedStorageEvent(store, "111", ms111, "e1", "record.write", "col_a");
        SeedStorageEvent(store, "222", ms222, "e2", "record.delete", "col_a");
        var reader = new ScyllaPlayerAnalyticsReader(store);

        var all = AsDict(await reader.GetProjectLogsAsync(Owner, ProjectId, null, null, null, null, 1, default));
        var logs = (List<Dictionary<string, JsonElement>>)all["logs"]!;
        Assert.Equal(2, (int)all["total"]!);
        Assert.Equal("222", logs[0]["_steamId"].GetString()); // newest (-10m) first
        Assert.Equal("Players", logs[0]["_collectionName"].GetString());
        Assert.Equal("delete", logs[0]["_op"].GetString());

        var filteredBySteam = AsDict(await reader.GetProjectLogsAsync(Owner, ProjectId, null, "111", null, null, 1, default));
        Assert.Equal(1, (int)filteredBySteam["total"]!);

        var filteredByOp = AsDict(await reader.GetProjectLogsAsync(Owner, ProjectId, null, null, "delete", null, 1, default));
        Assert.Equal(1, (int)filteredByOp["total"]!);

        var filteredBySearch = AsDict(await reader.GetProjectLogsAsync(Owner, ProjectId, null, null, null, "111", 1, default));
        Assert.Equal(1, (int)filteredBySearch["total"]!);

        var filteredByCollection = AsDict(await reader.GetProjectLogsAsync(Owner, ProjectId, "col_a", null, null, null, 1, default));
        Assert.Equal(2, (int)filteredByCollection["total"]!);
    }

    [Fact]
    public async Task GetProjectLogsAsync_ExcludesReadsAndNonStorageEvents()
    {
        var store = StoreWithCollection();
        var now = DateTimeOffset.UtcNow;
        var ms = now.AddMinutes(-5).ToUnixTimeMilliseconds();
        SeedProfile(store, "111", ms);
        SeedStorageEvent(store, "111", ms, "r1", "record.read", "col_a");          // reads excluded from write history
        SeedEvent(store, "111", ms + 1, "s1", "session.heartbeat", "session", ""); // non-storage events excluded
        var reader = new ScyllaPlayerAnalyticsReader(store);

        var all = AsDict(await reader.GetProjectLogsAsync(Owner, ProjectId, null, null, null, null, 1, default));
        Assert.Equal(0, (int)all["total"]!);
    }

    // ── Per-player transactions ──

    [Fact]
    public async Task GetPlayerTransactionsAsync_KeepsOnlyThatPlayersRows()
    {
        var store = StoreWithCollection();
        var now = DateTimeOffset.UtcNow;
        var ms111 = now.AddMinutes(-20).ToUnixTimeMilliseconds();
        var ms222 = now.AddMinutes(-10).ToUnixTimeMilliseconds();
        SeedStorageEvent(store, "111", ms111, "e1", "record.write", "col_a");
        SeedStorageEvent(store, "222", ms222, "e2", "record.write", "col_a");
        var reader = new ScyllaPlayerAnalyticsReader(store);

        var result = AsDict(await reader.GetPlayerTransactionsAsync(Owner, ProjectId, "111", null, 1, default));
        var transactions = (List<Dictionary<string, JsonElement>>)result["transactions"]!;
        Assert.Equal(1, (int)result["total"]!);
        Assert.Equal("111", transactions[0]["_steamId"].GetString());
        Assert.Equal("save", transactions[0]["_op"].GetString());
    }

    // ── Per-player ledger ──

    [Fact]
    public async Task GetPlayerLedgerAsync_SummarisesDeltasBySource()
    {
        var schema = new { properties = new { coins = new { type = "number", _ledger = true }, name = new { type = "string" } } };
        var store = StoreWithCollection(schema: schema);
        var now = DateTimeOffset.UtcNow;
        await SeedLedgerEntry(store, "col_a", "111", now.AddMinutes(-20), "coins", 50, "quest");
        await SeedLedgerEntry(store, "col_a", "111", now.AddMinutes(-10), "coins", -20, "shop");
        var reader = new ScyllaPlayerAnalyticsReader(store);

        var result = AsDict(await reader.GetPlayerLedgerAsync(Owner, ProjectId, "111", null, null, null, null, default));
        var entries = (List<Dictionary<string, JsonElement>>)result["entries"]!;
        var summary = (Dictionary<string, object?>)result["summary"]!;

        Assert.Equal(2, entries.Count);
        Assert.Equal(50d, Convert.ToDouble(summary["totalIncrease"]));
        Assert.Equal(-20d, Convert.ToDouble(summary["totalDecrease"]));
        Assert.Equal(30d, Convert.ToDouble(summary["netChange"]));
        Assert.Equal(2, Convert.ToInt32(summary["entryCount"]));

        var sources = (Dictionary<string, Dictionary<string, object?>>)summary["sources"]!;
        Assert.Equal(50d, Convert.ToDouble(sources["quest"]["total"]));
        Assert.Equal(1, Convert.ToInt32(sources["shop"]["count"]));
        Assert.Equal("shop", entries[0]["source"].GetString()); // newest first
        Assert.Equal("Players", entries[0]["_collectionName"].GetString());
    }

    [Fact]
    public async Task GetPlayerLedgerAsync_FiltersByFieldAndSource()
    {
        var schema = new { properties = new { coins = new { type = "number", _ledger = true }, xp = new { type = "number", _ledger = true } } };
        var store = StoreWithCollection(schema: schema);
        var now = DateTimeOffset.UtcNow;
        await SeedLedgerEntry(store, "col_a", "111", now.AddMinutes(-20), "coins", 50, "quest");
        await SeedLedgerEntry(store, "col_a", "111", now.AddMinutes(-10), "xp", 5, "kill");
        var reader = new ScyllaPlayerAnalyticsReader(store);

        var byField = AsDict(await reader.GetPlayerLedgerAsync(Owner, ProjectId, "111", "coins", null, null, null, default));
        Assert.Single((List<Dictionary<string, JsonElement>>)byField["entries"]!);

        var bySource = AsDict(await reader.GetPlayerLedgerAsync(Owner, ProjectId, "111", null, "kill", null, null, default));
        var srcEntries = (List<Dictionary<string, JsonElement>>)bySource["entries"]!;
        Assert.Single(srcEntries);
        Assert.Equal("xp", srcEntries[0]["field"].GetString());
    }

    [Fact]
    public async Task GetPlayerLedgerAsync_IgnoresNonLedgerAndGlobalCollections()
    {
        var store = new InMemoryNetworkStorageStore();
        // global collections are skipped; a per-steamid collection with no _ledger fields is skipped.
        await store.SeedCollectionAsync(ProjectId, "g", "Global", "global", new { properties = new { coins = new { type = "number", _ledger = true } } });
        await store.SeedCollectionAsync(ProjectId, "col_b", "NoLedger", "per-steamid", new { properties = new { name = new { type = "string" } } });
        // Even stray ledger rows on these collections must not surface.
        await SeedLedgerEntry(store, "g", "111", DateTimeOffset.UtcNow, "coins", 99, "quest");
        await SeedLedgerEntry(store, "col_b", "111", DateTimeOffset.UtcNow, "coins", 99, "quest");
        var reader = new ScyllaPlayerAnalyticsReader(store);

        var result = AsDict(await reader.GetPlayerLedgerAsync(Owner, ProjectId, "111", null, null, null, null, default));
        Assert.Empty((List<Dictionary<string, JsonElement>>)result["entries"]!);
    }

    // ── Project audit logs (ScyllaDB project_audit_logs) ──

    [Fact]
    public async Task GetProjectAuditLogsAsync_FiltersByActionAndPaginates()
    {
        var store = new InMemoryNetworkStorageStore();
        var ts1 = DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeMilliseconds();
        var ts2 = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        await store.SeedAuditLogAsync(ProjectId, ts1, "log1", "1", "collection.create");
        await store.SeedAuditLogAsync(ProjectId, ts2, "log2", "1", "collection.delete");
        var reader = new ScyllaPlayerAnalyticsReader(store);

        var all = AsDict(await reader.GetProjectAuditLogsAsync(Owner, ProjectId, 1, 50, null, null, null, null, default));
        var pagination = (Dictionary<string, object?>)all["pagination"]!;
        Assert.Equal(2, (int)pagination["total"]!);
        var logs = (List<Dictionary<string, JsonElement>>)all["logs"]!;
        Assert.Equal("collection.delete", logs[0]["action"].GetString()); // newest first

        var filtered = AsDict(await reader.GetProjectAuditLogsAsync(Owner, ProjectId, 1, 50, null, "collection.create", null, null, default));
        Assert.Equal(1, (int)((Dictionary<string, object?>)filtered["pagination"]!)["total"]!);
    }

    [Fact]
    public async Task Scans_OnEmptyStore_ReturnEmptyShapesNotNull()
    {
        var reader = new ScyllaPlayerAnalyticsReader(new InMemoryNetworkStorageStore());

        var logs = AsDict(await reader.GetProjectLogsAsync(Owner, ProjectId, null, null, null, null, 1, default));
        Assert.Equal(0, (int)logs["total"]!);
        Assert.Empty((List<Dictionary<string, JsonElement>>)logs["logs"]!);

        var transactions = AsDict(await reader.GetPlayerTransactionsAsync(Owner, ProjectId, "111", null, 1, default));
        Assert.Empty((List<Dictionary<string, JsonElement>>)transactions["transactions"]!);

        var ledger = AsDict(await reader.GetPlayerLedgerAsync(Owner, ProjectId, "111", null, null, null, null, default));
        Assert.Empty((List<Dictionary<string, JsonElement>>)ledger["entries"]!);
    }

    // ── Player timeline + ledger insights (ScyllaDB player_analytics_events) ──

    [Fact]
    public async Task GetPlayerLedgerInsightsAsync_ReadsEventsAndWrapsEnvelope()
    {
        var store = new InMemoryNetworkStorageStore();
        var ts = DateTimeOffset.UtcNow.AddMinutes(-5);
        var tsMs = ts.ToUnixTimeMilliseconds();
        var tsIso = ts.ToString("o");
        await store.SeedPlayerEventAsync(ProjectId, "111", tsMs, "d1", "tracked_field.delta", "tracked_field", "Coins", "", "col_a", new
        {
            steamId = "111", type = "tracked_field.delta", ts = tsIso,
            category = "tracked_field", sessionId = "s1", source = "quest", key = "111",
            endpointSlug = "", collectionId = "col_a", label = "Coins",
            payload = new { fieldPath = "coins", label = "Coins", kind = "currency", before = 100, after = 200, delta = 100 }
        });
        var reader = new ScyllaPlayerAnalyticsReader(store);
        var query = new SboxNetworkStorage.Application.NetworkStorage.PlayerLedgerInsightQuery(30, new SboxNetworkStorage.Application.NetworkStorage.LedgerInsightQuery());

        var result = AsDict(await reader.GetPlayerLedgerInsightsAsync(Owner, ProjectId, "111", query, default));
        Assert.Equal("111", result["steamId"]);
        Assert.Equal(ProjectId, result["projectId"]);
        Assert.Equal(30, result["days"]);
        var insights = (Dictionary<string, object?>)result["ledgerInsights"]!;
        Assert.True((bool)insights["hasData"]!);
        Assert.Equal(1, insights["fieldCount"]);
    }

    [Fact]
    public async Task GetPlayerAnalyticsAsync_ReturnsFullTimelinePayloadShape()
    {
        var store = new InMemoryNetworkStorageStore();
        var now = DateTimeOffset.UtcNow;
        var ts1 = now.AddMinutes(-2).ToUnixTimeMilliseconds();
        var ts2 = now.AddMinutes(-1).ToUnixTimeMilliseconds();
        var ts1Iso = now.AddMinutes(-2).ToString("o");
        var ts2Iso = now.AddMinutes(-1).ToString("o");
        await store.SeedPlayerEventAsync(ProjectId, "111", ts1, "hb", "session.heartbeat", "session", "", "", "", new
        {
            steamId = "111", type = "session.heartbeat", ts = ts1Iso,
            category = "session", sessionId = "s1", label = "", source = "server",
            endpointSlug = "", collectionId = "",
            payload = new { sessionSeconds = 30, fps = new { average = 20, min = 15, max = 25 } }
        });
        await store.SeedPlayerEventAsync(ProjectId, "111", ts2, "er", "error.runtime", "error", "Boom", "", "", new
        {
            steamId = "111", type = "error.runtime", ts = ts2Iso,
            category = "error", sessionId = "s1", label = "Boom", source = "server",
            endpointSlug = "", collectionId = "",
            payload = new { severity = "error" }
        });
        var reader = new ScyllaPlayerAnalyticsReader(store);
        var query = new SboxNetworkStorage.Application.NetworkStorage.PlayerTimelineQuery(30, null, "session", false, new SboxNetworkStorage.Application.NetworkStorage.LedgerInsightQuery());

        var result = AsDict(await reader.GetPlayerAnalyticsAsync(Owner, ProjectId, "111", null, query, default));
        Assert.Equal("111", result["steamId"]);
        Assert.Equal(2, result["rawTimelineCount"]);
        Assert.Equal(1, result["errorEventCount"]);
        Assert.Equal(50, result["lowFpsThreshold"]); // default threshold when settings null
        Assert.NotNull(result["timeline"]);
        Assert.NotNull(result["timelineGroups"]);
        Assert.NotNull(result["sessionJourney"]);
        Assert.NotNull(result["ledgerInsights"]);
        Assert.NotNull(result["collectionData"]);
    }

    [Fact]
    public async Task GetProjectLedgerInsightsAsync_AggregatesAcrossPlayers()
    {
        var store = new InMemoryNetworkStorageStore();
        var now = DateTimeOffset.UtcNow;
        var ts = now.AddMinutes(-5).ToUnixTimeMilliseconds();
        var tsIso = now.AddMinutes(-5).ToString("o");
        var seen1 = now.AddMinutes(-1).ToUnixTimeMilliseconds();
        var seen2 = now.AddMinutes(-2).ToUnixTimeMilliseconds();
        await store.SeedPlayerProfileAsync(ProjectId, "111", "A", seen1);
        await store.SeedPlayerProfileAsync(ProjectId, "222", "B", seen2);
        await store.SeedPlayerEventAsync(ProjectId, "111", ts, "a", "tracked_field.delta", "tracked_field", "", "", "col_a", new
        { steamId = "111", type = "tracked_field.delta", ts = tsIso, category = "tracked_field", collectionId = "col_a", source = "quest", label = "", endpointSlug = "", payload = new { fieldPath = "coins", before = 0, after = 300, delta = 300 } });
        await store.SeedPlayerEventAsync(ProjectId, "222", ts, "b", "tracked_field.delta", "tracked_field", "", "", "col_a", new
        { steamId = "222", type = "tracked_field.delta", ts = tsIso, category = "tracked_field", collectionId = "col_a", source = "quest", label = "", endpointSlug = "", payload = new { fieldPath = "coins", before = 0, after = 50, delta = 50 } });
        var reader = new ScyllaPlayerAnalyticsReader(store);
        var query = new SboxNetworkStorage.Application.NetworkStorage.ProjectLedgerInsightQuery(7, 100, 25, new SboxNetworkStorage.Application.NetworkStorage.LedgerInsightQuery());

        var result = AsDict(await reader.GetProjectLedgerInsightsAsync(Owner, ProjectId, query, default));
        var summary = (Dictionary<string, object?>)result["summary"]!;
        Assert.Equal(2, summary["playerCount"]);
        var topFields = (List<object?>)summary["topFields"]!;
        var coins = (Dictionary<string, object?>)topFields[0]!;
        Assert.Equal("col_a:coins", coins["fieldKey"]);
        Assert.Equal(2, coins["playerCount"]); // both players moved coins
        var topPlayers = (List<object?>)summary["topPlayers"]!;
        Assert.Equal("111", ((Dictionary<string, object?>)topPlayers[0]!)["steamId"]); // larger absolute movement first
    }

    private static Dictionary<string, object?> AsDict(object? value)
        => Assert.IsType<Dictionary<string, object?>>(value);
}
