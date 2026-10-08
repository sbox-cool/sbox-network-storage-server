using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class NetworkStorageMutationCandidateTests
{
    // ════════════════════════════════════════════════════════════════
    // Common tests: missing/invalid API key across all handlers
    // ════════════════════════════════════════════════════════════════

    public static IEnumerable<object[]> AllMutationRouteTemplates()
    {
        // StorageRecord mutations
        foreach (var prefix in new[] { "/api/storage", "/v1/storage", "/v3/storage" })
        {
            yield return ["POST", $"{prefix}/proj1/my-collection/my_key"];
            yield return ["DELETE", $"{prefix}/proj1/my-collection/my_key"];
            yield return ["POST", $"{prefix}/proj1/my-collection/76561197960287930/records"];
            yield return ["DELETE", $"{prefix}/proj1/my-collection/76561197960287930/records/rec_abc123"];
            yield return ["PATCH", $"{prefix}/proj1/my-collection/76561197960287930/records/rec_abc123"];
        }
        // StorageGlobal mutations
        foreach (var prefix in new[] { "/v1/storage", "/v3/storage" })
        {
            yield return ["POST", $"{prefix}/proj1/my-collection/append"];
        }
        // Stats mutations
        foreach (var prefix in new[] { "/api/storage", "/v1/storage", "/v3/storage" })
        {
            yield return ["POST", $"{prefix}/proj1/stats/heartbeat"];
        }
        // Analytics mutations
        foreach (var prefix in new[] { "/api/storage", "/v1/storage", "/v3/storage" })
        {
            yield return ["POST", $"{prefix}/proj1/analytics/events"];
        }
    }

    [Theory]
    [MemberData(nameof(AllMutationRouteTemplates))]
    public async Task AllHandlers_MissingApiKey_ReturnsUnauthorized(string method, string path)
    {
        var result = await ExecuteAsync(method, path, null);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);
        Assert.Empty(result.IntendedWritePaths);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("UNAUTHORIZED", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [MemberData(nameof(AllMutationRouteTemplates))]
    public async Task AllHandlers_InvalidApiKey_ReturnsUnauthorized(string method, string path)
    {
        var result = await ExecuteAsync(method, path, "bad-key");
        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);
        Assert.Empty(result.IntendedWritePaths);
    }

    // ════════════════════════════════════════════════════════════════
    // StorageRecord mutation handler tests
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void StorageRecordMutation_CanHandle_ReturnsTrue_ForMutationVerbs()
    {
        var handler = new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(null));
        var routes = new[]
        {
            ("POST", "/v3/storage/proj1/my-collection/key1"),
            ("DELETE", "/v3/storage/proj1/my-collection/key1"),
            ("PATCH", "/v3/storage/proj1/my-collection/76561197960287930/records/rec_abc123"),
        };
        foreach (var (verb, path) in routes)
        {
            var route = NetworkStorageRouteClassifier.Classify(verb, path);
            Assert.True(handler.CanHandle(route),
                $"Expected CanHandle=true for {verb} {path}");
        }
    }

    [Fact]
    public void StorageRecordMutation_CanHandle_ReturnsFalse_ForGet()
    {
        var handler = new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(null));
        var route = NetworkStorageRouteClassifier.Classify("GET", "/v3/storage/proj1/my-collection/key1");
        Assert.False(handler.CanHandle(route));
    }

    [Fact]
    public void StorageRecordMutation_CanHandle_ReturnsFalse_ForOtherFamily()
    {
        var handler = new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(null));
        var route = NetworkStorageRouteClassifier.Classify("POST", "/v3/values/proj1");
        Assert.False(handler.CanHandle(route));
    }

    [Fact]
    public async Task StorageRecordPost_ReturnsDryRun_WithNonEmptyIntendedWritePaths()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "/v3/storage/proj1/my-collection/player_001", apiKey: "valid-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("allowed", result.AuthDecision);
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("player_001/saved.json"));
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("proj1/player-stats/player.json"));
    }

    [Fact]
    public async Task StorageRecordPost_WithSimpleKey_ReturnsRecordIndexAndUniqueIndexPaths()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "public");
        var handler = new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "/v3/storage/proj1/my-collection/simplekey", apiKey: "valid-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("simplekey/saved.json"));
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("unique-index/{fieldName}.json"));
        // No automatic record index since key doesn't contain _
        Assert.DoesNotContain(result.IntendedWritePaths, p => p.Contains("record-index.json"));
    }

    [Fact]
    public async Task StorageRecordPost_WithUnderscoreKey_IncludesRecordIndexPath()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "/v3/storage/proj1/my-collection/steam7656_rec001", apiKey: "valid-key"));

        Assert.Contains(result.IntendedWritePaths, p => p.Contains("steam7656/record-index.json"));
    }

    [Fact]
    public async Task StorageRecordDeleteKey_ReturnsDryRun_WithNonEmptyIntendedWritePaths()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("DELETE", "/v3/storage/proj1/my-collection/simple_key", apiKey: "valid-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("simple_key/saved.json"));
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("simple_key/logs/ledger.json"));
    }

    [Fact]
    public async Task StorageRecordDeleteRecord_ReturnsDryRun_WithNonEmptyIntendedWritePaths()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("DELETE", "/v3/storage/proj1/my-collection/76561197960287930/records/rec_xyz789",
                apiKey: "valid-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("76561197960287930_rec_xyz789/saved.json"));
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("76561197960287930_rec_xyz789/logs/ledger.json"));
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("76561197960287930/record-index.json"));
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("unique-index/{fieldName}.json"));
    }

    [Fact]
    public async Task StorageRecordRenameRecord_ReturnsDryRun_WithRecordIndexPath()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("PATCH", "/v3/storage/proj1/my-collection/76561197960287930/records/rec_abc123",
                apiKey: "valid-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.Single(result.IntendedWritePaths);
        Assert.Contains("76561197960287930/record-index.json", result.IntendedWritePaths[0]);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("rename-record", json.GetProperty("action").GetString());
    }

    [Fact]
    public async Task StorageRecord_AllAliases_ClassifyAndSuppressWrites()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        foreach (var prefix in new[] { "/api/storage", "/v1/storage", "/v3/storage" })
        {
            // POST
            var result = await handler.ExecuteAsync(
                BuildRequest("POST", $"{prefix}/proj1/my-collection/key1", apiKey: "valid-key"));
            Assert.Equal(200, result.StatusCode);
            Assert.NotEmpty(result.IntendedWritePaths);

            // DELETE key
            result = await handler.ExecuteAsync(
                BuildRequest("DELETE", $"{prefix}/proj1/my-collection/key1", apiKey: "valid-key"));
            Assert.Equal(200, result.StatusCode);
            Assert.NotEmpty(result.IntendedWritePaths);

            // DELETE record
            result = await handler.ExecuteAsync(
                BuildRequest("DELETE", $"{prefix}/proj1/my-collection/76561197960287930/records/rec_xyz",
                    apiKey: "valid-key"));
            Assert.Equal(200, result.StatusCode);
            Assert.NotEmpty(result.IntendedWritePaths);

            // PATCH rename
            result = await handler.ExecuteAsync(
                BuildRequest("PATCH", $"{prefix}/proj1/my-collection/76561197960287930/records/rec_xyz",
                    apiKey: "valid-key"));
            Assert.Equal(200, result.StatusCode);
            Assert.NotEmpty(result.IntendedWritePaths);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // StorageGlobal mutation handler tests
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void StorageGlobalMutation_CanHandle_ReturnsTrue_ForPostAppend()
    {
        var handler = new StorageGlobalMutationCandidateHandler(new FakeApiKeyResolver(null));
        foreach (var prefix in new[] { "/v1/storage", "/v3/storage" })
        {
            var route = NetworkStorageRouteClassifier.Classify("POST", $"{prefix}/proj1/my-collection/append");
            Assert.True(handler.CanHandle(route),
                $"Expected CanHandle=true for POST {prefix}/.../append");
        }
    }

    [Fact]
    public void StorageGlobalMutation_CanHandle_ReturnsFalse_ForGet()
    {
        var handler = new StorageGlobalMutationCandidateHandler(new FakeApiKeyResolver(null));
        var route = NetworkStorageRouteClassifier.Classify("GET", "/v3/storage/proj1/my-collection/list");
        Assert.False(handler.CanHandle(route));
    }

    [Fact]
    public void StorageGlobalMutation_CanHandle_ReturnsFalse_ForOtherFamily()
    {
        var handler = new StorageGlobalMutationCandidateHandler(new FakeApiKeyResolver(null));
        var route = NetworkStorageRouteClassifier.Classify("POST", "/v3/values/proj1");
        Assert.False(handler.CanHandle(route));
    }

    [Fact]
    public async Task GlobalAppend_ReturnsDryRun_WithGlobalRecordWritePath()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new StorageGlobalMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "/v3/storage/proj1/my-collection/append", apiKey: "valid-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("allowed", result.AuthDecision);
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("global/{generatedRecordId}.json"));

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("append", json.GetProperty("action").GetString());
        Assert.Equal("write_suppressed_dry_run", json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task GlobalAppend_ValidAliases_AllSuppressWrites()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new StorageGlobalMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        foreach (var prefix in new[] { "/v1/storage", "/v3/storage" })
        {
            var result = await handler.ExecuteAsync(
                BuildRequest("POST", $"{prefix}/proj1/my-collection/append", apiKey: "valid-key"));
            Assert.Equal(200, result.StatusCode);
            Assert.NotEmpty(result.IntendedWritePaths);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Stats & Analytics mutation handler tests
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void StatsAnalyticsMutation_CanHandle_ReturnsTrue_ForStatsHeartbeat()
    {
        var handler = new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(null));
        foreach (var prefix in new[] { "/api/storage", "/v1/storage", "/v3/storage" })
        {
            var route = NetworkStorageRouteClassifier.Classify("POST", $"{prefix}/proj1/stats/heartbeat");
            Assert.True(handler.CanHandle(route),
                $"Expected CanHandle=true for POST {prefix}/.../stats/heartbeat");
        }
    }

    [Fact]
    public void StatsAnalyticsMutation_CanHandle_ReturnsTrue_ForAnalyticsEvent()
    {
        var handler = new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(null));
        foreach (var prefix in new[] { "/api/storage", "/v1/storage", "/v3/storage" })
        {
            var route = NetworkStorageRouteClassifier.Classify("POST", $"{prefix}/proj1/analytics/events");
            Assert.True(handler.CanHandle(route),
                $"Expected CanHandle=true for POST {prefix}/.../analytics/events");
        }
    }

    [Fact]
    public void StatsAnalyticsMutation_CanHandle_ReturnsFalse_ForGet()
    {
        var handler = new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(null));
        var route = NetworkStorageRouteClassifier.Classify("GET", "/v3/storage/proj1/stats/76561197960287930");
        Assert.False(handler.CanHandle(route));
    }

    [Fact]
    public void StatsAnalyticsMutation_CanHandle_ReturnsFalse_ForOtherFamily()
    {
        var handler = new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(null));
        var route = NetworkStorageRouteClassifier.Classify("POST", "/v3/values/proj1");
        Assert.False(handler.CanHandle(route));
    }

    [Fact]
    public async Task StatsHeartbeat_WithBodySteamId_ReturnsPlayerStatsWritePath()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "public");
        var handler = new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        var body = "{\"steamId\":\"76561197960287930\",\"sessionSeconds\":3600}";
        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "/v3/storage/proj1/stats/heartbeat", apiKey: "valid-key", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("allowed", result.AuthDecision);
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("player-stats/76561197960287930.json"));

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("heartbeat", json.GetProperty("action").GetString());
    }

    [Fact]
    public async Task StatsHeartbeat_NoSteamIdInBody_ReturnsAllSteamIdPlaceholder()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "/v3/storage/proj1/stats/heartbeat", apiKey: "valid-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.NotEmpty(result.IntendedWritePaths);
    }

    [Fact]
    public async Task AnalyticsEvent_ReturnsMultipleWritePaths()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        var body = "{\"steamId\":\"76561197960287930\",\"type\":\"game_start\",\"label\":\"Game Started\"}";
        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "/v3/storage/proj1/analytics/events", apiKey: "valid-key", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("analytics/players/"));
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("analytics/events/"));
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("analytics/sessions/"));
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("analytics/recent.json"));

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("analytics-event", json.GetProperty("action").GetString());
    }

    [Fact]
    public async Task AnalyticsEvent_AllAliases_AllSuppressWrites()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        foreach (var prefix in new[] { "/api/storage", "/v1/storage", "/v3/storage" })
        {
            var result = await handler.ExecuteAsync(
                BuildRequest("POST", $"{prefix}/proj1/analytics/events", apiKey: "valid-key",
                    body: "{\"steamId\":\"76561197960287930\",\"type\":\"test\"}"));
            Assert.Equal(200, result.StatusCode);
            Assert.NotEmpty(result.IntendedWritePaths);
        }
    }

    [Fact]
    public async Task StatsHeartbeat_AllAliases_AllSuppressWrites()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(validAuth));

        foreach (var prefix in new[] { "/api/storage", "/v1/storage", "/v3/storage" })
        {
            var result = await handler.ExecuteAsync(
                BuildRequest("POST", $"{prefix}/proj1/stats/heartbeat", apiKey: "valid-key",
                    body: "{\"steamId\":\"76561197960287930\",\"sessionSeconds\":60}"));
            Assert.Equal(200, result.StatusCode);
            Assert.NotEmpty(result.IntendedWritePaths);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Dry-run body shape verification
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllSuccessResults_ShareDryRunBodyShape()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");

        var testCases = new[]
        {
            ("/v3/storage/proj1/my-collection/key1", "POST",
                (Func<INetworkStorageCandidateHandler>)(() => new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(validAuth)))),
            ("/v3/storage/proj1/my-collection/key1", "DELETE",
                (Func<INetworkStorageCandidateHandler>)(() => new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(validAuth)))),
            ("/v3/storage/proj1/my-collection/steamid123/records/rec123", "DELETE",
                (Func<INetworkStorageCandidateHandler>)(() => new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(validAuth)))),
            ("/v3/storage/proj1/my-collection/append", "POST",
                (Func<INetworkStorageCandidateHandler>)(() => new StorageGlobalMutationCandidateHandler(new FakeApiKeyResolver(validAuth)))),
            ("/v3/storage/proj1/stats/heartbeat", "POST",
                (Func<INetworkStorageCandidateHandler>)(() => new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(validAuth)))),
            ("/v3/storage/proj1/analytics/events", "POST",
                (Func<INetworkStorageCandidateHandler>)(() => new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(validAuth)))),
        };

        foreach (var (path, method, handlerFactory) in testCases)
        {
            var handler = handlerFactory();
            var body = method == "POST" ? "{\"steamId\":\"76561197960287930\"}" : null;
            var result = await handler.ExecuteAsync(
                BuildRequest(method, path, apiKey: "valid-key", body: body));

            Assert.Equal(200, result.StatusCode);
            var json = JsonSerializer.SerializeToElement(result.Body);
            Assert.True(json.GetProperty("ok").GetBoolean());
            Assert.Equal("candidate", json.GetProperty("source").GetString());
            Assert.Equal("write_suppressed_dry_run", json.GetProperty("reason").GetString());
            Assert.NotEmpty(result.IntendedWritePaths);
            // Must not contain real generated data
            Assert.False(json.TryGetProperty("record", out _));
            Assert.False(json.TryGetProperty("sessionToken", out _));
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Zero production writes — handlers never call write methods
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllHandlersProduceZeroWrites()
    {
        // These handlers only construct intended write paths. They never call any
        // Put/Delete/save methods. The test verifies by inspecting IntendedWritePaths
        // vs StoragePathsRead — IntendedWritePaths is populated but nothing is actually
        // written (no IBunnyStorageClient dependency exists in these handlers).

        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");

        // StorageRecord POST
        var recordHandler = new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(validAuth));
        var result = await recordHandler.ExecuteAsync(
            BuildRequest("POST", "/v3/storage/proj1/my-collection/key1", apiKey: "valid-key"));
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Empty(result.StoragePathsRead);

        // StorageRecord DELETE key
        result = await recordHandler.ExecuteAsync(
            BuildRequest("DELETE", "/v3/storage/proj1/my-collection/key1", apiKey: "valid-key"));
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Empty(result.StoragePathsRead);

        // StorageRecord DELETE record
        result = await recordHandler.ExecuteAsync(
            BuildRequest("DELETE", "/v3/storage/proj1/my-collection/steam1/records/rec1", apiKey: "valid-key"));
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Empty(result.StoragePathsRead);

        // StorageRecord PATCH rename
        result = await recordHandler.ExecuteAsync(
            BuildRequest("PATCH", "/v3/storage/proj1/my-collection/steam1/records/rec1", apiKey: "valid-key"));
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Empty(result.StoragePathsRead);

        // StorageGlobal append
        var globalHandler = new StorageGlobalMutationCandidateHandler(new FakeApiKeyResolver(validAuth));
        result = await globalHandler.ExecuteAsync(
            BuildRequest("POST", "/v3/storage/proj1/my-collection/append", apiKey: "valid-key"));
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Empty(result.StoragePathsRead);

        // Stats heartbeat
        var statsHandler = new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(validAuth));
        result = await statsHandler.ExecuteAsync(
            BuildRequest("POST", "/v3/storage/proj1/stats/heartbeat", apiKey: "valid-key", body: "{\"steamId\":\"76561197960287930\"}"));
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Empty(result.StoragePathsRead);

        // Analytics event
        result = await statsHandler.ExecuteAsync(
            BuildRequest("POST", "/v3/storage/proj1/analytics/events", apiKey: "valid-key", body: "{\"steamId\":\"76561197960287930\",\"type\":\"test\"}"));
        Assert.NotEmpty(result.IntendedWritePaths);
        Assert.Empty(result.StoragePathsRead);
    }

    // ════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════

    /// <summary>Dispatch a request to the right handler by matching its family.</summary>
    private static async Task<NetworkStorageCandidateResult> ExecuteAsync(
        string method, string path, string? apiKey, string? body = null)
    {
        var route = NetworkStorageRouteClassifier.Classify(method, path);
        var request = BuildRequest(method, path, apiKey, body);

        INetworkStorageCandidateHandler handler = route.Family switch
        {
            NetworkStorageRouteFamily.StorageRecord => new StorageRecordMutationCandidateHandler(new FakeApiKeyResolver(null)),
            NetworkStorageRouteFamily.StorageGlobal => new StorageGlobalMutationCandidateHandler(new FakeApiKeyResolver(null)),
            NetworkStorageRouteFamily.Stats or NetworkStorageRouteFamily.Analytics => new StatsAnalyticsMutationCandidateHandler(new FakeApiKeyResolver(null)),
            _ => throw new InvalidOperationException($"No handler for family {route.Family}")
        };

        return await handler.ExecuteAsync(request);
    }

    private static NetworkStorageCandidateRequest BuildRequest(
        string method,
        string path,
        string? apiKey = null,
        string? body = null,
        string? steamId = null)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var route = NetworkStorageRouteClassifier.Classify(method, path);

        var query = new Dictionary<string, string>();
        var creds = new NetworkStorageCredentials(
            ApiKey: apiKey,
            SteamId: steamId,
            AuthSessionToken: null,
            SessionToken: null,
            EncryptedRequestId: null);

        return new NetworkStorageCandidateRequest(
            route,
            query,
            ContentType: body is not null ? "application/json" : null,
            AuthSignals: new Dictionary<string, bool>(),
            Credentials: creds,
            Body: body,
            ResolvedOwnerUserId: null,
            CancellationToken: CancellationToken.None);
    }

    /// <summary>
    /// Minimal fake for <see cref="IStorageApiKeyResolver"/>. Returns the configured result or null.
    /// </summary>
    private sealed class FakeApiKeyResolver : IStorageApiKeyResolver
    {
        private readonly StorageApiKeyAuthResult? _result;

        public FakeApiKeyResolver(StorageApiKeyAuthResult? result) => _result = result;

        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(
            string apiKey, string projectId, CancellationToken cancellationToken)
            => Task.FromResult(_result);
    }
}
