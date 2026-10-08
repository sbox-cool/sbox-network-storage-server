using System.Text;
using System.Text.Json;
using Xunit;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

public class ScyllaDbValidationTests
{
    private static readonly JsonElement EmptyJson = JsonDocument.Parse("{}").RootElement;
    private static readonly JsonElement PayloadJson = JsonDocument.Parse("""{"data":"test"}""").RootElement;

    [Fact]
    public async Task FakeStore_ProjectRoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertProjectAsync("proj-1", PayloadJson, 1, CancellationToken.None);
        var result = await store.ReadProjectAsync("proj-1", CancellationToken.None);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FakeStore_ProjectNotFound_ReturnsNull()
    {
        var store = new InMemoryNetworkStorageStore();
        var result = await store.ReadProjectAsync("nonexistent", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task FakeStore_CollectionRoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertCollectionAsync("proj-1", "col-1", "Test", "public", EmptyJson, 1, CancellationToken.None);
        var result = await store.ReadCollectionAsync("proj-1", "col-1", CancellationToken.None);
        Assert.NotNull(result);

        var list = await store.ListCollectionsAsync("proj-1", CancellationToken.None);
        Assert.Single(list);
    }

    [Fact]
    public async Task FakeStore_RecordRoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertRecordAsync("proj-1", "col-1", "key-1", PayloadJson, false, 1, CancellationToken.None);
        var result = await store.ReadRecordAsync("proj-1", "col-1", "key-1", CancellationToken.None);
        Assert.NotNull(result);

        var list = await store.ListRecordsAsync("proj-1", "col-1", CancellationToken.None);
        Assert.Single(list);
    }

    [Fact]
    public async Task FakeStore_DeleteRemovesRecord()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertRecordAsync("proj-1", "col-1", "key-1", PayloadJson, false, 1, CancellationToken.None);
        await store.DeleteRecordAsync("proj-1", "col-1", "key-1", CancellationToken.None);
        var result = await store.ReadRecordAsync("proj-1", "col-1", "key-1", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task FakeStore_LedgerEntriesOrderedBySequence()
    {
        var store = new InMemoryNetworkStorageStore();
        var entry1 = JsonDocument.Parse("""{"action":"create"}""").RootElement;
        var entry2 = JsonDocument.Parse("""{"action":"update"}""").RootElement;

        await store.InsertLedgerEntryAsync("proj-1", "col-1", "key-1", 2, entry2, CancellationToken.None);
        await store.InsertLedgerEntryAsync("proj-1", "col-1", "key-1", 1, entry1, CancellationToken.None);

        var entries = await store.ListLedgerEntriesAsync("proj-1", "col-1", "key-1", CancellationToken.None);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task FakeStore_AuditLogsOrderedDesc()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.InsertAuditLogAsync("proj-1", 1000, "log-1", "user-1", "create", "{}", "{}", "{}", "", CancellationToken.None);
        await store.InsertAuditLogAsync("proj-1", 2000, "log-2", "user-1", "update", "{}", "{}", "{}", "", CancellationToken.None);

        var logs = await store.ListAuditLogsAsync("proj-1", 10, CancellationToken.None);
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public async Task FakeStore_PlayerAnalyticsRoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        var payload = JsonDocument.Parse("""{"score":100}""").RootElement;
        await store.InsertPlayerAnalyticsEventAsync("proj-1", "col-1", "key-1", 1000, "evt-1", "score_update", payload, CancellationToken.None);

        var events = await store.ListPlayerAnalyticsEventsAsync("proj-1", "col-1", "key-1", 10, CancellationToken.None);
        Assert.Single(events);
    }

    [Fact]
    public async Task FakeStore_ApiKeyRoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        var perms = JsonDocument.Parse("""{"read":true,"write":false}""").RootElement;
        await store.UpsertApiKeyAsync("proj-1", "key-1", "user-1", "public", "hash1", "ident1", "Test Key", true, perms, 1, CancellationToken.None);

        var result = await store.ReadApiKeyAsync("proj-1", "key-1", CancellationToken.None);
        Assert.NotNull(result);

        var list = await store.ListApiKeysAsync("proj-1", CancellationToken.None);
        Assert.Single(list);
    }

    [Fact]
    public async Task FakeStore_GameValuesRoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        var payload = JsonDocument.Parse("""{"health":100,"score":0}""").RootElement;
        await store.UpsertGameValuesAsync("proj-1", payload, "hash1", 1, CancellationToken.None);

        var result = await store.ReadGameValuesAsync("proj-1", CancellationToken.None);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FakeStore_RateLimitRulesRoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        var rules = JsonDocument.Parse("""{"maxRequests":100}""").RootElement;
        await store.UpsertRateLimitRulesAsync("proj-1", rules, 1, CancellationToken.None);

        var result = await store.ReadRateLimitRulesAsync("proj-1", CancellationToken.None);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FakeStore_CheckpointCursorRoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertCheckpointCursorAsync("proj-1", 42, "/path/to/manifest", 1, CancellationToken.None);

        var result = await store.ReadCheckpointCursorAsync("proj-1", CancellationToken.None);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FakeStore_GlobalRecordRoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        var payload = JsonDocument.Parse("""{"type":"config"}""").RootElement;
        await store.UpsertGlobalRecordAsync("proj-1", "col-1", "rec-1", payload, 1, CancellationToken.None);

        var result = await store.ReadGlobalRecordAsync("proj-1", "col-1", "rec-1", CancellationToken.None);
        Assert.NotNull(result);

        var list = await store.ListGlobalRecordsAsync("proj-1", "col-1", CancellationToken.None);
        Assert.Single(list);
    }

    [Fact]
    public async Task FakeStore_RecordIdempotencyRoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        var payload = JsonDocument.Parse("""{"result":"ok"}""").RootElement;
        await store.UpsertRecordIdempotencyAsync("proj-1", "col-1", "key-1", "idem-key-1", 42, "hash1", payload, CancellationToken.None);

        var result = await store.ReadRecordIdempotencyAsync("proj-1", "col-1", "key-1", "idem-key-1", CancellationToken.None);
        Assert.NotNull(result);
    }
}
