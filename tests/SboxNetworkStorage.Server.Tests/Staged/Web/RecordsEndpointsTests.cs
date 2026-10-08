using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Contracts.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Live-route parity tests for the .NET-native Network Storage player-records
/// (save-slot index) API family
/// (<c>GET/POST /v3/storage/{projectId}/{collectionId}/{steamId}/records</c>,
/// <c>DELETE/PATCH .../records/{recordId}</c>, plus the <c>/v1</c> +
/// <c>/api/storage</c> aliases). Proves the Bun→.NET cutover: save-slot CRUD
/// executes natively via <c>IBunnyWorkspaceClient</c> (index) +
/// <c>INetworkStorageDataPlane</c> (save data), the Bun wire contract is
/// preserved, and no request is proxied to the dead Bun storage-api.
/// </summary>
public abstract class RecordsEndpointsTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string ProjectId = "proj-1";
    private const string SecretKey = "sbox_sk_testsecretkey";
    private const string CollectionId = "saves";
    private const string SteamId = "76561198000000001";

    private readonly SelfHostFactory _factory;

    protected RecordsEndpointsTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    private HttpClient CreateClient(
        bool projectEnabled = true,
        int maxRecords = 3,
        bool allowRecordDelete = true,
        string keyType = "secret",
        Dictionary<string, string>? permissions = null,
        bool collectionFound = true,
        string? existingIndexJson = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            // Dead Bun storage-api port — a 502 would prove the request proxied to Bun.
            builder.ConfigureServices(services => services.Configure<ScyllaDbOptions>(o => o.Primary = false));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new FakeKeyResolver(SecretKey, ProjectId, keyType, permissions));
                services.RemoveAll<INetworkStorageProjectService>();
                services.AddScoped<INetworkStorageProjectService>(_ => new FakeProjectService(projectEnabled, collectionFound ? [MakeCollection(maxRecords, allowRecordDelete)] : []));
                services.RemoveAll<IBunnyWorkspaceClient>();
                services.AddScoped<IBunnyWorkspaceClient>(_ => new FakeBunnyClient(existingIndexJson));
                services.RemoveAll<INetworkStorageDataPlane>();
                services.AddScoped<INetworkStorageDataPlane>(_ => new FakeDataPlane());
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static CollectionResource MakeCollection(int maxRecords, bool allowRecordDelete)
        => new(CollectionId, "Saves", null, "per-steamid", null, null, null, null, false, maxRecords, allowRecordDelete);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string RecordsUrl(string path, bool withApiKey = true)
        => withApiKey ? $"{path}?apiKey={SecretKey}" : path;

    // ── List ──

    [SkippableFact]
    public async Task List_WithValidKey_ReturnsRecords()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Sboxcool-Route-Owner", out var owner));
        Assert.Equal(".NET native", Assert.Single(owner));

        var body = await BodyAsync(response);
        Assert.True(body.TryGetProperty("records", out _));
        Assert.Equal(3, body.GetProperty("maxRecords").GetInt32());
    }

    [SkippableFact]
    public async Task List_V1Alias_ReturnsRecords()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(RecordsUrl($"/v1/storage/{ProjectId}/{CollectionId}/{SteamId}/records"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.True(body.TryGetProperty("records", out _));
    }

    [SkippableFact]
    public async Task List_ApiStorageAlias_ReturnsRecords()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(RecordsUrl($"/api/storage/{ProjectId}/{CollectionId}/{SteamId}/records"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Create ──

    [SkippableFact]
    public async Task Create_WithRecordName_ReturnsOk()
    {
        using var client = CreateClient();
        var content = new StringContent("{\"recordName\":\"Save 1\"}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records"), content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("Save 1", body.GetProperty("recordName").GetString());
        Assert.NotNull(body.GetProperty("recordId").GetString());
        Assert.Equal(6, body.GetProperty("recordId").GetString()!.Length);
    }

    [SkippableFact]
    public async Task Create_DefaultRecordName_IsSave()
    {
        using var client = CreateClient();
        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records"), content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("Save", body.GetProperty("recordName").GetString());
    }

    [SkippableFact]
    public async Task Create_AtLimit_ReturnsRecordLimitReached()
    {
        // Pre-populate index with maxRecords entries.
        var existingIndex = "{\"records\":[" +
            "{\"recordId\":\"abc001\",\"recordName\":\"A\",\"createdAt\":\"2026-01-01T00:00:00.000Z\",\"updatedAt\":\"2026-01-01T00:00:00.000Z\",\"isLegacy\":false}," +
            "{\"recordId\":\"abc002\",\"recordName\":\"B\",\"createdAt\":\"2026-01-01T00:00:00.000Z\",\"updatedAt\":\"2026-01-01T00:00:00.000Z\",\"isLegacy\":false}" +
            "]}";

        using var client = CreateClient(maxRecords: 2, existingIndexJson: existingIndex);
        var content = new StringContent("{\"recordName\":\"C\"}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records"), content);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("RECORD_LIMIT_REACHED", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("Maximum 2 record(s)", body.GetProperty("error").GetProperty("message").GetString());
    }

    // ── Delete ──

    [SkippableFact]
    public async Task Delete_ExistingRecord_ReturnsOk()
    {
        var existingIndex = "{\"records\":[" +
            "{\"recordId\":\"abc001\",\"recordName\":\"A\",\"createdAt\":\"2026-01-01T00:00:00.000Z\",\"updatedAt\":\"2026-01-01T00:00:00.000Z\",\"isLegacy\":false}" +
            "]}";

        using var client = CreateClient(existingIndexJson: existingIndex);
        using var response = await client.DeleteAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records/abc001"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.True(body.GetProperty("ok").GetBoolean());
    }

    [SkippableFact]
    public async Task Delete_WhenDeleteDisabled_ReturnsRecordDeleteDisabled()
    {
        var existingIndex = "{\"records\":[" +
            "{\"recordId\":\"abc001\",\"recordName\":\"A\",\"createdAt\":\"2026-01-01T00:00:00.000Z\",\"updatedAt\":\"2026-01-01T00:00:00.000Z\",\"isLegacy\":false}" +
            "]}";

        using var client = CreateClient(allowRecordDelete: false, existingIndexJson: existingIndex);
        using var response = await client.DeleteAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records/abc001"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("RECORD_DELETE_DISABLED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Delete_NotFound_ReturnsRecordNotFound()
    {
        using var client = CreateClient();
        using var response = await client.DeleteAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records/nonexistent"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("RECORD_NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Rename ──

    [SkippableFact]
    public async Task Rename_WithRecordName_ReturnsOk()
    {
        var existingIndex = "{\"records\":[" +
            "{\"recordId\":\"abc001\",\"recordName\":\"Old\",\"createdAt\":\"2026-01-01T00:00:00.000Z\",\"updatedAt\":\"2026-01-01T00:00:00.000Z\",\"isLegacy\":false}" +
            "]}";

        using var client = CreateClient(existingIndexJson: existingIndex);
        var content = new StringContent("{\"recordName\":\"New Name\"}", Encoding.UTF8, "application/json");
        using var response = await client.PatchAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records/abc001"), content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("New Name", body.GetProperty("recordName").GetString());
    }

    [SkippableFact]
    public async Task Rename_WithoutRecordName_ReturnsInvalidBody()
    {
        var existingIndex = "{\"records\":[" +
            "{\"recordId\":\"abc001\",\"recordName\":\"Old\",\"createdAt\":\"2026-01-01T00:00:00.000Z\",\"updatedAt\":\"2026-01-01T00:00:00.000Z\",\"isLegacy\":false}" +
            "]}";

        using var client = CreateClient(existingIndexJson: existingIndex);
        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PatchAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records/abc001"), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("INVALID_BODY", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("recordName", body.GetProperty("error").GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task Rename_NotFound_ReturnsRecordNotFound()
    {
        using var client = CreateClient();
        var content = new StringContent("{\"recordName\":\"X\"}", Encoding.UTF8, "application/json");
        using var response = await client.PatchAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records/nonexistent"), content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("RECORD_NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Error codes ──

    [SkippableFact]
    public async Task MissingApiKey_ReturnsUnauthorized()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("UNAUTHORIZED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task InvalidApiKey_ReturnsUnauthorized()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records?apiKey=bad-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("UNAUTHORIZED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task ProjectDisabled_ReturnsProjectDisabled()
    {
        using var client = CreateClient(projectEnabled: false);
        using var response = await client.GetAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("PROJECT_DISABLED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task PublicKey_ReturnsEndpointOnly()
    {
        using var client = CreateClient(keyType: "public");
        using var response = await client.GetAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("ENDPOINT_ONLY", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task SecretKeyWithoutCollectionsX_ReturnsForbidden()
    {
        // Secret key with collections:"r" (read but not execute).
        var perms = new Dictionary<string, string> { ["collections"] = "r" };
        using var client = CreateClient(permissions: perms);
        using var response = await client.GetAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("FORBIDDEN", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task CollectionNotFound_ReturnsNotFound()
    {
        using var client = CreateClient(collectionFound: false);
        using var response = await client.GetAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task InvalidSteamId_ReturnsInvalidKey()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(RecordsUrl($"/v3/storage/{ProjectId}/{CollectionId}/bad@steamid/records"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("INVALID_KEY", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task ErrorResponsesIncludeDocsUrl()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync($"/v3/storage/{ProjectId}/{CollectionId}/{SteamId}/records");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.NotNull(body.GetProperty("error").GetProperty("docsUrl").GetString());
    }

    // ── Fakes ──

    private sealed class FakeKeyResolver(string validKey, string projectId, string keyType, Dictionary<string, string>? permissions) : IStorageApiKeyResolver
    {
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string project, CancellationToken ct)
        {
            if (!string.Equals(apiKey, validKey, StringComparison.Ordinal) || !string.Equals(project, projectId, StringComparison.Ordinal))
                return Task.FromResult<StorageApiKeyAuthResult?>(null);
            return Task.FromResult<StorageApiKeyAuthResult?>(new StorageApiKeyAuthResult(42, projectId, Enabled: true, keyType, permissions));
        }
    }

    private sealed class FakeProjectService(bool enabled, IReadOnlyList<CollectionResource> collections) : INetworkStorageProjectService
    {
        public Task<NetworkStorageProjectAccessResult?> ResolveProjectAccessAsync(long userId, string projectId, CancellationToken ct)
            => Task.FromResult<NetworkStorageProjectAccessResult?>(new NetworkStorageProjectAccessResult(
                new BunnyProject(projectId, "Test", null, Enabled: enabled, null, null, null),
                Organization: null, StorageOwnerUserId: userId,
                CollectionCount: collections.Count, ApiKeyCount: 0, TeamMemberCount: 0, QueryCount: 0, WorkflowCount: 0, EndpointCount: 0,
                RequireSboxAuth: false, PlayerKeyMode: null, HasRateLimits: false, CanManage: true,
                HeartbeatStatus: null, HeartbeatColor: null, HeartbeatText: null));

        public Task<NetworkStorageProjectResources?> GetProjectResourcesForOwnerAsync(long storageOwnerUserId, string projectId, CancellationToken ct)
            => Task.FromResult<NetworkStorageProjectResources?>(new NetworkStorageProjectResources(collections, Array.Empty<EndpointResource>()));

        // Unused members throw NotImplementedException (not reached by the records endpoint path).
        public Task<NetworkStorageProjectCreateResult> CreateProjectAsync(long userId, string name, string? description, bool enabled, bool requireSboxAuth, string keyMode, string organizationId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NetworkStorageProjectResources?> GetProjectResourcesAsync(long userId, string projectId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NetworkStorageTeamData?> GetProjectTeamAsync(long storageOwnerUserId, string projectId, string? organizationId, string callerRole, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ApiKeyInfo>> GetProjectKeysAsync(long storageOwnerUserId, string projectId, CancellationToken ct) => throw new NotImplementedException();
        public Task<(ApiKeyInfo Key, string RawKey)> CreateProjectKeyAsync(long storageOwnerUserId, string projectId, string label, string keyType, Dictionary<string, string>? permissions, CancellationToken ct) => throw new NotImplementedException();
        public Task ToggleProjectKeyAsync(long storageOwnerUserId, string projectId, string key, CancellationToken ct) => throw new NotImplementedException();
        public Task RemoveProjectKeyAsync(long storageOwnerUserId, string projectId, string key, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateProjectKeyPermissionsAsync(long storageOwnerUserId, string projectId, string keyIdentifier, Dictionary<string, string> permissions, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteProjectAsync(long storageOwnerUserId, string projectId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateProjectSettingsAsync(long storageOwnerUserId, string projectId, string settingsTab, Dictionary<string, string> formValues, CancellationToken ct) => throw new NotImplementedException();
        public Task<ProjectUsageData> GetProjectUsageAsync(long storageOwnerUserId, string projectId, CancellationToken ct) => throw new NotImplementedException();
        public Task<ProjectRateLimits> GetProjectRateLimitsAsync(long storageOwnerUserId, string projectId, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveEndpointRateLimitsAsync(long storageOwnerUserId, string projectId, Dictionary<string, object> endpointRateLimits, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveRateLimitRulesAsync(long storageOwnerUserId, string projectId, IReadOnlyList<RateLimitRule> rules, CancellationToken ct) => throw new NotImplementedException();
        public Task<ProjectAuditLogResult> BrowseProjectLogsAsync(long storageOwnerUserId, string projectId, string? search, string? action, string? date, string sort, int page, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>
    /// In-memory IBunnyWorkspaceClient that serves only GetProjectResourceAsync
    /// + PutProjectResourceAsync. All other members return defaults / throw
    /// NotImplementedException (not used by the records endpoint path).
    /// </summary>
    private sealed class FakeBunnyClient(string? existingIndexJson) : IBunnyWorkspaceClient
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<BunnyProject>> GetUserProjectsAsync(long userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<BunnyProject> projects, CancellationToken ct) => throw new NotImplementedException();

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken ct)
        {
            var key = $"{userId}/{projectId}/{resourcePath}";
            if (_store.TryGetValue(key, out var json))
            {
                return Task.FromResult<T?>(JsonSerializer.Deserialize<T>(json, CamelCase));
            }
            if (existingIndexJson is not null && resourcePath.EndsWith("record-index.json", StringComparison.Ordinal))
            {
                return Task.FromResult<T?>(JsonSerializer.Deserialize<T>(existingIndexJson, CamelCase));
            }
            return Task.FromResult<T?>(default);
        }

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken ct) => throw new NotImplementedException();

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken ct)
        {
            var key = $"{userId}/{projectId}/{resourcePath}";
            _store[key] = JsonSerializer.Serialize(data, CamelCase);
            return Task.CompletedTask;
        }

        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken ct) => throw new NotImplementedException();
        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteRawAsync(string absolutePath, CancellationToken ct) => throw new NotImplementedException();

        private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }

    /// <summary>Minimal data plane fake that tracks deletes (no-op read/write).</summary>
    private sealed class FakeDataPlane : INetworkStorageDataPlane
    {
        public Task<RecordReadResult> ReadRecordAsync(long ownerUserId, string projectId, string collectionId, string recordKey, CancellationToken ct)
            => Task.FromResult(RecordReadResult.NotFound);
        public Task WriteRecordAsync(long ownerUserId, string projectId, string collectionId, string recordKey, JsonElement value, CancellationToken ct)
            => Task.CompletedTask;
        public Task DeleteRecordAsync(long ownerUserId, string projectId, string collectionId, string recordKey, CancellationToken ct)
            => Task.CompletedTask;
    }
}

public sealed class RecordsEndpointsTests_Sqlite(SqliteHostFactory factory) : RecordsEndpointsTests<SqliteHostFactory>(factory);

public sealed class RecordsEndpointsTests_Postgres(PostgresHostFactory factory) : RecordsEndpointsTests<PostgresHostFactory>(factory);
