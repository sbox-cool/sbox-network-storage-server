using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class NetworkStorageStorageGlobalReadCandidateTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly HttpClient client;

    protected NetworkStorageStorageGlobalReadCandidateTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static NetworkStorageCandidateRequest BuildRequest(
        string projectId,
        string? apiKey = "sbox_sk_testkey",
        string? recordId = null,
        Dictionary<string, string>? extraQuery = null)
    {
        var path = recordId is not null
            ? $"/v3/storage/{projectId}/my-collection/record/{recordId}"
            : $"/v3/storage/{projectId}/my-collection/list";
        var route = NetworkStorageRouteClassifier.Classify("GET", path);
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (apiKey is not null)
            query["apiKey"] = apiKey;
        if (extraQuery is not null)
        {
            foreach (var (k, v) in extraQuery)
                query[k] = v;
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

    private static JsonElement LoadFixture(string name)
    {
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-shadow", name));
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    // ── Single record: hit → 200 ──

    [SkippableFact]
    public async Task SingleRecordFoundReturnsRecordBody()
    {
        var fixture = LoadFixture("storage-global-record-sample.json");
        var handler = new StorageGlobalReadCandidateHandler(
            new FakeGlobalKeyResolver(validKey: "valid-key", keyType: "secret"),
            new FakeGlobalBunny(records: new Dictionary<string, JsonElement>
            {
                ["my-collection/global/rec_abc123.json"] = fixture
            }),
            new FakeGlobalStorageEnumerator());

        var result = await handler.ExecuteAsync(BuildRequest("proj-a", apiKey: "valid-key", recordId: "rec_abc123"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("secret", result.AuthDecision);
        Assert.Contains(result.StoragePathsRead,
            p => p.EndsWith("my-collection/global/rec_abc123.json", StringComparison.Ordinal));

        // Verify raw record body matches fixture
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("rec_abc123", json.GetProperty("id").GetString());
        Assert.Equal(1500, json.GetProperty("data").GetProperty("score").GetInt32());
        Assert.Equal("TestPlayer", json.GetProperty("data").GetProperty("playerName").GetString());
        Assert.Equal("2026-05-30T12:00:00.000Z", json.GetProperty("_timestamp").GetString());
    }

    // ── Single record: miss → 404 NOT_FOUND ──

    [SkippableFact]
    public async Task SingleRecordMissingReturnsNotFound()
    {
        var handler = new StorageGlobalReadCandidateHandler(
            new FakeGlobalKeyResolver(validKey: "valid-key", keyType: "secret"),
            new FakeGlobalBunny(),
            new FakeGlobalStorageEnumerator());

        var result = await handler.ExecuteAsync(BuildRequest("proj-a", apiKey: "valid-key", recordId: "nonexistent"));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("NOT_FOUND", result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("NOT_FOUND", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("Record not found", json.GetProperty("error").GetProperty("message").GetString());
    }

    // ── List: returns shaped array ──

    [SkippableFact]
    public async Task ListReturnsShapedRecordsArray()
    {
        var record1 = JsonSerializer.Deserialize<JsonElement>(
            """{"id":"r1","data":{"v":1},"_timestamp":"2026-05-30T12:00:00.000Z","_writerId":"u1"}""");
        var record2 = JsonSerializer.Deserialize<JsonElement>(
            """{"id":"r2","data":{"v":2},"_timestamp":"2026-05-30T11:00:00.000Z","_writerId":"u2"}""");

        var handler = new StorageGlobalReadCandidateHandler(
            new FakeGlobalKeyResolver(validKey: "valid-key", keyType: "public"),
            new FakeGlobalBunny(records: new Dictionary<string, JsonElement>
            {
                ["my-collection/global/r1.json"] = record1,
                ["my-collection/global/r2.json"] = record2,
            }),
            new FakeGlobalStorageEnumerator(entries: new[]
            {
                new BunnyStorageEntry("r1.json", IsDirectory: false),
                new BunnyStorageEntry("r2.json", IsDirectory: false),
            }));

        var result = await handler.ExecuteAsync(BuildRequest("proj-a", apiKey: "valid-key", recordId: null));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("public", result.AuthDecision);
        Assert.Contains(result.StoragePathsRead,
            p => p.EndsWith("my-collection/global", StringComparison.Ordinal));

        var json = JsonSerializer.SerializeToElement(result.Body);
        var records = json.GetProperty("records");
        Assert.Equal(2, records.GetArrayLength());

        // Verify sorted by _timestamp descending (newest first)
        Assert.Equal("r1", records[0].GetProperty("id").GetString());
        Assert.Equal("r2", records[1].GetProperty("id").GetString());

        // Only 2 records under the default limit (50) → page is not full → no cursor (matches Bun routeV3GlobalList).
        var cursor = json.GetProperty("cursor");
        Assert.Equal(JsonValueKind.Null, cursor.ValueKind);
    }

    // ── List: limit and cursor pagination ──

    [SkippableFact]
    public async Task ListRespectsLimitAndReturnsCursorWhenPageFull()
    {
        var records = new Dictionary<string, JsonElement>();
        var entries = new List<BunnyStorageEntry>();
        for (var i = 0; i < 3; i++)
        {
            var id = $"r{i}";
            var ts = $"2026-05-30T1{i}:00:00.000Z";
            records[$"my-collection/global/{id}.json"] = JsonSerializer.Deserialize<JsonElement>(
                $$"""{"id":"{{id}}","_timestamp":"{{ts}}","_writerId":"u{{i}}"}""");
            entries.Add(new BunnyStorageEntry($"{id}.json", IsDirectory: false));
        }

        var handler = new StorageGlobalReadCandidateHandler(
            new FakeGlobalKeyResolver(validKey: "valid-key", keyType: "secret"),
            new FakeGlobalBunny(records: records),
            new FakeGlobalStorageEnumerator(entries: entries.ToArray()));

        var query = new Dictionary<string, string> { ["limit"] = "2" };
        var result = await handler.ExecuteAsync(
            BuildRequest("proj-a", apiKey: "valid-key", recordId: null, extraQuery: query));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        var page = json.GetProperty("records");
        Assert.Equal(2, page.GetArrayLength());

        // Sorted descending: r2, r1 (r0's ts is latest but wait - loop: r0=10:00, r1=11:00, r2=12:00)
        // Actually: r0="10" → 10:00, r1="11" → 11:00, r2="12" → 12:00. Desc → r2, r1
        Assert.Equal("r2", page[0].GetProperty("id").GetString());
        Assert.Equal("r1", page[1].GetProperty("id").GetString());

        // Cursor = r1's timestamp since page is full (2 of 3)
        var cursor = json.GetProperty("cursor");
        Assert.Equal("2026-05-30T11:00:00.000Z", cursor.GetString());
    }

    // ── List: after filter ──

    [SkippableFact]
    public async Task ListRespectsAfterCursor()
    {
        var recordA = JsonSerializer.Deserialize<JsonElement>(
            """{"id":"a","_timestamp":"2026-05-30T12:00:00.000Z","_writerId":"u1"}""");
        var recordB = JsonSerializer.Deserialize<JsonElement>(
            """{"id":"b","_timestamp":"2026-05-30T11:30:00.000Z","_writerId":"u2"}""");
        var recordC = JsonSerializer.Deserialize<JsonElement>(
            """{"id":"c","_timestamp":"2026-05-30T10:00:00.000Z","_writerId":"u3"}""");

        var handler = new StorageGlobalReadCandidateHandler(
            new FakeGlobalKeyResolver(validKey: "valid-key", keyType: "secret"),
            new FakeGlobalBunny(records: new Dictionary<string, JsonElement>
            {
                ["my-collection/global/a.json"] = recordA,
                ["my-collection/global/b.json"] = recordB,
                ["my-collection/global/c.json"] = recordC,
            }),
            new FakeGlobalStorageEnumerator(entries: new[]
            {
                new BunnyStorageEntry("a.json", IsDirectory: false),
                new BunnyStorageEntry("b.json", IsDirectory: false),
                new BunnyStorageEntry("c.json", IsDirectory: false),
            }));

        var query = new Dictionary<string, string> { ["after"] = "2026-05-30T11:45:00.000Z" };
        var result = await handler.ExecuteAsync(
            BuildRequest("proj-a", apiKey: "valid-key", recordId: null, extraQuery: query));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        var page = json.GetProperty("records");
        Assert.Equal(2, page.GetArrayLength());
        // Records with _timestamp < 11:45: b (11:30) and c (10:00). Sorted desc: b, c
        Assert.Equal("b", page[0].GetProperty("id").GetString());
        Assert.Equal("c", page[1].GetProperty("id").GetString());
    }

    // ── Auth failure: missing key → 401 UNAUTHORIZED ──

    [SkippableFact]
    public async Task MissingApiKeyReturnsUnauthorized()
    {
        var handler = new StorageGlobalReadCandidateHandler(
            new FakeGlobalKeyResolver(validKey: null, keyType: null),
            new FakeGlobalBunny(),
            new FakeGlobalStorageEnumerator());

        var result = await handler.ExecuteAsync(BuildRequest("proj-a", apiKey: null));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("UNAUTHORIZED", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Auth failure: invalid key → 401 UNAUTHORIZED ──

    [SkippableFact]
    public async Task InvalidApiKeyReturnsUnauthorized()
    {
        var handler = new StorageGlobalReadCandidateHandler(
            new FakeGlobalKeyResolver(validKey: "real-key", keyType: "secret"),
            new FakeGlobalBunny(),
            new FakeGlobalStorageEnumerator());

        var result = await handler.ExecuteAsync(BuildRequest("proj-a", apiKey: "bad-key"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("UNAUTHORIZED", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Fakes ──

    private sealed class FakeGlobalKeyResolver : IStorageApiKeyResolver
    {
        private readonly string? _validKey;
        private readonly string? _keyType;

        public FakeGlobalKeyResolver(string? validKey, string? keyType)
        {
            _validKey = validKey;
            _keyType = keyType;
        }

        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string projectId, CancellationToken cancellationToken)
        {
            if (_validKey is null || _keyType is null)
                return Task.FromResult<StorageApiKeyAuthResult?>(null);

            if (!string.Equals(apiKey, _validKey, StringComparison.Ordinal))
                return Task.FromResult<StorageApiKeyAuthResult?>(null);

            return Task.FromResult<StorageApiKeyAuthResult?>(
                new StorageApiKeyAuthResult(
                    UserId: 42,
                    ProjectId: projectId,
                    Enabled: true,
                    KeyType: _keyType));
        }
    }

    private sealed class FakeGlobalBunny : IBunnyWorkspaceClient
    {
        private readonly IReadOnlyDictionary<string, JsonElement>? _records;

        public FakeGlobalBunny(IReadOnlyDictionary<string, JsonElement>? records = null)
        {
            _records = records;
        }

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
        {
            if (_records is not null && _records.TryGetValue(resourcePath, out var match))
            {
                // The T? annotation + value-type T means we return a boxable result.
                // For JsonElement (struct), use a non-null value with the right ValueKind.
                return Task.FromResult((T?)(object)match);
            }

            // Missing resource: return a JsonElement with Undefined ValueKind.
            return Task.FromResult((T?)(object)default(JsonElement));
        }

        public Task<IReadOnlyList<BunnyProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<BunnyProject>>(Array.Empty<BunnyProject>());

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

    private sealed class FakeGlobalStorageEnumerator : IWorkspaceStorageEnumerator
    {
        private readonly IReadOnlyList<BunnyStorageEntry>? _entries;

        public FakeGlobalStorageEnumerator(IReadOnlyList<BunnyStorageEntry>? entries = null)
        {
            _entries = entries;
        }

        public Task<IReadOnlyList<BunnyStorageEntry>> ListProjectResourceAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
        {
            return Task.FromResult(_entries ?? (IReadOnlyList<BunnyStorageEntry>)Array.Empty<BunnyStorageEntry>());
        }
    }
}

public sealed class NetworkStorageStorageGlobalReadCandidateTests_Sqlite(SqliteHostFactory factory) : NetworkStorageStorageGlobalReadCandidateTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageStorageGlobalReadCandidateTests_Postgres(PostgresHostFactory factory) : NetworkStorageStorageGlobalReadCandidateTests<PostgresHostFactory>(factory);
