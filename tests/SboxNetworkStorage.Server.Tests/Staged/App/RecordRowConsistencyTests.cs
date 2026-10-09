using System.Text.Json;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>
/// Tests that all three Network Storage read paths — the game-client endpoint
/// executor (ScyllaEndpointShadowDataSource), the direct record-CRUD API
/// (ScyllaNetworkStorageDataPlane), and the website dashboard browse API
/// (NetworkStorageController) — produce identical results for the same ScyllaDB
/// row. This is the "website and API are synced" invariant: no path can serve
/// a different view of the same record.
///
/// The tests exercise the shared RecordRow.ExtractPayload/ExtractKey/ExtractUpdatedAt
/// helpers that all three paths now use, plus the executor's ExtractRecordPayload
/// wrapper (which converts the JsonElement to the executor's value model).
/// </summary>
public class RecordRowConsistencyTests
{
    // ── ExtractPayload: the core invariant ─────────────────────────────

    [Fact]
    public void ExtractPayload_NormalRecord_ReturnsPayload()
    {
        var row = MakeRecordRow(
            key: "76561198021524886",
            payload: """{"totalLevel":399,"playerName":"cerbralone","totalKills":2243}""",
            deleted: false);

        var payload = RecordRow.ExtractPayload(row);

        Assert.NotNull(payload);
        Assert.Equal(399, payload!.Value.GetProperty("totalLevel").GetInt32());
        Assert.Equal("cerbralone", payload.Value.GetProperty("playerName").GetString());
        Assert.Equal(2243, payload.Value.GetProperty("totalKills").GetInt32());
    }

    [Fact]
    public void ExtractPayload_SoftDeletedTombstone_ReturnsNull()
    {
        var row = MakeRecordRow(
            key: "76561198021524886",
            payload: """{"totalLevel":399}""",
            deleted: true);

        // A tombstone reads as missing — matching Bun's storage layer and the
        // ScyllaNetworkStorageDataPlane behavior.
        Assert.Null(RecordRow.ExtractPayload(row));
    }

    [Fact]
    public void ExtractPayload_MissingPayloadJson_ReturnsNull()
    {
        // A row with no payload_json column (e.g. a schema migration in progress).
        var row = JsonSerializer.SerializeToElement(new
        {
            record_key = "76561198021524886",
            deleted = false,
            version = 1L,
            updated_at_unix_ms = 1781859860431L,
        });

        Assert.Null(RecordRow.ExtractPayload(row));
    }

    [Fact]
    public void ExtractPayload_NullPayload_ReturnsNull()
    {
        var row = JsonSerializer.SerializeToElement(new
        {
            record_key = "76561198021524886",
            payload_json = (JsonElement?)null,
            deleted = false,
            version = 1L,
            updated_at_unix_ms = 1781859860431L,
        });

        Assert.Null(RecordRow.ExtractPayload(row));
    }

    [Fact]
    public void ExtractPayload_NestedObject_PreservesStructure()
    {
        // Skills collection: {entries: {skill1: {level: 42, xp: 1000}, skill2: {level: 57, xp: 2000}}}
        var row = MakeRecordRow(
            key: "76561198021524886",
            payload: """{"entries":{"skill1":{"level":42,"xp":1000},"skill2":{"level":57,"xp":2000}}}""",
            deleted: false);

        var payload = RecordRow.ExtractPayload(row);

        Assert.NotNull(payload);
        var entries = payload!.Value.GetProperty("entries");
        Assert.Equal(42, entries.GetProperty("skill1").GetProperty("level").GetInt32());
        Assert.Equal(57, entries.GetProperty("skill2").GetProperty("level").GetInt32());
    }

    [Fact]
    public void ExtractPayload_ArrayPayload_PreservesElements()
    {
        var row = MakeRecordRow(
            key: "global-leaderboard",
            payload: """[{"steamId":"76561198021524886","level":399},{"steamId":"76561198033682021","level":237}]""",
            deleted: false);

        var payload = RecordRow.ExtractPayload(row);

        Assert.NotNull(payload);
        Assert.Equal(JsonValueKind.Array, payload!.Value.ValueKind);
        Assert.Equal(2, payload.Value.GetArrayLength());
    }

    [Fact]
    public void ExtractPayload_NumericPayload_Preserved()
    {
        var row = MakeRecordRow(
            key: "counter",
            payload: "42",
            deleted: false);

        var payload = RecordRow.ExtractPayload(row);

        Assert.NotNull(payload);
        Assert.Equal(JsonValueKind.Number, payload!.Value.ValueKind);
        Assert.Equal(42, payload.Value.GetInt32());
    }

    [Fact]
    public void ExtractPayload_BooleanPayload_Preserved()
    {
        var row = MakeRecordRow(
            key: "flag",
            payload: "true",
            deleted: false);

        var payload = RecordRow.ExtractPayload(row);

        Assert.NotNull(payload);
        Assert.Equal(JsonValueKind.True, payload!.Value.ValueKind);
    }

    // ── ExtractKey ──────────────────────────────────────────────────────

    [Fact]
    public void ExtractKey_PerPlayerRecord_ReturnsRecordKey()
    {
        var row = MakeRecordRow(key: "76561198021524886", payload: "{}", deleted: false);
        Assert.Equal("76561198021524886", RecordRow.ExtractKey(row));
    }

