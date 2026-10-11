using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class NetworkStorageRateLimitsTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly SelfHostFactory factory;

    protected NetworkStorageRateLimitsTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }
    private static NetworkStorageRequest BuildRequest(string projectId, string? apiKey = "sk-test-key")
    {
        var route = NetworkStorageRouteClassifier.Classify("GET", $"/v3/storage/{projectId}/rate-limits");
        return new NetworkStorageRequest(
            route,
            new Dictionary<string, string>(),
            ContentType: null,
            AuthSignals: new Dictionary<string, bool>(),
            Credentials: new NetworkStorageCredentials(ApiKey: apiKey, SteamId: null, AuthSessionToken: null, SessionToken: null, EncryptedRequestId: null),
            Body: null,
            ResolvedOwnerUserId: null,
            CancellationToken: CancellationToken.None);
    }

    [SkippableFact]
    public async Task ValidKeyWithConfiguredLimits_Returns200WithBunCompatiblePayload()
    {
        // Load fixture to construct expected rate limits matching the sampled shape
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-fixtures", "rate-limits-sample.json");
        var fixtureJson = await File.ReadAllTextAsync(fixturePath);
        using var fixtureDoc = JsonDocument.Parse(fixtureJson);
        var root = fixtureDoc.RootElement;

        // Build EndpointRateLimits dictionary from fixture
        var endpointLimits = JsonSerializer.Deserialize<Dictionary<string, object>>(
            root.GetProperty("endpointRateLimits").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Build rules from fixture
        var rules = new List<RateLimitRule>();
        foreach (var ruleEl in root.GetProperty("rules").EnumerateArray())
        {
            rules.Add(new RateLimitRule(
                ruleEl.GetProperty("id").GetString() ?? string.Empty,
                ruleEl.GetProperty("collection").GetString() ?? string.Empty,
                ruleEl.GetProperty("field").GetString() ?? string.Empty,
                ruleEl.GetProperty("action").GetString() ?? string.Empty,
                ruleEl.GetProperty("maxPerMinute").ValueKind == JsonValueKind.Null ? null : ruleEl.GetProperty("maxPerMinute").GetInt32(),
                ruleEl.GetProperty("maxPerHour").ValueKind == JsonValueKind.Null ? null : ruleEl.GetProperty("maxPerHour").GetInt32(),
                ruleEl.GetProperty("maxPerDay").ValueKind == JsonValueKind.Null ? null : ruleEl.GetProperty("maxPerDay").GetInt32(),
                ruleEl.GetProperty("enabled").GetBoolean(),
                ruleEl.GetProperty("scope").GetString() ?? "per_player"));
        }

        var resolver = new FakeResolver(new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "demo-project", Enabled: true, KeyType: "secret"));
        var projectService = new FakeProjectService(new ProjectRateLimits(endpointLimits, rules));
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertRateLimitRulesAsync("demo-project", root.GetProperty("rules"), 1, CancellationToken.None);
        await store.UpsertEndpointAsync("demo-project", "published", "published", "POST", true,
            JsonSerializer.SerializeToElement(new { rateLimit = root.GetProperty("endpointRateLimits") }), null, 1, CancellationToken.None);

        var handler = new RateLimitsHandler(resolver, projectService, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitsHandler>.Instance);
        var result = await handler.ExecuteAsync(BuildRequest("demo-project"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("secret", result.AuthDecision);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("demo-project", json.GetProperty("projectId").GetString());

        // Verify endpointRateLimits shape
        var erl = json.GetProperty("endpointRateLimits").GetProperty("published");
        Assert.True(erl.GetProperty("enabled").GetBoolean());

        var perPlayer = erl.GetProperty("perPlayer");
        Assert.Equal(60, perPlayer.GetProperty("perMinute").GetInt32());
        Assert.Equal(300, perPlayer.GetProperty("perHour").GetInt32());
        Assert.Equal(5000, perPlayer.GetProperty("perDay").GetInt32());

        var global = erl.GetProperty("global");
        Assert.Equal(1000, global.GetProperty("perMinute").GetInt32());
        Assert.Equal(10000, global.GetProperty("perHour").GetInt32());
        Assert.Equal(JsonValueKind.Null, global.GetProperty("perDay").ValueKind);

        // Verify rules array shape and camelCase fields
        var rulesArray = json.GetProperty("rules");
        Assert.Equal(3, rulesArray.GetArrayLength());

        var rule0 = rulesArray[0];
        Assert.Equal("rule-field-score", rule0.GetProperty("id").GetString());
        Assert.Equal("player_data", rule0.GetProperty("collection").GetString());
        Assert.Equal("score", rule0.GetProperty("field").GetString());
        Assert.Equal("reject", rule0.GetProperty("action").GetString());
        Assert.Equal(10, rule0.GetProperty("maxPerMinute").GetInt32());
        Assert.Equal(JsonValueKind.Null, rule0.GetProperty("maxPerHour").ValueKind);
        Assert.Equal(JsonValueKind.Null, rule0.GetProperty("maxPerDay").ValueKind);
        Assert.True(rule0.GetProperty("enabled").GetBoolean());
        Assert.Equal("per_player", rule0.GetProperty("scope").GetString());

        var rule2 = rulesArray[2];
        Assert.Equal("rule-field-xp", rule2.GetProperty("id").GetString());
        Assert.Equal("clamp", rule2.GetProperty("action").GetString());
        Assert.Equal(300, rule2.GetProperty("maxPerMinute").GetInt32());
        Assert.Equal(5000, rule2.GetProperty("maxPerHour").GetInt32());
        Assert.Equal(JsonValueKind.Null, rule2.GetProperty("maxPerDay").ValueKind);
        Assert.False(rule2.GetProperty("enabled").GetBoolean());
    }

    [SkippableFact]
    public async Task ValidKeyWithNoLimits_Returns200WithEmptyShape()
    {
        var resolver = new FakeResolver(new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "demo-project", Enabled: true, KeyType: "public"));
        var projectService = new FakeProjectService(new ProjectRateLimits(EndpointRateLimits: null, Rules: null));

        var handler = new RateLimitsHandler(resolver, projectService, new InMemoryNetworkStorageStore(), (Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitsHandler>.Instance));
        var result = await handler.ExecuteAsync(BuildRequest("demo-project"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("public", result.AuthDecision);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());

        Assert.Empty(json.GetProperty("endpointRateLimits").EnumerateObject());

        // Rules should be empty array
        var rules = json.GetProperty("rules");
        Assert.Equal(0, rules.GetArrayLength());
    }

    [SkippableFact]
    public async Task MissingApiKey_ReturnsUnauthorized()
    {
        var resolver = new FakeResolver(new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "demo-project", Enabled: true, KeyType: "secret"));
        var projectService = new FakeProjectService(new ProjectRateLimits(null, null));

        var handler = new RateLimitsHandler(resolver, projectService, new InMemoryNetworkStorageStore(), (Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitsHandler>.Instance));

        // No API key in credentials
        var result = await handler.ExecuteAsync(BuildRequest("demo-project", apiKey: null));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("anonymous", result.AuthDecision);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("UNAUTHORIZED", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Invalid or missing API key.", json.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("demo-project", json.GetProperty("projectId").GetString());
        Assert.Equal("auth", json.GetProperty("source").GetString());
    }

    [SkippableFact]
    public async Task InvalidApiKey_ReturnsUnauthorized()
    {
        var resolver = new FakeResolver(null); // key not resolved
        var projectService = new FakeProjectService(new ProjectRateLimits(null, null));

        var handler = new RateLimitsHandler(resolver, projectService, new InMemoryNetworkStorageStore(), (Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitsHandler>.Instance));
        var result = await handler.ExecuteAsync(BuildRequest("demo-project", apiKey: "sk-invalid"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("anonymous", result.AuthDecision);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("UNAUTHORIZED", json.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task DisabledApiKey_ReturnsUnauthorized()
    {
        var resolver = new FakeResolver(new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "demo-project", Enabled: false, KeyType: "public"));
        var projectService = new FakeProjectService(new ProjectRateLimits(null, null));

        var handler = new RateLimitsHandler(resolver, projectService, new InMemoryNetworkStorageStore(), (Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitsHandler>.Instance));
        var result = await handler.ExecuteAsync(BuildRequest("demo-project", apiKey: "sk-disabled"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
    }

    [SkippableFact]
    public void CanHandleAcceptsGetRateLimits()
    {
        var handler = new RateLimitsHandler(new FakeResolver(null), new FakeProjectService(new ProjectRateLimits(null, null)), new InMemoryNetworkStorageStore(), (Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitsHandler>.Instance));

        var route = NetworkStorageRouteClassifier.Classify("GET", "/v3/storage/my-project/rate-limits");
        Assert.True(handler.CanHandle(route));
        Assert.Equal(NetworkStorageRouteFamily.StorageRateLimits, handler.Family);
    }

    [SkippableFact]
    public void CanHandleRejectsNonGet()
    {
        var handler = new RateLimitsHandler(new FakeResolver(null), new FakeProjectService(new ProjectRateLimits(null, null)), new InMemoryNetworkStorageStore(), (Microsoft.Extensions.Logging.Abstractions.NullLogger<RateLimitsHandler>.Instance));

        var route = NetworkStorageRouteClassifier.Classify("POST", "/v3/storage/my-project/rate-limits");
        Assert.False(handler.CanHandle(route));
    }

    [SkippableFact]
    public async Task LiveRoute_V3RateLimits_IsServedNativelyWithoutGateway()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-fixtures", "rate-limits-sample.json");
        var fixtureJson = await File.ReadAllTextAsync(fixturePath);
        using var fixtureDoc = JsonDocument.Parse(fixtureJson);
        var root = fixtureDoc.RootElement;
        var endpointLimits = JsonSerializer.Deserialize<Dictionary<string, object>>(
            root.GetProperty("endpointRateLimits").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var rules = new List<RateLimitRule>();
        foreach (var ruleEl in root.GetProperty("rules").EnumerateArray())
        {
            rules.Add(new RateLimitRule(
                ruleEl.GetProperty("id").GetString() ?? string.Empty,
                ruleEl.GetProperty("collection").GetString() ?? string.Empty,
                ruleEl.GetProperty("field").GetString() ?? string.Empty,
                ruleEl.GetProperty("action").GetString() ?? string.Empty,
                ruleEl.GetProperty("maxPerMinute").ValueKind == JsonValueKind.Null ? null : ruleEl.GetProperty("maxPerMinute").GetInt32(),
                ruleEl.GetProperty("maxPerHour").ValueKind == JsonValueKind.Null ? null : ruleEl.GetProperty("maxPerHour").GetInt32(),
                ruleEl.GetProperty("maxPerDay").ValueKind == JsonValueKind.Null ? null : ruleEl.GetProperty("maxPerDay").GetInt32(),
                ruleEl.GetProperty("enabled").GetBoolean(),
                ruleEl.GetProperty("scope").GetString() ?? "per_player"));
        }

        using var liveClient = factory.WithWebHostBuilder(builder =>
        {
            
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new FakeResolver(new StorageApiKeyAuthResult(
                    UserId: 42, ProjectId: "demo-project", Enabled: true, KeyType: "secret")));
                services.RemoveAll<INetworkStorageProjectService>();
                services.AddScoped<INetworkStorageProjectService>(_ => new FakeProjectService(new ProjectRateLimits(endpointLimits, rules)));
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await liveClient.GetAsync("/v3/storage/demo-project/rate-limits?apiKey=sk-test-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var json = document.RootElement;
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("store", json.GetProperty("source").GetString());
        Assert.Equal("demo-project", json.GetProperty("projectId").GetString());
    }

    private sealed class FakeResolver : IStorageApiKeyResolver
    {
        private readonly StorageApiKeyAuthResult? _result;

        public FakeResolver(StorageApiKeyAuthResult? result) => _result = result;

        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(
            string apiKey, string projectId, CancellationToken cancellationToken)
            => Task.FromResult(_result);
    }

    private sealed class FakeProjectService : INetworkStorageProjectService
    {
        private readonly ProjectRateLimits _rateLimits;

        public FakeProjectService(ProjectRateLimits rateLimits) => _rateLimits = rateLimits;

        public Task<ProjectRateLimits> GetProjectRateLimitsAsync(
            long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => Task.FromResult(_rateLimits);

        public Task<NetworkStorageProjectCreateResult> CreateProjectAsync(
            long userId, string name, string? description, bool enabled, bool requireSboxAuth,
            string keyMode, string organizationId, CancellationToken cancellationToken, string? hostingProfile = null)
            => throw new NotSupportedException();

        public Task<NetworkStorageProjectAccessResult?> ResolveProjectAccessAsync(
            long userId, string projectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<NetworkStorageProjectResources?> GetProjectResourcesAsync(
            long userId, string projectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<NetworkStorageProjectResources?> GetProjectResourcesForOwnerAsync(
            long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<NetworkStorageTeamData?> GetProjectTeamAsync(
            long storageOwnerUserId, string projectId, string? organizationId,
            string callerRole, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ApiKeyInfo>> GetProjectKeysAsync(
            long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<(ApiKeyInfo Key, string RawKey)> CreateProjectKeyAsync(
            long storageOwnerUserId, string projectId, string label, string keyType,
            Dictionary<string, string>? permissions, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task ToggleProjectKeyAsync(
            long storageOwnerUserId, string projectId, string key, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task RemoveProjectKeyAsync(
            long storageOwnerUserId, string projectId, string key, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task UpdateProjectKeyPermissionsAsync(
            long storageOwnerUserId, string projectId, string keyIdentifier,
            Dictionary<string, string> permissions, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteProjectAsync(
            long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task UpdateProjectSettingsAsync(
            long storageOwnerUserId, string projectId, string settingsTab,
            Dictionary<string, string> formValues, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ProjectUsageData> GetProjectUsageAsync(
            long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SaveEndpointRateLimitsAsync(
            long storageOwnerUserId, string projectId,
            Dictionary<string, object> endpointRateLimits, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SaveRateLimitRulesAsync(
            long storageOwnerUserId, string projectId,
            IReadOnlyList<RateLimitRule> rules, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<SboxNetworkStorage.Contracts.NetworkStorage.ProjectAuditLogResult> BrowseProjectLogsAsync(long suid, string pid, string? s, string? a, string? d, string sort, int pg, int ps, CancellationToken ct) => Task.FromResult(new SboxNetworkStorage.Contracts.NetworkStorage.ProjectAuditLogResult(System.Array.Empty<SboxNetworkStorage.Contracts.NetworkStorage.ProjectAuditLogEntry>(), 0, 1, 1, ps, false, false));
    }
}

public sealed class NetworkStorageRateLimitsTests_Sqlite(SqliteHostFactory factory) : NetworkStorageRateLimitsTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageRateLimitsTests_Postgres(PostgresHostFactory factory) : NetworkStorageRateLimitsTests<PostgresHostFactory>(factory);
