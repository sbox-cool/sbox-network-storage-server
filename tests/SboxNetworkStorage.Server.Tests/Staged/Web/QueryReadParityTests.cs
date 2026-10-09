using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>
/// Proves the QUERY system reads the SAME record set as the website browse / record
/// API / game-client executor — i.e. a query cannot show different stats for the
/// same key. Before unification, the query executor required
/// <c>payload_json.ValueKind == Object</c> and did NOT handle the legacy
/// string-encoded payload branch, so a record stored as a JSON string would appear
/// on the website but be silently dropped from query results. Both paths now route
/// through <see cref="RecordRow"/>, eliminating the divergence.
/// </summary>
public sealed class QueryReadParityTests
{
    private const string Project = "proj1";
    private const string Collection = "players";

    private static NativeQueryExecutor Executor(QueryParityStore store)
        => new(store, NullLogger<NativeQueryExecutor>.Instance);

    /// <summary>A leaderboard query over `players` sorted by totalLevel desc.</summary>
    private static JsonElement LeaderboardQuery() => JsonDocument.Parse("""
    {
      "type": "leaderboard",
      "sources": [{ "collectionId": "players" }],
      "config": { "field": "totalLevel", "order": "desc", "limit": 100 }
    }
    """).RootElement;

    [Fact]
    public async Task Query_SkipsTombstones_SameAsBrowse()
    {
        // alice (live) + bob (tombstone). The browse path skips bob; the query must too.
        var store = new QueryParityStore(
            ObjectRow("alice", new { playername = "Alice", totalLevel = 10 }, deleted: false),
            ObjectRow("bob", new { playername = "Bob", totalLevel = 20 }, deleted: true));

        // ── Query path ──
        var result = await Executor(store).ExecuteWithQueryAsync(
            Project, "q", LeaderboardQuery(), values: null, bypassCache: true, CancellationToken.None);

        // ── Browse path (RecordRow) ──
        var browseKeys = BrowseKeys(store);

        Assert.NotNull(result);
        var queryKeys = QueryKeys(result!);

        // Both see only alice; bob's tombstone is invisible to both.
        Assert.Equal(browseKeys, queryKeys);
        Assert.Contains("alice", queryKeys);
        Assert.DoesNotContain("bob", queryKeys);
    }

    [Fact]
    public async Task Query_ReadsLegacyStringEncodedPayload_SameAsBrowse()
    {
        // alice stored normally; carol stored with a LEGACY string-encoded payload_json
        // (as an old migration tool wrote it). The website browse decodes it via
        // RecordRow; the query MUST also decode it — before unification it was dropped.
        var store = new QueryParityStore(
            ObjectRow("alice", new { playername = "Alice", totalLevel = 10 }, deleted: false),
            StringEncodedRow("carol", """{"playername":"Carol","totalLevel":30}"""));

        var result = await Executor(store).ExecuteWithQueryAsync(
            Project, "q", LeaderboardQuery(), values: null, bypassCache: true, CancellationToken.None);

        var browseKeys = BrowseKeys(store);

        Assert.NotNull(result);
        var queryKeys = QueryKeys(result!);

        // Both decode carol's string-encoded payload — neither drops her.
        Assert.Equal(browseKeys, queryKeys);
        Assert.Contains("carol", queryKeys);
        // And the decoded value is usable: carol (30) outranks alice (10).
        Assert.Equal("carol", result!.Entries![0].Key);
    }

    [Fact]
    public async Task Query_SkipsNullPayload_SameAsBrowse()
    {
        var store = new QueryParityStore(
            ObjectRow("alice", new { playername = "Alice", totalLevel = 10 }, deleted: false),
            NullPayloadRow("ghost"));

        var result = await Executor(store).ExecuteWithQueryAsync(
            Project, "q", LeaderboardQuery(), values: null, bypassCache: true, CancellationToken.None);

        var browseKeys = BrowseKeys(store);

        Assert.NotNull(result);
        var queryKeys = QueryKeys(result!);
        Assert.Equal(browseKeys, queryKeys);
        Assert.DoesNotContain("ghost", queryKeys);
    }

    // ── Browse path: the keys RecordRow.ExtractPayload exposes (website / API) ──
    private static HashSet<string> BrowseKeys(QueryParityStore store)
        => store.Rows
            .Where(r => RecordRow.ExtractPayload(r) is not null)
            .Select(r => RecordRow.ExtractKey(r))
            .OfType<string>()
            .ToHashSet();

    // ── Query path: the keys the query executor projected ──
    private static HashSet<string> QueryKeys(QueryResult result)
        => (result.Entries ?? new()).Select(e => e.Key).OfType<string>().ToHashSet();

    // ── Row builders matching INetworkStorageStore.BuildRecordRow shapes ──

    private static JsonElement ObjectRow(string key, object payload, bool deleted)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["record_key"] = key,
            ["payload_json"] = payload,
            ["deleted"] = deleted,
            ["version"] = 1L,
            ["updated_at_unix_ms"] = 1781859860431L,
        });

    /// <summary>Row whose payload_json is a raw JSON STRING (legacy migration shape).</summary>
    private static JsonElement StringEncodedRow(string key, string payloadJsonString)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["record_key"] = key,
            ["payload_json"] = payloadJsonString, // a string, not an object
            ["deleted"] = false,
            ["version"] = 1L,
            ["updated_at_unix_ms"] = 1781859860431L,
        });

    private static JsonElement NullPayloadRow(string key)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["record_key"] = key,
            ["payload_json"] = null,
            ["deleted"] = false,
            ["version"] = 1L,
            ["updated_at_unix_ms"] = 1781859860431L,
        });

    /// <summary>Minimal store serving a fixed record list for `players`.</summary>
    private sealed class QueryParityStore : EmptyNetworkStorageStore
    {
        public IReadOnlyList<JsonElement> Rows { get; }

        public QueryParityStore(params JsonElement[] rows) => Rows = rows;

        public override Task<IReadOnlyList<JsonElement>> ListRecordsAsync(string projectId, string collectionId, CancellationToken ct)
            => Task.FromResult(collectionId == Collection ? Rows : Array.Empty<JsonElement>());

        public override Task<JsonElement?> ReadCollectionAsync(string projectId, string collectionId, CancellationToken ct)
            => Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { name = Collection }));
    }
}
