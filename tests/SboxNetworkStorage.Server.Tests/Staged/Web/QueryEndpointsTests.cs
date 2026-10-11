using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Contracts.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Live-route parity tests for the .NET-native Network Storage query API family
/// (<c>GET /v3/queries/{projectId}/{queryId}</c> and the <c>/v1</c> +
/// <c>/api/storage</c> aliases). Proves the legacy server→.NET cutover: queries execute
/// natively via <c>NativeQueryExecutor</c> over the store, the legacy server wire contract
/// is preserved (HTTP 200 + result body, or HTTP status + error body), and no
/// request is proxied to the dead legacy server storage-api.
/// </summary>
public abstract class QueryEndpointsTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string ProjectId = "proj-1";
    private const string ApiKey = "test-key";
    private const string SecretKey = "sbox_sk_test-secret";
    private const string CollectionId = "scores";

    private readonly SelfHostFactory _factory;
    private INetworkStorageStore? _store;

    protected QueryEndpointsTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    // PORT-ADAPTED: instead of a hand-built stub store, every client gets a fresh database of the
    // fixture's driver seeded through the store's public writes (same rows the stub returned).
    private async Task<HttpClient> CreateClientAsync(
        bool projectEnabled = true,
        string? queryDefinitionJson = null,
        bool requiresSecretKey = false,
        (string Key, string PayloadJson)[]? records = null,
        (string SteamId, string PlayerName, bool IsOnline, long LastSeenUnixMs)[]? playerProfiles = null)
    {
        var defaultQuery = queryDefinitionJson ?? $$"""
        {
            "type": "leaderboard",
            "sources": [{"collectionId": "{{CollectionId}}"}],
            "config": {"field": "score", "order": "desc", "limit": 10}
        }
        """;

        var defaultRecords = records ??
        [
            ("p1", "{\"score\":100,\"name\":\"Alice\"}"),
            ("p2", "{\"score\":250,\"name\":\"Bob\"}"),
        ];

        var store = await _factory.NewStoreAsync();
        await SeedAsync(store, defaultQuery, requiresSecretKey, defaultRecords, playerProfiles ?? []);
        _store = store;

        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new FakeKeyResolver(ApiKey, ProjectId));
                services.RemoveAll<INetworkStorageProjectService>();
                services.AddScoped<INetworkStorageProjectService>(_ => new FakeProjectService(projectEnabled));
                services.RemoveAll<INetworkStorageStore>();
                services.AddSingleton(store);
                services.RemoveAll<IQueryValuesContextProvider>();
                services.AddScoped<IQueryValuesContextProvider>(_ => new FakeValuesProvider());
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static async Task SeedAsync(
        INetworkStorageStore store,
        string queryDefinitionJson,
        bool requiresSecretKey,
        (string Key, string PayloadJson)[] records,
        (string SteamId, string PlayerName, bool IsOnline, long LastSeenUnixMs)[] playerProfiles)
    {
        var ct = CancellationToken.None;
        await store.UpsertQueryAsync(ProjectId, "test-query", "test-query", requiresSecretKey,
            JsonDocument.Parse(queryDefinitionJson).RootElement.Clone(), 1, ct);
        await store.UpsertCollectionAsync(ProjectId, CollectionId, CollectionId, "public",
            JsonDocument.Parse("null").RootElement.Clone(), 1, ct);
        foreach (var (key, payloadJson) in records)
        {
            await store.UpsertRecordAsync(ProjectId, CollectionId, key, JsonDocument.Parse(payloadJson).RootElement.Clone(), deleted: false, version: 1, ct);
        }

        foreach (var profile in playerProfiles)
        {
            await store.UpsertPlayerProfileAsync(ProjectId, profile.SteamId, profile.PlayerName, profile.IsOnline, onlineSinceUnixMs: null,
                profile.LastSeenUnixMs, lastHeartbeatUnixMs: null, currentSessionId: null, currentSessionLastSeconds: null,
                totalSeconds: 60, sessionCount: 1, lastEventType: null, lastEndpointSlug: null, managedCountersJson: "{}", updatedAtUnixMs: 0, ct);
        }
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string QueryUrl(string path, bool withApiKey = true)
        => withApiKey ? $"{path}?apiKey={ApiKey}" : path;

    [SkippableFact]
    public async Task Execute_WithValidApiKey_ReturnsLeaderboard()
    {
        using var client = await CreateClientAsync();
        using var response = await client.GetAsync(QueryUrl($"/v3/queries/{ProjectId}/test-query"));

        // Wire-contract parity: HTTP 200 with the result body.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyAsync(response);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("test-query", body.GetProperty("queryId").GetString());
        Assert.Equal("leaderboard", body.GetProperty("type").GetString());
        Assert.NotNull(body.GetProperty("entries"));
        Assert.NotEmpty(body.GetProperty("entries").EnumerateArray());
    }

    [SkippableFact]
    public async Task Execute_MissingApiKey_ReturnsUnauthorized()
    {
        using var client = await CreateClientAsync();
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, QueryUrl($"/v3/queries/{ProjectId}/test-query", withApiKey: false)));

        // 401, not a legacy server 502.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("UNAUTHORIZED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Execute_InvalidApiKey_ReturnsUnauthorized()
    {
        using var client = await CreateClientAsync();
        using var response = await client.GetAsync($"/v3/queries/{ProjectId}/test-query?apiKey=bad-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("UNAUTHORIZED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Execute_ProjectDisabled_ReturnsDisabled()
    {
        using var client = await CreateClientAsync(projectEnabled: false);
        using var response = await client.GetAsync(QueryUrl($"/v3/queries/{ProjectId}/test-query"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("DISABLED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Execute_QueryNotFound_ReturnsNotFound()
    {
        using var client = await CreateClientAsync();
        using var response = await client.GetAsync(QueryUrl($"/v3/queries/{ProjectId}/nonexistent-query"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Execute_V1Alias_ReturnsResult()
    {
        using var client = await CreateClientAsync();
        using var response = await client.GetAsync(QueryUrl($"/v1/queries/{ProjectId}/test-query"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("leaderboard", body.GetProperty("type").GetString());
    }

    [SkippableFact]
    public async Task Execute_ApiStorageAlias_ReturnsResult()
    {
        using var client = await CreateClientAsync();
        using var response = await client.GetAsync(QueryUrl($"/api/storage/{ProjectId}/queries/test-query"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.True(body.GetProperty("ok").GetBoolean());
    }

    [SkippableFact]
    public async Task Execute_LiveResult_SetsNoStoreCacheHeader()
    {
        using var client = await CreateClientAsync();
        using var response = await client.GetAsync($"/v3/queries/{ProjectId}/test-query?apiKey={ApiKey}&fresh=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Cache-Control", out var cache));
        Assert.Contains("no-store", Assert.Single(cache));
    }

    [SkippableFact]
    public async Task Execute_PublicKeyIsServedTheSharedResult_OnlyASecretKeyForcesAFreshRun()
    {
        using var client = await CreateClientAsync(queryDefinitionJson: $$"""
            { "type": "count", "sources": [{"collectionId": "{{CollectionId}}"}], "config": {}, "cache": {"ttlSeconds": 300} }
            """);
        using (var first = await client.GetAsync(QueryUrl($"/v3/queries/{ProjectId}/test-query")))
            Assert.Equal(2, (await BodyAsync(first)).GetProperty("count").GetInt32());

        // Written straight to the store, past the server's write tracking, so only a fresh run can see it.
        await _store!.UpsertRecordAsync(ProjectId, CollectionId, "p3", JsonDocument.Parse("{\"score\":1}").RootElement.Clone(), deleted: false, version: 1, CancellationToken.None);

        using (var publicLive = await client.GetAsync($"/v3/queries/{ProjectId}/test-query?apiKey={ApiKey}&live=1"))
        {
            var body = await BodyAsync(publicLive);
            Assert.Equal(2, body.GetProperty("count").GetInt32());
            Assert.False(body.TryGetProperty("fromCache", out _));
            Assert.Contains("no-store", Assert.Single(publicLive.Headers.GetValues("Cache-Control")));
        }

        using (var publicCached = await client.GetAsync($"/v3/queries/{ProjectId}/test-query?apiKey={ApiKey}&cache=1"))
        {
            var body = await BodyAsync(publicCached);
            Assert.Equal(2, body.GetProperty("count").GetInt32());
            Assert.True(body.GetProperty("fromCache").GetBoolean());
            Assert.True(body.TryGetProperty("expiresAt", out _));
        }

        using var secretLive = new HttpRequestMessage(HttpMethod.Get, $"/v3/queries/{ProjectId}/test-query?apiKey={ApiKey}&live=1");
        secretLive.Headers.Add("x-secret-key", SecretKey);
        using var secretResponse = await client.SendAsync(secretLive);
        Assert.Equal(3, (await BodyAsync(secretResponse)).GetProperty("count").GetInt32());
    }

    [SkippableFact]
    public async Task Execute_CountQuery_ReturnsCount()
    {
        var queryJson = $$"""
        {
            "type": "count",
            "sources": [{"collectionId": "{{CollectionId}}"}],
            "config": {}
        }
        """;
        using var client = await CreateClientAsync(queryDefinitionJson: queryJson);
        using var response = await client.GetAsync(QueryUrl($"/v3/queries/{ProjectId}/test-query"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("count", body.GetProperty("type").GetString());
        Assert.Equal(2, body.GetProperty("count").GetInt32());
    }

    [SkippableFact]
    public async Task Execute_SumQuery_ReturnsSum()
    {
        var queryJson = $$"""
        {
            "type": "sum",
            "sources": [{"collectionId": "{{CollectionId}}"}],
            "config": {"field": "score"}
        }
        """;
        using var client = await CreateClientAsync(queryDefinitionJson: queryJson);
        using var response = await client.GetAsync(QueryUrl($"/v3/queries/{ProjectId}/test-query"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("sum", body.GetProperty("type").GetString());
        Assert.Equal(350.0, body.GetProperty("sum").GetDouble());
    }

    [SkippableFact]
    public async Task Execute_LeaderboardEntries_UseCamelCaseWireContract()
    {
        // The s&box RunQuery client reads entry.GetProperty("rank"/"key"/"value")
        // and performance.keysScanned — all camelCase. A PascalCase regression
        // (Rank/Key/Value) returns HTTP 200 but silently breaks every in-game
        // leaderboard because GetProperty("key") throws on the real payload.
        using var client = await CreateClientAsync();
        using var response = await client.GetAsync(QueryUrl($"/v3/queries/{ProjectId}/test-query"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);

        var entry = body.GetProperty("entries").EnumerateArray().First();

        // camelCase keys present with the expected kinds.
        Assert.Equal(JsonValueKind.Number, entry.GetProperty("rank").ValueKind);
        Assert.Equal(JsonValueKind.String, entry.GetProperty("key").ValueKind);
        Assert.Equal(JsonValueKind.Number, entry.GetProperty("value").ValueKind);
        Assert.True(entry.TryGetProperty("data", out _));
        Assert.True(entry.TryGetProperty("outputValues", out _));

        // No PascalCase leak — the exact shape RunQuery chokes on.
        Assert.False(entry.TryGetProperty("Rank", out _));
        Assert.False(entry.TryGetProperty("Key", out _));
        Assert.False(entry.TryGetProperty("Value", out _));
        Assert.False(entry.TryGetProperty("OutputValues", out _));

        // Default records: Bob(250) > Alice(100), sorted desc.
        Assert.Equal(1, entry.GetProperty("rank").GetInt32());
        Assert.Equal("p2", entry.GetProperty("key").GetString());
        Assert.Equal(250.0, entry.GetProperty("value").GetDouble());

        // performance contract is camelCase too.
        var perf = body.GetProperty("performance");
        Assert.True(perf.TryGetProperty("keysScanned", out _));
        Assert.True(perf.TryGetProperty("at", out _));
        Assert.False(perf.TryGetProperty("KeysScanned", out _));
    }

    [SkippableFact]
    public async Task Execute_WithPlayerProfileEnrichment_MergesDisplayNameIntoEntries()
    {
        // Auto-detect: playerProfile.* in config.fields triggers enrichment.
        // No separate toggle or config.enrichment block needed.
        var steamId1 = "76561198000000001";
        var steamId2 = "76561198000000002";
        var records = new[]
        {
            (steamId1, "{\"score\":100}"),
            (steamId2, "{\"score\":250}"),
        };
        var profiles = new[]
        {
            (steamId1, "Alice", true, 1700000000000L),
            (steamId2, "Bob", false, 1700000001000L),
        };
        var queryJson = $$"""
        {
            "type": "leaderboard",
            "sources": [{"collectionId": "{{CollectionId}}"}],
            "config": {
                "field": "score", "order": "desc", "limit": 10,
                "fields": ["playerProfile.playerName", "playerProfile.isOnline"]
            }
        }
        """;

        using var client = await CreateClientAsync(queryDefinitionJson: queryJson, records: records, playerProfiles: profiles);
        using var response = await client.GetAsync(QueryUrl($"/v3/queries/{ProjectId}/test-query"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        var entries = body.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);

        // Bob (score=250) ranks first, Alice (score=100) second.
        var first = entries[0];
        Assert.Equal(steamId2, first.GetProperty("key").GetString());
        Assert.Equal(250.0, first.GetProperty("value").GetDouble());

        // Enrichment field is nested under playerProfile in the output values.
        Assert.True(first.TryGetProperty("outputValues", out var outVals));
        Assert.True(outVals.TryGetProperty("playerProfile.playerName", out var nameEl));
        Assert.Equal("Bob", nameEl.GetString());
        Assert.True(outVals.TryGetProperty("playerProfile.isOnline", out var onlineEl));
        Assert.False(onlineEl.GetBoolean());

        // Data also carries the nested enrichment dict.
        Assert.True(first.TryGetProperty("data", out var dataEl));
        Assert.True(dataEl.TryGetProperty("playerProfile", out var profileEl));
        Assert.Equal("Bob", profileEl.GetProperty("playerName").GetString());

        // Second entry (Alice) is enriched too.
        var second = entries[1];
        Assert.True(second.TryGetProperty("outputValues", out var outVals2));
        Assert.Equal("Alice", outVals2.GetProperty("playerProfile.playerName").GetString());
        Assert.True(outVals2.GetProperty("playerProfile.isOnline").GetBoolean());
    }

    [SkippableFact]
    public async Task Execute_WithoutPlayerProfileFields_DoesNotEnrich()
    {
        // No playerProfile.* in config.fields → no enrichment, even if profiles exist.
        var steamId = "76561198000000001";
        var records = new[] { (steamId, "{\"score\":100}") };
        var profiles = new[] { (steamId, "Alice", true, 1700000000000L) };

        using var client = await CreateClientAsync(records: records, playerProfiles: profiles);
        using var response = await client.GetAsync(QueryUrl($"/v3/queries/{ProjectId}/test-query"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        var entry = body.GetProperty("entries").EnumerateArray().First();
        Assert.True(entry.TryGetProperty("data", out var dataEl));
        Assert.False(dataEl.TryGetProperty("playerProfile", out _));
    }

    // ── Fakes ──

    private sealed class FakeKeyResolver(string validKey, string projectId) : IStorageApiKeyResolver
    {
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string project, CancellationToken cancellationToken)
            => Task.FromResult(!string.Equals(project, projectId, StringComparison.Ordinal) ? null
                : string.Equals(apiKey, validKey, StringComparison.Ordinal) ? new StorageApiKeyAuthResult(42, project, true, "public")
                : string.Equals(apiKey, SecretKey, StringComparison.Ordinal) ? new StorageApiKeyAuthResult(42, project, true, "secret")
                : null);
    }

    private sealed class FakeProjectService(bool enabled) : INetworkStorageProjectService
    {
        public Task<NetworkStorageProjectAccessResult?> ResolveProjectAccessAsync(long userId, string projectId, CancellationToken cancellationToken)
            => Task.FromResult<NetworkStorageProjectAccessResult?>(new NetworkStorageProjectAccessResult(
                new WorkspaceProject(projectId, "Test Project", null, Enabled: enabled, null, null, null, EnableAuthSessions: false, AuthSessionTtlSeconds: 3600),
                Organization: null, StorageOwnerUserId: userId,
                RequireSboxAuth: false, PlayerKeyMode: null, CanManage: true));

        public Task<NetworkStorageProjectCreateResult> CreateProjectAsync(long userId, string name, string? description, bool enabled, bool requireSboxAuth, string keyMode, string organizationId, CancellationToken cancellationToken, string? hostingProfile = null)
            => throw new NotImplementedException();
        public Task<NetworkStorageProjectResources?> GetProjectResourcesAsync(long userId, string projectId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<NetworkStorageProjectResources?> GetProjectResourcesForOwnerAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<NetworkStorageTeamData?> GetProjectTeamAsync(long storageOwnerUserId, string projectId, string? organizationId, string callerRole, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ApiKeyInfo>> GetProjectKeysAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<(ApiKeyInfo Key, string RawKey)> CreateProjectKeyAsync(long storageOwnerUserId, string projectId, string label, string keyType, Dictionary<string, string>? permissions, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task ToggleProjectKeyAsync(long storageOwnerUserId, string projectId, string key, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task RemoveProjectKeyAsync(long storageOwnerUserId, string projectId, string key, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task UpdateProjectKeyPermissionsAsync(long storageOwnerUserId, string projectId, string keyIdentifier, Dictionary<string, string> permissions, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task DeleteProjectAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task UpdateProjectSettingsAsync(long storageOwnerUserId, string projectId, string settingsTab, Dictionary<string, string> formValues, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<ProjectUsageData> GetProjectUsageAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<ProjectRateLimits> GetProjectRateLimitsAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task SaveEndpointRateLimitsAsync(long storageOwnerUserId, string projectId, Dictionary<string, object> endpointRateLimits, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task SaveRateLimitRulesAsync(long storageOwnerUserId, string projectId, IReadOnlyList<RateLimitRule> rules, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<ProjectAuditLogResult> BrowseProjectLogsAsync(long storageOwnerUserId, string projectId, string? search, string? action, string? date, string sort, int page, int pageSize, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class FakeValuesProvider : IQueryValuesContextProvider
    {
        public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(string projectId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

}

public sealed class QueryEndpointsTests_Sqlite(SqliteHostFactory factory) : QueryEndpointsTests<SqliteHostFactory>(factory);

public sealed class QueryEndpointsTests_Postgres(PostgresHostFactory factory) : QueryEndpointsTests<PostgresHostFactory>(factory);
