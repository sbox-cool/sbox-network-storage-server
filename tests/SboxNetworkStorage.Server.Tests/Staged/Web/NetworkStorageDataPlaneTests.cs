using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Tests for the store-only Network Storage data plane. All reads and writes
/// go through the store — no workspace fallback, no newest-wins comparison.
/// </summary>
public sealed class NetworkStorageDataPlaneTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    // ── Reads ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Read_ReturnsStoreValue()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertRecordAsync("p1", "players", "k1", Json("""{"score":10}"""), deleted: false, version: 1, CancellationToken.None);
        var plane = new StoreNetworkStorageDataPlane(store);

        var r = await plane.ReadRecordAsync(77, "p1", "players", "k1", CancellationToken.None);

        Assert.True(r.Found);
        Assert.Equal("store", r.Source);
        Assert.Equal(10, r.Value.GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task Read_ReturnsNotFound_WhenAbsent()
    {
        var store = new InMemoryNetworkStorageStore();
        var plane = new StoreNetworkStorageDataPlane(store);

        var r = await plane.ReadRecordAsync(77, "p1", "players", "k1", CancellationToken.None);

        Assert.False(r.Found);
    }

    [Fact]
    public async Task Read_ReturnsNotFound_ForTombstone()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertRecordAsync("p1", "players", "k1", Json("null"), deleted: true, version: 1, CancellationToken.None);
        var plane = new StoreNetworkStorageDataPlane(store);

        var r = await plane.ReadRecordAsync(77, "p1", "players", "k1", CancellationToken.None);

        Assert.False(r.Found);
    }


    [Fact]
    public async Task ReadWriteRead_RoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        var plane = new StoreNetworkStorageDataPlane(store);

        await plane.WriteRecordAsync(77, "p1", "players", "k1", Json("""{"hp":100}"""), CancellationToken.None);
        var r = await plane.ReadRecordAsync(77, "p1", "players", "k1", CancellationToken.None);

        Assert.True(r.Found);
        Assert.Equal("store", r.Source);
        Assert.Equal(100, r.Value.GetProperty("hp").GetInt32());
    }

    // ── Writes ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Write_PersistsToStore()
    {
        var store = new InMemoryNetworkStorageStore();
        var plane = new StoreNetworkStorageDataPlane(store);

        await plane.WriteRecordAsync(77, "p1", "players", "k1", Json("""{"score":42}"""), CancellationToken.None);
        var r = await plane.ReadRecordAsync(77, "p1", "players", "k1", CancellationToken.None);

        Assert.True(r.Found);
        Assert.Equal(42, r.Value.GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task Delete_WritesTombstone()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertRecordAsync("p1", "players", "k1", Json("""{"score":1}"""), deleted: false, version: 1, CancellationToken.None);
        var plane = new StoreNetworkStorageDataPlane(store);

        await plane.DeleteRecordAsync(77, "p1", "players", "k1", CancellationToken.None);
        var r = await plane.ReadRecordAsync(77, "p1", "players", "k1", CancellationToken.None);

        Assert.False(r.Found);
    }

    // ── Multiple reads are deterministic ────────────────────────────────────

    [Fact]
    public async Task MultipleReads_Consistent()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertRecordAsync("p1", "players", "k1", Json("""{"score":999}"""), deleted: false, version: 1, CancellationToken.None);
        var plane = new StoreNetworkStorageDataPlane(store);

        for (var i = 0; i < 10; i++)
        {
            var r = await plane.ReadRecordAsync(77, "p1", "players", "k1", CancellationToken.None);
            Assert.True(r.Found);
            Assert.Equal("store", r.Source);
            Assert.Equal(999, r.Value.GetProperty("score").GetInt32());
        }
    }

    // ── Global collection routing (collectionType: "global") ───────────────
    // A global collection stores rows in global_records, NOT records. The data
    // plane must route by collectionType — without it, reads return NotFound and
    // writes go to the wrong table (records instead of global_records).

    private static InMemoryNetworkStorageStore StoreWithGlobalCollection()
    {
        var store = new InMemoryNetworkStorageStore();
        // Register a global collection (collectionType: "global" in definition_json).
        store.UpsertCollectionAsync("p1", "leaderboard_global", "Leaderboard", "public",
            JsonDocument.Parse("""{"collectionType":"global"}""").RootElement, 1, CancellationToken.None).GetAwaiter().GetResult();
        return store;
    }

    [Fact]
    public async Task Read_GlobalCollection_ReadsFromGlobalRecords()
    {
        var store = StoreWithGlobalCollection();
        await store.UpsertGlobalRecordAsync("p1", "leaderboard_global", "default",
            Json("""{"entriesByPlayer":{"76561198021524886":{"totalLevel":399}}}"""), 1, CancellationToken.None);
        var plane = new StoreNetworkStorageDataPlane(store);

        var r = await plane.ReadRecordAsync(77, "p1", "leaderboard_global", "default", CancellationToken.None);

        Assert.True(r.Found);
        Assert.Equal("store", r.Source);
        Assert.True(r.Value.TryGetProperty("entriesByPlayer", out var ebp));
        Assert.True(ebp.TryGetProperty("76561198021524886", out _));
    }

    [Fact]
    public async Task Write_GlobalCollection_WritesToGlobalRecords()
    {
        var store = StoreWithGlobalCollection();
        var plane = new StoreNetworkStorageDataPlane(store);

        await plane.WriteRecordAsync(77, "p1", "leaderboard_global", "default",
            Json("""{"entriesByPlayer":{}}"""), CancellationToken.None);

        // The record must be in global_records, NOT records.
        Assert.NotNull(await store.ReadGlobalRecordAsync("p1", "leaderboard_global", "default", CancellationToken.None));
        Assert.Null(await store.ReadRecordAsync("p1", "leaderboard_global", "default", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_GlobalCollection_DeletesFromGlobalRecords()
    {
        var store = StoreWithGlobalCollection();
        await store.UpsertGlobalRecordAsync("p1", "leaderboard_global", "default",
            Json("""{"entriesByPlayer":{}}"""), 1, CancellationToken.None);
        var plane = new StoreNetworkStorageDataPlane(store);

        await plane.DeleteRecordAsync(77, "p1", "leaderboard_global", "default", CancellationToken.None);

        // Global records use hard-delete (no tombstone column). The record must be gone.
        Assert.Null(await store.ReadGlobalRecordAsync("p1", "leaderboard_global", "default", CancellationToken.None));
    }

    [Fact]
    public async Task Read_GlobalCollection_NotFound_ReturnsNotFound()
    {
        var store = StoreWithGlobalCollection();
        var plane = new StoreNetworkStorageDataPlane(store);

        var r = await plane.ReadRecordAsync(77, "p1", "leaderboard_global", "missing", CancellationToken.None);

        Assert.False(r.Found);
    }
}
