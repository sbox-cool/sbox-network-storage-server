using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class NetworkStorageStatsReadCandidateTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly HttpClient client;

    protected NetworkStorageStatsReadCandidateTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static NetworkStorageRequest BuildRequest(
        string projectId,
        string steamId,
        string? apiKey = "sbox_sk_testpublickey",
        string method = "GET")
    {
        var route = NetworkStorageRouteClassifier.Classify(method, $"/api/storage/{projectId}/stats/{steamId}");
        var query = new Dictionary<string, string>();
        if (apiKey is not null)
        {
            query["apiKey"] = apiKey;
        }

        return new NetworkStorageRequest(
            route,
            query,
            ContentType: null,
            AuthSignals: new Dictionary<string, bool>(),
            Credentials: new NetworkStorageCredentials(
                ApiKey: apiKey,
                SteamId: null,
                AuthSessionToken: null,
                SessionToken: null,
                EncryptedRequestId: null),
            Body: null,
            ResolvedOwnerUserId: null,
            CancellationToken: CancellationToken.None);
    }

    // ── Success: valid key + matching stats file → 200 + Bun-compatible body ──

    [SkippableFact]
    public async Task ValidKeyReturnsOkWithStatsPayload()
    {
        var fixture = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-shadow", "storage-stats-sample.json"));
        using var fixtureDoc = JsonDocument.Parse(fixture);
        var expectedStats = fixtureDoc.RootElement.Clone();

        var handler = new StatsReadHandler(
            new FakeKeyResolver("test-public-key", "demo-project", "public"),
            new FakeBunnyStats(expectedStats, "76561197960287930"));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", "76561197960287930", apiKey: "test-public-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("public", result.AuthDecision);
        Assert.Contains(result.StoragePathsRead,
            p => p.EndsWith("player-stats/76561197960287930.json", StringComparison.Ordinal));

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("76561197960287930", json.GetProperty("steamId").GetString());
        Assert.Equal(3600, json.GetProperty("playTimeSeconds").GetInt64());
        Assert.Equal(12, json.GetProperty("sessionCount").GetInt32());
        Assert.Equal("TestPlayer", json.GetProperty("displayName").GetString());
    }

    // ── Not found: valid key but no stats file → 404 NOT_FOUND ──

    [SkippableFact]
    public async Task MissingStatsFileReturnsNotFound()
    {
        var handler = new StatsReadHandler(
            new FakeKeyResolver("test-public-key", "demo-project", "public"),
            new FakeBunnyStats(default, "nonexistent-player"));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", "nonexistent-player", apiKey: "test-public-key"));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("NOT_FOUND", result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("NOT_FOUND", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Auth failure: missing key → 401 UNAUTHORIZED ──

    [SkippableFact]
    public async Task MissingApiKeyReturnsUnauthorized()
    {
        var handler = new StatsReadHandler(
            new FakeKeyResolver(null, "demo-project", null),
            new FakeBunnyStats(default, "some-player"));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", "some-player", apiKey: null));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("UNAUTHORIZED", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Auth failure: invalid key → 401 UNAUTHORIZED ──

    [SkippableFact]
    public async Task InvalidApiKeyReturnsUnauthorized()
    {
        var handler = new StatsReadHandler(
            new FakeKeyResolver("bad-key", "demo-project", null),
            new FakeBunnyStats(default, "some-player"));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", "some-player", apiKey: "bad-key"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("UNAUTHORIZED", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── CanHandle: POST returns false ──

    [SkippableFact]
    public void CanHandleReturnsFalseForPost()
    {
        var route = NetworkStorageRouteClassifier.Classify("POST", "/api/storage/demo-project/stats/some-player");
        var handler = new StatsReadHandler(
            new FakeKeyResolver("test-public-key", "demo-project", "public"),
            new FakeBunnyStats(default, "some-player"));

        Assert.False(handler.CanHandle(route));
    }

    // ── CanHandle: GET returns true ──

    [SkippableFact]
    public void CanHandleReturnsTrueForGet()
    {
        var route = NetworkStorageRouteClassifier.Classify("GET", "/api/storage/demo-project/stats/some-player");
        var handler = new StatsReadHandler(
            new FakeKeyResolver("test-public-key", "demo-project", "public"),
            new FakeBunnyStats(default, "some-player"));

        Assert.True(handler.CanHandle(route));
    }

    // ── Shadow endpoint integration test ──

    // ── Fakes ──

    private sealed class FakeKeyResolver : IStorageApiKeyResolver
    {
        private readonly string? _validKey;
        private readonly string? _projectId;
        private readonly string? _keyType;

        public FakeKeyResolver(string? validKey, string? projectId, string? keyType)
        {
            _validKey = validKey;
            _projectId = projectId;
            _keyType = keyType;
        }

        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string projectId, CancellationToken cancellationToken)
        {
            if (_validKey is null || _keyType is null)
            {
                return Task.FromResult<StorageApiKeyAuthResult?>(null);
            }

            return Task.FromResult<StorageApiKeyAuthResult?>(
                new StorageApiKeyAuthResult(
                    UserId: 42,
                    ProjectId: projectId,
                    Enabled: true,
                    KeyType: _keyType));
        }
    }

    private sealed class FakeBunnyStats : IWorkspaceStore
    {
        private readonly JsonElement _stats;
        private readonly string _matchingSteamId;
        private readonly bool _throwsOnRead;

        public FakeBunnyStats(
            JsonElement stats,
            string matchingSteamId,
            bool throwsOnRead = false)
        {
            _stats = stats;
            _matchingSteamId = matchingSteamId;
            _throwsOnRead = throwsOnRead;
        }

        public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkspaceProject>>(
                new List<WorkspaceProject>
                {
                    new(Id: "demo-project", Name: "Test Project", Description: null, Enabled: true,
                        CreatedAt: null, UpdatedAt: null, CompiledAt: null)
                });

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
        {
            if (_throwsOnRead)
            {
                throw new InvalidOperationException($"simulated read failure for {resourcePath}");
            }

            // Expect path like "player-stats/{steamId}.json". Mirror the real client: missing → default(T)
            // (default(JsonElement) == Undefined), never a null-cast which would throw for value types.
            var expectedPath = $"player-stats/{_matchingSteamId}.json";
            if (resourcePath != expectedPath || _stats.ValueKind == JsonValueKind.Undefined)
            {
                return Task.FromResult<T?>(default);
            }

            return Task.FromResult((T?)(object)_stats);
        }

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => Task.FromResult<WorkspaceProjectUsage?>(null);

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken)
            => Task.FromResult<T?>(default);

        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

public sealed class NetworkStorageStatsReadCandidateTests_Sqlite(SqliteHostFactory factory) : NetworkStorageStatsReadCandidateTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageStatsReadCandidateTests_Postgres(PostgresHostFactory factory) : NetworkStorageStatsReadCandidateTests<PostgresHostFactory>(factory);
