using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>
/// Verifies foreign-key join resolution in <see cref="NativeQueryExecutor"/>:
/// left/inner semantics, joined fields as sort targets and output columns, and
/// the O(N+M) scan guarantee (each foreign collection scanned exactly once).
/// </summary>
public sealed class NativeQueryExecutorJoinTests
{
    private const string Project = "proj1";

    // ── Fixtures ──

    private static JsonElement Record(string key, object payload, bool deleted = false)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["record_key"] = key,
            ["payload_json"] = payload,
            ["deleted"] = deleted,
        });

    private static JsonElement Query(string json) => JsonDocument.Parse(json).RootElement;

    private static NativeQueryExecutor Executor(FakeStore store)
        => new(store, NullLogger<NativeQueryExecutor>.Instance);

    // Base "players" + foreign "fishValues" keyed by the fish-type record key.
    private static FakeStore PlayersAndFish() => new(new()
    {
        ["players"] =
        [
            Record("alice", new { playername = "Alice", heaviestFishType = "trout", totallevel = 10 }),
            Record("bob",   new { playername = "Bob",   heaviestFishType = "salmon", totallevel = 20 }),
            Record("carol", new { playername = "Carol", heaviestFishType = "pike",  totallevel = 30 }),
        ],
        ["fishValues"] =
        [
            Record("trout",  new { fishType = "trout",  value = 100 }),
            Record("salmon", new { fishType = "salmon", value = 250 }),
            // no "pike" — carol will be unmatched
        ],
    });

    private static string LeaderboardWithJoin(string field, string joinType, string outputFields = "")
        => $$"""
        {
          "type": "leaderboard",
          "sources": [{ "collectionId": "players" }],
          "config": {
            "field": "{{field}}",
            "order": "desc",
            "limit": 100{{(outputFields.Length > 0 ? $", \"fields\": [{outputFields}]" : "")}},
            "joins": [
              { "sourceCollectionId": "fishValues", "alias": "fish", "type": "{{joinType}}", "localKey": "heaviestFishType", "foreignKey": "fishType" }
            ]
          }
        }
        """;

    // ── Left join ──

    [Fact]
    public async Task LeftJoin_KeepsUnmatchedEntries_WithNullForeign()
    {
        var store = PlayersAndFish();
        var result = await Executor(store).ExecuteWithQueryAsync(
            Project, "q1", Query(LeaderboardWithJoin("totallevel", "left")), values: null, bypassCache: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("leaderboard", result!.Type);
        // All three players present (carol unmatched but kept by left join), sorted by totallevel desc.
        Assert.Equal(3, result.Entries!.Count);
        Assert.Equal("carol", result.Entries[0].Key);
        Assert.Equal("bob", result.Entries[1].Key);
        Assert.Equal("alice", result.Entries[2].Key);

        // Matched entry exposes joined fish data; unmatched has fish == null.
        var bob = (Dictionary<string, object?>)result.Entries[1].Data!;
        var bobFish = (Dictionary<string, object?>)bob["fish"]!;
        Assert.Equal(250d, Convert.ToDouble(bobFish["value"]));

        var carol = (Dictionary<string, object?>)result.Entries[0].Data!;
        Assert.True(carol.ContainsKey("fish"));
        Assert.Null(carol["fish"]);
    }

    // ── Inner join ──

    [Fact]
    public async Task InnerJoin_ExcludesUnmatchedEntries()
    {
        var store = PlayersAndFish();
        var result = await Executor(store).ExecuteWithQueryAsync(
            Project, "q1", Query(LeaderboardWithJoin("totallevel", "inner")), values: null, bypassCache: true, CancellationToken.None);

        Assert.NotNull(result);
        // carol (pike) has no matching fishValues row → excluded.
        Assert.Equal(2, result!.Entries!.Count);
        Assert.DoesNotContain(result.Entries, e => e.Key == "carol");
        Assert.Contains(result.Entries, e => e.Key == "alice");
        Assert.Contains(result.Entries, e => e.Key == "bob");
    }

    // ── Joined field as sort target ──

    [Fact]
    public async Task JoinedField_UsableAsSortTarget()
    {
        var store = PlayersAndFish();
        // Sort by the joined fish.value desc → salmon(250) > trout(100). Inner join drops pike.
        var result = await Executor(store).ExecuteWithQueryAsync(
            Project, "q1", Query(LeaderboardWithJoin("fish.value", "inner")), values: null, bypassCache: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Entries!.Count);
        Assert.Equal("bob", result.Entries[0].Key);   // salmon 250
        Assert.Equal(250d, result.Entries[0].Value);
        Assert.Equal("alice", result.Entries[1].Key); // trout 100
        Assert.Equal(100d, result.Entries[1].Value);
    }

    // ── Joined field as output column ──

    [Fact]
    public async Task JoinedField_UsableAsOutputColumn()
    {
        var store = PlayersAndFish();
        var result = await Executor(store).ExecuteWithQueryAsync(
            Project, "q1", Query(LeaderboardWithJoin("totallevel", "inner", "\"playername\", \"fish.value\"")),
            values: null, bypassCache: true, CancellationToken.None);

        Assert.NotNull(result);
        var bob = (Dictionary<string, object?>)result!.Entries!.First(e => e.Key == "bob").Data!;
        // Output is filtered to the two selected fields, including the joined one.
        // Bun pickFields nests dot paths: fish.value → { fish: { value: ... } }
        Assert.Equal(new[] { "playername", "fish" }.OrderBy(x => x), bob.Keys.OrderBy(x => x));
        Assert.Equal("Bob", bob["playername"]);
        var fish = (Dictionary<string, object?>)bob["fish"]!;
        Assert.Equal(250d, Convert.ToDouble(fish["value"]));
    }

    // ── Performance: foreign collection scanned exactly once ──

    [Fact]
    public async Task Join_ScansEachCollectionExactlyOnce()
    {
        var store = PlayersAndFish();
        await Executor(store).ExecuteWithQueryAsync(
            Project, "q1", Query(LeaderboardWithJoin("totallevel", "left")), values: null, bypassCache: true, CancellationToken.None);

        // 1 scan for base "players" + 1 scan for foreign "fishValues" = 2 total.
        // A naive O(N*M) implementation would re-scan the foreign collection per entry.
        Assert.Equal(2, store.ScanCount);
        Assert.Equal(1, store.ScanCountFor("players"));
        Assert.Equal(1, store.ScanCountFor("fishValues"));
    }

    // ── Foreign key resolved via record key when field absent ──

    [Fact]
    public async Task ForeignKey_FallsBackToRecordKey()
    {
        // fishValues here have NO fishType field — only the record key holds the type.
        var store = new FakeStore(new()
        {
            ["players"] = [Record("alice", new { heaviestFishType = "trout", totallevel = 10 })],
            ["fishValues"] = [Record("trout", new { value = 999 })],
        });
        var result = await Executor(store).ExecuteWithQueryAsync(
            Project, "q1", Query(LeaderboardWithJoin("fish.value", "inner")), values: null, bypassCache: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!.Entries!);
        Assert.Equal(999d, result.Entries![0].Value);
    }

    // ── Fake store ──

    private sealed class FakeStore(Dictionary<string, List<JsonElement>> byCollection) : EmptyNetworkStorageStore
    {
        private readonly Dictionary<string, List<JsonElement>> _byCollection = byCollection;
        private readonly Dictionary<string, int> _scans = new(StringComparer.Ordinal);
        public int ScanCount { get; private set; }
        public int ScanCountFor(string collectionId) => _scans.TryGetValue(collectionId, out var n) ? n : 0;

        public override Task<IReadOnlyList<JsonElement>> ListRecordsAsync(string projectId, string collectionId, CancellationToken ct)
        {
            ScanCount++;
            _scans[collectionId] = ScanCountFor(collectionId) + 1;
            var list = _byCollection.TryGetValue(collectionId, out var l) ? l : [];
            return Task.FromResult<IReadOnlyList<JsonElement>>(list);
        }

        // All other INetworkStorageStore members inherit EmptyNetworkStorageStore's
        // virtual NotImplementedException throws — extend-only interface growth no
        // longer breaks this fake.
    }
}