    [Fact]
    public void ExtractKey_GlobalRecord_ReturnsRecordId()
    {
        var row = JsonSerializer.SerializeToElement(new
        {
            record_id = "default",
            payload_json = JsonDocument.Parse("""{"entriesByPlayer":{}}""").RootElement,
            version = 1L,
            created_at_unix_ms = 1781859860431L,
        });

        Assert.Equal("default", RecordRow.ExtractKey(row));
    }

    [Fact]
    public void ExtractKey_NoKey_ReturnsNull()
    {
        var row = JsonSerializer.SerializeToElement(new { payload_json = JsonDocument.Parse("{}").RootElement });
        Assert.Null(RecordRow.ExtractKey(row));
    }

    // ── ExtractUpdatedAt ────────────────────────────────────────────────

    [Fact]
    public void ExtractUpdatedAt_PerPlayerRecord_ReturnsUpdatedAt()
    {
        var row = MakeRecordRow(key: "76561198021524886", payload: "{}", deleted: false, updatedAt: 1781859860431);
        Assert.Equal(1781859860431L, RecordRow.ExtractUpdatedAt(row));
    }

    [Fact]
    public void ExtractUpdatedAt_GlobalRecord_ReturnsCreatedAt()
    {
        var row = JsonSerializer.SerializeToElement(new
        {
            record_id = "default",
            payload_json = JsonDocument.Parse("{}").RootElement,
            version = 1L,
            created_at_unix_ms = 1781859860431L,
        });

        Assert.Equal(1781859860431L, RecordRow.ExtractUpdatedAt(row));
    }

    [Fact]
    public void ExtractUpdatedAt_Missing_ReturnsZero()
    {
        var row = JsonSerializer.SerializeToElement(new { record_key = "k", payload_json = JsonDocument.Parse("{}").RootElement });
        Assert.Equal(0L, RecordRow.ExtractUpdatedAt(row));
    }

    // ── Cross-path consistency: executor vs data plane vs browse API ────
    //
    // These tests verify the invariant: for any given ScyllaDB row, all three
    // read paths extract the same payload. The executor wraps the result in
    // EndpointExpression's value model (Dictionary<string, object?>), while the
    // data plane and browse API return JsonElement — but the DATA must be identical.

    [Fact]
    public void AllPaths_AgreeOn_NormalRecord()
    {
        var row = MakeRecordRow(
            key: "76561198021524886",
            payload: """{"totalLevel":399,"playerName":"cerbralone","totalKills":2243}""",
            deleted: false);

        // Data plane / browse API path: JsonElement
        var jsonPayload = RecordRow.ExtractPayload(row);
        Assert.NotNull(jsonPayload);

        // Executor path: value-model object via ExtractRecordPayload
        var executorPayload = StoreEndpointDataSource.ExtractRecordPayload(row);
        Assert.NotNull(executorPayload);

        // Both must report the same totalLevel
        Assert.Equal(399, jsonPayload!.Value.GetProperty("totalLevel").GetInt32());
        var executorDict = Assert.IsType<Dictionary<string, object?>>(executorPayload);
        Assert.Equal(399.0, executorDict["totalLevel"]);
    }

    [Fact]
    public void AllPaths_AgreeOn_Tombstone()
    {
        var row = MakeRecordRow(key: "k", payload: """{"totalLevel":399}""", deleted: true);

        // All three paths must treat a tombstone as missing.
        Assert.Null(RecordRow.ExtractPayload(row));
        Assert.Null(StoreEndpointDataSource.ExtractRecordPayload(row));
    }

    [Fact]
    public void AllPaths_AgreeOn_MissingPayload()
    {
        var row = JsonSerializer.SerializeToElement(new
        {
            record_key = "k",
            deleted = false,
            version = 1L,
            updated_at_unix_ms = 0L,
        });

        Assert.Null(RecordRow.ExtractPayload(row));
        Assert.Null(StoreEndpointDataSource.ExtractRecordPayload(row));
    }

    [Fact]
    public void AllPaths_AgreeOn_NestedSkillsObject()
    {
        var row = MakeRecordRow(
            key: "76561198021524886",
            payload: """{"entries":{"combat":{"level":200,"xp":50000},"magic":{"level":199,"xp":48000}}}""",
            deleted: false);

        var jsonPayload = RecordRow.ExtractPayload(row);
        var executorPayload = StoreEndpointDataSource.ExtractRecordPayload(row);

        Assert.NotNull(jsonPayload);
        Assert.NotNull(executorPayload);

        // Verify the nested structure is preserved in both
        var jsonCombat = jsonPayload!.Value.GetProperty("entries").GetProperty("combat");
        Assert.Equal(200, jsonCombat.GetProperty("level").GetInt32());

        var executorDict = Assert.IsType<Dictionary<string, object?>>(executorPayload);
        var executorEntries = Assert.IsType<Dictionary<string, object?>>(executorDict["entries"]);
        var executorCombat = Assert.IsType<Dictionary<string, object?>>(executorEntries["combat"]);
        Assert.Equal(200.0, executorCombat["level"]);
    }

    // ── Helper: build a row that matches ScyllaDbResourceStore.BuildRecordRow ──

    private static JsonElement MakeRecordRow(string key, string payload, bool deleted, long updatedAt = 1781859860431)
    {
        using var doc = JsonDocument.Parse(payload);
        return JsonSerializer.SerializeToElement(new
        {
            record_key = key,
            payload_json = doc.RootElement.Clone(),
            deleted = deleted,
            version = 1L,
            updated_at_unix_ms = updatedAt,
        });
    }
}
