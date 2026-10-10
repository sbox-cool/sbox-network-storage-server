using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

public sealed class NativeQueryExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_Leaderboard_ReturnsTopEntries()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "lb", new Dictionary<string, object?>
        {
            ["type"] = "leaderboard",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "scores" } },
            ["config"] = new Dictionary<string, object?> { ["field"] = "score", ["order"] = "desc", ["limit"] = 3 }
        });
        store.AddRecords("proj", "scores", new[]
        {
            ("p1", "{\"score\":100,\"name\":\"Alice\"}"),
            ("p2", "{\"score\":250,\"name\":\"Bob\"}"),
            ("p3", "{\"score\":50,\"name\":\"Charlie\"}"),
            ("p4", "{\"score\":200,\"name\":\"Dave\"}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "lb", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("leaderboard", result.Type);
        Assert.NotNull(result.Entries);
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal("p2", result.Entries[0].Key); // Bob: 250
        Assert.Equal(250.0, result.Entries[0].Value);
        Assert.Equal(1, result.Entries[0].Rank);
        Assert.Equal("p4", result.Entries[1].Key); // Dave: 200
        Assert.Equal("p1", result.Entries[2].Key); // Alice: 100
    }

    [Fact]
    public async Task ExecuteAsync_Leaderboard_OverGlobalCollection_ReturnsEntries()
    {
        // A global collection (collectionType: "global") stores its rows in
        // global_records, NOT records. Before the routing fix, the query scan
        // always read the records table — empty for global collections — so a
        // leaderboard query over leaderboard_global returned zero entries even
        // though global_records had data. This is the "collection data vs query
        // results" mismatch: the browse page showed data, the query returned none.
        var store = new FakeQueryStore();
        store.AddCollection("proj", "leaderboard_global", "Leaderboard", collectionType: "global");
        store.AddQuery("proj", "lb", new Dictionary<string, object?>
        {
            ["type"] = "leaderboard",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "leaderboard_global" } },
            ["config"] = new Dictionary<string, object?> { ["field"] = "totalLevel", ["order"] = "desc", ["limit"] = 3 }
        });
        // Each global record is a player entry keyed by steamId (record_id).
        store.AddGlobalRecords("proj", "leaderboard_global", new[]
        {
            ("76561198021524886", "{\"totalLevel\":399,\"totalKills\":2243}"),
            ("76561198033682021", "{\"totalLevel\":238,\"totalKills\":402}"),
            ("76561197973975370", "{\"totalLevel\":169,\"totalKills\":317}"),
            ("76561198104292858", "{\"totalLevel\":81,\"totalKills\":39}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "lb", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("leaderboard", result.Type);
        Assert.NotNull(result.Entries);
        Assert.Equal(3, result.Entries!.Count);
        // Sorted desc by totalLevel: cerbralone(399), Sherwood(238), Geflipte(169)
        Assert.Equal("76561198021524886", result.Entries[0].Key);
        Assert.Equal(399.0, result.Entries[0].Value);
        Assert.Equal("76561198033682021", result.Entries[1].Key);
        Assert.Equal("76561197973975370", result.Entries[2].Key);
    }
    public async Task ExecuteAsync_Count_ReturnsTotalEntries()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "cnt", new Dictionary<string, object?>
        {
            ["type"] = "count",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "players" } },
            ["config"] = new Dictionary<string, object?>()
        });
        store.AddRecords("proj", "players", new[]
        {
            ("p1", "{\"level\":10}"),
            ("p2", "{\"level\":20}"),
            ("p3", "{\"level\":30}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "cnt", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("count", result.Type);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task ExecuteAsync_Sum_ReturnsSumOfField()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "sum", new Dictionary<string, object?>
        {
            ["type"] = "sum",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "scores" } },
            ["config"] = new Dictionary<string, object?> { ["field"] = "score" }
        });
        store.AddRecords("proj", "scores", new[]
        {
            ("p1", "{\"score\":100}"),
            ("p2", "{\"score\":250}"),
            ("p3", "{\"score\":50}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "sum", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("sum", result.Type);
        Assert.Equal(400.0, result.Sum);
        Assert.Equal(3, result.Counted);
    }

    [Fact]
    public async Task ExecuteAsync_Average_ReturnsAverageOfField()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "avg", new Dictionary<string, object?>
        {
            ["type"] = "average",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "scores" } },
            ["config"] = new Dictionary<string, object?> { ["field"] = "score" }
        });
        store.AddRecords("proj", "scores", new[]
        {
            ("p1", "{\"score\":100}"),
            ("p2", "{\"score\":200}"),
            ("p3", "{\"score\":300}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "avg", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("average", result.Type);
        Assert.Equal(200.0, result.Average);
    }

    [Fact]
    public async Task ExecuteAsync_MinMax_ReturnsMinMaxValues()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "mn", new Dictionary<string, object?>
        {
            ["type"] = "min",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "scores" } },
            ["config"] = new Dictionary<string, object?> { ["field"] = "score" }
        });
        store.AddQuery("proj", "mx", new Dictionary<string, object?>
        {
            ["type"] = "max",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "scores" } },
            ["config"] = new Dictionary<string, object?> { ["field"] = "score" }
        });
        store.AddRecords("proj", "scores", new[]
        {
            ("p1", "{\"score\":100}"),
            ("p2", "{\"score\":250}"),
            ("p3", "{\"score\":50}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var minResult = await executor.ExecuteAsync("proj", "mn", null, true, CancellationToken.None);
        var maxResult = await executor.ExecuteAsync("proj", "mx", null, true, CancellationToken.None);

        Assert.NotNull(minResult);
        Assert.Equal("min", minResult.Type);
        Assert.Equal(50.0, minResult.Value);
        Assert.Equal("p3", minResult.Key);

        Assert.NotNull(maxResult);
        Assert.Equal("max", maxResult.Type);
        Assert.Equal(250.0, maxResult.Value);
        Assert.Equal("p2", maxResult.Key);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownQuery_ReturnsNull()
    {
        var store = new FakeQueryStore();
        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        Assert.Null(await executor.ExecuteAsync("proj", "nonexistent", null, true, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_CountWithCondition_FiltersEntries()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "cnt_gt", new Dictionary<string, object?>
        {
            ["type"] = "count",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "players" } },
            ["config"] = new Dictionary<string, object?> { ["field"] = "level", ["condition"] = "gt", ["conditionValue"] = "15" }
        });
        store.AddRecords("proj", "players", new[]
        {
            ("p1", "{\"level\":10}"),
            ("p2", "{\"level\":20}"),
            ("p3", "{\"level\":30}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "cnt_gt", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ExecuteAsync_ExcludesDeletedRecords()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "cnt", new Dictionary<string, object?>
        {
            ["type"] = "count",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "players" } },
            ["config"] = new Dictionary<string, object?>()
        });
        store.AddRecords("proj", "players", new[]
        {
            ("p1", "{\"level\":10}"),
            ("p2", "{\"level\":20}"),
        });
        store.AddDeletedRecord("proj", "players", "p3", "{\"level\":30}");

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "cnt", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count); // p3 is deleted, excluded
    }

    [Fact]
    public async Task ExecuteAsync_LeaderboardEntry_IncludesRecordData()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "lb", new Dictionary<string, object?>
        {
            ["type"] = "leaderboard",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "scores" } },
            ["config"] = new Dictionary<string, object?> { ["field"] = "score", ["limit"] = 1 }
        });
        store.AddRecords("proj", "scores", new[]
        {
            ("p1", "{\"score\":100,\"name\":\"Alice\",\"level\":5}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "lb", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.Entries);
        Assert.Single(result.Entries);
        Assert.Equal("p1", result.Entries[0].Key);
        // Without output fields, Data is the full record
        Assert.NotNull(result.Entries[0].Data);
    }

    [Fact]
    public async Task ExecuteAsync_NestedFieldPath_ResolvesMetric()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "lb", new Dictionary<string, object?>
        {
            ["type"] = "leaderboard",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "scores" } },
            ["config"] = new Dictionary<string, object?> { ["field"] = "stats.kills", ["limit"] = 2 }
        });
        store.AddRecords("proj", "scores", new[]
        {
            ("p1", "{\"stats\":{\"kills\":10}}"),
            ("p2", "{\"stats\":{\"kills\":30}}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "lb", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("p2", result.Entries![0].Key);
        Assert.Equal(30.0, result.Entries[0].Value);
    }

    // ── Multi-source merge (previously fell back to legacy server) ──

    [Fact]
    public async Task ExecuteAsync_MultipleSources_MergesByKey()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "merged", new Dictionary<string, object?>
        {
            ["type"] = "leaderboard",
            ["sources"] = new List<object?>
            {
                new Dictionary<string, object?> { ["collectionId"] = "a", ["alias"] = "srcA" },
                new Dictionary<string, object?> { ["collectionId"] = "b", ["alias"] = "srcB" }
            },
            ["config"] = new Dictionary<string, object?> { ["field"] = "srcA.score", ["limit"] = 10 }
        });
        store.AddRecords("proj", "a", new[]
        {
            ("p1", "{\"score\":100}"),
            ("p2", "{\"score\":200}"),
        });
        store.AddRecords("proj", "b", new[]
        {
            ("p1", "{\"name\":\"Alice\"}"),
            ("p2", "{\"name\":\"Bob\"}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "merged", null, true, CancellationToken.None);

        // Multi-source is now supported natively — must NOT return null.
        Assert.NotNull(result);
        Assert.Equal("leaderboard", result.Type);
        Assert.NotNull(result.Entries);
        Assert.Equal(2, result.Entries.Count);
        // Sorted desc by srcA.score: p2(200) then p1(100)
        Assert.Equal("p2", result.Entries[0].Key);
        Assert.Equal(200.0, result.Entries[0].Value);
    }

    // ── Computed fields (previously fell back to legacy server) ──

    [Fact]
    public async Task ExecuteAsync_ComputedFields_EvaluatesNatively()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "comp", new Dictionary<string, object?>
        {
            ["type"] = "leaderboard",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "scores" } },
            ["config"] = new Dictionary<string, object?>
            {
                ["field"] = "ratio",
                ["computedFields"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["name"] = "ratio", ["expression"] = "{{kills}} / {{deaths}}" }
                },
                ["limit"] = 10
            }
        });
        store.AddRecords("proj", "scores", new[]
        {
            ("p1", "{\"kills\":10,\"deaths\":5}"),  // ratio = 2
            ("p2", "{\"kills\":20,\"deaths\":4}"),  // ratio = 5
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "comp", null, true, CancellationToken.None);

        // Computed fields are now supported natively — must NOT return null.
        Assert.NotNull(result);
        Assert.Equal("leaderboard", result.Type);
        Assert.NotNull(result.Entries);
        Assert.Equal(2, result.Entries.Count);
        // Sorted desc by ratio: p2(5) then p1(2)
        Assert.Equal("p2", result.Entries[0].Key);
        Assert.Equal(5.0, result.Entries[0].Value);
    }

    // ── Object-valued config.field (previously fell back to legacy server) ──

    [Fact]
    public async Task ExecuteAsync_ObjectValuedField_ResolvesNatively()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "objfield", new Dictionary<string, object?>
        {
            ["type"] = "leaderboard",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "scores" } },
            ["config"] = new Dictionary<string, object?>
            {
                ["field"] = new Dictionary<string, object?> { ["expression"] = "{{kills}} * 2" },
                ["limit"] = 10
            }
        });
        store.AddRecords("proj", "scores", new[]
        {
            ("p1", "{\"kills\":10}"),
            ("p2", "{\"kills\":20}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "objfield", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("leaderboard", result.Type);
        Assert.NotNull(result.Entries);
        Assert.Equal(2, result.Entries.Count);
        // Sorted desc: p2(40) then p1(20)
        Assert.Equal("p2", result.Entries[0].Key);
        Assert.Equal(40.0, result.Entries[0].Value);
    }

    // ── Cache ──

    [Fact]
    public async Task ExecuteAsync_CachesResult_WhenNotBypassed()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "cnt", new Dictionary<string, object?>
        {
            ["type"] = "count",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "players" } },
            ["config"] = new Dictionary<string, object?>()
        });
        store.AddRecords("proj", "players", new[]
        {
            ("p1", "{\"level\":10}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);

        // First call: live, populates cache.
        var result1 = await executor.ExecuteAsync("proj", "cnt", null, false, CancellationToken.None);
        Assert.NotNull(result1);
        Assert.False(result1.FromCache);

        // Second call: should return from cache.
        var result2 = await executor.ExecuteAsync("proj", "cnt", null, false, CancellationToken.None);
        Assert.NotNull(result2);
        Assert.True(result2.FromCache);
    }

    [Fact]
    public async Task ExecuteAsync_BypassCache_ReturnsLiveResult()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "cnt", new Dictionary<string, object?>
        {
            ["type"] = "count",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "players" } },
            ["config"] = new Dictionary<string, object?>()
        });
        store.AddRecords("proj", "players", new[]
        {
            ("p1", "{\"level\":10}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);

        // First call: live, populates cache.
        await executor.ExecuteAsync("proj", "cnt", null, false, CancellationToken.None);

        // Second call with bypassCache=true: should NOT return from cache.
        var result = await executor.ExecuteAsync("proj", "cnt", null, true, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result.FromCache);
    }

    // ── Performance tracking ──

    [Fact]
    public async Task ExecuteAsync_Performance_IncludesSourceInfo()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "lb", new Dictionary<string, object?>
        {
            ["type"] = "leaderboard",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "scores" } },
            ["config"] = new Dictionary<string, object?> { ["field"] = "score" }
        });
        store.AddRecords("proj", "scores", new[]
        {
            ("p1", "{\"score\":100}"),
            ("p2", "{\"score\":200}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "lb", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.Performance);
        Assert.Equal(2, result.Performance.KeysScanned);
        Assert.NotNull(result.Performance.Sources);
        Assert.Single(result.Performance.Sources);
        Assert.Equal("scores", result.Performance.Sources[0].CollectionId);
        Assert.Equal(2, result.Performance.Sources[0].KeysScanned);
        Assert.NotNull(result.Performance.At);
    }

    [Fact]
    public async Task ExecuteAsync_OutputColumns_AreExecutedNatively()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "cols", new Dictionary<string, object?>
        {
            ["type"] = "leaderboard",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "scores" } },
            ["config"] = new Dictionary<string, object?>
            {
                ["field"] = "score",
                ["fields"] = new List<object?> { "name", "level" }
            }
        });
        store.AddRecords("proj", "scores", new[]
        {
            ("p1", "{\"score\":100,\"name\":\"Alice\",\"level\":5}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "cols", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("leaderboard", result!.Type);
        Assert.Equal(new[] { "name", "level" }, result.OutputFields);
    }

    [Fact]
    public async Task ExecuteAsync_ExpressionMetric_UsesValueExpressionAlias()
    {
        var store = new FakeQueryStore();
        store.AddQuery("proj", "expr", new Dictionary<string, object?>
        {
            ["type"] = "leaderboard",
            ["sources"] = new List<object?> { new Dictionary<string, object?> { ["collectionId"] = "scores" } },
            ["config"] = new Dictionary<string, object?> { ["valueExpression"] = "{{kills}} * 10", ["order"] = "desc" }
        });
        store.AddRecords("proj", "scores", new[]
        {
            ("p1", "{\"kills\":3}"),
            ("p2", "{\"kills\":5}"),
        });

        var executor = new NativeQueryExecutor(store, NullLogger<NativeQueryExecutor>.Instance);
        var result = await executor.ExecuteAsync("proj", "expr", null, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("p2", result.Entries![0].Key);
        Assert.Equal(50.0, result.Entries[0].Value);
    }

    // ── Fake store ──

    private sealed class FakeQueryStore : EmptyNetworkStorageStore
    {
        private readonly Dictionary<string, JsonElement> _queries = new();
        private readonly Dictionary<string, List<JsonElement>> _records = new();
        private readonly Dictionary<string, List<JsonElement>> _globalRecords = new();
        private readonly Dictionary<string, JsonElement> _collections = new();

        public void AddQuery(string projectId, string queryId, Dictionary<string, object?> def)
        {
            // Wrap in the store row shape: { definition_json: <def>, requires_secret_key, name, ... }
            var row = new Dictionary<string, object?>
            {
                ["query_id"] = queryId,
                ["name"] = queryId,
                ["requires_secret_key"] = false,
                ["definition_json"] = def,
                ["version"] = 1L,
                ["updated_at_unix_ms"] = 0L
            };
            _queries[$"{projectId}:{queryId}"] = JsonDocument.Parse(JsonSerializer.Serialize(row)).RootElement.Clone();
        }
        public void AddCollection(string projectId, string collectionId, string name, string? collectionType = null)
        {
            var def = new Dictionary<string, object?>();
            if (collectionType is not null) def["collectionType"] = collectionType;
            var row = new Dictionary<string, object?>
            {
                ["collection_id"] = collectionId,
                ["name"] = name,
                ["visibility"] = "public",
                ["definition_json"] = def,
                ["version"] = 1L
            };
            _collections[$"{projectId}:{collectionId}"] = JsonDocument.Parse(JsonSerializer.Serialize(row)).RootElement.Clone();
        }

        public void AddGlobalRecords(string projectId, string collectionId, (string recordId, string payloadJson)[] records)
        {
            var key = $"{projectId}:{collectionId}";
            _globalRecords[key] = records.Select(r => BuildGlobalRecord(r.recordId, r.payloadJson)).ToList();
        }

        public void AddRecords(string projectId, string collectionId, (string key, string payloadJson)[] records)
        {
            var key = $"{projectId}:{collectionId}";
            _records[key] = records.Select(r => BuildRecord(r.key, r.payloadJson, deleted: false)).ToList();
        }

        public void AddDeletedRecord(string projectId, string collectionId, string key, string payloadJson)
        {
            var mapKey = $"{projectId}:{collectionId}";
            if (!_records.TryGetValue(mapKey, out var list)) { list = new List<JsonElement>(); _records[mapKey] = list; }
            list.Add(BuildRecord(key, payloadJson, deleted: true));
        }

        private static JsonElement BuildRecord(string key, string payloadJson, bool deleted)
        {
            using var payloadDoc = JsonDocument.Parse(payloadJson);
            var obj = new Dictionary<string, object?>
            {
                ["record_key"] = key,
                ["payload_json"] = payloadDoc.RootElement.Clone(),
                ["deleted"] = deleted,
                ["version"] = 1L
            };
            return JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement.Clone();
        }

        private static JsonElement BuildGlobalRecord(string recordId, string payloadJson)
        {
            using var payloadDoc = JsonDocument.Parse(payloadJson);
            var obj = new Dictionary<string, object?>
            {
                ["record_id"] = recordId,
                ["payload_json"] = payloadDoc.RootElement.Clone(),
                ["version"] = 1L,
                ["created_at_unix_ms"] = 0L
            };
            return JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement.Clone();
        }

        public override Task<JsonElement?> ReadQueryAsync(string projectId, string queryId, CancellationToken ct)
        {
            var key = $"{projectId}:{queryId}";
            return Task.FromResult(_queries.TryGetValue(key, out var q) ? q : (JsonElement?)null);
        }

        public override Task<IReadOnlyList<JsonElement>> ListRecordsAsync(string projectId, string collectionId, CancellationToken ct)
        {
            var key = $"{projectId}:{collectionId}";
            return Task.FromResult<IReadOnlyList<JsonElement>>(
                _records.TryGetValue(key, out var r) ? r : Array.Empty<JsonElement>());
        }

        public override Task<JsonElement?> ReadCollectionAsync(string projectId, string collectionId, CancellationToken ct)
        {
            var key = $"{projectId}:{collectionId}";
            return Task.FromResult(_collections.TryGetValue(key, out var c) ? c : (JsonElement?)null);
        }

        public override Task<IReadOnlyList<JsonElement>> ListGlobalRecordsAsync(string projectId, string collectionId, CancellationToken ct)
        {
            var key = $"{projectId}:{collectionId}";
            return Task.FromResult<IReadOnlyList<JsonElement>>(
                _globalRecords.TryGetValue(key, out var r) ? r : Array.Empty<JsonElement>());
        }

        // All other INetworkStorageStore members inherit EmptyNetworkStorageStore's
        // virtual NotImplementedException throws — extend-only interface growth no
        // longer breaks this fake.
    }
}
