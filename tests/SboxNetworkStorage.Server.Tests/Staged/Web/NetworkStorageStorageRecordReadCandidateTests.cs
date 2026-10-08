using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class NetworkStorageStorageRecordReadCandidateTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly HttpClient client;

    protected NetworkStorageStorageRecordReadCandidateTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static NetworkStorageCandidateRequest BuildKeyReadRequest(string projectId, string collectionId, string key, string? apiKey = "sbox_sk_testpublickey")
    {
        var route = NetworkStorageRouteClassifier.Classify("GET", $"/v3/storage/{projectId}/{collectionId}/{key}");
        var query = new Dictionary<string, string>();
        if (apiKey is not null)
        {
            query["apiKey"] = apiKey;
        }

        return new NetworkStorageCandidateRequest(
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

    private static NetworkStorageCandidateRequest BuildRecordsListRequest(string projectId, string collectionId, string steamId, string? apiKey = "sbox_sk_testpublickey")
    {
        var route = NetworkStorageRouteClassifier.Classify("GET", $"/v3/storage/{projectId}/{collectionId}/{steamId}/records");
        var query = new Dictionary<string, string>();
        if (apiKey is not null)
        {
            query["apiKey"] = apiKey;
        }

        return new NetworkStorageCandidateRequest(
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

    // ── Key read: success ──

    [SkippableFact]
    public async Task KeyReadReturnsBunCompatibleOkPayload()
    {
        var fixture = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-shadow", "storage-record-saved-sample.json"));
        using var fixtureDoc = JsonDocument.Parse(fixture);
        var expected = fixtureDoc.RootElement.Clone();

        var handler = new StorageRecordReadCandidateHandler(
            new FakeKeyResolver("valid-key", "demo-project", "public"),
            new FakeBunnyStorageRecord(
                savedData: expected,
                projectId: "demo-project"));

        var result = await handler.ExecuteAsync(BuildKeyReadRequest("demo-project", "saves", "player_data", apiKey: "valid-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("public", result.AuthDecision);
        Assert.Contains(result.StoragePathsRead,
            p => p.Contains("saves/data/player_data/saved.json", StringComparison.Ordinal));

        var json = JsonSerializer.SerializeToElement(result.Body);

        Assert.Equal(expected.GetProperty("playerName").GetString(), json.GetProperty("playerName").GetString());
        Assert.Equal(expected.GetProperty("score").GetInt32(), json.GetProperty("score").GetInt32());
        Assert.Equal(expected.GetProperty("level").GetInt32(), json.GetProperty("level").GetInt32());
        Assert.Equal(expected.GetProperty("_savedAt").GetInt64(), json.GetProperty("_savedAt").GetInt64());
    }

    // ── Key read: missing/not-found ──

    [SkippableFact]
    public async Task KeyReadMissingReturnsNotFound()
    {
        var handler = new StorageRecordReadCandidateHandler(
            new FakeKeyResolver("valid-key", "demo-project", "public"),
            new FakeBunnyStorageRecord(
                savedData: default,
                projectId: "demo-project"));

        var result = await handler.ExecuteAsync(BuildKeyReadRequest("demo-project", "saves", "nonexistent_key", apiKey: "valid-key"));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("NOT_FOUND", result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("NOT_FOUND", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Key not found.", json.GetProperty("error").GetProperty("message").GetString());
    }

    // ── Records list: success ──

    [SkippableFact]
    public async Task RecordsListReturnsOkWithRecords()
    {
        var fixture = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-shadow", "storage-record-index-sample.json"));
        using var fixtureDoc = JsonDocument.Parse(fixture);
        var indexData = fixtureDoc.RootElement.Clone();
        var expectedRecords = indexData.GetProperty("records");

        var handler = new StorageRecordReadCandidateHandler(
            new FakeKeyResolver("valid-key", "demo-project", "secret"),
            new FakeBunnyStorageRecord(
                recordIndex: indexData,
                projectId: "demo-project"));

        var result = await handler.ExecuteAsync(BuildRecordsListRequest("demo-project", "saves", "76561197960265728", apiKey: "valid-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("secret", result.AuthDecision);
        Assert.Contains(result.StoragePathsRead,
            p => p.Contains("saves/data/76561197960265728/record-index.json", StringComparison.Ordinal));

        var json = JsonSerializer.SerializeToElement(result.Body);
        var records = json.GetProperty("records");
        Assert.Equal(expectedRecords.GetArrayLength(), records.GetArrayLength());

        for (var i = 0; i < expectedRecords.GetArrayLength(); i++)
        {
            Assert.Equal(
                expectedRecords[i].GetProperty("recordId").GetString(),
                records[i].GetProperty("recordId").GetString());
            Assert.Equal(
                expectedRecords[i].GetProperty("recordName").GetString(),
                records[i].GetProperty("recordName").GetString());
        }

        Assert.Equal(1, json.GetProperty("maxRecords").GetInt32());
    }

    // ── Records list: missing index → empty list ──

    [SkippableFact]
    public async Task RecordsListWithNoIndexReturnsEmptyRecords()
    {
        var handler = new StorageRecordReadCandidateHandler(
            new FakeKeyResolver("valid-key", "demo-project", "public"),
            new FakeBunnyStorageRecord(
                savedData: default,
                projectId: "demo-project"));

        var result = await handler.ExecuteAsync(BuildRecordsListRequest("demo-project", "saves", "76561197960265728", apiKey: "valid-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        var records = json.GetProperty("records");
        Assert.Equal(0, records.GetArrayLength());
        Assert.Equal(1, json.GetProperty("maxRecords").GetInt32());
    }

    // ── Auth failure: missing key → 401 UNAUTHORIZED ──

    [SkippableFact]
    public async Task MissingApiKeyReturnsUnauthorized()
    {
        var handler = new StorageRecordReadCandidateHandler(
            new FakeKeyResolver(null, "demo-project", null),
            new FakeBunnyStorageRecord(projectId: "demo-project"));

        var result = await handler.ExecuteAsync(BuildKeyReadRequest("demo-project", "saves", "player_data", apiKey: null));

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
        var handler = new StorageRecordReadCandidateHandler(
            new FakeKeyResolver("real-key", "demo-project", null),
            new FakeBunnyStorageRecord(projectId: "demo-project"));

        var result = await handler.ExecuteAsync(BuildKeyReadRequest("demo-project", "saves", "player_data", apiKey: "bad-key"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("UNAUTHORIZED", json.GetProperty("error").GetProperty("code").GetString());
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
            if (_validKey is null || _keyType is null || !string.Equals(apiKey, _validKey, StringComparison.Ordinal))
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

    private sealed class FakeBunnyStorageRecord : IBunnyWorkspaceClient
    {
        private readonly JsonElement _savedData;   // Key read: the stored saved.json value
        private readonly JsonElement _recordIndex; // Records list: the record-index.json value
        private readonly string _projectId;
        private readonly bool _throwsOnRead;

        public FakeBunnyStorageRecord(
            JsonElement savedData = default,
            JsonElement recordIndex = default,
            string? projectId = null,
            bool throwsOnRead = false)
        {
            _savedData = savedData.ValueKind == JsonValueKind.Undefined ? default : savedData;
            _recordIndex = recordIndex.ValueKind == JsonValueKind.Undefined ? default : recordIndex;
            _projectId = projectId ?? "demo-project";
            _throwsOnRead = throwsOnRead;
        }

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
        {
            if (_throwsOnRead)
            {
                throw new InvalidOperationException($"simulated read failure for {resourcePath}");
            }

            // resourcePath looks like:
            //   "{collectionId}/data/{key}/saved.json"           → key read
            //   "{collectionId}/data/{steamId}/record-index.json" → records list
            // Mirror the real BunnyWorkspaceClient: a missing resource yields default(T)
            // (default(JsonElement) == Undefined), never a null-cast which would throw for value types.
            if (resourcePath.EndsWith("/saved.json", StringComparison.Ordinal))
            {
                return Task.FromResult(_savedData.ValueKind == JsonValueKind.Undefined ? default : (T?)(object)_savedData);
            }

            if (resourcePath.EndsWith("/record-index.json", StringComparison.Ordinal))
            {
                return Task.FromResult(_recordIndex.ValueKind == JsonValueKind.Undefined ? default : (T?)(object)_recordIndex);
            }

            return Task.FromResult<T?>(default);
        }

        public Task<IReadOnlyList<BunnyProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<BunnyProject>>([]);

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => Task.FromResult<WorkspaceProjectUsage?>(null);

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<BunnyProject> projects, CancellationToken cancellationToken)
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

public sealed class NetworkStorageStorageRecordReadCandidateTests_Sqlite(SqliteHostFactory factory) : NetworkStorageStorageRecordReadCandidateTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageStorageRecordReadCandidateTests_Postgres(PostgresHostFactory factory) : NetworkStorageStorageRecordReadCandidateTests<PostgresHostFactory>(factory);
