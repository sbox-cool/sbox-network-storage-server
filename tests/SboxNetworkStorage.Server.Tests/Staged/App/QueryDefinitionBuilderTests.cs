using System.Collections.Generic;
using System.Linq;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public sealed class QueryDefinitionBuilderTests
{
    private static QueryFormInput Input(
        string? field = "score",
        string order = "desc",
        int limit = 50,
        int ttl = 600,
        bool requiresSecretKey = true,
        IReadOnlyList<string>? fields = null,
        IReadOnlyList<QueryJoinInput>? joins = null,
        params QuerySourceInput[] sources)
        => new("q1", "My Query", "leaderboard", field, order, limit, ttl, requiresSecretKey,
            sources.Length == 0 ? new[] { new QuerySourceInput("col1", "p") } : sources,
            fields ?? Array.Empty<string>(),
            joins ?? Array.Empty<QueryJoinInput>());

    [Fact]
    public void Build_NewQuery_NestsConfigAndCacheNotTopLevel()
    {
        var result = QueryDefinitionBuilder.Build(Input());

        var config = Assert.IsType<Dictionary<string, object>>(result["config"]);
        Assert.Equal("score", config["field"]);
        Assert.Equal("desc", config["order"]);
        Assert.Equal(50, config["limit"]);

        var cache = Assert.IsType<Dictionary<string, object>>(result["cache"]);
        Assert.Equal(600, cache["ttlSeconds"]);

        Assert.True((bool)result["requiresSecretKey"]);

        // The engines read config/cache, never these top-level keys.
        Assert.False(result.ContainsKey("field"));
        Assert.False(result.ContainsKey("order"));
        Assert.False(result.ContainsKey("limit"));
        Assert.False(result.ContainsKey("cacheTtlSeconds"));
    }

    [Fact]
    public void Build_TopLevelSources_WithCollectionIdAndAlias()
    {
        var result = QueryDefinitionBuilder.Build(Input(sources: new[]
        {
            new QuerySourceInput("colA", "players"),
            new QuerySourceInput("colB", null),
            new QuerySourceInput("   ", "ignored") // blank id dropped
        }));

        var sources = Assert.IsType<List<object>>(result["sources"]);
        Assert.Equal(2, sources.Count);
        var first = Assert.IsType<Dictionary<string, object>>(sources[0]);
        Assert.Equal("colA", first["collectionId"]);
        Assert.Equal("players", first["alias"]);
        var second = Assert.IsType<Dictionary<string, object>>(sources[1]);
        Assert.Equal("colB", second["collectionId"]);
        Assert.False(second.ContainsKey("alias"));
    }

    [Fact]
    public void Build_ExistingQuery_PreservesAdvancedConfigAndDropsStaleTopLevel()
    {
        var existing = new Dictionary<string, object>
        {
            ["id"] = "q1",
            ["field"] = "oldTopLevel",
            ["order"] = "asc",
            ["limit"] = 10L,
            ["cacheTtlSeconds"] = 120L,
            ["config"] = new Dictionary<string, object>
            {
                ["field"] = "oldConfigField",
                ["joins"] = new List<object> { new Dictionary<string, object> { ["source"] = "x" } },
                ["computedFields"] = new List<object> { "calc" },
            },
            ["createdAt"] = "2020-01-01T00:00:00Z",
        };

        var result = QueryDefinitionBuilder.Build(Input(field: "newField"), existing);

        var config = Assert.IsType<Dictionary<string, object>>(result["config"]);
        Assert.Equal("newField", config["field"]);
        Assert.True(config.ContainsKey("joins"));
        Assert.True(config.ContainsKey("computedFields"));

        // Stale top-level copies a prior buggy save wrote are removed.
        Assert.False(result.ContainsKey("field"));
        Assert.False(result.ContainsKey("order"));
        Assert.False(result.ContainsKey("limit"));
        Assert.False(result.ContainsKey("cacheTtlSeconds"));

        // createdAt preserved; updatedAt set.
        Assert.Equal("2020-01-01T00:00:00Z", result["createdAt"]);
        Assert.True(result.ContainsKey("updatedAt"));
    }

    [Fact]
    public void Build_BlankField_RemovesFieldFromConfig()
    {
        var existing = new Dictionary<string, object>
        {
            ["config"] = new Dictionary<string, object> { ["field"] = "old" },
        };

        var result = QueryDefinitionBuilder.Build(Input(field: null), existing);

        var config = Assert.IsType<Dictionary<string, object>>(result["config"]);
        Assert.False(config.ContainsKey("field"));
    }

    [Fact]
    public void Build_SecretKeyToggleOff_PersistsFalse()
    {
        var result = QueryDefinitionBuilder.Build(Input(requiresSecretKey: false));
        Assert.False((bool)result["requiresSecretKey"]);
    }
    [Fact]
    public void Build_WithJoins_PersistsInConfigJoins()
    {
        var joins = new List<QueryJoinInput>
        {
            new("colB", "fish", "left", "heaviestFishType", "fishType"),
            new("colC", "values", "inner", "fishId", "id"),
        };
        var result = QueryDefinitionBuilder.Build(Input(joins: joins));
        var config = Assert.IsType<Dictionary<string, object>>(result["config"]);
        var configJoins = Assert.IsType<List<object>>(config["joins"]);
        Assert.Equal(2, configJoins.Count);
        var first = Assert.IsType<Dictionary<string, object>>(configJoins[0]);
        Assert.Equal("colB", first["sourceCollectionId"]);
        Assert.Equal("fish", first["alias"]);
        Assert.Equal("left", first["type"]);
        Assert.Equal("heaviestFishType", first["localKey"]);
        Assert.Equal("fishType", first["foreignKey"]);
        var second = Assert.IsType<Dictionary<string, object>>(configJoins[1]);
        Assert.Equal("inner", second["type"]);
    }
    [Fact]
    public void Build_WithOutputFields_PersistsInConfigFields()
    {
        var fields = new List<string> { "playername", "totallevel", "heaviestWeightKg" };
        var result = QueryDefinitionBuilder.Build(Input(fields: fields));
        var config = Assert.IsType<Dictionary<string, object>>(result["config"]);
        var configFields = Assert.IsAssignableFrom<IEnumerable<string>>(config["fields"]);
        Assert.Equal(3, configFields.Count());
        Assert.Contains("playername", configFields);
        Assert.Contains("totallevel", configFields);
    }
    [Fact]
    public void Build_NoJoins_PreservesExistingConfigJoins()
    {
        var existing = new Dictionary<string, object>
        {
            ["config"] = new Dictionary<string, object>
            {
                ["joins"] = new List<object> { new Dictionary<string, object> { ["source"] = "x" } },
            },
        };
        var result = QueryDefinitionBuilder.Build(Input(), existing);
        var config = Assert.IsType<Dictionary<string, object>>(result["config"]);
        Assert.True(config.ContainsKey("joins"));
    }
    [Fact]
    public void Build_EmptyJoinCollectionId_IsSkipped()
    {
        var joins = new List<QueryJoinInput>
        {
            new("", "empty", "left", "k", "k"),
            new("colB", "valid", "left", "k", "k"),
        };
        var result = QueryDefinitionBuilder.Build(Input(joins: joins));
        var config = Assert.IsType<Dictionary<string, object>>(result["config"]);
        var configJoins = Assert.IsType<List<object>>(config["joins"]);
        Assert.Single(configJoins);
    }
}
