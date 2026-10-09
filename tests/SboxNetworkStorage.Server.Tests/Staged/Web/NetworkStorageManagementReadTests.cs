using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class NetworkStorageManagementReadCandidateTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly SelfHostFactory factory;
    private readonly HttpClient client;

    protected NetworkStorageManagementReadCandidateTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
        client = factory.WithWebHostBuilder(builder =>
        {
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static NetworkStorageRequest BuildRequest(
        string projectId,
        string? apiKey = "sbox_sk_testsecretkey",
        string? routeSuffix = "endpoints",
        string? additionalParam = null)
    {
        var path = additionalParam is not null
            ? $"/v3/manage/{projectId}/{routeSuffix}/{additionalParam}"
            : $"/v3/manage/{projectId}/{routeSuffix}";
        var route = NetworkStorageRouteClassifier.Classify("GET", path);
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

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ══════════════════════════════════════════════════════════════════
    // Success: each implemented route returns 200 + expected shape
    // ══════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task GameValuesReturnsOkWithData()
    {
        const string sampleJson = """[{"name":"speed","values":{"walk":10}}]""";
        var gv = ParseJson(sampleJson);

        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1") { ResourceResponses = { ["game-values.json"] = gv } },
            new FakeProjectService());

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "game-values"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("secret", result.AuthDecision);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(1, json.GetProperty("data").GetArrayLength());
    }

    [SkippableFact]
    public async Task EndpointsReturnsOkWithData()
    {
        const string sampleJson = """[{"slug":"test-ep","method":"GET","enabled":true}]""";
        var eps = ParseJson(sampleJson);

        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1") { ResourceResponses = { ["endpoints.json"] = eps } },
            new FakeProjectService());

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "endpoints"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        var data = json.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("test-ep", data[0].GetProperty("slug").GetString());
    }

    [SkippableFact]
    public async Task CollectionsReturnsOkWithData()
    {
        const string sampleJson = """[{"name":"weapons","collectionType":"keyvalue"}]""";
        var collections = ParseJson(sampleJson);

        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1") { ResourceResponses = { ["collections.json"] = collections } },
            new FakeProjectService());

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "collections"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("weapons", json.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [SkippableFact]
    public async Task WorkflowsReturnsOkWithData()
    {
        const string sampleJson = """[{"id":"wf-1","name":"Test Workflow"}]""";
        var wfs = ParseJson(sampleJson);

        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1") { ResourceResponses = { ["workflows.json"] = wfs } },
            new FakeProjectService());

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "workflows"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("wf-1", json.GetProperty("data")[0].GetProperty("id").GetString());
    }

    [SkippableTheory]
    [InlineData("""[{"id":"q-1","name":"top_miners","type":"leaderboard"}]""", 1)]
    [InlineData(null, 0)]
    public async Task QueriesReturnsOkWithData(string? resourceJson, int expectedCount)
    {
        var workspace = new FakeBunnyManagement("proj-1");
        if (resourceJson is not null)
        {
            workspace.ResourceResponses["queries.json"] = ParseJson(resourceJson);
        }

        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            workspace,
            new FakeProjectService());

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "queries"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("secret", result.AuthDecision);
        Assert.Contains("network-storage/users/42/proj-1/queries.json", result.StoragePathsRead);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        var data = json.GetProperty("data");
        Assert.Equal(expectedCount, data.GetArrayLength());
        if (expectedCount > 0)
        {
            Assert.Equal("q-1", data[0].GetProperty("id").GetString());
            Assert.Equal("top_miners", data[0].GetProperty("name").GetString());
        }
    }

    [SkippableFact]
    public async Task TestsReturnsOkWithData()
    {
        const string sampleJson = """[{"id":"t-1","name":"Test Case 1"}]""";
        var tests = ParseJson(sampleJson);

        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1") { ResourceResponses = { ["tests.json"] = tests } },
            new FakeProjectService());

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "tests"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("t-1", json.GetProperty("data")[0].GetProperty("id").GetString());
    }

    [SkippableFact]
    public async Task RateLimitRulesReturnsOkWithExpectedShape()
    {
        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1"),
            new FakeProjectService(hasRateLimits: true));

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "rate-limit-rules"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("proj-1", json.GetProperty("projectId").GetString());
        Assert.Equal("configuration", json.GetProperty("source").GetString());
        Assert.True(json.TryGetProperty("endpointRateLimits", out _));
        Assert.True(json.TryGetProperty("rules", out var rules));
        Assert.Equal(0, rules.GetArrayLength());
    }

    [SkippableFact]
    public async Task SettingsReturnsOkWithSettingsShape()
    {
        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1"),
            new FakeProjectService());

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "settings"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("proj-1", json.GetProperty("projectId").GetString());
        Assert.True(json.TryGetProperty("settings", out var settings));
        Assert.True(settings.TryGetProperty("enabled", out _));
        Assert.True(json.TryGetProperty("config", out _));
        Assert.True(json.TryGetProperty("enableAuthSessions", out _));
        Assert.True(json.TryGetProperty("enableEncryptedRequests", out _));
    }

    [SkippableFact]
    public async Task ConfigAliasReturnsSameShapeAsSettings()
    {
        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1"),
            new FakeProjectService());

        var settingsResult = await handler.ExecuteAsync(
            BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "settings"));
        var configResult = await handler.ExecuteAsync(
            BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "config"));

        Assert.Equal(settingsResult.StatusCode, configResult.StatusCode);
        var settingsJson = JsonSerializer.SerializeToElement(settingsResult.Body);
        var configJson = JsonSerializer.SerializeToElement(configResult.Body);
        Assert.Equal(
            settingsJson.GetProperty("projectId").GetString(),
            configJson.GetProperty("projectId").GetString());
    }

    [SkippableFact]
    public async Task GamePackageReturnsOkWithGamePackageData()
    {
        const string sampleJson = """{"currentRevisionId":42,"lastSyncedAt":"2026-05-01T12:00:00Z"}""";
        var gp = ParseJson(sampleJson);

        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1") { ResourceResponses = { ["game-package.json"] = gp } },
            new FakeProjectService());

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "game-package"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        var gamePackage = json.GetProperty("gamePackage");
        Assert.Equal(42, gamePackage.GetProperty("currentRevisionId").GetInt32());
        Assert.True(gamePackage.TryGetProperty("lastSyncedAtUnix", out var unixEl));
        // 2026-05-01T12:00:00Z
        Assert.Equal(1777636800, unixEl.GetInt64());
    }

    [SkippableFact]
    public async Task GamePackageNullReturnsOkWithNullGamePackage()
    {
        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1"),
            new FakeProjectService());

        var result = await handler.ExecuteAsync(
            BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "game-package"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("gamePackage").ValueKind);
    }

    [SkippableFact]
    public async Task ValidateReturnsOkWithChecks()
    {
        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1"),
            new FakeProjectService());

        var result = await handler.ExecuteAsync(
            BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "validate"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.True(json.TryGetProperty("project", out var project));
        Assert.Equal("proj-1", project.GetProperty("id").GetString());
        Assert.True(json.TryGetProperty("checks", out var checks));
        Assert.True(checks.GetProperty("projectId").GetProperty("ok").GetBoolean());
        Assert.True(checks.GetProperty("secretKey").GetProperty("ok").GetBoolean());
    }

    // ══════════════════════════════════════════════════════════════════
    // Auth failure: invalid/missing key returns management auth error
    // ══════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task MissingApiKeyReturnsUnauthorized()
    {
        var handler = new ManagementReadHandler(
            new FakeKeyResolver(null, "proj-1"),
            new FakeBunnyManagement("proj-1"),
            new FakeProjectService());

        var result = await handler.ExecuteAsync(
            BuildRequest("proj-1", apiKey: null, routeSuffix: "endpoints"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("UNAUTHORIZED",
            json.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task InvalidApiKeyReturnsUnauthorized()
    {
        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1"),
            new FakeProjectService());

        var result = await handler.ExecuteAsync(
            BuildRequest("proj-1", apiKey: "sk-wrong", routeSuffix: "collections"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
    }

    [SkippableFact]
    public async Task PublicApiKeyRejectedForManagement()
    {
        // Management routes require secret keys only; public keys must be rejected
        var resolver = new FakeKeyResolver("pk-valid", "proj-1", keyType: "public");
        var handler = new ManagementReadHandler(
            resolver,
            new FakeBunnyManagement("proj-1"),
            new FakeProjectService());

        var result = await handler.ExecuteAsync(
            BuildRequest("proj-1", apiKey: "pk-valid", routeSuffix: "endpoints"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);
    }

    [SkippableFact]
    public async Task DisabledProjectReturnsProjectDisabled()
    {
        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "disabled-proj"),
            new FakeBunnyManagement("disabled-proj", enabled: false),
            new FakeProjectService());

        var result = await handler.ExecuteAsync(
            BuildRequest("disabled-proj", apiKey: "sk-valid", routeSuffix: "endpoints"));

        Assert.Equal(403, result.StatusCode);
        Assert.Equal("PROJECT_DISABLED", result.PublicErrorCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("PROJECT_DISABLED",
            json.GetProperty("error").GetProperty("code").GetString());
    }

    // ══════════════════════════════════════════════════════════════════
    // Non-GET not handled
    // ══════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task NonGetMethodNotHandled()
    {
        var route = NetworkStorageRouteClassifier.Classify("POST", "/v3/manage/proj-1/endpoints");
        Assert.False(new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1"),
            new FakeProjectService()).CanHandle(route));
    }

    // ══════════════════════════════════════════════════════════════════
    // Unsupported routes return explicit MANAGEMENT_ROUTE_NOT_IMPLEMENTED
    // ══════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task SyncJobReturnsNotImplemented()
    {
        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1"),
            new FakeProjectService());

        var result = await handler.ExecuteAsync(
            BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "sync-jobs", additionalParam: "job-1"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("MANAGEMENT_ROUTE_NOT_IMPLEMENTED",
            json.GetProperty("error").GetString());
    }

    [SkippableFact]
    public async Task AgentManifestReturnsNotImplemented()
    {
        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeBunnyManagement("proj-1"),
            new FakeProjectService());

        var result = await handler.ExecuteAsync(
            BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "agent-manifest"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("MANAGEMENT_ROUTE_NOT_IMPLEMENTED",
            json.GetProperty("error").GetString());
    }

    // ══════════════════════════════════════════════════════════════════
    // Shadow endpoint integration test
    // ══════════════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════════════
    // Store read failure handling
    // ══════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task ResourceReadFailureReturnsReadError()
    {
        var workspace = new FakeBunnyManagement("proj-1", throwsOnRead: true);

        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            workspace,
            new FakeProjectService());

        var result = await handler.ExecuteAsync(
            BuildRequest("proj-1", apiKey: "sk-valid", routeSuffix: "endpoints"));

        Assert.Equal(500, result.StatusCode);
        Assert.Equal("MANAGEMENT_READ_ERROR", result.PublicErrorCode);
    }

    // ══════════════════════════════════════════════════════════════════
    // Missing project (key resolved but project gone) returns auth error
    // ══════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task MissingProjectReturnsUnauthorized()
    {
        var workspace = new FakeBunnyManagement("other-proj"); // no "missing-proj" in projects list

        var handler = new ManagementReadHandler(
            new FakeKeyResolver("sk-valid", "missing-proj"),
            workspace,
            new FakeProjectService());

        var result = await handler.ExecuteAsync(
            BuildRequest("missing-proj", apiKey: "sk-valid", routeSuffix: "endpoints"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
    }

    // ══════════════════════════════════════════════════════════════════
    // Fakes
    // ══════════════════════════════════════════════════════════════════

    [SkippableTheory]
    [InlineData("endpoints", """[{"slug":"test-ep","method":"GET","enabled":true}]""", "slug", "test-ep")]
    [InlineData("queries", """[{"id":"q-1","name":"top_miners","type":"leaderboard"}]""", "id", "q-1")]
    public async Task LiveRoute_ManagementResources_AreServedNativelyWithoutGateway(
        string resource, string sampleJson, string identityField, string identity)
    {
        var data = ParseJson(sampleJson);

        using var liveClient = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new FakeKeyResolver("sk-valid", "proj-1"));
                services.RemoveAll<IWorkspaceStore>();
                services.AddScoped<IWorkspaceStore>(_ => new FakeBunnyManagement("proj-1") { ResourceResponses = { [$"{resource}.json"] = data } });
                services.RemoveAll<INetworkStorageProjectService>();
                services.AddScoped<INetworkStorageProjectService>(_ => new FakeProjectService());
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await liveClient.GetAsync($"/v3/manage/proj-1/{resource}?apiKey=sk-valid");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var json = document.RootElement;
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(identity, json.GetProperty("data")[0].GetProperty(identityField).GetString());
    }
    private sealed class FakeKeyResolver : IStorageApiKeyResolver
    {
        private readonly string? _validKey;
        private readonly string? _projectId;
        private readonly string? _keyType;
        private readonly bool _disabled;

        public FakeKeyResolver(string? validKey, string? projectId, string? keyType = "secret", bool disabled = false)
        {
            _validKey = validKey;
            _projectId = projectId;
            _keyType = keyType;
            _disabled = disabled;
        }

        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(
            string apiKey, string projectId, CancellationToken cancellationToken)
        {
            if (_validKey is null || _keyType is null)
            {
                return Task.FromResult<StorageApiKeyAuthResult?>(null);
            }

            if (!string.Equals(apiKey, _validKey, StringComparison.Ordinal))
            {
                return Task.FromResult<StorageApiKeyAuthResult?>(null);
            }

            return Task.FromResult<StorageApiKeyAuthResult?>(
                new StorageApiKeyAuthResult(
                    UserId: 42,
                    ProjectId: projectId,
                    Enabled: !_disabled,
                    KeyType: _keyType));
        }
    }

    private sealed class FakeBunnyManagement : IWorkspaceStore
    {
        private readonly string _projectId;
        private readonly bool _enabled;
        private readonly bool _throwsOnRead;

        public Dictionary<string, JsonElement> ResourceResponses { get; } = new();

        public FakeBunnyManagement(string projectId, bool enabled = true, bool throwsOnRead = false)
        {
            _projectId = projectId;
            _enabled = enabled;
            _throwsOnRead = throwsOnRead;
        }

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
        {
            if (_throwsOnRead)
            {
                throw new InvalidOperationException($"simulated read failure for {resourcePath}");
            }

            if (ResourceResponses.TryGetValue(resourcePath, out var element))
            {
                return Task.FromResult((T?)(object)element);
            }

            return Task.FromResult<T?>(default);
        }

        public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<WorkspaceProject>>(
                new List<WorkspaceProject>
                {
                    new(Id: _projectId, Name: "Test Project", Description: null, Enabled: _enabled,
                        CreatedAt: null, UpdatedAt: null, CompiledAt: null)
                });
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

    private sealed class FakeProjectService : INetworkStorageProjectService
    {
        private readonly bool _hasRateLimits;

        public FakeProjectService(bool hasRateLimits = false)
        {
            _hasRateLimits = hasRateLimits;
        }

        public Task<ProjectRateLimits> GetProjectRateLimitsAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProjectRateLimits(
                EndpointRateLimits: _hasRateLimits ? new Dictionary<string, object> { ["test-ep"] = new { maxPerMinute = 100 } } : null,
                Rules: _hasRateLimits ? Array.Empty<RateLimitRule>() : null));
        }

        public Task<NetworkStorageProjectCreateResult> CreateProjectAsync(long userId, string name, string? description, bool enabled, bool requireSboxAuth, string keyMode, string organizationId, CancellationToken cancellationToken)
            => Task.FromResult(new NetworkStorageProjectCreateResult("new-proj"));

        public Task<NetworkStorageProjectAccessResult?> ResolveProjectAccessAsync(long userId, string projectId, CancellationToken cancellationToken)
            => Task.FromResult<NetworkStorageProjectAccessResult?>(null);

        public Task<NetworkStorageProjectResources?> GetProjectResourcesAsync(long userId, string projectId, CancellationToken cancellationToken)
            => Task.FromResult<NetworkStorageProjectResources?>(null);
        public Task<NetworkStorageProjectResources?> GetProjectResourcesForOwnerAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken) => GetProjectResourcesAsync(storageOwnerUserId, projectId, cancellationToken);

        public Task<NetworkStorageTeamData?> GetProjectTeamAsync(long storageOwnerUserId, string projectId, string? organizationId, string callerRole, CancellationToken cancellationToken)
            => Task.FromResult<NetworkStorageTeamData?>(null);

        public Task<IReadOnlyList<ApiKeyInfo>> GetProjectKeysAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ApiKeyInfo>>(Array.Empty<ApiKeyInfo>());

        public Task<(ApiKeyInfo Key, string RawKey)> CreateProjectKeyAsync(long storageOwnerUserId, string projectId, string label, string keyType, Dictionary<string, string>? permissions, CancellationToken cancellationToken)
            => Task.FromResult<(ApiKeyInfo, string)>((null!, null!));

        public Task ToggleProjectKeyAsync(long storageOwnerUserId, string projectId, string key, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RemoveProjectKeyAsync(long storageOwnerUserId, string projectId, string key, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task UpdateProjectKeyPermissionsAsync(long storageOwnerUserId, string projectId, string keyIdentifier, Dictionary<string, string> permissions, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteProjectAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task UpdateProjectSettingsAsync(long storageOwnerUserId, string projectId, string settingsTab, Dictionary<string, string> formValues, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<ProjectUsageData> GetProjectUsageAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => Task.FromResult(new ProjectUsageData(CurrentRequests: 0, CurrentBytesIn: 0, CurrentBytesOut: 0, CurrentComputeUnits: 0, CurrentAvgComputeUnits: 0, CurrentAvgCpuUtilPct: 0, CurrentErrors: 0, CurrentBillableBandwidth: 0, PreviousRequests: 0, PreviousBytesIn: 0, PreviousBytesOut: 0, PreviousComputeUnits: 0, PreviousErrors: 0, PreviousBillableBandwidth: 0, PreviousStorageBytes: 0, StorageFootprintBytes: 0, StorageDataBytes: 0, StorageLogBytes: 0, CurrentPeriodStart: 0, CurrentPeriodEnd: 0, PreviousPeriodStart: 0, PreviousPeriodEnd: 0, DailyRequests: null!, ResponseTime: null!, TopEndpoints: null!));

        public Task SaveEndpointRateLimitsAsync(long storageOwnerUserId, string projectId, Dictionary<string, object> endpointRateLimits, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SaveRateLimitRulesAsync(long storageOwnerUserId, string projectId, IReadOnlyList<RateLimitRule> rules, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task<SboxNetworkStorage.Contracts.NetworkStorage.ProjectAuditLogResult> BrowseProjectLogsAsync(long suid, string pid, string? s, string? a, string? d, string sort, int pg, int ps, CancellationToken ct) => Task.FromResult(new SboxNetworkStorage.Contracts.NetworkStorage.ProjectAuditLogResult(System.Array.Empty<SboxNetworkStorage.Contracts.NetworkStorage.ProjectAuditLogEntry>(), 0, 1, 1, ps, false, false));
    }
}

public sealed class NetworkStorageManagementReadCandidateTests_Sqlite(SqliteHostFactory factory) : NetworkStorageManagementReadCandidateTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageManagementReadCandidateTests_Postgres(PostgresHostFactory factory) : NetworkStorageManagementReadCandidateTests<PostgresHostFactory>(factory);
